using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Presentation;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The reusable reporting aggregation layer (ADR-0063). Aggregates over the
/// override-aware <c>resolved_transactions</c> view (effective amount / posted_at
/// / is_hidden, merged rows excluded) joined to <c>accounts</c> for the category
/// kind — LINQ/EF throughout, no raw SQL (the OverviewRepository precedent).
///
/// The measure (spending/income/net) always selects category-leg postings by
/// kind; <see cref="ReportSpec.GroupBy"/> chooses the grouping key:
/// <list type="bullet">
///   <item><b>Category</b> — the category account itself (nests via parent_id, so
///   <see cref="ReportSpec.Rollup"/> applies).</item>
///   <item><b>Account</b> — the real account paired with the categorized posting
///   (the view's counterparty for that posting_index — the cash/credit account
///   that "paid"). Split-safe: each posting_index pairs one category leg with one
///   cash leg.</item>
///   <item><b>Payee</b> — the transaction payee text.</item>
/// </list>
///
/// Sign: per the symmetric-posting model (per-posting sum-to-zero, ADR-0019/0025)
/// the cash leg and the category leg are opposite-signed, so expense-category
/// legs are positive and income-category legs negative. We normalize to positive
/// magnitudes for Spending/Income; Net = income − spending (income +, expense −).
/// </summary>
public sealed class ReportingRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ReportingRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // Pre-rollup intermediate: one row per (month, group, kind, currency), and per day
    // as well for a currency other than the reporting one, which converts at each
    // posting's own date (ADR-0114 D7).
    private sealed record Cell(
        int Year, int Month, int Day, Guid? GroupId, string GroupName, Guid? ParentId, string Kind, decimal Sum,
        string Currency);

    /// <summary>A money figure of the report in the reporting currency, unrounded until
    /// it is returned, with the missing rates it needed (ADR-0114 D9).</summary>
    private readonly record struct Money(decimal Value, IReadOnlyList<RateUnavailable>? Why)
    {
        public static Money Zero => new(0m, null);
        public static Money Of(ReportRow row) => new(row.Amount ?? 0m, row.RateUnavailable);
        public decimal? Amount => Why is null ? Value : null;
        public static Money operator +(Money x, Money y) =>
            new(x.Value + y.Value, x.Why is null ? y.Why : y.Why is null ? x.Why : [.. x.Why.Union(y.Why)]);
        public static Money operator -(Money x) => new(-x.Value, x.Why);
        public static Money Sum(IEnumerable<Money> all) => all.Aggregate(Zero, (a, b) => a + b);
    }

    private static Money SumOf(IEnumerable<ReportRow> rows) => Money.Sum(rows.Select(Money.Of));

    private static ReportRow WithMoney(ReportRow row, Money m) =>
        row with { Amount = m.Amount, RateUnavailable = m.Why };

    /// <summary>A figure as it is returned: rounded once, at the stored money scale.</summary>
    private static decimal? Rounded(decimal? amount) => amount is { } a ? ConvertedTotal.Round(a) : null;

    /// <summary>
    /// One leg a report or listing reads: the view's row, its account, and what the
    /// memo-and-tags rule presents for it from the reader's perspective.
    /// </summary>
    private sealed class Posting
    {
        public ResolvedTransactionView Row { get; init; } = null!;
        public AccountRow Account { get; init; } = null!;

        /// <summary>The leg whose stored presentation <see cref="Shown"/> is.</summary>
        public Guid NotesLegId { get; init; }

        public LegPresentation Shown { get; init; } = null!;

        /// <summary>The currency the posting's money is in: the real account's on its
        /// other side; a posting between two categories takes the category's own.</summary>
        public string Currency { get; init; } = string.Empty;
    }

    /// <summary>
    /// The posting's in-play tags (<see cref="StoredNotesQuery.InPlay"/>), from the leg
    /// <paramref name="fromCounterparty"/> presents it from, include <paramref name="tagId"/>.
    /// </summary>
    /// <remarks>
    /// Reads the tag's headers first (<see cref="StoredNotesQuery.InPlayTagHeaders"/>),
    /// so the caller runs this and the query it filters in one snapshot.
    /// </remarks>
    private async Task<IQueryable<Posting>> WithInPlayTagAsync(
        IQueryable<Posting> q, Expression<Func<ResolvedTransactionView, bool>> fromCounterparty, Guid tagId,
        CancellationToken cancellationToken)
    {
        var headers = await StoredNotesQuery.InPlayTagHeaders(_db, tagId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var has = StoredNotesQuery.HasInPlayTagFrom(_db, fromCounterparty, tagId, headers);
        Expression<Func<Posting, bool>> posting = x => has.Inline(x.Row);
        return q.Where(posting.Expand());
    }

    /// <summary>The posting's in-play tags, each once.</summary>
    private Expression<Func<Posting, IEnumerable<TagRow>>> TagsInPlay()
    {
        var tagsOf = StoredNotesQuery.TagsOf(_db, StoredNotesQuery.InPlay);
        Expression<Func<Posting, IEnumerable<TagRow>>> tags = x => tagsOf.Inline(x.NotesLegId);
        return tags.Expand();
    }

    /// <summary>No tag is in play for the posting (<see cref="StoredNotesQuery.Untagged"/>).</summary>
    private Expression<Func<Posting, bool>> Untagged()
    {
        var untagged = StoredNotesQuery.Untagged(_db);
        Expression<Func<Posting, bool>> none = x => untagged.Inline(x.NotesLegId);
        return none.Expand();
    }

    public Task<ReportResult> SummarizeAsync(
        ReportSpec spec, CancellationToken cancellationToken = default) =>
        SummarizeAsync(spec, today: null, cancellationToken);

    /// <summary>
    /// A summary as the app and MCP receive it: <see cref="SummarizeAsync(ReportSpec, DateOnly?, CancellationToken)"/>,
    /// then the ledger's display currency, read once (<see cref="DisplayCurrency"/>),
    /// through <see cref="ReportSummaryDto.From"/>.
    /// </summary>
    /// <remarks>
    /// The one way both callers read a summary, so a figure withheld on a ledger
    /// whose accounts span currencies is withheld from both. Budget progress calls
    /// the aggregation directly, several times per request, and never reads the
    /// currency here.
    /// </remarks>
    public async Task<ReportSummaryDto> SummaryAsync(
        ReportSpec spec, DateOnly? today, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var result = await SummarizeAsync(spec, today, cancellationToken).ConfigureAwait(false);
        var currency = await DisplayCurrency.ForLedgerAsync(_db, spec.LedgerId, cancellationToken).ConfigureAwait(false);
        return ReportSummaryDto.From(result, currency);
    }

    /// <summary>
    /// <see cref="SummarizeAsync(ReportSpec, CancellationToken)"/>, with a sliding
    /// window resolved against the caller's local date.
    /// </summary>
    /// <param name="today">The caller's local date, never part of the spec, which a
    /// saved report persists. When sent, a sliding window ends where the register's
    /// Scheduled begins (<see cref="ScheduledBoundary.ScheduledFrom"/>), and an
    /// aligned window snaps from the last instant before it, so the current period
    /// is the caller's. Null resolves against the clock's instant, as MCP always has.</param>
    public async Task<ReportResult> SummarizeAsync(
        ReportSpec spec, DateOnly? today, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // A daily series is category-only by construction — the other two
        // dimensions never put the day in their SQL key, so honouring the
        // request would mean silently returning MONTHLY buckets labelled as
        // days. Failing loudly is the only honest option; the caller is code,
        // and code can be fixed.
        if (spec.TimeBucket == ReportTimeBucket.Day && spec.GroupBy != ReportGroupBy.Category)
            throw new ReportRequestException(
                "A daily time bucket is supported for the Category dimension only.");

        var kinds = spec.Measure switch
        {
            ReportMeasure.Spending => new[] { "expense" },
            ReportMeasure.Income => new[] { "income" },
            _ => new[] { "income", "expense" },   // Net
        };

        // Category-account postings only — which inherently excludes account↔account
        // transfers (those have no category leg). That is the report's definition:
        // spending = expense-category postings, transfers excluded. Each posting
        // carries the memo-and-tags rule's presentation from the real account it
        // pairs with, which is what a tag filter or a tag grouping counts. A posting
        // between two categories is read from the category it is grouped on: its own,
        // or the other one when the report groups by the account a posting pairs with.
        var groupedOnCounterparty = spec.GroupBy == ReportGroupBy.Account;
        // Every figure that combines accounts is in the ledger's reporting currency
        // (ADR-0114 D7). A posting's money is in the currency of the real account on its
        // other side, the one that paid; a posting between two categories has none and
        // takes the category's own, as the transaction listing names it. Grouped by
        // account, the posting is read from the account it is grouped under, so it is in
        // that account's currency, and each row is in one currency.
        var reporting = await DisplayCurrency.ForLedgerAsync(_db, spec.LedgerId, cancellationToken)
            .ConfigureAwait(false);
        var q =
            from l in StoredNotesQuery.ReportLegs(_db, groupedOnCounterparty)
            join a in _db.Accounts.AsNoTracking() on l.Row.AccountId equals a.Id
            join cp in _db.Accounts.AsNoTracking() on l.Row.CounterpartyAccountId equals (Guid?)cp.Id into cps
            from c in cps.DefaultIfEmpty()
            where a.LedgerId == spec.LedgerId
                  && a.AccountType == "category"
                  && a.CategoryKind != null
                  && kinds.Contains(a.CategoryKind)
                  && !l.Row.IsHidden
                  && l.Row.IsMergedInto == null
            select new Posting
            {
                Row = l.Row, Account = a, NotesLegId = l.NotesLegId, Shown = l.Shown,
                Currency = c != null && (groupedOnCounterparty || c.AccountType != "category")
                    ? c.CurrencyCode
                    : a.CurrencyCode,
            };

        // A sliding window resolves HERE, against the instant the report runs —
        // see ReportSpec.ResolveWindow for why not at save time.
        var (rangeFrom, rangeTo) = today is null
            ? spec.ResolveWindow(_clock.GetUtcNow().UtcDateTime)
            : spec.ResolveWindow(
                ScheduledBoundary.LastCountedInstant(today, _clock),
                ScheduledBoundary.ScheduledFrom(today, _clock));
        if (rangeFrom is { } from) q = q.Where(x => x.Row.PostedAt >= from);
        if (rangeTo is { } to) q = q.Where(x => x.Row.PostedAt < to);

        // A tag filter reads the tag's headers before the report: both in one snapshot.
        await using var snapshot = spec.TagId is null
            ? null
            : await ReadSnapshot.BeginAsync(_db, cancellationToken).ConfigureAwait(false);
        if (spec.TagId is { } tag)
            // The tags in play for the posting, as the real account shows it: on a
            // split entered from that real account the transaction's tags count for
            // every line's money, a line's own count for that line, and a
            // category-side override never counts.
            q = await WithInPlayTagAsync(
                q, StoredNotesQuery.ReportFromCounterparty(groupedOnCounterparty), tag, cancellationToken).ConfigureAwait(false);

        // Aggregate at month granularity in SQL (Year/Month translate cleanly),
        // keyed by the chosen dimension; coarser buckets + rollup are rolled up in
        // memory. The grouped result is small (#groups × #months).
        var grouped = spec.GroupBy switch
        {
            ReportGroupBy.Account => await q
                .Where(x => x.Row.CounterpartyAccountId != null)
                .GroupBy(x => new
                {
                    x.Row.PostedAt.Year,
                    x.Row.PostedAt.Month,
                    Day = x.Currency == reporting ? 1 : x.Row.PostedAt.Day,
                    GroupId = x.Row.CounterpartyAccountId,
                    GroupName = x.Row.CounterpartyAccountName,
                    Kind = x.Account.CategoryKind!,
                    x.Currency,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.Day, g.Key.GroupId,
                    g.Key.GroupName ?? "(account)", null, g.Key.Kind, g.Sum(x => x.Row.Amount), g.Key.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false),

            // One row per tag, over BOTH levels. See the local function below:
            // the union has to be over POSTING IDENTITY, not over the values
            // being summed.
            ReportGroupBy.Tag => await TagCellsAsync().ConfigureAwait(false),

            // Cash flow: income against expense, per period. The kind was
            // always here — every other arm already selects it into the Cell,
            // because the MEASURE is an allow-list over it — and the only
            // thing missing was the ability to group BY it. GroupId stays null
            // because a kind is not a row in `accounts`; the name is the kind
            // itself, which is also what the client renders.
            ReportGroupBy.CategoryKind => await q
                .GroupBy(x => new
                {
                    x.Row.PostedAt.Year,
                    x.Row.PostedAt.Month,
                    Day = x.Currency == reporting ? 1 : x.Row.PostedAt.Day,
                    Kind = x.Account.CategoryKind!,
                    x.Currency,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.Day, null,
                    g.Key.Kind, null, g.Key.Kind, g.Sum(x => x.Row.Amount), g.Key.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false),

            ReportGroupBy.Payee => await q
                .GroupBy(x => new
                {
                    x.Row.PostedAt.Year,
                    x.Row.PostedAt.Month,
                    Day = x.Currency == reporting ? 1 : x.Row.PostedAt.Day,
                    x.Row.Payee,
                    Kind = x.Account.CategoryKind!,
                    x.Currency,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.Day, null,
                    g.Key.Payee ?? "(no payee)", null, g.Key.Kind, g.Sum(x => x.Row.Amount), g.Key.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false),

            // The day is in the SQL key ONLY for a daily series. Putting it
            // there unconditionally would multiply every existing caller's row
            // count by up to 31 — a 13-month trend over 20 categories goes from
            // ~260 rows to ~8000 — for data they then collapse away again.
            _ when spec.TimeBucket == ReportTimeBucket.Day => await q
                .GroupBy(x => new
                {
                    x.Row.PostedAt.Year,
                    x.Row.PostedAt.Month,
                    x.Row.PostedAt.Day,
                    CategoryId = x.Account.Id,
                    x.Account.Name,
                    x.Account.ParentId,
                    Kind = x.Account.CategoryKind!,
                    x.Currency,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.Day, g.Key.CategoryId,
                    g.Key.Name, g.Key.ParentId, g.Key.Kind, g.Sum(x => x.Row.Amount), g.Key.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false),

            _ => await q
                .GroupBy(x => new
                {
                    x.Row.PostedAt.Year,
                    x.Row.PostedAt.Month,
                    Day = x.Currency == reporting ? 1 : x.Row.PostedAt.Day,
                    CategoryId = x.Account.Id,
                    x.Account.Name,
                    x.Account.ParentId,
                    Kind = x.Account.CategoryKind!,
                    x.Currency,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.Day, g.Key.CategoryId,
                    g.Key.Name, g.Key.ParentId, g.Key.Kind, g.Sum(x => x.Row.Amount), g.Key.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
        };

        // Tag cells, kept out of the switch because the dimension needs two
        // passes and the switch arms are single expressions.
        //
        // ATTRIBUTION IS PER POSTING: each posting counts under the tags in play
        // for it as its real account shows it, and under nothing a sibling line
        // carries, so the rent line's money is never charged to the utilities
        // line's tag. A posting's tags are taken distinct by tag, so a tag on both
        // the transaction and the line counts the posting once.
        async Task<List<Cell>> TagCellsAsync()
        {
            var tagged = await q
                .SelectMany(TagsInPlay(), (x, t) => new
                {
                    x.Row.PostedAt.Year,
                    x.Row.PostedAt.Month,
                    Day = x.Currency == reporting ? 1 : x.Row.PostedAt.Day,
                    t.Id,
                    t.Name,
                    Kind = x.Account.CategoryKind!,
                    x.Row.Amount,
                    x.Currency,
                })
                .GroupBy(y => new { y.Year, y.Month, y.Day, y.Id, y.Name, y.Kind, y.Currency })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Id,
                    g.Key.Name, null, g.Key.Kind, g.Sum(y => y.Amount), g.Key.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            // Untagged postings get their own row rather than being dropped.
            // Without it the report's total silently disagrees with the same
            // period's spending report, and the reader has no way to see that
            // the difference is "money you never tagged" rather than an error.
            // Untagged is the complement of what the cells above count, so every
            // posting is in a tag's cell or in "(untagged)", never both, never neither.
            var untagged = await q
                .Where(Untagged())
                .GroupBy(x => new
                {
                    x.Row.PostedAt.Year,
                    x.Row.PostedAt.Month,
                    Day = x.Currency == reporting ? 1 : x.Row.PostedAt.Day,
                    Kind = x.Account.CategoryKind!,
                    x.Currency,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.Day, null,
                    "(untagged)", null, g.Key.Kind, g.Sum(x => x.Row.Amount), g.Key.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            tagged.AddRange(untagged);
            return tagged;
        }

        // Normalize each cell to the measure's convention.
        // `kind` is the CATEGORY's kind, not a property of the posting: there is
        // no such thing as an income leg or an expense leg, only a leg posted
        // to a category marked one way or the other. The sign is the data's,
        // and either kind can carry either sign — a refund posted to an
        // expense category is negative, a reversal of income is positive.
        //
        // So nothing here branches on the sign, and Net does not branch on
        // kind at all. Spending reports the stored amount; Income and Net
        // negate it. The familiar "income up, costs down" reading of a Net
        // report is a consequence of how those postings usually sum, not a
        // rule this applies — which is why a refund correctly RAISES net
        // instead of deepening a cost.
        static decimal Value(ReportMeasure m, string kind, decimal sum) => m switch
        {
            ReportMeasure.Spending => sum,
            ReportMeasure.Income => -sum,
            _ => -sum,
        };

        // Into the reporting currency: a cell in another currency is one day's postings,
        // converted at that day's rate (ADR-0114 D7); one in the reporting currency is
        // already there. Every rate in one load. Unrounded until each figure is returned.
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        foreach (var g in grouped.Where(g => g.Currency != reporting))
            needs.Add(g.Currency, reporting, new DateOnly(g.Year, g.Month, g.Day));
        var rates = await lookup.LoadAsync(spec.LedgerId, needs, cancellationToken).ConfigureAwait(false);
        Money InReporting(Cell g)
        {
            var value = Value(spec.Measure, g.Kind, g.Sum);
            if (g.Currency == reporting) return new Money(value, null);
            var rate = rates.RateFor(g.Currency, reporting, new DateOnly(g.Year, g.Month, g.Day));
            return rate.Unavailable is { } why ? new Money(0m, [why]) : new Money(value * rate.Rate!.Value, null);
        }

        // Collapse months into the requested bucket + (period, group) key.
        var cells = grouped
            .GroupBy(g => new
            {
                Period = PeriodLabel(spec.TimeBucket, g.Year, g.Month, g.Day),
                g.GroupId,
                g.GroupName,
                g.ParentId,
            })
            .Select(g =>
            {
                var m = Money.Sum(g.Select(InReporting));
                return new ReportRow(
                    g.Key.Period, g.Key.GroupId, g.Key.GroupName, g.Key.ParentId, m.Amount,
                    RateUnavailable: m.Why);
            })
            .ToList();

        // Grouped by account, a row is that account's own figure, so it stays in the
        // account's currency and needs no rate (ADR-0114 D7): its sum per period and
        // across the window, unconverted. The cells above stay converted, because the
        // totals, the rest and the top-N ranking combine accounts. Each account's
        // postings are in its own currency (the projection above), so one per group.
        var byAccount = spec.GroupBy == ReportGroupBy.Account;
        var native = byAccount
            ? grouped
                .GroupBy(g => (Period: PeriodLabel(spec.TimeBucket, g.Year, g.Month, g.Day), g.GroupId))
                .ToDictionary(g => g.Key, g => g.Sum(c => Value(spec.Measure, c.Kind, c.Sum)))
            : null;
        var nativeTotal = byAccount
            ? grouped.GroupBy(g => g.GroupId).ToDictionary(g => g.Key!.Value, g => g.Sum(c => Value(spec.Measure, c.Kind, c.Sum)))
            : null;
        var currencyOf = byAccount
            ? grouped.GroupBy(g => g.GroupId).ToDictionary(g => g.Key!.Value, g => g.First().Currency)
            : null;

        // Grand total = the sum of the cells before rollup, so it stays correct
        // whether or not parent rows are materialized as subtotals. For every
        // dimension but Tag that is the sum of the underlying postings; a tag cell
        // counts a posting once per tag it carries, so a Tag total is the sum of the
        // tag rows, which can exceed the same window's spending.
        var total = SumOf(cells);

        // Per-period totals, on the same footing as Total (before rollup and before
        // top-N), so a chart's column height never comes from re-summing rows that
        // overlap or were cut. Zero-filled across the window when it has both
        // bounds: a period with no postings is a zero, not a missing column.
        var periodTotals = PeriodTotals(spec.TimeBucket, cells, rangeFrom, rangeTo);

        // Each group's own postings, kept for a top-N cut: under rollup the cells
        // below become subtrees, and the rest is summed from these.
        var leaves = cells;

        // Category-tree rollup: parent amounts include descendants. Build the full
        // ancestor set per (period, category) so the model gets the whole tree.
        // The parent map stays null for a flat result.
        // The category tree, read once: roll-up walks it, and every category row is
        // named by its path through it.
        var tree = spec.GroupBy == ReportGroupBy.Category
            ? await CategoryTree.LoadAsync(_db, spec.LedgerId, cancellationToken).ConfigureAwait(false)
            : null;
        IReadOnlyDictionary<Guid, Guid?>? parentOf = null;
        if (spec.Rollup && tree is not null)
        {
            cells = RollUpCategoryTree(tree, cells);
            parentOf = tree.ParentOf;
        }

        // Every group's full name (ADR-0104 amendment 2026-11-20): a category's path
        // from its top-level category, else the one name. The client picks what to show.
        IReadOnlyList<string> PathOf(Guid? id, string name) =>
            tree is not null && id is { } category ? tree.PathOf(category, name) : [name];

        // Per-group totals across the window, before top-N: each group's figure
        // whether or not it made the cut. After rollup, so a parent's total is its
        // subtree's, as its rows are.
        // Ordered by the converted figure, so accounts in different currencies rank on
        // one footing; an account's own total is what it shows.
        var groupTotals = cells
            .GroupBy(c => new { c.GroupId, c.GroupName })
            .Select(g =>
            {
                var m = SumOf(g);
                var path = PathOf(g.Key.GroupId, g.Key.GroupName);
                var shown = byAccount
                    ? new ReportGroupTotal(
                        g.Key.GroupId, g.Key.GroupName, Rounded(nativeTotal![g.Key.GroupId!.Value]),
                        CurrencyCode: currencyOf![g.Key.GroupId!.Value], GroupPath: path)
                    : new ReportGroupTotal(g.Key.GroupId, g.Key.GroupName, Rounded(m.Amount), m.Why, reporting, path);
                return (Rank: Math.Abs(Rounded(m.Amount) ?? 0m), Shown: shown);
            })
            .OrderByDescending(g => g.Rank)
            .ThenBy(g => g.Shown.GroupName, StringComparer.Ordinal)
            .ThenBy(g => g.Shown.GroupId)
            .Select(g => g.Shown)
            .ToList();

        // Top-N by absolute total over the whole range, then keep only those
        // groups' cells (so a time series is limited to the top movers). Keyed by
        // a stable group key (id, or name for the payee dimension). The kept rows
        // are marked drawn or not, and what no drawn row holds is the rest.
        ReportRest? rest = null;
        if (spec.TopN is { } topN && topN > 0)
        {
            // A group whose total needs a missing rate ranks as nothing: it cannot be
            // measured, so it is never preferred to one that can (ADR-0114 D9).
            var keep = cells
                .GroupBy(GroupKey)
                .Select(g => new { g.Key, Total = SumOf(g).Amount ?? 0m })
                .OrderByDescending(x => Math.Abs(x.Total))
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Take(topN)
                .Select(x => x.Key)
                .ToHashSet();
            (cells, rest) = DrawnAndRest(cells, leaves, keep, parentOf, periodTotals);
            if (rest.Only is { } only)
                rest = rest with { Only = only with { GroupPath = PathOf(only.GroupId, only.GroupName) } };
        }

        var rows = cells
            .OrderBy(c => c.Period, StringComparer.Ordinal)
            .ThenByDescending(c => Math.Abs(c.Amount ?? 0m))
            .ThenBy(GroupKey, StringComparer.Ordinal)
            // A parent's subtree row before its own postings' row. Without a cut
            // no row is direct and the order is as it was.
            .ThenBy(c => c.IsDirect)
            .Select(c => byAccount
                ? c with
                {
                    Amount = Rounded(native![(c.Period, c.GroupId)]),
                    RateUnavailable = null,
                    CurrencyCode = currencyOf![c.GroupId!.Value],
                    GroupPath = PathOf(c.GroupId, c.GroupName),
                }
                : c with { Amount = Rounded(c.Amount), CurrencyCode = reporting, GroupPath = PathOf(c.GroupId, c.GroupName) })
            .ToList();

        return new ReportResult(
            rows, Rounded(total.Amount), rangeFrom, rangeTo, periodTotals, groupTotals, rest, total.Why);
    }

    /// <summary>
    /// The rows a top-N cut keeps, each marked drawn or not, with the extra rows
    /// for a kept parent's own postings, and the rest: the postings no drawn row
    /// holds, which a chart shows as one series.
    /// </summary>
    /// <remarks>
    /// <para>Drawn rows never overlap, so stacked with the rest they come to each
    /// period's total. Flat, every kept row is drawn, because each holds only its
    /// group's own postings. Rolled up, a kept row holds its whole subtree, so it is
    /// drawn only when no kept row lies below it. A kept row that has one is drawn
    /// by its kept descendants and by its own postings, which come back as
    /// <see cref="ReportRow.IsDirect"/> rows; without them a parent's own postings
    /// would be drawn nowhere.</para>
    /// <para>The rest is summed from the postings no drawn row holds, rather than
    /// taken as the period total less the drawn rows. The two agree exactly when
    /// the drawn rows and the rest partition the money, which is what the tests
    /// check; subtracting would make that hold whatever was drawn.</para>
    /// </remarks>
    /// <param name="cells">Every group's rows before the cut, rolled up when the report is.</param>
    /// <param name="leaves">Every group's own postings, before rollup.</param>
    /// <param name="keep">The group keys the cut keeps.</param>
    /// <param name="parentOf">The category tree when the report is rolled up; null when it is flat.</param>
    /// <param name="periodTotals">The periods the rest is reported for, in order.</param>
    private static (List<ReportRow> Rows, ReportRest Undrawn) DrawnAndRest(
        IReadOnlyList<ReportRow> cells,
        IReadOnlyList<ReportRow> leaves,
        HashSet<string> keep,
        IReadOnlyDictionary<Guid, Guid?>? parentOf,
        IReadOnlyList<ReportPeriodTotal> periodTotals)
    {
        // Every category with a kept row somewhere below it. Empty for a flat
        // result, where no row lies below another.
        var aboveKept = new HashSet<Guid>();
        if (parentOf is not null)
        {
            foreach (var kept in cells.Where(c => keep.Contains(GroupKey(c))))
            {
                if (kept.GroupId is not { } id) continue;
                var cur = parentOf.GetValueOrDefault(id);
                var guard = 0;
                while (cur is { } node && guard++ < 100 && aboveKept.Add(node))
                    cur = parentOf.GetValueOrDefault(node);
            }
        }

        bool IsKept(Guid id) => keep.Contains(id.ToString());

        var rows = cells
            .Where(c => keep.Contains(GroupKey(c)))
            .Select(c => c with { Drawn = c.GroupId is not { } id || !aboveKept.Contains(id) })
            .ToList();

        // A kept parent drawn by its kept descendants: its own postings, one row per
        // period, under its own id.
        rows.AddRange(leaves
            .Where(l => parentOf is not null && l.GroupId is { } id && IsKept(id) && aboveKept.Contains(id))
            .Select(l => l with { Drawn = true, IsDirect = true }));

        // The rolled-up rows' names, for a dropped subtree named at its highest category.
        var nameOf = cells
            .Where(c => c.GroupId is not null)
            .GroupBy(c => c.GroupId!.Value)
            .ToDictionary(g => g.Key, g => g.First().GroupName);

        // The series a group's own postings fall in, with the group that names it, or
        // null when a drawn row holds them.
        (string Key, ReportRestGroup Group)? SeriesOf(ReportRow leaf)
        {
            if (parentOf is null || leaf.GroupId is not { } id)
                return keep.Contains(GroupKey(leaf))
                    ? null
                    : (GroupKey(leaf), new ReportRestGroup(leaf.GroupId, leaf.GroupName, IsDirect: false));

            // Kept: drawn whole, or by its own row when a kept row lies below it.
            if (IsKept(id)) return null;
            // Not kept, but a kept row lies below it: its own postings are a series of the rest.
            if (aboveKept.Contains(id))
                return ("direct:" + GroupKey(leaf), new ReportRestGroup(id, leaf.GroupName, IsDirect: true));

            // In the rest with its whole subtree, unless a kept ancestor draws it whole.
            // The series is the highest category above it in the same position, so a
            // dropped parent and its children are one series. A category with a kept
            // row below it ends the walk: nothing above it can draw this one whole,
            // since a row drawn whole has no kept row below it.
            var top = id;
            var cur = parentOf.GetValueOrDefault(id);
            var guard = 0;
            while (cur is { } node && guard++ < 100)
            {
                if (aboveKept.Contains(node)) break;
                if (IsKept(node)) return null;
                top = node;
                cur = parentOf.GetValueOrDefault(node);
            }
            return (top.ToString(), new ReportRestGroup(
                top, nameOf.TryGetValue(top, out var name) ? name : leaf.GroupName, IsDirect: false));
        }

        var undrawn = leaves
            .Select(l => (Series: SeriesOf(l), Cell: l))
            .Where(x => x.Series is not null)
            .ToList();
        var byPeriod = undrawn
            .Where(x => x.Cell.Period is not null)
            .GroupBy(x => x.Cell.Period!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => SumOf(g.Select(x => x.Cell)), StringComparer.Ordinal);
        var series = undrawn
            .Select(x => x.Series!.Value)
            .DistinctBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        var restTotal = SumOf(undrawn.Select(x => x.Cell));
        var rest = new ReportRest(
            series.Count,
            Rounded(restTotal.Amount),
            periodTotals.Select(p =>
            {
                var m = byPeriod.GetValueOrDefault(p.Period, Money.Zero);
                return new ReportPeriodTotal(p.Period, Rounded(m.Amount), m.Why);
            }).ToList(),
            // One series is named by its group, so a chart labels it rather than "Other (1)".
            series.Count == 1 ? series[0].Group : null,
            restTotal.Why);

        return (rows, rest);
    }

    /// <summary>
    /// One total per period of <paramref name="bucket"/>: every period the window
    /// spans when it has both bounds, else the periods that have postings. Empty
    /// for <see cref="ReportTimeBucket.None"/>, whose one total is
    /// <see cref="ReportResult.Total"/>.
    /// </summary>
    private static List<ReportPeriodTotal> PeriodTotals(
        ReportTimeBucket bucket, IEnumerable<ReportRow> cells, DateTime? from, DateTime? to)
    {
        if (bucket == ReportTimeBucket.None) return [];

        var sums = cells
            .Where(c => c.Period is not null)
            .GroupBy(c => c.Period!)
            .ToDictionary(g => g.Key, g => SumOf(g), StringComparer.Ordinal);

        static ReportPeriodTotal Total(string label, Money m) => new(label, Rounded(m.Amount), m.Why);

        if (from is { } start && to is { } end)
        {
            // The walk stops before the exclusive end: a window ending at midnight
            // on the 1st has no column for that month.
            var periods = new List<ReportPeriodTotal>();
            for (var cursor = StartOfBucket(bucket, start); cursor < end; cursor = NextBucket(bucket, cursor))
            {
                var label = PeriodLabel(bucket, cursor.Year, cursor.Month, cursor.Day)!;
                periods.Add(Total(label, sums.GetValueOrDefault(label, Money.Zero)));
            }
            return periods;
        }

        return sums
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Total(kv.Key, kv.Value))
            .ToList();
    }

    private static DateTime StartOfBucket(ReportTimeBucket bucket, DateTime at) => bucket switch
    {
        ReportTimeBucket.Day => new DateTime(at.Year, at.Month, at.Day, 0, 0, 0, DateTimeKind.Utc),
        ReportTimeBucket.Quarter => new DateTime(at.Year, ((at.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ReportTimeBucket.Year => new DateTime(at.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(at.Year, at.Month, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static DateTime NextBucket(ReportTimeBucket bucket, DateTime at) => bucket switch
    {
        ReportTimeBucket.Day => at.AddDays(1),
        ReportTimeBucket.Quarter => at.AddMonths(3),
        ReportTimeBucket.Year => at.AddYears(1),
        _ => at.AddMonths(1),
    };

    // Stable per-group key for top-N / dedup: the id when present, else the name
    // (the payee dimension has no id).
    private static string GroupKey(ReportRow r) =>
        r.GroupId?.ToString() ?? ("name:" + r.GroupName);

    /// <summary>A ledger's category tree: each category's parent and name.</summary>
    private sealed class CategoryTree
    {
        private readonly Dictionary<Guid, IReadOnlyList<string>> _paths = [];

        private CategoryTree(IReadOnlyDictionary<Guid, Guid?> parentOf, IReadOnlyDictionary<Guid, string> nameOf)
        {
            ParentOf = parentOf;
            NameOf = nameOf;
        }

        public IReadOnlyDictionary<Guid, Guid?> ParentOf { get; }
        public IReadOnlyDictionary<Guid, string> NameOf { get; }

        public static async Task<CategoryTree> LoadAsync(
            AppDbContext db, Guid ledgerId, CancellationToken cancellationToken)
        {
            var cats = await db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == ledgerId && a.AccountType == "category")
                .Select(a => new { a.Id, a.ParentId, a.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return new CategoryTree(
                cats.ToDictionary(c => c.Id, c => c.ParentId),
                cats.ToDictionary(c => c.Id, c => c.Name));
        }

        /// <summary>
        /// The names from the category's top-level ancestor down to its own. A
        /// category the tree does not hold is named by <paramref name="name"/> alone.
        /// The walk stops at a category already on it, so a cycle in the parent
        /// links, which the schema does not allow, ends rather than loops.
        /// </summary>
        public IReadOnlyList<string> PathOf(Guid id, string name)
        {
            if (_paths.TryGetValue(id, out var cached)) return cached;
            if (!NameOf.ContainsKey(id)) return [name];

            var names = new List<string>();
            var seen = new HashSet<Guid>();
            var cur = (Guid?)id;
            while (cur is { } node && seen.Add(node) && NameOf.TryGetValue(node, out var n))
            {
                names.Add(n);
                cur = ParentOf.GetValueOrDefault(node);
            }
            names.Reverse();
            _paths[id] = names;
            return names;
        }
    }

    // Propagate each leaf category's amount onto all its ancestors, per period,
    // so the result carries the whole tree with parent subtotals. Cycles are
    // guarded; the grand total is computed pre-rollup by the caller.
    private static List<ReportRow> RollUpCategoryTree(CategoryTree tree, List<ReportRow> leaves)
    {
        var parentOf = tree.ParentOf;
        var nameOf = tree.NameOf;

        var rolled = new Dictionary<(string? Period, Guid Id), Money>();
        foreach (var leaf in leaves)
        {
            if (leaf.GroupId is not { } id) continue;
            var cur = (Guid?)id;
            var guard = 0;
            var money = Money.Of(leaf);
            while (cur is { } node && guard++ < 100)
            {
                var key = (leaf.Period, node);
                rolled[key] = rolled.TryGetValue(key, out var acc) ? acc + money : money;
                cur = parentOf.TryGetValue(node, out var p) ? p : null;
            }
        }

        var rows = rolled
            .Select(kv => new ReportRow(
                kv.Key.Period,
                kv.Key.Id,
                nameOf.TryGetValue(kv.Key.Id, out var n) ? n : "(category)",
                parentOf.TryGetValue(kv.Key.Id, out var par) ? par : null,
                kv.Value.Amount,
                RateUnavailable: kv.Value.Why))
            .ToList();
        return rows;
    }

    private static string? PeriodLabel(ReportTimeBucket bucket, int year, int month, int day) => bucket switch
    {
        ReportTimeBucket.None => null,
        ReportTimeBucket.Year => year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
        ReportTimeBucket.Quarter => $"{year:D4}-Q{(month - 1) / 3 + 1}",
        ReportTimeBucket.Day => $"{year:D4}-{month:D2}-{day:D2}",
        _ => $"{year:D4}-{month:D2}",   // Month
    };

    /// <summary>
    /// The transaction line-drill (ADR-0063 §D5 v2): filtered/sorted/paged rows
    /// from the override-aware view. Offset paging with a deterministic sort
    /// (the chosen key, then leg id) so a page is reproducible while the data is
    /// unchanged; <see cref="TransactionLinesResult.HasMore"/> peeks one row past
    /// the limit to avoid a count query. Min/Max bound the absolute amount.
    /// </summary>
    public async Task<TransactionLinesResult> ListTransactionsAsync(
        TransactionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, 500);
        var offset = Math.Max(0, query.Offset);

        // Every read below, the filters' header reads, the page and its lines, sees one state.
        await using var snapshot = await ReadSnapshot.BeginAsync(_db, cancellationToken).ConfigureAwait(false);

        // Each line is presented as the register of the leg in scope shows it: the
        // scoped account's; with no account but a category, the category the line
        // pairs with (the budget drill-down's register); with neither, the line's own,
        // or for a line between two real accounts the posting's outflow leg (for a
        // zero amount, the originating side's leg); a query that asks for it
        // (PresentFromOwnLeg) reads every line from its own leg, as each account's register does.
        var scope = query.AccountId is not null ? ListingScope.Account
            : query.CategoryId is not null || query.CategoryIds is { Count: > 0 } ? ListingScope.Counterparty
            : query.PresentFromOwnLeg ? ListingScope.Account
            : ListingScope.Ledger;
        IQueryable<Posting> Lines() =>
            from l in StoredNotesQuery.ListingLegs(_db, scope)
            join a in _db.Accounts.AsNoTracking() on l.Row.AccountId equals a.Id
            where a.LedgerId == query.LedgerId && !l.Row.IsHidden && l.Row.IsMergedInto == null
            select new Posting { Row = l.Row, Account = a, NotesLegId = l.NotesLegId, Shown = l.Shown };
        var q = Lines();

        if (query.AccountId is { } acct)
            q = q.Where(x => x.Row.AccountId == acct);
        else
            // Money side only when not scoped to a specific account: a transaction's
            // category legs are never lines of their own, and a transfer between two
            // real accounts lists both of its legs.
            q = q.Where(x => x.Account.AccountType != "category");

        if (query.CategoryId is { } cat)
            q = q.Where(x => x.Row.CounterpartyAccountId == cat);

        if (query.CategoryIds is { Count: > 0 } cats)
        {
            // Materialized so EF translates Contains to `= ANY(@ids)` rather
            // than unrolling one OR per id.
            var ids = cats.ToList();
            q = q.Where(x => x.Row.CounterpartyAccountId != null
                             && ids.Contains(x.Row.CounterpartyAccountId.Value));
        }

        if (query.TagId is { } tag)
            // The tags in play for the line: what its register shows, and on a
            // split-side line the transaction's tags with the line's own.
            q = await WithInPlayTagAsync(
                q, StoredNotesQuery.ListingFromCounterparty(scope), tag, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query.Payee))
            q = q.Where(x => x.Row.Payee == query.Payee);

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            // The memo the line shows, and on the split side the transaction's.
            var pattern = "%" + query.Text.Trim() + "%";
            // A line matches by its header's payee or by a memo it shows, and a memo the
            // line shows is a stored memo of a leg of its header. So the headers that can
            // match are read first, from the headers and the legs alone: those whose
            // payee matches, with those that have a leg with a matching stored memo.
            // Only their lines are joined to their other leg, presented and matched
            // exactly.
            var payeeHeaders = _db.TxnHeaders
                .Where(h => h.LedgerId == query.LedgerId && h.Payee != null && EF.Functions.ILike(h.Payee, pattern))
                .Select(h => h.Id);
            var memoHeaders = _db.StoredLegPresentations
                .Where(m => m.LedgerId == query.LedgerId)
                .Where(m => (m.Memo != null && EF.Functions.ILike(m.Memo, pattern))
                            || (m.HeaderMemo != null && EF.Functions.ILike(m.HeaderMemo, pattern)))
                .Select(m => m.HeaderId);
            var candidates = await payeeHeaders.Union(memoHeaders)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            // Two filters. The headers are a list of values, which the database applies
            // to the headers before any leg is joined and knows the size of; as a
            // subquery the set would be joined above the view's own joins, or tested row
            // by row after them.
            q = q.Where(x => candidates.Contains(x.Row.HeaderId))
                .Where(x =>
                    (x.Row.Payee != null && EF.Functions.ILike(x.Row.Payee, pattern)) ||
                    (x.Shown.Memo != null && EF.Functions.ILike(x.Shown.Memo, pattern)) ||
                    (x.Shown.InPlayHeaderMemo != null && EF.Functions.ILike(x.Shown.InPlayHeaderMemo, pattern)));
        }

        q = query.Direction switch
        {
            TransactionDirection.Inflow => q.Where(x => x.Row.Amount > 0m),
            TransactionDirection.Outflow => q.Where(x => x.Row.Amount < 0m),
            _ => q,
        };

        if (query.MinAmount is { } min)
            q = q.Where(x => x.Row.Amount >= min || x.Row.Amount <= -min);
        if (query.MaxAmount is { } max)
            q = q.Where(x => x.Row.Amount <= max && x.Row.Amount >= -max);

        if (query.FromUtc is { } from) q = q.Where(x => x.Row.PostedAt >= from);
        if (query.ToUtc is { } to) q = q.Where(x => x.Row.PostedAt < to);

        var desc = query.Descending;
        var ordered = query.Sort switch
        {
            TransactionSort.Amount => desc
                ? q.OrderByDescending(x => x.Row.Amount)
                : q.OrderBy(x => x.Row.Amount),
            TransactionSort.AbsAmount => desc
                ? q.OrderByDescending(x => x.Row.Amount < 0m ? -x.Row.Amount : x.Row.Amount)
                : q.OrderBy(x => x.Row.Amount < 0m ? -x.Row.Amount : x.Row.Amount),
            _ => desc
                ? q.OrderByDescending(x => x.Row.PostedAt).ThenByDescending(x => x.Row.HeaderSeq)
                : q.OrderBy(x => x.Row.PostedAt).ThenBy(x => x.Row.HeaderSeq),
        };
        // Within one header, legs read in the order the user arranged them. leg_index
        // IS posting_index (resolved_transactions aliases it), and every UI read path
        // already sorts by it — tiebreaking straight to the leg's gen_random_uuid()
        // primary key would make this listing disagree with the register and the
        // editor for every split, showing an agent a split's lines shuffled.
        ordered = desc ? ordered.ThenByDescending(x => x.Row.LegIndex) : ordered.ThenBy(x => x.Row.LegIndex);
        // Deterministic tiebreak so offset paging is reproducible.
        ordered = desc ? ordered.ThenByDescending(x => x.Row.Id) : ordered.ThenBy(x => x.Row.Id);

        // The page is chosen first by the leg's key alone, then its lines are read by that
        // key: sorting every matching line with its memo and account names carried along
        // costs more than reading the page's lines again. The two reads share one
        // snapshot with the filters' header reads above, so a line hidden or merged
        // between them cannot shorten the page; whether more follow is decided by the
        // first read alone.
        var peeked = await ordered
            .Skip(offset)
            .Take(limit + 1)   // peek one past for HasMore
            .Select(x => x.Row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = peeked.Count > limit;
        var pageIds = peeked.Take(limit).ToList();
        var position = pageIds.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        var rows = (await Lines()
                .Where(x => pageIds.Contains(x.Row.Id))
                .Select(LineOf())
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(x => position[x.Id])
            .ToList();

        var page = rows.Select(x => new TransactionLine(
                x.HeaderId,
                x.PostedAt,
                x.Payee,
                x.AccountId,
                x.AccountName,
                x.CounterpartyAccountId,
                x.CounterpartyAccountName,
                x.Amount,
                x.Memo,
                x.Status,
                x.Kind == LegKind.SplitSide ? x.HeaderMemo : null,
                // In the database's order for names, as the register lists them.
                x.Tags ?? Array.Empty<string>(),
                x.Id,
                x.CurrencyCode))
            .ToList();

        return new TransactionLinesResult(page, limit, offset, hasMore);
    }

    /// <summary>A line as <see cref="ListTransactionsAsync"/> reads it, its in-play tag names aggregated in the statement.</summary>
    private sealed class LineRow
    {
        public Guid Id { get; init; }
        public Guid HeaderId { get; init; }
        public DateTime PostedAt { get; init; }
        public string? Payee { get; init; }
        public Guid AccountId { get; init; }
        public string AccountName { get; init; } = string.Empty;
        public string CurrencyCode { get; init; } = string.Empty;
        public Guid? CounterpartyAccountId { get; init; }
        public string? CounterpartyAccountName { get; init; }
        public decimal Amount { get; init; }
        public string? Memo { get; init; }
        public string Status { get; init; } = string.Empty;
        public LegKind Kind { get; init; }
        public string? HeaderMemo { get; init; }
        public string[]? Tags { get; init; }
    }

    private Expression<Func<Posting, LineRow>> LineOf()
    {
        var names = StoredNotesQuery.TagNamesOf(_db, StoredNotesQuery.InPlay);
        Expression<Func<Posting, LineRow>> line = x => new LineRow
        {
            Id = x.Row.Id,
            HeaderId = x.Row.HeaderId,
            PostedAt = x.Row.PostedAt,
            Payee = x.Row.Payee,
            AccountId = x.Row.AccountId,
            AccountName = x.Account.Name,
            // The currency the amount is in. A category holds no money of its own: the
            // amount on its line is its posting's, in the currency of the account on the
            // posting's other side. Only a line between two categories has no such
            // account, and it takes the category's own code.
            CurrencyCode = x.Account.AccountType == "category" && x.Row.CounterpartyAccountType != "category"
                ? _db.Accounts.Where(c => c.Id == x.Row.CounterpartyAccountId)
                      .Select(c => c.CurrencyCode)
                      .FirstOrDefault() ?? x.Account.CurrencyCode
                : x.Account.CurrencyCode,
            CounterpartyAccountId = x.Row.CounterpartyAccountId,
            CounterpartyAccountName = x.Row.CounterpartyAccountName,
            Amount = x.Row.Amount,
            Memo = x.Shown.Memo,
            Status = x.Row.Status,
            Kind = x.Shown.Kind,
            HeaderMemo = x.Shown.InPlayHeaderMemo,
            Tags = names.Inline(x.NotesLegId),
        };
        return line.Expand();
    }
}
