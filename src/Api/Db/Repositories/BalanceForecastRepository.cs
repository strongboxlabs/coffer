using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Each account's balance from the caller's today through a short window ahead: the
/// home screen's Cash ahead. A projection beside the register, built only from what the
/// register and the agenda already say.
/// </summary>
/// <remarks>
/// <para><b>Day 0 is the register's.</b> It is <see cref="AccountBalancesRepository"/>'s
/// current balance for the caller's today, the one read every current balance goes
/// through, so the projection starts where the register stands. A brokerage's balance is
/// its cash.</para>
/// <para><b>Committed rows come from the register's running balance.</b> Each row dated in
/// the window, on the register's visible side (<see cref="RegisterQuery.CandidateHeaders"/>,
/// whose conditions are the partial index's), carries the balance it leaves on each
/// account it touches (<c>txn_header_account_balances</c>, read on its key). The balance
/// at the end of a day is the one the last such row of that day leaves. They do not come
/// from the agenda, which dates a posted occurrence by its slot and does not list a row
/// entered by hand.</para>
/// <para><b>Un-posted occurrences come from the agenda</b>
/// (<see cref="RemindersRepository.GetUpcomingAsync"/>, kind <c>reminder</c>), so their
/// dates, loan amounts, estimates and acknowledgement floor are the Bills widget's. One
/// dated today or earlier is placed on day 1, since day 0 is the register's; one dated
/// before today is overdue and also counted. Each template line on an account in scope
/// applies, from one batched read of the template lines. A loan places its computed
/// payment on its source; an estimate places its amount on the source and the negation
/// on the other line when that is in scope, and an estimating reminder with no history
/// yet places the amount entered, as the agenda gives it, its lines as entered. Both
/// mark what they reach as resting on an estimate. An investment template applies its cash
/// lines only: its security lines sit on the holdings sibling, which is never in scope.
/// What cannot be placed is listed, never guessed.</para>
/// <para><b>A reminder between currencies places each side in its own.</b> A fixed one
/// places each template line as the post writes it, the other line's own amount on the
/// other account. An estimating one places the source estimate on the source and, on the
/// other account, that estimate at the latest derived rate, for every occurrence, overdue
/// ones included, so a series is still planned once (ADR-0114 D13, ADR-0109 D8). It reads
/// the estimate through the agenda's and the fire's one helper
/// (<see cref="RemindersRepository.LatestEstimatesAsync"/>). With the latest rate missing
/// the reminder is listed as <see cref="Reasons.RateUnavailable"/>, naming each missing
/// stored leg, and nothing of it is placed. No figure adds two currencies: the combined
/// line converts each account at the latest rate.</para>
/// </remarks>
public sealed class BalanceForecastRepository
{
    /// <summary>The window when the caller names none.</summary>
    public const int DefaultDays = 30;

    /// <summary>The longest window: the widest the home screen offers.</summary>
    public const int MaxDays = 60;

    /// <summary>
    /// How far back an un-posted occurrence is still placed: the Bills widget's fixed
    /// look-back for overdue reminders, so the two count the same occurrences.
    /// </summary>
    public const int OverdueLookbackDays = 90;

    /// <summary>Why a reminder is left out (<see cref="BalanceForecastExclusionDto.Reason"/>).</summary>
    public static class Reasons
    {
        /// <summary>The template has no lines: it names no account to move. The auto-post
        /// job's key for the same fact.</summary>
        public const string NoLines = "no-lines";

        /// <summary>A computed amount cannot be placed on the accounts in scope.</summary>
        public const string OverrideNotPlaceable = "override-not-placeable";

        /// <summary>A managed loan payment across currencies, which can never post
        /// (ADR-0114 D13), or a loan whose computed payment would land on an account of
        /// another currency than its loan's.</summary>
        public const string CrossCurrency = "cross-currency";

        /// <summary>An estimating reminder between currencies whose other side needs the
        /// latest derived rate, and a stored leg of it is missing (ADR-0114 D13); the row
        /// names each one.</summary>
        public const string RateUnavailable = "rate-unavailable";
    }

    private readonly AppDbContext _db;
    private readonly AccountBalancesRepository _balances;
    private readonly RemindersRepository _reminders;
    private readonly TimeProvider _clock;

    public BalanceForecastRepository(
        AppDbContext db, AccountBalancesRepository balances, RemindersRepository reminders, TimeProvider clock)
    {
        _db = db;
        _balances = balances;
        _reminders = reminders;
        _clock = clock;
    }

    /// <summary>The window actually used for <paramref name="days"/>: the default when
    /// none is sent, else clamped to one day through <see cref="MaxDays"/>.</summary>
    public static int ClampDays(int? days) => Math.Clamp(days ?? DefaultDays, 1, MaxDays);

    /// <param name="today">The caller's local date, resolved as every current value
    /// resolves it (<see cref="ScheduledBoundary"/>).</param>
    public async Task<BalanceForecastDto> GetAsync(
        Guid ledgerId, DateOnly? today, int? days, CancellationToken cancellationToken = default)
    {
        var day0 = ScheduledBoundary.Today(today, _clock);
        var span = ClampDays(days);

        // Every account a reminder is paid from: the sources of the series the agenda
        // expands (active and materialized), not a category or a holdings sibling,
        // inactive ones too.
        var sources = _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.IsActive && r.TemplateHeaderId != null && r.SourceAccountId != null)
            .Select(r => r.SourceAccountId!.Value);
        var scope = (await DisplayCurrency.NetWorthAccounts(_db, ledgerId)
                .Where(a => sources.Contains(a.Id))
                .Select(a => new ScopeAccount(a.Id, a.Name, a.CurrencyCode))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.Id)
            .ToList();

        var inScope = scope.ToDictionary(a => a.Id);
        Guid[] scopeIds = [.. inScope.Keys];

        // Day 0, the register's own read at the same resolved date.
        var current = await _balances
            .GetCurrentBalancesAsync(ledgerId, activeOnly: false, day0, cancellationToken)
            .ConfigureAwait(false);

        var committed = scopeIds.Length == 0
            ? new List<CommittedRow>()
            : await CommittedRowsAsync(ledgerId, day0, span, scopeIds, cancellationToken).ConfigureAwait(false);

        var placed = await PlaceOccurrencesAsync(ledgerId, day0, span, inScope, cancellationToken)
            .ConfigureAwait(false);

        var dates = Enumerable.Range(0, span + 1).Select(day0.AddDays).ToList();
        var accounts = scope
            .Select(a =>
            {
                var points = Project(
                    dates,
                    current.GetValueOrDefault(a.Id),
                    committed.Where(c => c.AccountId == a.Id).ToList(),
                    placed.Deltas.GetValueOrDefault(a.Id));
                var firstEstimate = placed.FirstEstimate.TryGetValue(a.Id, out var day) ? day : (DateOnly?)null;
                return new BalanceForecastAccountDto(
                    a.Id, a.Name, a.CurrencyCode, points[0].Balance, LowestOf(points, firstEstimate), points);
            })
            .ToList();

        // The combined line, in the reporting currency: every account at the latest rate,
        // on day 0 and on each forecast day alike (ADR-0114 D7), summed and rounded once
        // per day. A missing rate leaves no line, named (ADR-0114 D9), never a line of the
        // accounts that converted.
        BalanceForecastCombinedDto? combined = null;
        IReadOnlyList<RateUnavailable> missing = RateReasons.None;
        if (accounts.Count > 0)
        {
            var reporting = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
            var lookup = new ExchangeRateLookup(_db, _clock);
            var needs = lookup.NewNeeds();
            foreach (var a in accounts) needs.AddLatest(a.CurrencyCode, reporting);
            var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
            var totals = dates
                .Select((d, i) =>
                {
                    var total = rates.NewTotal(reporting);
                    foreach (var a in accounts) total.AddLatest(a.Points[i].Balance, a.CurrencyCode);
                    return total.Result;
                })
                .ToList();
            missing = RateReasons.Of([.. totals.Select(t => t.Unavailable)]);
            if (missing.Count == 0)
            {
                var points = dates.Select((d, i) => new BalanceForecastPointDto(d, totals[i].Amount!.Value)).ToList();
                // The first day an estimate landed on any account in scope.
                DateOnly? firstEstimate = placed.FirstEstimate.Count == 0 ? null : placed.FirstEstimate.Values.Min();
                combined = new BalanceForecastCombinedDto(
                    reporting, points[0].Balance, LowestOf(points, firstEstimate), points);
            }
        }

        return new BalanceForecastDto(
            accounts, combined, placed.IncludesEstimates, placed.OverdueOnDay1, placed.Excluded, missing);
    }

    /// <summary>
    /// The register's rows dated in the window on the accounts in scope, each with the
    /// balance it leaves there, in the register's order.
    /// </summary>
    /// <remarks>
    /// Read from <c>txn_headers</c> by ledger and date under the register's visibility
    /// predicate, which is the partial index's, then joined to
    /// <c>txn_header_account_balances</c> on its key. A hidden, merged-away or template
    /// header leaves no running balance (the balance walk skips it), and the predicate
    /// keeps the read on the index.
    /// </remarks>
    private async Task<List<CommittedRow>> CommittedRowsAsync(
        Guid ledgerId, DateOnly day0, int span, Guid[] scopeIds, CancellationToken cancellationToken)
    {
        var from = ScheduledBoundary.ScheduledFrom(day0);
        var to = from.AddDays(span);
        return (await RegisterQuery.CandidateHeaders(_db, ledgerId, through: null)
                .Where(h => h.PostedAt >= from && h.PostedAt < to)
                .Join(_db.TxnHeaderAccountBalances.AsNoTracking(),
                      h => h.Id, b => b.HeaderId,
                      (h, b) => new { h.PostedAt, h.Seq, b.AccountId, b.BalanceAfter })
                .Where(x => scopeIds.Contains(x.AccountId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(x => new CommittedRow(x.AccountId, DateOnly.FromDateTime(x.PostedAt), x.PostedAt, x.Seq, x.BalanceAfter))
            .OrderBy(r => r.PostedAt)
            .ThenBy(r => r.Seq)
            .ToList();
    }

    /// <summary>
    /// The un-posted occurrences in the window, placed on the accounts in scope by the
    /// day they apply, with what was left out and why.
    /// </summary>
    private async Task<Placement> PlaceOccurrencesAsync(
        Guid ledgerId, DateOnly day0, int span, IReadOnlyDictionary<Guid, ScopeAccount> scope,
        CancellationToken cancellationToken)
    {
        var unposted = (await _reminders
                .GetUpcomingAsync(ledgerId, day0.AddDays(-OverdueLookbackDays), day0.AddDays(span), cancellationToken)
                .ConfigureAwait(false))
            .Where(o => string.Equals(o.Kind, "reminder", StringComparison.Ordinal))
            .ToList();
        if (unposted.Count == 0) return Placement.Empty;

        Guid[] seriesIds = [.. unposted.Select(o => o.ReminderId).Distinct()];
        var series = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && seriesIds.Contains(r.Id))
            .Join(_db.TxnHeaders.AsNoTracking(),
                  r => r.TemplateHeaderId, h => h.Id,
                  (r, h) => new SeriesFacts(
                      r.Id, h.Id, r.SourceAccountId,
                      // The auto-post job's test for the same fact: a template with no lines
                      // has no owner.
                      h.OriginatingAccountId == null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The one batched read of the template lines, each with its account's type and
        // currency.
        Guid[] templateIds = [.. series.Select(s => s.TemplateHeaderId)];
        var legsByTemplate = (await _db.TxnLegs.AsNoTracking()
                .Where(l => templateIds.Contains(l.HeaderId))
                .Join(_db.Accounts.AsNoTracking(),
                      l => l.AccountId, a => a.Id,
                      (l, a) => new TemplateLeg(l.HeaderId, l.AccountId, l.Amount, a.AccountType, a.CurrencyCode))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(l => l.HeaderId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TemplateLeg>)g.ToList());

        var loanDriven = await _reminders.LoanDrivenSeriesAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var loanAcross = await _reminders.LoanSeriesAcrossCurrenciesAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        // Every estimate at the latest rate, as a forecast day converts (ADR-0114 D7, D13).
        var latestEstimates = await _reminders.LatestEstimatesAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        // Every un-posted occurrence of one series is planned alike (a loan's payment, the
        // amount entered, or the estimate at the latest rate), so a series is planned once,
        // from its first occurrence.
        var firstOccurrence = unposted.GroupBy(o => o.ReminderId).ToDictionary(g => g.Key, g => g.First());
        var plans = series.ToDictionary(
            s => s.Id,
            s => Plan(
                s, legsByTemplate.GetValueOrDefault(s.TemplateHeaderId, []),
                firstOccurrence[s.Id], scope, loanDriven, loanAcross,
                latestEstimates.TryGetValue(s.Id, out var latest) ? latest : null));

        var deltas = new Dictionary<Guid, SortedDictionary<DateOnly, decimal>>();
        // Per account, the first day an estimated amount (or its negation) lands on it.
        var firstEstimate = new Dictionary<Guid, DateOnly>();
        var overdue = 0;
        var includesEstimates = false;
        var excluded = new List<(DateOnly First, BalanceForecastExclusionDto Row)>();
        var listed = new HashSet<Guid>();
        foreach (var o in unposted)
        {
            if (!plans.TryGetValue(o.ReminderId, out var plan)) continue;
            if (plan.Excluded is { } reason)
            {
                if (listed.Add(o.ReminderId))
                    excluded.Add((o.Date, new BalanceForecastExclusionDto(o.ReminderId, reason, plan.RateUnavailable)));
                continue;
            }
            if (plan.Effects.Count == 0) continue;

            // Day 0 is the register's own balance, so anything due by today lands on day 1.
            var applies = o.Date <= day0 ? day0.AddDays(1) : o.Date;
            foreach (var (account, amount) in plan.Effects)
            {
                if (!deltas.TryGetValue(account, out var byDay)) deltas[account] = byDay = new();
                byDay[applies] = byDay.GetValueOrDefault(applies) + amount;
                if (plan.IsEstimate && (!firstEstimate.TryGetValue(account, out var first) || applies < first))
                    firstEstimate[account] = applies;
            }
            if (o.Date < day0) overdue++;
            includesEstimates |= plan.IsEstimate;
        }

        return new Placement(
            deltas,
            firstEstimate,
            overdue,
            includesEstimates,
            excluded.OrderBy(e => e.First).ThenBy(e => e.Row.ReminderId).Select(e => e.Row).ToList());
    }

    /// <summary>
    /// What one series' occurrence does to the accounts in scope: the amounts it places,
    /// nothing (it touches no account in scope), or why it cannot be placed.
    /// </summary>
    /// <param name="latest">The series' estimate at the latest rate
    /// (<see cref="RemindersRepository.LatestEstimatesAsync"/>) when it estimates.</param>
    private static SeriesPlan Plan(
        SeriesFacts series, IReadOnlyList<TemplateLeg> legs, UpcomingOccurrence occurrence,
        IReadOnlyDictionary<Guid, ScopeAccount> scope, IReadOnlySet<Guid> loanDriven,
        IReadOnlySet<Guid> loanAcross,
        (RemindersRepository.EstimateResolution Series, ReminderEstimate AtLatestRate)? latest)
    {
        var source = series.SourceAccountId;
        var sourceInScope = source is { } s && scope.ContainsKey(s);

        // No lines, no account: listed unless its source is known and out of scope.
        if (series.HasNoLines)
            return source is null || sourceInScope ? SeriesPlan.Exclude(Reasons.NoLines) : SeriesPlan.Nothing;

        var inScopeLegs = legs.Where(l => scope.ContainsKey(l.AccountId)).ToList();

        // A loan first, as the agenda ranks it: its amount is the computed full payment,
        // placed on the source. Its principal, interest and escrow lines take a split
        // priced as of each payment's own date, which this read does not compute, so a
        // reminder with one of them in scope is listed rather than half-placed.
        if (loanDriven.Contains(series.Id))
        {
            if (!sourceInScope && inScopeLegs.Count == 0) return SeriesPlan.Nothing;
            // The payment is in the loan's currency. A managed series whose paying account
            // or real escrow account is in another one can never post (ADR-0114 D13), by
            // the fires' own predicate, so it is still listed as cross-currency, whichever
            // of its accounts is in scope; and so is any other series whose loan line is in
            // another currency than its source, where the computed payment would land.
            var sourceCurrency = source is { } paying && scope.TryGetValue(paying, out var sourceAccount)
                ? sourceAccount.CurrencyCode
                : null;
            if (loanAcross.Contains(series.Id)
                || (sourceCurrency is not null
                    && legs.Any(l => l.AccountType == "loan"
                                     && !string.Equals(l.CurrencyCode, sourceCurrency, StringComparison.Ordinal))))
                return SeriesPlan.Exclude(Reasons.CrossCurrency);
            if (inScopeLegs.Any(l => l.AccountId != source)) return SeriesPlan.Exclude(Reasons.OverrideNotPlaceable);
            return SeriesPlan.Place([(source!.Value, occurrence.Amount)], isEstimate: false);
        }

        // An estimating reminder places its estimate at the latest rate, the same for every
        // occurrence: the source estimate, and on a transfer between currencies the other
        // side at the latest derived rate (ADR-0114 D13). With no estimate it places the
        // amount the agenda gives its occurrence, the one the Bills widget shows: the amount
        // entered, which the reminder uses until it has a history to estimate from. Either
        // way the figure rests on the series' estimate.
        if (occurrence.Estimate is not null)
        {
            if (inScopeLegs.Count == 0 && !sourceInScope) return SeriesPlan.Nothing;
            var estimate = latest?.AtLatestRate ?? occurrence.Estimate;

            // The other side needs the latest derived rate and a stored leg of it is
            // missing: listed, naming each, and nothing of it placed.
            if (string.Equals(estimate.UnavailableReason, RemindersRepository.EstimateUnavailable.RateUnavailable,
                    StringComparison.Ordinal))
                return SeriesPlan.Exclude(Reasons.RateUnavailable, estimate.RateUnavailable);

            if (estimate.Amount is not { } amount)
            {
                // No estimate yet: the template's lines as entered, the source taking the
                // agenda's amount, which is their net there.
                var entered = inScopeLegs
                    .Where(l => l.AccountId != source)
                    .Select(l => (l.AccountId, l.Amount))
                    .ToList();
                if (sourceInScope) entered.Add((source!.Value, occurrence.Amount));
                return SeriesPlan.Place(entered, isEstimate: true);
            }

            // An estimate: on the source line, and on the other its own estimate between
            // currencies or else the negation, the two amounts the post would write.
            var sourceLeg = legs.FirstOrDefault(l => l.AccountId == source);
            var otherLeg = legs.FirstOrDefault(l => l.AccountId != source);
            if (sourceLeg is null || otherLeg is null) return SeriesPlan.Exclude(Reasons.OverrideNotPlaceable);

            var effects = new List<(Guid, decimal)>();
            if (sourceInScope) effects.Add((sourceLeg.AccountId, amount));
            if (scope.ContainsKey(otherLeg.AccountId))
                effects.Add((otherLeg.AccountId, estimate.CounterpartyAmount ?? -amount));
            return SeriesPlan.Place(effects, isEstimate: true);
        }

        // Every other reminder: each template line on an account in scope, as the post
        // would write it to that account.
        return inScopeLegs.Count == 0
            ? SeriesPlan.Nothing
            : SeriesPlan.Place([.. inScopeLegs.Select(l => (l.AccountId, l.Amount))], isEstimate: false);
    }

    /// <summary>One account's balance at the end of each date.</summary>
    private static List<BalanceForecastPointDto> Project(
        IReadOnlyList<DateOnly> dates, decimal today, IReadOnlyList<CommittedRow> rows,
        SortedDictionary<DateOnly, decimal>? deltas)
    {
        var points = new List<BalanceForecastPointDto>(dates.Count);
        var registerBalance = today;
        var placed = 0m;
        var next = 0;
        foreach (var date in dates)
        {
            while (next < rows.Count && rows[next].Date <= date)
                registerBalance = rows[next++].BalanceAfter;
            if (deltas is not null && deltas.TryGetValue(date, out var delta)) placed += delta;
            points.Add(new BalanceForecastPointDto(date, registerBalance + placed));
        }
        return points;
    }

    /// <summary>
    /// The lowest balance over the points and the first date it is reached, estimated
    /// when an estimated amount landed on the figure's accounts on or before that date
    /// (<paramref name="firstEstimate"/>, the first such day, or null for none).
    /// </summary>
    private static BalanceForecastLowestDto LowestOf(IReadOnlyList<BalanceForecastPointDto> points, DateOnly? firstEstimate)
    {
        var lowest = points[0];
        foreach (var p in points)
            if (p.Balance < lowest.Balance) lowest = p;
        return new BalanceForecastLowestDto(lowest.Balance, lowest.Date, firstEstimate is { } day && day <= lowest.Date);
    }

    private sealed record ScopeAccount(Guid Id, string Name, string CurrencyCode);

    private sealed record CommittedRow(Guid AccountId, DateOnly Date, DateTime PostedAt, long Seq, decimal BalanceAfter);

    private sealed record SeriesFacts(Guid Id, Guid TemplateHeaderId, Guid? SourceAccountId, bool HasNoLines);

    private sealed record TemplateLeg(Guid HeaderId, Guid AccountId, decimal Amount, string AccountType, string CurrencyCode);

    /// <param name="RateUnavailable">On a <see cref="Reasons.RateUnavailable"/> exclusion,
    /// each missing stored leg; null otherwise.</param>
    private sealed record SeriesPlan(
        IReadOnlyList<(Guid Account, decimal Amount)> Effects, bool IsEstimate, string? Excluded,
        IReadOnlyList<RateUnavailable>? RateUnavailable = null)
    {
        public static readonly SeriesPlan Nothing = new([], false, null);

        public static SeriesPlan Exclude(string reason, IReadOnlyList<RateUnavailable>? rateUnavailable = null) =>
            new([], false, reason, rateUnavailable);

        public static SeriesPlan Place(IReadOnlyList<(Guid Account, decimal Amount)> effects, bool isEstimate) =>
            new(effects, isEstimate && effects.Count > 0, null);
    }

    /// <param name="FirstEstimate">Per account, the first day an estimated amount or its
    /// negation lands on it.</param>
    private sealed record Placement(
        IReadOnlyDictionary<Guid, SortedDictionary<DateOnly, decimal>> Deltas,
        IReadOnlyDictionary<Guid, DateOnly> FirstEstimate,
        int OverdueOnDay1,
        bool IncludesEstimates,
        IReadOnlyList<BalanceForecastExclusionDto> Excluded)
    {
        public static readonly Placement Empty = new(
            new Dictionary<Guid, SortedDictionary<DateOnly, decimal>>(), new Dictionary<Guid, DateOnly>(), 0, false, []);
    }
}
