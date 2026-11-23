using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Reminders;
using Coffer.Api.Scheduling;
using Coffer.Domain.Money;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Asks whether every derived projection still agrees with what it is derived from:
/// the transactions, or, for holdings_currency, each holdings account's brokerage, or,
/// for reminder_auto_post, the ledger's reminders. Writes nothing.
/// </summary>
/// <remarks>
/// See <see cref="LedgerConsistencyReport"/> for why this exists. The short
/// version: the projections are maintained by EF interceptors, any write that
/// bypasses the ChangeTracker skips them, and without this the only way to find
/// out is recomputing — which destroys the evidence.
/// <para>
/// Every comparison here derives its expectation from a PURE function that the
/// corresponding writer also uses (migrations 202 and 206), so a check can never
/// disagree with a repair about what the right answer is.
/// </para>
/// </remarks>
public sealed class LedgerConsistencyRepository
{
    private const int MaxMismatchesPerProjection = 100;

    /// <summary>
    /// "As of the end of time" — the check's horizon must match the writer's, and the
    /// writer has none. Named rather than inlined so a reader sees the intent instead
    /// of wondering why a consistency check cares about the year 9999.
    /// </summary>
    private static readonly DateTime NoHorizon =
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

    /// <summary>
    /// Headers the stored-notes check compares per round trip (<see cref="CheckStoredNotesAsync"/>):
    /// the writers' batch; smaller under test, so a test spans several chunks cheaply.
    /// </summary>
    internal int StoredNotesChunkSize { get; init; } = LegNotes.BatchSize;

    private readonly AppDbContext _db;
    private readonly RegisterRepository _register;
    private readonly HoldingsRecomputeService _holdings;
    private readonly TimeProvider _clock;

    public LedgerConsistencyRepository(
        AppDbContext db,
        RegisterRepository register,
        HoldingsRecomputeService holdings,
        TimeProvider clock)
    {
        _db = db;
        _register = register;
        _holdings = holdings;
        _clock = clock;
    }

    /// <summary>
    /// The stored verdict, or null when nobody has ever checked the ledger. Read through
    /// the app role, so the table's read policy (mig 242) is the backstop.
    /// </summary>
    public async Task<LedgerConsistencyStateDto?> GetStateAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _db.LedgerConsistencyStates.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId)
            .Select(s => new LedgerConsistencyStateDto(
                s.LastCheckedAt, s.Healthy, s.FailingProjections, s.LastRepairedAt))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <param name="ledgerId">The ledger to check.</param>
    /// <param name="today">The caller's local date, as the register takes it, for the
    /// one current value the report carries: the unbacked-price advisory's holding
    /// value. Null for the register's fallback (the background monitor). The checks
    /// themselves are unbounded, like the writers they compare against.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<LedgerConsistencyReport> CheckAsync(
        Guid ledgerId,
        DateOnly? today = null,
        CancellationToken cancellationToken = default)
    {
        var projections = new List<ProjectionConsistency>
        {
            (await CheckBalancesAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckHoldingsAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckRealizedGainsAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckPostingCountsAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckTradePricesAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckStoredNotesAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckHoldingsCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckQuoteCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
        };
        var accounts = await AccountFactsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var categoryAmounts = await CheckCategoryAmountsAsync(ledgerId, accounts, cancellationToken)
            .ConfigureAwait(false);
        projections.Add(categoryAmounts.Check.Projection);
        projections.Add((await CheckReminderAutoPostAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection);

        // Advisory, so it takes no part in Healthy: a price nothing derives may
        // be perfectly correct, and the ledger is not broken for holding one.
        var labels = await SecurityLabelsAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var scheduledFrom = ScheduledBoundary.ScheduledFrom(today, _clock);
        var unbackedRows = await _db.UnbackedPriceSecurities(ledgerId, scheduledFrom)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var (reporting, held) = await HoldingValuesAsync(
                ledgerId, today, scheduledFrom, unbackedRows.Select(u => u.SecurityId).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        // Ordered by the converted value: the function's own order sums currencies.
        // A value that needs a missing rate cannot be ranked, so it follows every valued
        // one; then the function's tiebreaks, count and id.
        var unbacked = unbackedRows
            .Select(u =>
            {
                var value = held.GetValueOrDefault(u.SecurityId, Converted.Of(0m));
                return new UnbackedPriceAdvisory(
                    SecurityId: u.SecurityId,
                    Security: labels.GetValueOrDefault(u.SecurityId, "(security)"),
                    Count: (int)u.UnbackedCount,
                    Earliest: u.Earliest,
                    Latest: u.Latest,
                    HoldingValue: value.Amount,
                    CurrencyCode: reporting,
                    RateUnavailable: value.Unavailable);
            })
            .OrderBy(a => a.HoldingValue is null)
            .ThenByDescending(a => a.HoldingValue ?? 0m)
            .ThenByDescending(a => a.Count)
            .ThenBy(a => a.SecurityId)
            .ToList();

        var oversold = await CheckOversoldAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        // Advisories too (ADR-0114 D15): a figure stored before each currency kept its own
        // places, and a code stored before the accepted list, are kept as they are and
        // leave the ledger sound, so neither takes any part in Healthy.
        var unfit = await UnfitAmountsAsync(ledgerId, accounts, categoryAmounts, cancellationToken)
            .ConfigureAwait(false);
        var refused = await RefusedCurrenciesAsync(ledgerId, accounts, cancellationToken)
            .ConfigureAwait(false);

        return new LedgerConsistencyReport(
            Healthy: projections.All(p => p.Healthy) && oversold.Count == 0,
            Projections: projections,
            UnbackedPrices: unbacked,
            Oversold: oversold)
        {
            UnfitAmounts = unfit,
            RefusedCurrencies = refused,
        };
    }

    /// <summary>The account type of a category, which holds no money of its own (ADR-0114 D5).</summary>
    private const string CategoryType = "category";

    /// <summary>
    /// One account as the Category amounts check and the minor-units advisories read it:
    /// its type and currency, and the account a row names for it (a Holdings sibling's
    /// brokerage, whose register shows its lines; itself otherwise).
    /// </summary>
    private sealed record AccountFact(
        Guid Id,
        string Name,
        string AccountType,
        string CurrencyCode,
        decimal OpeningBalance,
        bool IsSystem,
        Guid ShownId,
        string ShownName)
    {
        public bool IsCategory => string.Equals(AccountType, CategoryType, StringComparison.Ordinal);

        /// <summary>The places money in this account is held to (ADR-0114 D15).</summary>
        public int Places => CurrencyMinorUnits.PlacesOf(CurrencyCode);
    }

    /// <summary>Every account of the ledger (<see cref="AccountFact"/>), by id.</summary>
    private async Task<Dictionary<Guid, AccountFact>> AccountFactsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var rows = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new
            {
                a.Id, a.Name, a.AccountType, a.CurrencyCode, a.OpeningBalance, a.IsSystem, a.HoldingsAccountId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var brokerageOf = rows.Where(a => a.HoldingsAccountId is not null)
            .GroupBy(a => a.HoldingsAccountId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        return rows.ToDictionary(a => a.Id, a =>
        {
            var shown = brokerageOf.TryGetValue(a.Id, out var brokerage) ? (brokerage.Id, brokerage.Name) : (a.Id, a.Name);
            return new AccountFact(a.Id, a.Name, a.AccountType, a.CurrencyCode, a.OpeningBalance, a.IsSystem,
                shown.Item1, shown.Item2);
        });
    }

    /// <summary>One transaction, or one reminder's template, as an advisory row or a Category
    /// amounts line names it. <c>IsHidden</c>: a hidden transaction, which its register
    /// shows only in the Hidden view (ADR-0072 D1). <c>IsMergedAway</c>: a transaction
    /// merged into another, which no register view shows.</summary>
    private sealed record HeaderFact(
        DateOnly Date, string? Payee, bool IsTemplate, Guid? ReminderId, bool IsHidden, bool IsMergedAway);

    /// <summary>The headers <paramref name="headerIds"/> names (<see cref="HeaderFact"/>), a
    /// template with the reminder whose template it is.</summary>
    private async Task<Dictionary<Guid, HeaderFact>> HeaderFactsAsync(
        IReadOnlyCollection<Guid> headerIds, CancellationToken cancellationToken)
    {
        if (headerIds.Count == 0) return [];
        var ids = headerIds.ToList();
        var headers = await _db.TxnHeaders.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.PostedAt, h.Payee, h.IsRecurringTemplate, h.IsHidden, MergedAway = h.IsMergedInto != null })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var templateIds = headers.Where(h => h.IsRecurringTemplate).Select(h => h.Id).ToList();
        var reminders = templateIds.Count == 0
            ? []
            : await _db.RecurringTransactions.AsNoTracking()
                .Where(r => r.TemplateHeaderId != null && templateIds.Contains(r.TemplateHeaderId.Value))
                .Select(r => new { r.Id, TemplateHeaderId = r.TemplateHeaderId!.Value })
                .ToDictionaryAsync(r => r.TemplateHeaderId, r => r.Id, cancellationToken)
                .ConfigureAwait(false);
        return headers.ToDictionary(h => h.Id, h => new HeaderFact(
            DateOnly.FromDateTime(h.PostedAt),
            h.Payee,
            h.IsRecurringTemplate,
            reminders.TryGetValue(h.Id, out var reminderId) ? reminderId : null,
            h.IsHidden,
            h.MergedAway));
    }

    /// <summary>
    /// A line of a transaction, or of a reminder's template, as an advisory row: on
    /// <paramref name="account"/>, in its currency. A hidden transaction says so, so its
    /// link opens the register's Hidden view, the one view that shows it (ADR-0072 D1).
    /// </summary>
    private static UnfitAmountAdvisory LineRow(
        Guid headerId, HeaderFact header, AccountFact account, string? reason) =>
        header.IsTemplate
            ? new UnfitAmountAdvisory(
                UnfitAmountKinds.Reminder, account.ShownId, account.ShownName, account.CurrencyCode, account.Places,
                Payee: header.Payee, ReminderId: header.ReminderId, Reason: reason)
            : new UnfitAmountAdvisory(
                UnfitAmountKinds.Transaction, account.ShownId, account.ShownName, account.CurrencyCode, account.Places,
                HeaderId: headerId, Date: header.Date, Payee: header.Payee, Reason: reason, Hidden: header.IsHidden);

    /// <summary>Where a line is, as the Maintenance panel's advisory lines name it: the
    /// account, the date and the payee of a transaction, or the reminder.</summary>
    private static string LineScope(HeaderFact header, AccountFact account) =>
        header.IsTemplate
            ? (header.Payee ?? "(reminder)") + " · reminder"
            : account.ShownName + " · " + header.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
              + (header.Payee is { } payee ? " · " + payee : "");

    /// <summary>
    /// One posting that holds a category line, by its shape: how many real lines and how
    /// many category lines it has, and the sum of every line's amount.
    /// </summary>
    private sealed class PostingShape
    {
        public Guid HeaderId { get; init; }
        public int PostingIndex { get; init; }
        public int RealLines { get; init; }
        public int CategoryLines { get; init; }
        public decimal Sum { get; init; }
    }

    /// <summary>Every posting of the ledger with a category line on it
    /// (<see cref="PostingShape"/>), a reminder's template included, in the database.</summary>
    private IQueryable<PostingShape> PostingsWithACategoryLine(Guid ledgerId) =>
        (from l in _db.TxnLegs.AsNoTracking()
         where l.LedgerId == ledgerId
         join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
         group new { l.Amount, Category = a.AccountType == CategoryType }
             by new { l.HeaderId, l.PostingIndex } into g
         select new PostingShape
         {
             HeaderId = g.Key.HeaderId,
             PostingIndex = g.Key.PostingIndex,
             RealLines = g.Count(x => !x.Category),
             CategoryLines = g.Count(x => x.Category),
             Sum = g.Sum(x => x.Amount),
         })
        .Where(p => p.CategoryLines > 0);

    /// <summary>What the Category amounts check found: the projection, the advisory rows for
    /// the shapes its repair leaves, and the postings it reports, which the places advisory
    /// does not list again.</summary>
    private sealed record CategoryAmountsCheck(
        CheckResult Check,
        IReadOnlyList<UnfitAmountAdvisory> Advisory,
        IReadOnlySet<(Guid HeaderId, int PostingIndex)> Reported);

    /// <summary>
    /// The category lines that do not carry the real side's amount negated
    /// (<see cref="ConsistencyProjections.CategoryAmounts"/>, ADR-0114 D13): a FINDING,
    /// counted in the verdict, whose repair sets each to the real line negated.
    /// </summary>
    /// <remarks>
    /// <para>Judged by structure alone. A posting of exactly one real line and one category
    /// line is out of step when the two do not sum to zero, whatever their signs or zeros,
    /// and is reported here. Every other posting with a category line that does not sum to
    /// zero is listed in the "Amounts with more decimals than their currency" advisory with
    /// its reason, and nothing rewrites it: both lines on categories
    /// (<see cref="UnfitAmountReasons.CategoryLineBetweenCategories"/>), or more than one
    /// real or category line (<see cref="UnfitAmountReasons.CategoryLineManyLines"/>),
    /// except on a transaction merged into another, which is not listed until deleting its
    /// winner un-merges it (see below). A posting of one category line and nothing else has
    /// no other line to mirror, and is in neither.</para>
    /// <para>The finding counts every stored posting, a reminder's template and a hidden or
    /// merged-away row included: each is a stored figure a register, a fire, a restore or
    /// an un-merge reads, and the repair, not an edit, is its cure, so it reaches a row no
    /// one can open (settled by Claude, 2026-11-17).</para>
    /// <para>The shapes the repair leaves are part of the places advisory instead, whose
    /// cure is an edit, so a row merged into another is not listed there, as the advisory
    /// and the relabel check leave it out for its places (decided by the owner, 2026-11-17;
    /// ADR-0114 D15).</para>
    /// <para><c>Checked</c> counts the postings of one real line and one category line. The
    /// shapes are counted in the database; only the postings that do not sum to zero are
    /// read back.</para>
    /// </remarks>
    private async Task<CategoryAmountsCheck> CheckCategoryAmountsAsync(
        Guid ledgerId, IReadOnlyDictionary<Guid, AccountFact> accounts, CancellationToken cancellationToken)
    {
        var checkedCount = await PostingsWithACategoryLine(ledgerId)
            .CountAsync(p => p.RealLines == 1 && p.CategoryLines == 1, cancellationToken)
            .ConfigureAwait(false);
        var outOfStep = await PostingsWithACategoryLine(ledgerId)
            .Where(p => p.Sum != 0m && (p.RealLines > 0 || p.CategoryLines > 1))
            .Select(p => new { p.HeaderId, p.PostingIndex })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (outOfStep.Count == 0)
            return new CategoryAmountsCheck(
                Build(ConsistencyProjections.CategoryAmounts, checkedCount, []), [], new HashSet<(Guid, int)>());

        var headerIds = outOfStep.Select(p => p.HeaderId).Distinct().ToList();
        var legs = (await _db.TxnLegs.AsNoTracking()
                .Where(l => headerIds.Contains(l.HeaderId))
                .Select(l => new { l.HeaderId, l.PostingIndex, l.AccountId, l.Amount })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(l => accounts.ContainsKey(l.AccountId))
            .ToLookup(l => (l.HeaderId, l.PostingIndex));
        var headers = await HeaderFactsAsync(headerIds, cancellationToken).ConfigureAwait(false);

        var mismatches = new List<(AccountFact Account, HeaderFact Header, Guid HeaderId, ConsistencyMismatch Mismatch)>();
        var advisory = new List<UnfitAmountAdvisory>();
        var reported = new HashSet<(Guid, int)>();
        foreach (var posting in outOfStep)
        {
            if (!headers.TryGetValue(posting.HeaderId, out var header)) continue;
            // Judged again from the lines read here, not from the shape counted above: the
            // check takes no lock, so an edit committed between the two reads may have
            // removed the posting, mirrored it or changed its shape.
            var lines = legs[(posting.HeaderId, posting.PostingIndex)].ToList();
            var real = lines.Where(l => !accounts[l.AccountId].IsCategory).ToList();
            var categories = lines.Where(l => accounts[l.AccountId].IsCategory).ToList();
            if (lines.Sum(l => l.Amount) == 0m) continue;
            if (categories.Count == 0 || (real.Count == 0 && categories.Count < 2)) continue;

            if (real.Count == 1 && categories.Count == 1)
            {
                var account = accounts[real[0].AccountId];
                reported.Add((posting.HeaderId, posting.PostingIndex));
                // A register shows the line, so it links there: a hidden one in the Hidden
                // view alone (ADR-0072 D1). No register shows a reminder's template or a row
                // merged into another, so those name the line with no ids and no link.
                var shown = !header.IsTemplate && !header.IsMergedAway;
                mismatches.Add((account, header, posting.HeaderId, new ConsistencyMismatch(
                    Scope: LineScope(header, account),
                    Field: "category line",
                    Stored: categories[0].Amount,
                    Expected: -real[0].Amount,
                    AccountId: shown ? account.ShownId : null,
                    HeaderId: shown ? posting.HeaderId : null,
                    Hidden: shown && header.IsHidden)));
                continue;
            }

            // The repair leaves it. Listed on each real line's account, whose register shows
            // the posting; with no real line, on each category's own, in its own currency
            // (ADR-0114 D15). A row merged into another is not listed: no screen shows or
            // opens it, so the advisory would name a line no screen reaches (ADR-0114 D15).
            if (header.IsMergedAway) continue;
            var reason = real.Count == 0 && categories.Count == 2
                ? UnfitAmountReasons.CategoryLineBetweenCategories
                : UnfitAmountReasons.CategoryLineManyLines;
            foreach (var line in real.Count > 0 ? real : categories)
                advisory.Add(LineRow(posting.HeaderId, header, accounts[line.AccountId], reason));
        }

        var ordered = mismatches
            .OrderBy(m => m.Account.ShownName, StringComparer.Ordinal)
            .ThenBy(m => m.Header.IsTemplate)
            .ThenBy(m => m.Header.Date)
            .ThenBy(m => m.HeaderId)
            .Select(m => m.Mismatch)
            .ToList();
        return new CategoryAmountsCheck(
            Build(ConsistencyProjections.CategoryAmounts, checkedCount, ordered), advisory, reported);
    }

    /// <summary>
    /// The "Amounts with more decimals than their currency" advisory
    /// (<see cref="LedgerConsistencyReport.UnfitAmounts"/>, ADR-0114 D15): every stored
    /// figure with more places than its currency allows, and the category lines the Category
    /// amounts repair leaves (<paramref name="categoryAmounts"/>).
    /// </summary>
    /// <remarks>
    /// <para>A line is judged at the currency it reads in: a real line at its account's, a
    /// category line beside a real line at that real line's (ADR-0114 D13), and a line of a
    /// posting between categories at its category's own code. One row per transaction and
    /// account, so a category line that mirrors an unfit real line is not listed twice
    /// (it names the real line's account), and a category line the Category amounts check
    /// reports is not listed again: its repair makes it the real line negated. A line of a
    /// posting that repair leaves is listed once, with that posting's reason, even when the
    /// line also has more places than its currency. A
    /// reminder's template line is a reminder row, on the template line's account.</para>
    /// <para>A transaction merged into another is not listed (decided by the owner,
    /// 2026-11-17; ADR-0114 D15): no screen shows or opens it, so its line would be a dead
    /// end, and the relabel check leaves it out of its count for the same reason. Deleting
    /// its winner un-merges it (<c>is_merged_into</c> ON DELETE SET NULL), and a line of it
    /// that does not fit is then listed like any other row. A hidden row is listed, with
    /// its Hidden view link.</para>
    /// <para>Account-level figures follow: an opening balance at its account's currency,
    /// a loan's money terms (principal, escrow, fixed payment) at the loan's, and a budget
    /// target at the currency it is stamped with.</para>
    /// <para>The lines are compared in the database, one statement per places value the
    /// ledger's currencies use, so only the figures that do not fit are read back.</para>
    /// </remarks>
    private async Task<List<UnfitAmountAdvisory>> UnfitAmountsAsync(
        Guid ledgerId,
        IReadOnlyDictionary<Guid, AccountFact> accounts,
        CategoryAmountsCheck categoryAmounts,
        CancellationToken cancellationToken)
    {
        // (header, account of the line's currency): one row each.
        var lines = new HashSet<(Guid HeaderId, Guid AccountId)>();
        var categoryIds = accounts.Values.Where(a => a.IsCategory).Select(a => a.Id).ToList();

        foreach (var group in accounts.Values.Where(a => !a.IsCategory).GroupBy(a => a.Places))
        {
            var places = group.Key;
            var ids = group.Select(a => a.Id).ToList();

            // A real line, at its own account's places.
            var real = await _db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == ledgerId && ids.Contains(l.AccountId) && Math.Round(l.Amount, places) != l.Amount)
                .Select(l => new { l.HeaderId, l.AccountId })
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var l in real) lines.Add((l.HeaderId, l.AccountId));

            // A category line beside a real line, at the real line's places.
            var beside = await (
                    from c in _db.TxnLegs.AsNoTracking()
                    where c.LedgerId == ledgerId && categoryIds.Contains(c.AccountId)
                          && Math.Round(c.Amount, places) != c.Amount
                    join r in _db.TxnLegs.AsNoTracking()
                        on new { c.HeaderId, c.PostingIndex } equals new { r.HeaderId, r.PostingIndex }
                    where ids.Contains(r.AccountId)
                    select new { c.HeaderId, c.PostingIndex, r.AccountId })
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var l in beside.Where(l => !categoryAmounts.Reported.Contains((l.HeaderId, l.PostingIndex))))
                lines.Add((l.HeaderId, l.AccountId));
        }

        foreach (var group in accounts.Values.Where(a => a.IsCategory).GroupBy(a => a.Places))
        {
            var places = group.Key;
            var ids = group.Select(a => a.Id).ToList();

            // A line of a posting between categories, at its category's own places.
            var between = await _db.TxnLegs.AsNoTracking()
                .Where(c => c.LedgerId == ledgerId && ids.Contains(c.AccountId)
                            && Math.Round(c.Amount, places) != c.Amount
                            && !_db.TxnLegs.Any(r => r.LedgerId == ledgerId
                                                     && r.HeaderId == c.HeaderId && r.PostingIndex == c.PostingIndex
                                                     && _db.Accounts.Any(a => a.Id == r.AccountId
                                                                              && a.AccountType != CategoryType)))
                .Select(c => new { c.HeaderId, c.AccountId })
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var l in between) lines.Add((l.HeaderId, l.AccountId));
        }

        var headers = await HeaderFactsAsync(lines.Select(l => l.HeaderId).Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<UnfitAmountAdvisory>(categoryAmounts.Advisory);
        // A line the Category amounts repair leaves is already listed, with its reason, on
        // the same transaction and account: its row stands for the figure that does not fit too.
        var reasoned = categoryAmounts.Advisory
            .Select(r => (r.Kind, r.AccountId, r.HeaderId, r.ReminderId))
            .ToHashSet();
        foreach (var (headerId, accountId) in lines)
        {
            if (!headers.TryGetValue(headerId, out var header) || !accounts.TryGetValue(accountId, out var account))
                continue;
            // A row merged into another is left out (ADR-0114 D15): no screen shows or opens
            // it. Deleting its winner un-merges it, and it is then listed like any other row.
            if (header.IsMergedAway) continue;
            var row = LineRow(headerId, header, account, reason: null);
            if (!reasoned.Contains((row.Kind, row.AccountId, row.HeaderId, row.ReminderId)))
                rows.Add(row);
        }

        foreach (var account in accounts.Values.Where(a => !CurrencyMinorUnits.Fits(a.OpeningBalance, a.CurrencyCode)))
            rows.Add(new UnfitAmountAdvisory(
                UnfitAmountKinds.OpeningBalance, account.ShownId, account.ShownName, account.CurrencyCode, account.Places));

        var loans = await _db.LoanTerms.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .Select(t => new { t.AccountId, t.OriginalPrincipal, t.EscrowAmount, t.FixedPayment })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var loan in loans)
        {
            if (!accounts.TryGetValue(loan.AccountId, out var account)) continue;
            var code = account.CurrencyCode;
            if (CurrencyMinorUnits.Fits(loan.OriginalPrincipal, code)
                && CurrencyMinorUnits.Fits(loan.EscrowAmount, code)
                && (loan.FixedPayment is not { } fixedPayment || CurrencyMinorUnits.Fits(fixedPayment, code)))
                continue;
            rows.Add(new UnfitAmountAdvisory(
                UnfitAmountKinds.LoanTerms, account.ShownId, account.ShownName, code, account.Places));
        }

        var targetCodes = await _db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .Select(t => t.CurrencyCode)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var group in targetCodes.GroupBy(CurrencyMinorUnits.PlacesOf))
        {
            var places = group.Key;
            var codes = group.ToList();
            var targets = await _db.BudgetTargets.AsNoTracking()
                .Where(t => t.LedgerId == ledgerId && codes.Contains(t.CurrencyCode)
                            && Math.Round(t.Amount, places) != t.Amount)
                .Select(t => new { t.CategoryId, t.TargetMonth, t.CurrencyCode })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var target in targets)
            {
                if (!accounts.TryGetValue(target.CategoryId, out var category)) continue;
                rows.Add(new UnfitAmountAdvisory(
                    UnfitAmountKinds.BudgetTarget, category.ShownId, category.ShownName, target.CurrencyCode, places,
                    Month: target.TargetMonth));
            }
        }

        return rows
            .Distinct()
            .OrderBy(r => r.Account, StringComparer.Ordinal)
            .ThenBy(r => KindOrder(r.Kind))
            .ThenBy(r => r.Date)
            .ThenBy(r => r.Month)
            .ThenBy(r => r.Payee, StringComparer.Ordinal)
            .ThenBy(r => r.HeaderId)
            .ThenBy(r => r.ReminderId)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();

        static int KindOrder(string kind) => kind switch
        {
            UnfitAmountKinds.Transaction => 0,
            UnfitAmountKinds.OpeningBalance => 1,
            UnfitAmountKinds.LoanTerms => 2,
            UnfitAmountKinds.BudgetTarget => 3,
            _ => 4,
        };
    }

    /// <summary>
    /// The "Currencies Coffer no longer accepts" advisory
    /// (<see cref="LedgerConsistencyReport.RefusedCurrencies"/>, ADR-0114 D15): the ledger's
    /// reporting currency, then each real account's, where the code is not one an account or
    /// a ledger can now take. A system account is not listed: a Holdings sibling keeps its
    /// brokerage's code and the Uncategorized category the ledger's, each listed there. No
    /// category is listed either: the account-currency rule does not apply to one, and its
    /// row takes the ledger's reporting currency, which it cannot be given another of, so the
    /// reporting-currency row covers it.
    /// </summary>
    private async Task<List<RefusedCurrencyAdvisory>> RefusedCurrenciesAsync(
        Guid ledgerId, IReadOnlyDictionary<Guid, AccountFact> accounts, CancellationToken cancellationToken)
    {
        var rows = new List<RefusedCurrencyAdvisory>();
        var reporting = await _db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reporting is not null && !CurrencyMinorUnits.IsAcceptedAccountCurrency(reporting))
            rows.Add(new RefusedCurrencyAdvisory(RefusedCurrencyKinds.Reporting, null, null, reporting));
        rows.AddRange(accounts.Values
            .Where(a => !a.IsSystem && !a.IsCategory && !CurrencyMinorUnits.IsAcceptedAccountCurrency(a.CurrencyCode))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.Id)
            .Select(a => new RefusedCurrencyAdvisory(RefusedCurrencyKinds.Account, a.Id, a.Name, a.CurrencyCode)));
        return rows;
    }

    /// <summary>
    /// What each advisory security's holding is worth now, in the ledger's reporting
    /// currency (ADR-0114 D7): every position <c>holdings_as_of_now</c> gives, at its
    /// current price (in the price's currency) or else at its cost basis (in its
    /// brokerage's), converted at the latest rate and summed, rounded once. A security
    /// nobody holds has no entry and is worth 0. A missing rate makes the value null and
    /// names it (ADR-0114 D9). One read of the as-of-now function and one rate load, for
    /// all the advisory's securities together.
    /// </summary>
    private async Task<(string Reporting, Dictionary<Guid, Converted> Values)> HoldingValuesAsync(
        Guid ledgerId, DateOnly? today, DateTime scheduledFrom, Guid[] securityIds,
        CancellationToken cancellationToken)
    {
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        if (securityIds.Length == 0) return (currencies.Reporting, []);

        var positions = await _db.HoldingsAsOfNow(ledgerId, scheduledFrom, null)
            .AsNoTracking()
            .Where(h => securityIds.Contains(h.SecurityId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var valued = positions
            .Select(p => (p.SecurityId,
                Value: p.Price is { } px ? ReportingScale.MarketValue(p.Quantity, px) : p.CostBasis,
                Currency: p.Price is null ? currencies.OfPositionsOn(p.AccountId) : p.PriceCurrency!))
            .ToList();

        var at = RateDay.Latest();
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        foreach (var v in valued) at.Declare(needs, v.Currency, currencies.Reporting);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var values = valued
            .GroupBy(v => v.SecurityId)
            .ToDictionary(g => g.Key, g =>
            {
                var total = rates.NewTotal(currencies.Reporting);
                foreach (var v in g) total.Add(v.Value, v.Currency, at);
                return total.Result;
            });
        return (currencies.Reporting, values);
    }

    /// <summary>
    /// Positions a sale took more shares from than they held (FU-097, migration 256).
    /// </summary>
    /// <remarks>
    /// Not a projection: nothing disagrees with the transactions, the transactions
    /// themselves are short. So it is listed, not repaired: the purchase the walk is
    /// missing is not in the data, and a repair would have to invent one or cut the
    /// sale. Asked of the same walk the writer persists, so the
    /// list is exactly the positions whose stored gain is overstated.
    /// </remarks>
    private async Task<List<OversoldPosition>> CheckOversoldAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var shortfalls = await _db.HoldingsFifoShortfalls(ledgerId)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (shortfalls.Count == 0) return [];

        var accounts = await TradingAccountsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var securities = await SecurityLabelsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return shortfalls
            .Select(s =>
            {
                var account = accounts.GetValueOrDefault(s.AccountId, (Id: s.AccountId, Name: "(account)"));
                return new OversoldPosition(
                    AccountId: account.Id,
                    Account: account.Name,
                    SecurityId: s.SecurityId,
                    Security: securities.GetValueOrDefault(s.SecurityId, "(security)"),
                    SharesShort: s.Shortfall);
            })
            .OrderBy(o => o.Account, StringComparer.Ordinal)
            .ThenBy(o => o.Security, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Running balances, via the read-only walk (mig 206).</summary>
    private async Task<CheckResult> CheckBalancesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var report = await _register.CheckBalancesAsync(ledgerId, cancellationToken)
                                    .ConfigureAwait(false);
        return BalancesFrom(report);
    }

    /// <summary>
    /// Holdings quantity and cost basis, against the same FIFO walk the recompute
    /// persists (mig 202).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer and the walk hold different sets of positions, so this compares what
    /// both should hold. <c>recompute_holdings_cost_basis</c>, given an (account,
    /// security), creates the position's row when there is none and then stores the
    /// walk's figures, and nothing removes the row when the position closes: a closed
    /// position stays stored at quantity 0. <c>holdings_cost_basis_as_of</c> returns
    /// only the positions whose walked quantity is not 0.
    /// </para>
    /// <list type="bullet">
    /// <item><description>A position the walk returns is compared on quantity and on
    /// cost basis, and one with no stored row is drift, reason
    /// <see cref="ConsistencyReasons.NotStored"/>, one mismatch per figure, each
    /// with Stored 0. Every save that writes an investment leg, or changes a header
    /// field the walk reads on a header that has one (its date, action, hidden or
    /// merged flag: a soft-delete, a restore, a merge, a re-date), recomputes the
    /// position of each such leg, and the recompute creates the row, so a position the
    /// walk holds with no row is a per-position recompute that did not run.</description></item>
    /// <item><description>A stored row the walk does not return is a position whose
    /// walked quantity is 0, which the recompute stores as 0: a stored quantity other
    /// than 0 is drift. Its cost basis is not compared. The walk returns none for it,
    /// and a position oversold and bought back to 0 keeps the cost of the purchase
    /// (<see cref="CheckOversoldAsync"/>).</description></item>
    /// </list>
    /// <para>
    /// The stored rows and the walk read one state (<see cref="ReadSnapshot"/>), so a
    /// position saved while the check runs is not reported as missing, nor its figures as
    /// drift. Inside a caller's transaction below REPEATABLE READ, which would give each
    /// read its own state, the check refuses to run rather than join it.
    /// </para>
    /// </remarks>
    private async Task<CheckResult> CheckHoldingsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        await using var snapshot = await ReadSnapshot.BeginAsync(_db, cancellationToken, requireRepeatableRead: true)
            .ConfigureAwait(false);

        var stored = await _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId)
            .Select(h => new { h.AccountId, h.SecurityId, h.Quantity, h.CostBasis })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // NO upper bound, because the WRITER has none. recompute_holdings_cost_basis
        // calls holdings_fifo_walk(account, security, NULL) — every event, whenever
        // posted — so stored holdings include future-dated ones. Asking here for
        // "as of now" would compare stored-including-future against expected-excluding-
        // future, and a scheduled transaction is a first-class state (the register's
        // whole "scheduled" tab is headers posted after now). One future-dated trade
        // would then report drift the repair cannot fix: the repair walks
        // unbounded, re-stores the same values, and the next check reports the same
        // mismatch, forever.
        //
        // A check must expect exactly what the writer produces. The bound is a
        // non-nullable parameter on the EF binding, so "no bound" is expressed as the
        // maximum representable instant; the SQL's own comparisons are
        // `posted_at <= p_as_of`, which that satisfies for every real event.
        var walked = (await _db.HoldingsCostBasisAsOf(ledgerId, NoHorizon, null)
            .Select(r => new { r.AccountId, r.SecurityId, r.Quantity, r.CostBasis })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(r => (r.AccountId, r.SecurityId));

        // The labels below need no snapshot. One the check joined is its caller's to end.
        if (snapshot is not null) await snapshot.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

        var names = await AccountNamesAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var securities = await SecurityLabelsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        string Scope(Guid accountId, Guid securityId) =>
            names.GetValueOrDefault(accountId, "(account)")
            + " / " + securities.GetValueOrDefault(securityId, "(security)");

        // The two kinds the balances and trade prices name too (ConsistencyReasons): a
        // stored row that holds other figures, and a position the walk holds with no
        // stored row.
        var mismatches = new List<ConsistencyMismatch>();
        foreach (var h in stored)
        {
            var scope = Scope(h.AccountId, h.SecurityId);
            if (!walked.TryGetValue((h.AccountId, h.SecurityId), out var w))
            {
                // The walk holds no shares here, so the recompute stores quantity 0.
                if (h.Quantity != 0m)
                    mismatches.Add(new ConsistencyMismatch(scope, "quantity", h.Quantity, 0m,
                        AccountId: h.AccountId, SecurityId: h.SecurityId,
                        Reason: ConsistencyReasons.ValueDiffers));
                continue;
            }
            if (w.Quantity != h.Quantity)
                mismatches.Add(new ConsistencyMismatch(scope, "quantity", h.Quantity, w.Quantity,
                    AccountId: h.AccountId, SecurityId: h.SecurityId,
                    Reason: ConsistencyReasons.ValueDiffers));
            if (w.CostBasis != h.CostBasis)
                mismatches.Add(new ConsistencyMismatch(scope, "cost_basis", h.CostBasis, w.CostBasis,
                    AccountId: h.AccountId, SecurityId: h.SecurityId,
                    Reason: ConsistencyReasons.ValueDiffers));
        }

        var storedPositions = stored.Select(h => (h.AccountId, h.SecurityId)).ToHashSet();
        var unstored = walked.Values
            .Where(w => !storedPositions.Contains((w.AccountId, w.SecurityId)))
            .ToList();
        foreach (var w in unstored)
        {
            // Said as not stored, with Stored 0, as a missing balance row is: the
            // figures alone would read as a stored 0.
            var scope = Scope(w.AccountId, w.SecurityId);
            mismatches.Add(new ConsistencyMismatch(scope, "quantity, not stored", 0m, w.Quantity,
                AccountId: w.AccountId, SecurityId: w.SecurityId,
                Reason: ConsistencyReasons.NotStored));
            mismatches.Add(new ConsistencyMismatch(scope, "cost_basis, not stored", 0m, w.CostBasis,
                AccountId: w.AccountId, SecurityId: w.SecurityId,
                Reason: ConsistencyReasons.NotStored));
        }

        // Positions examined: every stored row, and every walked position with none.
        return Build(ConsistencyProjections.Holdings, stored.Count + unstored.Count, mismatches);
    }

    /// <summary>
    /// Realized gains per (account, security), against the pure walk (mig 206).
    /// </summary>
    /// <remarks>
    /// Compared at the grain the table STORES — one rounded row per disposal,
    /// summed — rather than by rounding a total. Rounding a sum and summing
    /// rounded rows differ by up to a cent per disposal, so comparing a rounded
    /// total reports drift that does not exist.
    /// </remarks>
    /// <summary>
    /// Per-disposal realized gains, against the pure walk (mig 206, widened by 217).
    /// </summary>
    /// <remarks>
    /// <para>
    /// PER ROW, and across every money column, because the two obvious shortcuts each
    /// hide the drift they exist to find. Comparing <c>SUM(realized_gain)</c> per
    /// (account, security) is blind twice over: two disposals drifting a cent in
    /// opposite directions in the same position sum to zero and report healthy, and the
    /// three long-term columns go uncompared — the walk returns them since mig 217 —
    /// so a ledger whose entire short/long tax split had been zeroed would pass. Those
    /// columns are NOT NULL DEFAULT 0, so such a zeroing is a silent write rather than
    /// an error.
    /// </para>
    /// <para>
    /// The comparison is over the INTERSECTION on <c>sell_leg_id</c> — rows that exist
    /// on both sides — and deliberately does not report rows present in one and not the
    /// other. The walk legitimately omits disposals on hidden headers, on merged headers
    /// (mig 163) and on <c>transfer_shares</c> actions (ADR-0065 D1), so an install
    /// carrying legacy rows of those shapes would light up with drift that no repair can
    /// resolve. This file already records what that costs: cry wolf once and the check
    /// gets ignored forever. Row-set divergence is a real question and wants its own
    /// answer, not a false positive bolted onto this one.
    /// </para>
    /// </remarks>
    private async Task<CheckResult> CheckRealizedGainsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var positions = await _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId)
            .Select(h => new { h.AccountId, h.SecurityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var names = await AccountNamesAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var mismatches = new List<ConsistencyMismatch>();
        foreach (var p in positions)
        {
            var stored = await _db.RealizedGains.AsNoTracking()
                .Where(g => g.AccountId == p.AccountId && g.SecurityId == p.SecurityId)
                .Select(g => new
                {
                    g.SellLegId,
                    g.Proceeds,
                    g.CostBasisSold,
                    g.RealizedGain,
                    g.ProceedsLt,
                    g.CostBasisSoldLt,
                    g.RealizedGainLt,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var walked = await _db.RealizedGainsWalk(p.AccountId, p.SecurityId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var walkedByLeg = walked
                .GroupBy(w => w.SellLegId)
                .ToDictionary(g => g.Key, g => g.First());

            var accountName = names.GetValueOrDefault(p.AccountId, "(account)");
            foreach (var row in stored)
            {
                if (!walkedByLeg.TryGetValue(row.SellLegId, out var w)) continue;

                // One mismatch per COLUMN, not one per row: a reader fixing this needs
                // to know that the long-term split drifted rather than that "the row"
                // did, and the two have different causes.
                Compare("proceeds", row.Proceeds, w.Proceeds);
                Compare("cost_basis_sold", row.CostBasisSold, w.CostBasisSold);
                Compare("realized_gain", row.RealizedGain, w.RealizedGain);
                Compare("proceeds_lt", row.ProceedsLt, w.ProceedsLt);
                Compare("cost_basis_sold_lt", row.CostBasisSoldLt, w.CostBasisSoldLt);
                Compare("realized_gain_lt", row.RealizedGainLt, w.RealizedGainLt);

                // No cap here even though only MaxMismatchesPerProjection are
                // displayed: Build reports mismatches.Count as the TOTAL, so
                // stopping early would make a badly drifted ledger report exactly
                // 100 and understate itself at the moment it most needs not to.
                void Compare(string field, decimal storedValue, decimal expected)
                {
                    if (storedValue == expected) return;
                    mismatches.Add(new ConsistencyMismatch(
                        Scope: accountName + " / " + p.SecurityId + " / " + row.SellLegId,
                        Field: field,
                        Stored: storedValue,
                        Expected: expected,
                        AccountId: p.AccountId,
                        SecurityId: p.SecurityId));
                }
            }
        }

        // POSITIONS examined, not disposals compared. Briefly changed to the latter on
        // the reasoning that the unit of comparison had moved to the row — wrong, and an
        // existing vacuity guard said so: a position holding shares it has never sold is
        // still a position this check looked at, and reporting 0 for a healthy ledger
        // that simply has not sold anything reads as "this projection did nothing".
        return Build(ConsistencyProjections.RealizedGains, positions.Count, mismatches);
    }

    /// <summary>
    /// The denormalised posting counts on <c>txn_legs</c> (mig 120), against the
    /// definition the recompute uses.
    /// </summary>
    /// <remarks>
    /// <c>header_total_postings</c> is <c>COUNT(DISTINCT posting_index)</c>, NOT a
    /// count of legs — a two-leg transfer shares one posting index and counts as
    /// one. Counting legs would flag every multi-leg header in a perfectly
    /// consistent ledger, which is the failure mode a consistency check can least
    /// afford: cry wolf once and it gets ignored forever.
    /// </remarks>
    private async Task<CheckResult> CheckPostingCountsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var actual = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId)
            .GroupBy(l => l.HeaderId)
            .Select(g => new
            {
                HeaderId = g.Key,
                Total = g.Select(l => l.PostingIndex).Distinct().Count(),
                Stored = g.Max(l => l.HeaderTotalPostings),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // No cap here, for the reason spelled out on the realized-gains compare:
        // Build reports mismatches.Count as the TOTAL, so stopping at the display
        // cap made a badly drifted ledger report exactly 100 — understating itself
        // at the moment it most needs not to, and capping the repair with it.
        var mismatches = actual
            .Where(a => a.Stored != a.Total)
            .Select(a => new ConsistencyMismatch(
                Scope: "header " + a.HeaderId,
                Field: "header_total_postings",
                Stored: a.Stored,
                Expected: a.Total,
                HeaderId: a.HeaderId))
            .ToList();

        return Build(ConsistencyProjections.PostingCounts, actual.Count, mismatches);
    }

    /// <summary>
    /// Trade-derived <c>security_prices</c>, via <c>fn_trade_price_check</c>
    /// (migration 232).
    /// </summary>
    /// <remarks>
    /// The rule lives in the function, not here, and that is the point: it is a
    /// reader of migration 177's rank-gated writer, and a re-statement of the
    /// writer's logic in LINQ would be a second copy free to drift from the
    /// thing it is checking. The function already treats the days a
    /// <c>fetch</c> or <c>manual</c> price legitimately outranks as consistent,
    /// which is why this projection could not simply ask "is there a row?".
    ///
    /// <para>The function returns the days it EXAMINED, not only the ones that
    /// disagree, so <c>Checked</c> means here what it means everywhere else in
    /// this report — and a healthy ledger does not report "0 examined", which
    /// would be indistinguishable from a query that silently returns nothing.
    /// No cap on the scan for the same reason the realized-gains check has
    /// none: <see cref="Build"/> reports the true total and truncates only what
    /// is displayed.</para>
    /// </remarks>
    private async Task<CheckResult> CheckTradePricesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var examined = await _db.TradePriceCheck(ledgerId)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var labels = await SecurityLabelsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var accounts = await TradingAccountsAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var mismatches = new List<ConsistencyMismatch>();
        foreach (var d in examined.Where(d => d.Drifted))
        {
            // Security, account, day — the three things needed to go find the trade
            // behind the expected figure and judge it. The header id travels separately
            // so a link can target it exactly.
            var scope = labels.GetValueOrDefault(d.SecurityId, "(security)")
                        + (d.AccountId is not Guid acct
                            ? ""
                            : " · " + accounts.GetValueOrDefault(
                                  acct, (Id: acct, Name: "(account)")).Name)
                        + " · " + d.PriceDate.ToString("yyyy-MM-dd");
            Guid? accountId = d.AccountId is Guid a
                ? accounts.GetValueOrDefault(a, (Id: a, Name: "")).Id
                : null;

            // One mismatch per figure, as the holdings name quantity and cost basis
            // apart: a stored row can hold another price, another currency, or both.
            if (d.Reason == ConsistencyReasons.NotStored || d.StoredPrice != d.ExpectedPrice)
            {
                mismatches.Add(new ConsistencyMismatch(
                    Scope: scope,
                    // Said the way a reader can act on. "missing" means the day's trade
                    // seeded no price at all; the other means a row is sitting on that
                    // day claiming a price the day's last trade does not imply. Every
                    // writer re-derives both the days a trade arrives on and the days it
                    // leaves, so either is a write that went past them, or a day left
                    // stale or unpriced before they re-derived the days a trade leaves
                    // (ADR-0084 D4, 2026-11-03 amendment).
                    Field: d.Reason == ConsistencyReasons.NotStored
                        ? "no price for the day's trade"
                        : "price disagrees with the day's last trade",
                    // A missing row stores nothing; 0 is the honest reading of
                    // "there is no stored value", and Diff then equals the whole
                    // expected price rather than a difference against a fiction.
                    Stored: d.StoredPrice ?? 0m,
                    Expected: d.ExpectedPrice,
                    AccountId: accountId,
                    SecurityId: d.SecurityId,
                    HeaderId: d.HeaderId,
                    PriceDate: d.PriceDate,
                    Reason: d.Reason));
            }

            // A stored row in another currency than the brokerage that made the day's trade
            // (migration 274): a value mismatch on the currency, named in the field. The
            // figures carry nothing, as there is no number to compare, and the price, when
            // it differs too, is the mismatch above.
            if (d.Reason == ConsistencyReasons.ValueDiffers && d.StoredCurrency != d.ExpectedCurrency)
            {
                mismatches.Add(new ConsistencyMismatch(
                    Scope: scope,
                    Field: $"price in {d.StoredCurrency}, the day's last trade in {d.ExpectedCurrency}",
                    Stored: 0m,
                    Expected: 0m,
                    AccountId: accountId,
                    SecurityId: d.SecurityId,
                    HeaderId: d.HeaderId,
                    PriceDate: d.PriceDate,
                    Reason: ConsistencyReasons.ValueDiffers));
            }
        }

        return Build(ConsistencyProjections.TradePrices, examined.Count, mismatches);
    }

    /// <summary>
    /// Each brokerage's holdings account's currency, against the brokerage's
    /// (<see cref="HoldingsAccountCurrency"/>, ADR-0019 Rule 4, 2026-11-04 amendment).
    /// </summary>
    /// <remarks>
    /// The holdings account is a system account nobody edits, and a brokerage's currency
    /// edit writes it, so a difference is a write that went past that edit, or one an
    /// earlier version left. One mismatch per holdings account, named by its brokerage, the
    /// account a person knows, whose id it carries as its AccountId; the field names both
    /// currencies, and the two figures carry nothing, as the stored notes' do.
    /// <c>Checked</c> counts the holdings accounts a brokerage names.
    /// </remarks>
    private async Task<CheckResult> CheckHoldingsCurrencyAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var rows = await HoldingsAccountCurrency.ReadAsync(_db, ledgerId, null, cancellationToken)
            .ConfigureAwait(false);
        var mismatches = rows
            .Where(r => r.Stored != r.Expected)
            .Select(r => new ConsistencyMismatch(
                Scope: r.BrokerageNames,
                Field: $"its holdings are recorded in {r.Stored}, but the brokerage is in {r.Expected}",
                Stored: 0m,
                Expected: 0m,
                AccountId: r.BrokerageId))
            .ToList();
        return Build(ConsistencyProjections.HoldingsCurrency, rows.Count, mismatches);
    }

    /// <summary>
    /// The ledger's <c>reminder-auto-post</c> job, against its reminders (ADR-0097,
    /// 2026-11-18 amendment): reminders that ask to be auto-posted
    /// (<see cref="AutoPostRequests"/>, the rule the Moneydance import turns the job on by)
    /// and no job row for the ledger mean nothing ever posts them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row that is there is never a finding, enabled or not. The job has no off switch and
    /// the scheduler never disables it (<c>SchedulerRunner</c>), so a disabled row is one an
    /// earlier version left; turning it back on stays an explicit act (a time change on the
    /// Reminders page saves it enabled), as <see cref="SchedulesRepository.EnsureCreatedEnabledAsync"/>
    /// keeps it. Only a ledger with no row at all is reported: one whose auto-posting reminder
    /// was imported, or saved, by a version that did not turn the job on.
    /// </para>
    /// <para>
    /// The ledger is what is examined, so <c>Checked</c> is 1 whether or not any reminder
    /// asks. A finding is one mismatch, scoped to the job, whose field says how many
    /// reminders it leaves unposted; the two figures carry nothing, as the stored notes'
    /// do. The reminders are read before the row, so a job created between the two reads
    /// is seen.
    /// </para>
    /// </remarks>
    private async Task<CheckResult> CheckReminderAutoPostAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var asking = await AutoPostRequests.In(_db, ledgerId)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        var mismatches = new List<ConsistencyMismatch>();
        if (asking > 0
            && !await new SchedulesRepository(_db)
                .ExistsAsync(ledgerId, JobTypes.ReminderAutoPost, cancellationToken)
                .ConfigureAwait(false))
        {
            mismatches.Add(new ConsistencyMismatch(
                Scope: "Auto-post job",
                Field: asking == 1
                    ? "not set up, so the reminder set to auto-post is never posted"
                    : $"not set up, so the {asking.ToString(System.Globalization.CultureInfo.InvariantCulture)} reminders set to auto-post are never posted",
                Stored: 0m,
                Expected: 0m));
        }
        return Build(ConsistencyProjections.ReminderAutoPost, 1, mismatches);
    }

    /// <summary>
    /// The currency of each security's fetched prices, against what Yahoo named
    /// (<see cref="ConsistencyProjections.QuoteCurrency"/>, ADR-0114 D5, amendment).
    /// </summary>
    /// <remarks>
    /// Two kinds, one mismatch per security. A security with fetch rows and no
    /// <c>quote_currency_confirmed_at</c> is <see cref="ConsistencyReasons.CurrencyUnconfirmed"/>:
    /// its rows may still carry the USD stamp every fetch had before the fix. A confirmed
    /// security whose fetch rows are not all in one currency is
    /// <see cref="ConsistencyReasons.ValueDiffers"/>, against the currency of its latest
    /// fetch row: a confirmation re-stamps every fetch row to the currency it read, so the
    /// latest row carries it, and any other currency is a write that went past it. The
    /// figures carry nothing; the field names the currencies and how many rows.
    /// <c>Checked</c> counts the securities with fetch rows.
    /// </remarks>
    private async Task<CheckResult> CheckQuoteCurrencyAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var groups = await _db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && p.Source == Entities.PriceSource.Fetch)
            .GroupBy(p => new { p.SecurityId, p.CurrencyCode })
            .Select(g => new { g.Key.SecurityId, g.Key.CurrencyCode, Rows = g.Count(), Latest = g.Max(p => p.PriceDate) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var securityIds = groups.Select(g => g.SecurityId).Distinct().ToList();
        var confirmedAt = await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && securityIds.Contains(s.Id))
            .Select(s => new { s.Id, s.QuoteCurrencyConfirmedAt })
            .ToDictionaryAsync(s => s.Id, s => s.QuoteCurrencyConfirmedAt, cancellationToken)
            .ConfigureAwait(false);
        var labels = await SecurityLabelsAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var mismatches = new List<ConsistencyMismatch>();
        foreach (var bySecurity in groups.GroupBy(g => g.SecurityId)
                     .OrderBy(g => labels.GetValueOrDefault(g.Key, "(security)"), StringComparer.Ordinal)
                     .ThenBy(g => g.Key))
        {
            var scope = labels.GetValueOrDefault(bySecurity.Key, "(security)");
            var rows = bySecurity.Sum(g => g.Rows);
            if (confirmedAt.GetValueOrDefault(bySecurity.Key) is null)
            {
                var stored = string.Join(", ", bySecurity.Select(g => g.CurrencyCode).Order(StringComparer.Ordinal));
                mismatches.Add(new ConsistencyMismatch(
                    Scope: scope,
                    Field: $"{rows} fetched {Prices(rows)} in {stored}, a currency Yahoo has not confirmed",
                    Stored: 0m,
                    Expected: 0m,
                    SecurityId: bySecurity.Key,
                    Reason: ConsistencyReasons.CurrencyUnconfirmed));
                continue;
            }

            // A security has one row per day, so the latest day names one currency.
            var confirmed = bySecurity.MaxBy(g => g.Latest)!.CurrencyCode;
            var others = bySecurity.Where(g => g.CurrencyCode != confirmed).ToList();
            if (others.Count == 0) continue;
            var otherRows = others.Sum(g => g.Rows);
            mismatches.Add(new ConsistencyMismatch(
                Scope: scope,
                Field: $"{otherRows} fetched {Prices(otherRows)} in "
                       + string.Join(", ", others.Select(g => g.CurrencyCode).Order(StringComparer.Ordinal))
                       + $", but Yahoo quotes it in {confirmed}",
                Stored: 0m,
                Expected: 0m,
                SecurityId: bySecurity.Key,
                Reason: ConsistencyReasons.ValueDiffers));
        }
        return Build(ConsistencyProjections.QuoteCurrency, securityIds.Count, mismatches);

        static string Prices(int n) => n == 1 ? "price" : "prices";
    }

    /// <summary>
    /// The stored memo-and-tags presentation (migration 264), against what the rule
    /// computes from each header's rows now (<see cref="LegNotes.DriftOfLedgerAsync"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>No false drift from a write in flight.</b> The inputs and the stored values
    /// are read in one REPEATABLE READ snapshot (<see cref="ReadSnapshot"/>), so a writer
    /// that has changed an input and not yet committed its recompute is seen as neither;
    /// inside a caller's transaction below REPEATABLE READ the check refuses to run rather
    /// than join it. The headers that differ
    /// are then read again in a READ COMMITTED transaction under the recompute's own
    /// ordered header lock: a recompute that committed between the two reads is seen
    /// there, and only what still differs is reported.</para>
    /// <para>One mismatch per header. It names the header (and its originating account,
    /// for the link) and no memo or tag; the two figures carry nothing, as there is no
    /// single number to compare.</para>
    /// <para><b>Memory bounded by the chunk, not the ledger.</b> Both reads compare
    /// <see cref="StoredNotesChunkSize"/> headers at a time, the first paging the ledger's
    /// headers on their id inside the snapshot; only the ids of the headers that differ
    /// outlive a chunk, and no text is built for a difference. The check runs on a
    /// schedule, so what it holds at its peak must not grow with the ledger.</para>
    /// </remarks>
    internal async Task<CheckResult> CheckStoredNotesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        LegNotes.Drift first;
        await using (var snapshot = await ReadSnapshot.BeginAsync(_db, cancellationToken, requireRepeatableRead: true)
                         .ConfigureAwait(false))
        {
            first = await LegNotes.DriftOfLedgerAsync(_db, ledgerId, StoredNotesChunkSize, cancellationToken)
                .ConfigureAwait(false);
            // One the check joined is its caller's to end.
            if (snapshot is not null) await snapshot.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var drifted = new List<Guid>();
        if (first.Headers.Count > 0)
        {
            var ids = first.Headers.Order().ToArray();
            await using var recheck = await _db.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            _ = await _db.LockTxnHeaders(ids).Select(r => r.Locked)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            drifted.AddRange((await LegNotes.DriftAmongAsync(_db, ids, StoredNotesChunkSize, cancellationToken)
                .ConfigureAwait(false)).Order());
            await recheck.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var owners = drifted.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : await _db.TxnHeaders.AsNoTracking()
                .Where(h => drifted.Contains(h.Id))
                .Select(h => new { h.Id, h.OriginatingAccountId })
                .ToDictionaryAsync(h => h.Id, h => h.OriginatingAccountId, cancellationToken)
                .ConfigureAwait(false);
        var mismatches = drifted
            .Select(h => new ConsistencyMismatch(
                Scope: "header " + h,
                Field: "memo and tags shown",
                Stored: 0m,
                Expected: 0m,
                AccountId: owners.GetValueOrDefault(h),
                HeaderId: h))
            .ToList();
        return Build(ConsistencyProjections.StoredNotes, first.Checked, mismatches);
    }

    /// <summary>
    /// Repair one projection: rewrite what the check finds disagreeing in it, touching only
    /// what the check reported, and commit. Returns that check, which is what was rewritten.
    /// </summary>
    /// <remarks>
    /// Every projection the report names is repairable, so a reader is never told
    /// about a problem the product cannot fix — the state in which damage sits
    /// unrepaired while ad-hoc queries are written to look at it.
    /// <para>
    /// Repairs are TARGETED: the disagreeing (account, security) pairs or headers,
    /// not the whole ledger. Fixing a handful of headers should not rewrite tens of
    /// thousands of rows, and a
    /// full-ledger FIFO recompute is heavy enough that doing it needlessly is its own
    /// hazard.
    /// </para>
    /// <para>
    /// The rewrite is committed when this returns, so the caller checks the whole ledger
    /// after it (<see cref="CheckAsync"/>) for the verdict the repair records
    /// (<see cref="ProjectionRepairResult"/>): a full check, not this projection's alone,
    /// because a verdict read off one projection would call a ledger healthy while another
    /// still disagrees. That check is the caller's, not part of this, because the repair's
    /// record must not depend on it: a check that fails after the commit leaves the rewrite
    /// standing, and the repair is recorded all the same (BalancesEndpoints).
    /// </para>
    /// <para>
    /// Some rewrites commit in pieces (balances per account, holdings and realized gains
    /// per position, posting counts per header), so one cancelled partway leaves part of it
    /// committed. The repair endpoint therefore passes no token of the request's.
    /// </para>
    /// <para>
    /// Every rewrite takes the ledger's write lock (migration 275) for each piece it
    /// commits, never across pieces: the balances, holdings and realized-gains recomputes
    /// take it themselves, one statement at a time; the others take it as the first
    /// statement of their transaction (<see cref="LedgerWriteLock"/>), except the
    /// quote_currency repair, which recomputes no balance or position and so writes in a
    /// plain transaction (<c>QuoteCurrencyConfirmation</c>). A writer of the
    /// ledger in flight is waited for, and one held past the wait refuses the piece
    /// (<see cref="LedgerWriteBusyException"/>, or SQLSTATE
    /// <see cref="LedgerWriteLock.BusySqlState"/> from a recompute), which the repair
    /// endpoint answers as a 422.
    /// </para>
    /// </remarks>
    /// <param name="ledgerId">The ledger to repair.</param>
    /// <param name="projection">One of <see cref="ConsistencyProjections.All"/>.</param>
    /// <param name="repairedByUserId">Who asked for the repair. Only the reminder auto-post
    /// repair records anyone: it creates the job configured by them, as a reminder save
    /// creates it configured by whoever saved.</param>
    /// <param name="cancellationToken">Cancels the rewrite, between the pieces it commits.</param>
    public async Task<ProjectionConsistency> RewriteAsync(
        Guid ledgerId,
        string projection,
        Guid repairedByUserId,
        CancellationToken cancellationToken) =>
        (await RewriteAsync(ledgerId, projection, repairedByUserId, currencyLookup: null, cancellationToken)
            .ConfigureAwait(false))
        .Repaired;

    /// <summary>What a rewrite worked from, and the reported rows it could not rewrite
    /// (<see cref="ProjectionRepairResult.NotRepaired"/>).</summary>
    public sealed record RewriteOutcome(
        ProjectionConsistency Repaired, IReadOnlyList<ConsistencyMismatch> NotRepaired);

    /// <summary>
    /// <see cref="RewriteAsync(Guid, string, Guid, CancellationToken)"/>, with the currency
    /// lookup the <see cref="ConsistencyProjections.QuoteCurrency"/> repair asks (ADR-0114
    /// D5, amendment). That repair is egress: the caller passes a lookup only under the
    /// repairing user's opt-in for it, and without one the repair throws
    /// <see cref="InvalidOperationException"/> before it writes anything.
    /// </summary>
    public async Task<RewriteOutcome> RewriteAsync(
        Guid ledgerId,
        string projection,
        Guid repairedByUserId,
        Quotes.IQuoteCurrencyLookup? currencyLookup,
        CancellationToken cancellationToken)
    {
        var notRepaired = new List<ConsistencyMismatch>();
        var repaired = await RewriteCoreAsync(
                ledgerId, projection, repairedByUserId, currencyLookup, notRepaired, cancellationToken)
            .ConfigureAwait(false);
        return new RewriteOutcome(repaired, notRepaired);
    }

    private async Task<ProjectionConsistency> RewriteCoreAsync(
        Guid ledgerId,
        string projection,
        Guid repairedByUserId,
        Quotes.IQuoteCurrencyLookup? currencyLookup,
        List<ConsistencyMismatch> notRepaired,
        CancellationToken cancellationToken)
    {
        // Balances have their own whole-ledger repair, which reports the rows it
        // rewrote; the others compare first so the repair knows what to touch.
        if (projection == ConsistencyProjections.Balances)
        {
            var healed = await _register.VerifyAndHealBalancesAsync(ledgerId, cancellationToken)
                                        .ConfigureAwait(false);
            return BalancesFrom(healed).Projection;
        }

        var before = projection switch
        {
            ConsistencyProjections.Holdings =>
                await CheckHoldingsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.RealizedGains =>
                await CheckRealizedGainsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.PostingCounts =>
                await CheckPostingCountsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.TradePrices =>
                await CheckTradePricesAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.StoredNotes =>
                await CheckStoredNotesAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.HoldingsCurrency =>
                await CheckHoldingsCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.QuoteCurrency =>
                await CheckQuoteCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.CategoryAmounts =>
                (await CheckCategoryAmountsAsync(
                        ledgerId, await AccountFactsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
                        cancellationToken)
                    .ConfigureAwait(false)).Check,
            ConsistencyProjections.ReminderAutoPost =>
                await CheckReminderAutoPostAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(projection), projection,
                     "Unknown projection."),
        };
        if (before.Projection.Healthy) return before.Projection;

        if (projection == ConsistencyProjections.ReminderAutoPost)
        {
            // The write a reminder saved with Auto-post makes, and a Moneydance import's:
            // create-if-absent, enabled at the default hour, configured by whoever repairs, so
            // a row created since the check is left exactly as it is. In a transaction that
            // takes the ledger's write lock first (migration 275), as every repair does. It
            // posts nothing itself: the job posts, on its next run, only what asks then.
            await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false);
            await new SchedulesRepository(_db).EnsureCreatedEnabledAsync(
                    ledgerId, JobTypes.ReminderAutoPost, ScheduleDefaults.ReminderAutoPostHour,
                    repairedByUserId, _clock.GetUtcNow().UtcDateTime, cancellationToken)
                .ConfigureAwait(false);
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            return before.Projection;
        }

        if (projection == ConsistencyProjections.CategoryAmounts)
        {
            // ADR-0114 D13: a category line carries the real side's amount negated. Unlike
            // every other repair this rewrites a figure a person or an import entered, not a
            // derived one, so it touches only the clean shape, one real line and one category
            // line, read again under the ledger's write lock (migration 275) so a write in
            // flight is waited for and what it committed is what is judged. Tracked updates,
            // so the leg recompute moves each category's balances in the same save; no real
            // line changes, so no account's balance moves, and a category line has no
            // reconciliation row (ADR-0082) to carry a cleared figure.
            await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false);
            var postings = (await PostingsWithACategoryLine(ledgerId)
                    .Where(p => p.RealLines == 1 && p.CategoryLines == 1 && p.Sum != 0m)
                    .Select(p => new { p.HeaderId, p.PostingIndex })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(p => (p.HeaderId, p.PostingIndex))
                .ToHashSet();
            if (postings.Count > 0)
            {
                var headerIds = postings.Select(p => p.HeaderId).Distinct().ToList();
                var categoryIds = (await _db.Accounts.AsNoTracking()
                        .Where(a => a.LedgerId == ledgerId && a.AccountType == CategoryType)
                        .Select(a => a.Id)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false))
                    .ToHashSet();
                var legs = await _db.TxnLegs
                    .Where(l => headerIds.Contains(l.HeaderId))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var posting in legs.GroupBy(l => (l.HeaderId, l.PostingIndex))
                             .Where(g => postings.Contains(g.Key)))
                {
                    var line = posting.Single(l => categoryIds.Contains(l.AccountId));
                    var real = posting.Single(l => !categoryIds.Contains(l.AccountId));
                    line.Amount = -real.Amount;
                }
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            return before.Projection;
        }

        if (projection == ConsistencyProjections.QuoteCurrency)
        {
            // The quote refresh's own lookup and re-stamp (QuoteCurrencyConfirmation), over
            // every security reported, uncapped: Yahoo names each one's currency, which
            // confirms it and re-stamps its fetch rows in another (converting a minor unit's
            // figures). The lookup runs before any transaction opens, so no row is held across
            // a request. A security whose symbol is not public, or has none, cannot be asked
            // (ADR-0054 D2), and one whose lookup fails is not rewritten: each is named in
            // notRepaired with the code that says why.
            if (currencyLookup is null)
                throw new InvalidOperationException(
                    "The quote_currency repair asks Yahoo for each security's currency, so it needs a "
                    + "currency lookup, which is passed only under the repairing user's Yahoo opt-in.");
            var reported = before.AllMismatches
                .Select(m => m.SecurityId).OfType<Guid>().Distinct().ToList();
            var askable = await _db.Securities.AsNoTracking()
                .Where(s => s.LedgerId == ledgerId
                            && reported.Contains(s.Id)
                            && s.QuoteSymbolPublic
                            && (s.QuoteSymbol != null || s.Ticker != null))
                .OrderBy(s => s.Id)
                .Select(s => new Quotes.QuoteSecurityRef(s.Id, s.QuoteSymbol ?? s.Ticker!, s.QuoteSymbolPublic))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var (_, lookupErrors) = await Quotes.QuoteCurrencyConfirmation.LookupAndConfirmAsync(
                    _db, ledgerId, currencyLookup, askable, _clock, cancellationToken)
                .ConfigureAwait(false);

            // Named as the check named them, with the reason: the lookup's code, or that the
            // security could not be asked.
            var asked = askable.Select(a => a.SecurityId).ToHashSet();
            var failed = lookupErrors
                .Where(e => e.SecurityId is not null)
                .GroupBy(e => e.SecurityId!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var m in before.AllMismatches.Where(m => m.SecurityId is not null))
            {
                var id = m.SecurityId!.Value;
                if (!asked.Contains(id))
                    notRepaired.Add(m with
                    {
                        Field = "its symbol is not public, so Yahoo was not asked",
                        Reason = ConsistencyReasons.QuoteSymbolNotPublic,
                    });
                else if (failed.TryGetValue(id, out var error))
                    notRepaired.Add(m with { Field = error.Message, Reason = error.Code });
            }
            return before.Projection;
        }

        if (projection == ConsistencyProjections.HoldingsCurrency)
        {
            // The account edit's own write (HoldingsAccountCurrency.AlignAsync), over the
            // holdings accounts that disagree, read again uncapped, in one transaction:
            // each takes its brokerage's currency. No price is written: a trade price is in
            // its brokerage's currency, which this does not change (migration 274).
            //
            // The ledger's write lock first (migration 275), so a currency edit in flight is
            // waited for and its new code read. The brokerages are then locked FOR SHARE, in
            // id order, before the read that is written from (the trade-price lock order,
            // TradePriceLocks); the holdings rows are written in id order.
            await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false);
            var drifted = await TradePriceLocks.LockThenReadAsync(
                    _db, ledgerId,
                    read: async ct => (await HoldingsAccountCurrency.ReadAsync(_db, ledgerId, null, ct)
                            .ConfigureAwait(false))
                        .Where(r => r.Stored != r.Expected)
                        .OrderBy(r => r.HoldingsAccountId)
                        .ToList(),
                    days: _ => [],
                    accounts: rows => rows.SelectMany(r => r.BrokerageIds),
                    forWrite: false,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in drifted)
            {
                await HoldingsAccountCurrency.AlignAsync(_db, ledgerId, row.HoldingsAccountId, cancellationToken)
                    .ConfigureAwait(false);
            }
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            return before.Projection;
        }

        if (projection == ConsistencyProjections.StoredNotes)
        {
            // The recompute every writer runs, over the drifted headers only, under its
            // ordered header lock, in one READ COMMITTED transaction that takes the ledger's
            // write lock first (migration 275), so a writer in flight is waited for.
            var drifted = before.AllMismatches
                .Select(m => m.HeaderId).Where(id => id is not null).Select(id => id!.Value)
                .Distinct().ToList();
            await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false);
            await LegNotes.RecomputeAsync(_db, drifted, cancellationToken).ConfigureAwait(false);
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            return before.Projection;
        }

        // EVERY branch below targets before.AllMismatches, never
        // before.Projection.Mismatches. The latter is capped at
        // MaxMismatchesPerProjection for display; repairing from it fixes the first
        // hundred disagreements and reports the repair it just performed, so a
        // ledger with five hundred needs five clicks and says so nowhere.
        if (projection == ConsistencyProjections.TradePrices)
        {
            // Repaired through the rank-gated upsert primitive (migrations 177, 274),
            // one drifted day at a time, rather than by writing security_prices from
            // here.
            // The eligibility rule is already restated in four places (see
            // database-schema.md under security_price_upsert_from_trade); a fifth
            // copy living in the repair would mean a ledger could be "repaired"
            // into a state the writer would never produce.
            //
            // The primitive is rank-gated, so a day a fetch/manual price has
            // taken ownership of since the check ran is left alone rather than
            // clobbered — the repair cannot undo a truer price.
            //
            // missing / value only, selected by the function's own reason: it
            // returns every day it examined, and a day that agrees, or that a feed
            // close or a manual price owns, carries none. So the repair writes the
            // days the check reported and nothing else, at the price the rule
            // derives and in the currency of the brokerage that made the trade (migration
            // 274).
            // Uncapped, like AllMismatches.
            //
            // The figures are read after the trade-price lock order's first step
            // (TradePriceLocks): FOR NO KEY UPDATE, in id order, on every account whose
            // currency the rule reads for the days it writes, and read again once those
            // are locked. A currency edit or a trade's re-derivation of one of those days
            // in flight is waited for, so the figures are what they commit; one that
            // starts later waits for this repair. No holdings account is locked: the rule
            // reads none. All before any price row is written, in one READ COMMITTED
            // transaction that takes the ledger's write lock first (migration 275): a trade
            // saved in several steps takes those accounts once per step, so without the lock
            // a later step could wait for an account this repair holds while this repair
            // waits for one the earlier step holds.
            await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false);
            var repairable = await TradePriceLocks.LockThenReadAsync(
                    _db, ledgerId,
                    read: ct => _db.TradePriceCheck(ledgerId)
                        .AsNoTracking()
                        .Where(d => d.Reason == ConsistencyReasons.NotStored
                                    || d.Reason == ConsistencyReasons.ValueDiffers)
                        .ToListAsync(ct),
                    days: rows => rows.Select(d => (d.SecurityId, d.PriceDate)),
                    accounts: _ => [],
                    forWrite: true,
                    cancellationToken)
                .ConfigureAwait(false);

            // In the order every writer of trade prices writes its rows
            // (TradePriceRecomputeService.InWriteOrder), the lock order's third step.
            foreach (var d in TradePriceRecomputeService.InWriteOrder(
                         repairable, r => (ledgerId, r.SecurityId, r.PriceDate)))
            {
                _ = await _db.SecurityPriceUpsertFromTrade(
                        ledgerId, d.SecurityId, d.PriceDate, d.ExpectedPrice, d.ExpectedCurrency)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            return before.Projection;
        }

        if (projection == ConsistencyProjections.PostingCounts)
        {
            // One header per transaction, each taking the ledger's write lock first
            // (migration 275). The recompute's UPDATE computes the counts in subqueries
            // that keep the statement's snapshot: begun while a writer holds the header's
            // legs, it would wait for them and then write over the writer's counts with
            // the ones it read before the writer committed. Behind the lock it begins once
            // the writer has committed, and reads what it committed.
            foreach (var headerId in before.AllMismatches
                         .Select(m => m.HeaderId).Where(id => id is not null)
                         .Select(id => id!.Value).Distinct())
            {
                await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
                    .ConfigureAwait(false);
                _ = await _db.RecomputePostingCountsForHeader(headerId)
                    .Select(r => r.HeaderId)
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
                await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return before.Projection;
        }

        // Holdings and realized gains are the SAME projection from the writer's point
        // of view — recompute_holdings_cost_basis rebuilds quantity, cost basis and
        // realized_gains together — so both repair through one call over the
        // disagreeing pairs. Given a pair with no holdings row, it creates the row, so
        // a position reported as not stored is written back.
        var pairs = before.AllMismatches
            .Where(m => m.AccountId is not null && m.SecurityId is not null)
            .Select(m => (m.AccountId!.Value, m.SecurityId!.Value))
            .Distinct()
            .ToList();
        await _holdings.RecomputeAsync(pairs, cancellationToken).ConfigureAwait(false);
        return before.Projection;
    }

    /// <summary>
    /// Shape a <see cref="BalanceHealthReport"/> as this class's check result.
    /// </summary>
    /// <remarks>
    /// One builder for both entry points — the check and the repair's report of the
    /// rows it rewrote —
    /// because two copies of the same projection, with the same six field
    /// mappings, drift apart, and a repair then describes a row differently from
    /// the check that found it.
    /// <para>
    /// Uncapped on the way in: <c>Drifted</c> is the full set (<c>DriftedCount</c>
    /// is its own <c>Count</c>), and the cap belongs on the way out.
    /// </para>
    /// </remarks>
    private static CheckResult BalancesFrom(BalanceHealthReport report)
    {
        var mismatches = report.Drifted
            .Select(d => new ConsistencyMismatch(
                Scope: d.AccountName + " @ " + d.PostedAt.ToString("yyyy-MM-dd"),
                // A missing row is said as such: its Stored is 0 because nothing is
                // stored, the trade prices' reading of a missing row, and the figures
                // alone would read as a stored 0. So is a row the walk does not produce:
                // its Expected is 0 because nothing is derived, and the repair removes it.
                Field: d.Reason switch
                {
                    ConsistencyReasons.NotStored => "balance_after, not stored",
                    ConsistencyReasons.NotDerived => "balance_after, not derived",
                    _ => "balance_after",
                },
                Stored: d.StoredBefore,
                Expected: d.RecomputedAfter,
                AccountId: d.AccountId,
                HeaderId: d.HeaderId,
                Reason: d.Reason))
            .ToList();

        return new CheckResult(
            new ProjectionConsistency(
                ConsistencyProjections.Balances,
                report.Healthy,
                report.RowsChecked,
                report.DriftedCount,
                mismatches.Take(MaxMismatchesPerProjection).ToList()),
            mismatches);
    }

    private async Task<Dictionary<Guid, string>> AccountNamesAsync(
        Guid ledgerId, CancellationToken cancellationToken) =>
        (await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(a => a.Id, a => a.Name);

    /// <summary>
    /// Display labels for this ledger's securities — ticker where there is one,
    /// otherwise the name, and both where they differ.
    /// </summary>
    /// <remarks>
    /// A report a reader cannot act on is not a report. Naming rows by bare GUID
    /// (`security &lt;guid&gt; on &lt;date&gt;`) tells someone deciding whether to
    /// repair absolutely nothing about which holding is affected — they would have
    /// to look up the id by hand, once per row, to form a view. The holdings check
    /// uses these labels for its security half too.
    /// </remarks>
    private async Task<Dictionary<Guid, string>> SecurityLabelsAsync(
        Guid ledgerId, CancellationToken cancellationToken) =>
        (await _db.Securities.AsNoTracking()
            .Where(x => x.LedgerId == ledgerId)
            .Select(x => new { x.Id, x.Ticker, x.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(
            x => x.Id,
            x => string.IsNullOrWhiteSpace(x.Ticker) ? x.Name
                 : string.Equals(x.Ticker, x.Name, StringComparison.OrdinalIgnoreCase) ? x.Ticker!
                 : $"{x.Ticker} — {x.Name}");

    /// <summary>
    /// Account names for display, with a holdings sibling resolved to the brokerage
    /// it belongs to.
    /// </summary>
    /// <remarks>
    /// A security leg lives on the holdings sibling, which is a system account the
    /// reader never navigates to and may not recognise — and whose register is not
    /// where the trade is read. Both the name and the id resolve to the BROKERAGE,
    /// so the row names, and can link to, the account someone would actually open.
    /// <para>
    /// Safe to redirect the id because nothing repairs by it: the trade-price repair
    /// targets (security, day). It is carried for navigation only.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, (Guid Id, string Name)>> TradingAccountsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var rows = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new { a.Id, a.Name, a.HoldingsAccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var map = rows.ToDictionary(a => a.Id, a => (a.Id, a.Name));
        foreach (var brokerage in rows.Where(a => a.HoldingsAccountId is not null))
            map[brokerage.HoldingsAccountId!.Value] = (brokerage.Id, brokerage.Name);
        return map;
    }

    private static CheckResult Build(
        string name, int checkedCount, List<ConsistencyMismatch> mismatches) =>
        new(new ProjectionConsistency(
                name,
                Healthy: mismatches.Count == 0,
                Checked: checkedCount,
                MismatchedCount: mismatches.Count,
                Mismatches: mismatches.Take(MaxMismatchesPerProjection).ToList()),
            mismatches);

    /// <summary>
    /// One projection's check result, carrying the UNTRUNCATED mismatch list beside
    /// the client-facing <see cref="ProjectionConsistency"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectionConsistency.Mismatches"/> is capped at
    /// <see cref="MaxMismatchesPerProjection"/> because it is what the client
    /// renders, and a badly drifted ledger should not ship forty thousand rows to
    /// draw a list of a hundred. The REPAIR needs all of them.
    /// <para>
    /// A repair that iterated that capped list would fix a ledger with five hundred
    /// disagreeing headers a hundred at a time — and each pass would report the
    /// repair it had just performed, so nothing on screen would say four more were
    /// needed. A repair that silently fixes what fits
    /// in the display is the same class of defect as a check that disagrees with
    /// its own repair: it trains the reader to believe a problem is handled.
    /// </para>
    /// </remarks>
    internal sealed record CheckResult(
        ProjectionConsistency Projection,
        IReadOnlyList<ConsistencyMismatch> AllMismatches);
}
