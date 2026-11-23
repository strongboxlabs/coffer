using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Spend-versus-typical for one month, composed from the aggregation that
/// already exists.
/// </summary>
/// <remarks>
/// <para>This adds NO aggregation of its own. <see cref="ReportingRepository"/>
/// already computes spend by category with the right posting selection, sign
/// normalisation and tree rollup, and its own header says it exists so that
/// "BOTH the MCP tools and a future in-app Reports" share one engine rather than
/// growing a parallel one. This is its REST caller, so the app shows what it
/// computes, not only an MCP client.</para>
///
/// <para><b>Two calls, both with <see cref="ReportTimeBucket.None"/>.</b> One
/// wider call bucketed by month would be one round trip instead of two, and is
/// deliberately not what this does. A bucketed call derives its period label
/// from SQL date parts evaluated in the DATABASE SESSION timezone, which agrees
/// with the C# UTC convention only because the connection pins the session to
/// UTC (AppUserDbConnectionInterceptor). With <c>None</c> there is no label: the
/// half-open <c>[from, to)</c> bounds passed here define the window, and the
/// boundary does not depend on that pin. Two round trips for a screen load is a
/// cheap price for removing a whole class of off-by-one-month bug.</para>
///
/// <para><b>The means read the daily cells.</b> A ledger younger than the window
/// averages each day of the month over the window's months it already existed in on
/// that day (<see cref="MonthsAveragedByDay"/>), so every mean — a row's typical, the
/// total's, the daily profile — is taken from the same per-day leaf cells, over the
/// same [from, to) bounds. With the whole window the count is one number and each
/// mean is the window total over the window's length exactly, as before.</para>
/// </remarks>
public sealed class BudgetProgressRepository
{
    private readonly ReportingRepository _reporting;
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public BudgetProgressRepository(ReportingRepository reporting, AppDbContext db, TimeProvider clock)
    {
        _reporting = reporting;
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// How much each ANCESTOR's derived normal has to move to account for the typed
    /// targets beneath it (ADR-0099 D1a).
    /// </summary>
    /// <remarks>
    /// <para>The invariant buys the simplification that makes this one pass: because a
    /// target and its ancestors are mutually exclusive, no targeted node can have a
    /// targeted ancestor — so EVERY targeted descendant is a "nearest" one, and the
    /// deltas can simply be accumulated up each chain without working out which
    /// targets shadow which.</para>
    ///
    /// <para>The delta is <c>target − typical</c>, because the ancestor's rolled-up
    /// normal ALREADY contains the descendant's normal; substituting means removing
    /// what was counted and adding what was decided.</para>
    /// </remarks>
    private static Dictionary<Guid, decimal> MarkDeltaByAncestor(
        IReadOnlyDictionary<Guid, Guid?> parentOf,
        IReadOnlyDictionary<Guid, decimal> targets,
        IReadOnlyDictionary<Guid, decimal> typicalByCategory)
    {
        var delta = new Dictionary<Guid, decimal>();
        foreach (var (targetedId, amount) in targets)
        {
            var shift = amount - (typicalByCategory.TryGetValue(targetedId, out var t) ? t : 0m);
            if (shift == 0m) continue;

            var cur = parentOf.TryGetValue(targetedId, out var p) ? p : null;
            // Same depth guard as ReportingRepository's rollup: a parent_id cycle
            // should be impossible, but an infinite loop in a request is a worse
            // way to discover otherwise.
            for (var guard = 0; cur is { } node && guard < 100; guard++)
            {
                delta[node] = delta.TryGetValue(node, out var acc) ? acc + shift : shift;
                cur = parentOf.TryGetValue(node, out var next) ? next : null;
            }
        }
        return delta;
    }

    /// <summary>
    /// The authoritative mark for one category: its own typed target when it has
    /// one, otherwise its derived normal shifted by whatever is typed beneath it.
    /// Null only when there is genuinely nothing to compare against.
    /// </summary>
    private static decimal? MarkFor(
        Guid categoryId,
        IReadOnlyDictionary<Guid, decimal> targets,
        IReadOnlyDictionary<Guid, decimal> typicalByCategory,
        IReadOnlyDictionary<Guid, decimal> delta)
    {
        if (targets.TryGetValue(categoryId, out var typed)) return typed;

        var hasTypical = typicalByCategory.TryGetValue(categoryId, out var typical);
        var hasDelta = delta.TryGetValue(categoryId, out var shift);
        if (!hasTypical && !hasDelta) return null;

        // A category with no history of its own but a typed descendant marks at
        // that descendant's number, which (typical ?? 0) + shift gives directly.
        var mark = (hasTypical ? typical : 0m) + (hasDelta ? shift : 0m);
        return mark < 0m ? 0m : mark;
    }

    /// <summary>
    /// A category and every category beneath it.
    /// </summary>
    /// <remarks>
    /// The budget table is a rollup, so a row's number already contains its
    /// descendants. Listing only the row's DIRECT postings would show a list
    /// that does not add up to the figure the reader just clicked, which is the
    /// question they are asking. Walking here rather than in the client keeps
    /// it to one request per expansion; the walk itself is
    /// <see cref="CategoryTree.DescendantsAsync"/>, shared with the register's
    /// subtree scope.
    /// </remarks>
    public async Task<IReadOnlyCollection<Guid>> CategorySubtreeAsync(
        Guid ledgerId, Guid categoryId, CancellationToken cancellationToken = default) =>
        await CategoryTree
            .DescendantsAsync(_db, ledgerId, categoryId, includeRoot: true, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Default trailing window. Three months is long enough to absorb a
    /// fortnightly shop landing twice in one month and short enough that a rent
    /// rise stops looking anomalous within a quarter.</summary>
    public const int DefaultWindowMonths = 3;

    /// <param name="monthStartUtc">
    /// First instant of the reported month, UTC. The caller owns the calendar
    /// arithmetic so that the month the user picked and the month queried cannot
    /// disagree.
    /// </param>
    public Task<BudgetProgressDto> GetAsync(
        Guid ledgerId,
        DateTime monthStartUtc,
        int windowMonths,
        CancellationToken cancellationToken = default) =>
        GetAsync(ledgerId, monthStartUtc, windowMonths, today: null, cancellationToken);

    /// <inheritdoc cref="GetAsync(Guid, DateTime, int, CancellationToken)"/>
    /// <param name="today">The caller's local date (<see cref="ScheduledBoundary"/>),
    /// which places "day D of N" and splits off the scheduled rows; null for the
    /// register's fallback.</param>
    public async Task<BudgetProgressDto> GetAsync(
        Guid ledgerId,
        DateTime monthStartUtc,
        int windowMonths,
        DateOnly? today,
        CancellationToken cancellationToken = default)
    {
        if (windowMonths < 1) throw new ArgumentOutOfRangeException(nameof(windowMonths));

        var monthEndUtc = monthStartUtc.AddMonths(1);
        var windowStartUtc = monthStartUtc.AddMonths(-windowMonths);

        // The reported month.
        var actual = await _reporting.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            FromUtc = monthStartUtc,
            ToUtc = monthEndUtc,
            Measure = ReportMeasure.Spending,
            GroupBy = ReportGroupBy.Category,
            TimeBucket = ReportTimeBucket.None,
            Rollup = true,
        }, cancellationToken).ConfigureAwait(false);

        // The trailing window, EXCLUDING the reported month — comparing a month
        // against an average that contains it drags the average toward whatever
        // happened and shrinks every deviation.
        var history = await _reporting.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            FromUtc = windowStartUtc,
            ToUtc = monthStartUtc,
            Measure = ReportMeasure.Spending,
            GroupBy = ReportGroupBy.Category,
            TimeBucket = ReportTimeBucket.None,
            Rollup = true,
        }, cancellationToken).ConfigureAwait(false);

        // ── The months each day of the month averages over ───────────────
        //
        // Divide by the window's months the LEDGER EXISTED IN, never by the number
        // of months that happened to return a cell. A single mattress bought once
        // in three months is a third of a mattress a month, not a mattress a month —
        // and dividing by "months with data" is exactly how a one-off becomes a
        // permanent expectation. But a month before the ledger's first transaction
        // is not a month of no spending, it is no history at all: dividing by it
        // makes a young ledger's usual a fraction of what it spends. So each day of
        // the month divides by the window's months in which the ledger already
        // existed on that day (MonthsAveragedByDay), and every figure below that
        // averages the window uses the same count.
        var dailyWindow = await DailyAsync(
            ledgerId, windowStartUtc, monthStartUtc, cancellationToken).ConfigureAwait(false);

        var windowFirstDay = DateOnly.FromDateTime(windowStartUtc);
        var firstTransaction = await LedgerHistoryStart
            .FirstTransactionDayAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var monthsAveraged = MonthsAveragedByDay(firstTransaction, windowFirstDay, windowMonths);

        var categoryTree = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "category")
            .Select(a => new { a.Id, a.ParentId, a.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var parentOf = categoryTree.ToDictionary(c => c.Id, c => c.ParentId);

        // A NEGATIVE trailing mean is real and has to be dropped, not scaled.
        // Refunds land as credits in an expense category, so a category whose
        // only recent activity was a return sums below zero. Treated as a
        // "normal" it is nonsense in both directions: you cannot overspend a
        // negative budget, and every month with any spending at all would show
        // as over. Null means "no usable history", which the UI already knows
        // how to render.
        // A category whose window, or a descendant's, holds a day that needs a missing
        // rate has no usable mean: it is null, named, never a mean of what converted
        // (ADR-0114 D9). Rolled up the tree, as the means are.
        var missing = new Dictionary<Guid, List<RateUnavailable>>();
        void Miss(Guid category, IEnumerable<RateUnavailable> why)
        {
            var cur = (Guid?)category;
            for (var guard = 0; cur is { } node && guard < 100; guard++)
            {
                if (!missing.TryGetValue(node, out var list)) missing[node] = list = [];
                foreach (var w in why) if (!list.Contains(w)) list.Add(w);
                cur = parentOf.TryGetValue(node, out var p) ? p : null;
            }
        }
        foreach (var cell in dailyWindow.Where(c => c.RateUnavailable is not null))
            Miss(cell.CategoryId, cell.RateUnavailable!);
        var windowConverted = dailyWindow.Where(c => c.Amount is not null).ToList();

        var typicalByCategory = MeanByCategory(windowConverted, parentOf, monthsAveraged)
            .Where(kv => kv.Value > 0m && !missing.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Union both sides: a category can have spend this month and no history
        // (new), or history and no spend this month (stopped). Both are rows the
        // reader wants to see; only the empty intersection is uninteresting.
        var names = new Dictionary<Guid, (string Name, Guid? ParentId)>();
        foreach (var r in actual.Rows.Concat(history.Rows))
            if (r.GroupId is not null) names[r.GroupId.Value] = (r.GroupName, r.ParentId);

        var actualByCategory = actual.Rows
            .Where(r => r.GroupId is not null)
            .ToDictionary(r => r.GroupId!.Value, r => (r.Amount, r.RateUnavailable));
        var actualMissing = actual.Rows
            .Where(r => r.GroupId is not null && r.RateUnavailable is not null)
            .ToDictionary(r => r.GroupId!.Value, r => r.RateUnavailable!);

        // ── Stored targets, and the D1a mark ────────────────────────────
        //
        // Two more round trips, deliberately, on top of the two summaries above.
        // The category tree is needed because a target's ancestors may not appear
        // in EITHER summary — a parent with no direct spend of its own is absent
        // from the leaf rows — and because a targeted category with no activity at
        // all still has to render as a row, or setting a target on a quiet
        // category would look like the write failed.
        var monthFirst = DateOnly.FromDateTime(monthStartUtc);
        var typedTargets = await _db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId && t.TargetMonth == monthFirst)
            .Select(t => new { t.CategoryId, t.Amount, t.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A target keeps the currency it was typed in (ADR-0114 D12). One typed in
        // another than the reporting currency converts like any combined figure: a past
        // month at the rate for its last day, the current month (and a future one) at
        // the latest rate. A missing rate makes the target, and every mark built on it,
        // null and named.
        var reporting = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var localTodayForRates = ScheduledBoundary.Today(today, _clock);
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        var targetDay = monthFirst.AddMonths(1) <= localTodayForRates
            ? RateDay.Of(monthFirst.AddMonths(1).AddDays(-1), needs.ServerToday)
            : RateDay.Latest();
        foreach (var t in typedTargets) targetDay.Declare(needs, t.CurrencyCode, reporting);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        var targets = new Dictionary<Guid, decimal>();
        foreach (var t in typedTargets)
        {
            var rate = targetDay.Read(rates, t.CurrencyCode, reporting);
            if (rate.Unavailable is { } why) Miss(t.CategoryId, [why]);
            else targets[t.CategoryId] = ConvertedTotal.Round(t.Amount * rate.Rate!.Value);
        }
        // Every typed category, converted or not: a target that could not convert is
        // still a row, with its figures null and named.
        var targeted = typedTargets.Select(t => t.CategoryId).ToHashSet();
        var targetCurrency = typedTargets.ToDictionary(t => t.CategoryId, t => t.CurrencyCode);

        // A targeted category that neither spent this month nor has history is
        // still a row: the user typed a number against it and expects to see it.
        foreach (var c in categoryTree)
            if (targeted.Contains(c.Id) && !names.ContainsKey(c.Id))
                names[c.Id] = (c.Name, c.ParentId);

        var delta = MarkDeltaByAncestor(parentOf, targets, typicalByCategory);

        var rows = names
            .Select(kv =>
            {
                var why = RateReasons.Of(
                    actualMissing.GetValueOrDefault(kv.Key) ?? [],
                    missing.GetValueOrDefault(kv.Key) ?? []);
                return new BudgetCategoryRow(
                    CategoryId: kv.Key,
                    Name: kv.Value.Name,
                    ParentId: kv.Value.ParentId,
                    Actual: actualByCategory.TryGetValue(kv.Key, out var a) ? a.Amount : 0m,
                    // Null, not zero, when there is no history. A new category
                    // showing "typically 0" reads as infinitely over budget.
                    Typical: typicalByCategory.TryGetValue(kv.Key, out var t) ? t : null,
                    Target: targets.TryGetValue(kv.Key, out var tg) ? tg : null,
                    // Stored or not, whether or not it converted: the client cannot
                    // tell an unconverted target from none by Target alone.
                    TargetCurrencyCode: targetCurrency.GetValueOrDefault(kv.Key),
                    // A mark built on a figure that could not convert has no value.
                    Mark: missing.ContainsKey(kv.Key) ? null : MarkFor(kv.Key, targets, typicalByCategory, delta),
                    RateUnavailable: why.Count > 0 ? why : null);
            })
            .OrderByDescending(r => r.Actual ?? 0m)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.CategoryId)
            .ToList();

        // Totals come from the RESULT totals, not from summing the rows: with
        // Rollup the parent rows are subtotals and re-summing them double-counts
        // every descendant. ReportResult.Total is documented as the true sum of
        // the underlying postings for exactly this reason.

        // ── The two daily series the hero draws ──────────────────────────
        //
        // Rollup is deliberately OFF here, unlike the two summaries above. A
        // rolled-up daily series double-counts the moment the client sums
        // categories for the all-categories line: the parent cell already
        // contains its children's. Leaf cells sum cleanly and the client can
        // still filter to one category when the user focuses a row.
        var dailyActual = await DailyAsync(
            ledgerId, monthStartUtc, monthEndUtc, cancellationToken).ConfigureAwait(false);

        // Collapse the window's real dates onto a day-of-month profile, each day
        // divided by the months it averages over — the same rule as `typical`, for
        // the same reason: divide by months that HAD spend and one big day becomes
        // a permanent feature of the shape.
        var normal = dailyWindow
            .GroupBy(c => (c.CategoryId, c.Day))
            .Select(g =>
            {
                var why = RateReasons.Of([.. g.Where(c => c.RateUnavailable is not null).Select(c => c.RateUnavailable!)]);
                return why.Count > 0
                    ? new BudgetDailyCell(g.Key.CategoryId, g.Key.Day, null, why)
                    : new BudgetDailyCell(
                        g.Key.CategoryId, g.Key.Day, g.Sum(c => c.Amount!.Value) / monthsAveraged[g.Key.Day]);
            })
            .ToList();

        // The ledger-wide usual, on the same footing as Total: the leaf cells
        // before any rollup, so no subtotal is counted twice. Null when any of them
        // needs a missing rate.
        var windowWhy = RateReasons.Of([.. dailyWindow.Where(c => c.RateUnavailable is not null).Select(c => c.RateUnavailable!)]);
        decimal? typicalTotal = windowWhy.Count > 0 ? null : MeanOf(windowConverted, monthsAveraged);

        var daysInMonth = DateTime.DaysInMonth(monthStartUtc.Year, monthStartUtc.Month);
        var localToday = ScheduledBoundary.Today(today, _clock);
        var monthFirstDay = DateOnly.FromDateTime(monthStartUtc);
        var elapsed =
            localToday >= monthFirstDay.AddMonths(1) ? daysInMonth   // a past month is whole
            : localToday < monthFirstDay ? 0                         // a future month has not started
            : localToday.Day;                                        // the current month, so far

        // The month stays whole (ADR-0109 D10; ADR-0099's 2026-10-31 amendment): the
        // actual counts the rows the register shows as Scheduled. This is their part
        // of it, from the same daily cells, so no reader subtracts one figure from
        // another to find it:
        // every day after the caller's today, which is all of a future month and
        // none of a past one.
        var scheduledCells = dailyActual
            .Where(c => monthFirstDay.AddDays(c.Day - 1) > localToday)
            .ToList();
        var scheduledWhy = RateReasons.Of([.. scheduledCells.Where(c => c.RateUnavailable is not null).Select(c => c.RateUnavailable!)]);
        decimal? scheduledTotal = scheduledWhy.Count > 0 ? null : scheduledCells.Sum(c => c.Amount!.Value);

        // Only asked when the month is empty — see the DTO. Bounded to two
        // years so an empty FUTURE month does not scan all history.
        string? latestWithSpending = null;
        if (actual.Total == 0m)
            latestWithSpending = await LatestMonthWithSpendingAsync(
                ledgerId, monthEndUtc, cancellationToken).ConfigureAwait(false);

        return new BudgetProgressDto(
            Month: monthStartUtc.ToString("yyyy-MM"),
            WindowMonths: windowMonths,
            Rows: rows,
            ActualTotal: actual.Total,
            TypicalTotal: typicalTotal is { } tt && tt > 0m ? tt : null,
            CurrencyCode: reporting,
            DaysInMonth: daysInMonth,
            ElapsedDays: elapsed,
            DailyActual: dailyActual,
            DailyNormal: normal,
            LatestMonthWithSpending: latestWithSpending,
            ScheduledTotal: scheduledTotal,
            // Only when the ledger began inside the window (or later): the labels
            // that name the window then say what was averaged instead.
            HistoryFrom: firstTransaction is { } first && first > windowFirstDay ? first : null,
            RateUnavailable: RateReasons.Of(actual.RateUnavailable ?? [], windowWhy, scheduledWhy) is { Count: > 0 } all
                ? all
                : null);
    }

    /// <summary>
    /// How many of the window's months each day of the month averages over: the
    /// months in which the ledger already existed on that day, counting from its
    /// first transaction of any kind, at least one and at most the window's length.
    /// Indexed by day of the month, 1 to 31; index 0 is unused.
    /// </summary>
    /// <remarks>
    /// <para>A ledger with no transactions averages the whole window: it has nothing
    /// to average either way, and its labels name the whole window.</para>
    /// <para>A day a window month does not have (the 31st of a 30-day month) is read
    /// as that month's last day, so a month the ledger existed in by its end counts
    /// for it. With the whole window every month counts for every day, which is the
    /// window's length; this keeps a young ledger on the same footing.</para>
    /// <para>At least one, because a day of the month that no window month reached
    /// has no spend to divide; the count only has to be a number.</para>
    /// </remarks>
    internal static int[] MonthsAveragedByDay(DateOnly? firstTransaction, DateOnly windowFirstDay, int windowMonths)
    {
        var byDay = new int[32];
        for (var day = 1; day <= 31; day++)
        {
            var months = 0;
            for (var k = 0; k < windowMonths; k++)
            {
                var month = windowFirstDay.AddMonths(k);
                var date = new DateOnly(
                    month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)));
                if (firstTransaction is not { } first || first <= date) months++;
            }
            byDay[day] = Math.Max(months, 1);
        }
        return byDay;
    }

    /// <summary>
    /// The mean month of every category over the window: each leaf cell over the
    /// months its day averages, rolled up the category tree as the summaries roll
    /// their rows up, so a parent's mean is its subtree's.
    /// </summary>
    /// <remarks>
    /// The cells are summed per category and count first and divided once per
    /// count, so with the whole window, one count for every day, a category's mean
    /// is its window total over the window's length exactly.
    /// </remarks>
    private static Dictionary<Guid, decimal> MeanByCategory(
        IEnumerable<BudgetDailyCell> windowCells,
        IReadOnlyDictionary<Guid, Guid?> parentOf,
        IReadOnlyList<int> monthsAveraged)
    {
        var sums = new Dictionary<(Guid Category, int Months), decimal>();
        foreach (var cell in windowCells)
        {
            var months = monthsAveraged[cell.Day];
            var cur = (Guid?)cell.CategoryId;
            // The rollup's depth guard: a parent_id cycle should be impossible.
            for (var guard = 0; cur is { } node && guard < 100; guard++)
            {
                var key = (node, months);
                // Only converted cells reach here (the caller drops the rest).
                var amount = cell.Amount!.Value;
                sums[key] = sums.TryGetValue(key, out var acc) ? acc + amount : amount;
                cur = parentOf.TryGetValue(node, out var p) ? p : null;
            }
        }
        return sums
            .GroupBy(kv => kv.Key.Category)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value / kv.Key.Months));
    }

    /// <summary>The mean month of every leaf cell together: <see cref="MeanByCategory"/>
    /// without the tree.</summary>
    private static decimal MeanOf(IEnumerable<BudgetDailyCell> windowCells, IReadOnlyList<int> monthsAveraged) =>
        windowCells
            .GroupBy(c => monthsAveraged[c.Day])
            .Sum(g => g.Sum(c => c.Amount!.Value) / g.Key);

    /// <summary>
    /// The latest month at or before <paramref name="beforeUtc"/> with any
    /// spending, or null. Reuses the aggregation rather than querying the
    /// tables directly, so "has spending" means exactly what it means
    /// everywhere else on this screen.
    /// </summary>
    private async Task<string?> LatestMonthWithSpendingAsync(
        Guid ledgerId, DateTime beforeUtc, CancellationToken cancellationToken)
    {
        var result = await _reporting.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            FromUtc = beforeUtc.AddMonths(-24),
            ToUtc = beforeUtc,
            Measure = ReportMeasure.Spending,
            GroupBy = ReportGroupBy.Category,
            TimeBucket = ReportTimeBucket.Month,
        }, cancellationToken).ConfigureAwait(false);

        // Periods are "yyyy-MM", so ordinal comparison IS chronological order.
        return result.Rows
            // A month whose spending needs a missing rate still has spending.
            .Where(x => x.Period is not null && (x.Amount > 0m || x.RateUnavailable is not null))
            .Select(x => x.Period!)
            .DefaultIfEmpty(null!)
            .Max();
    }

    /// <summary>
    /// Per-category, per-day spend over a half-open window, as leaf cells.
    /// </summary>
    /// <remarks>
    /// The day comes from the period label the aggregation produces
    /// (<c>yyyy-MM-dd</c>), which is trustworthy only because the session
    /// timezone is pinned to UTC — Postgres extracts date parts from a
    /// timestamptz in the session zone, so an unpinned session would file a
    /// late-evening transaction under tomorrow for anyone west of UTC. See
    /// AppUserDbConnectionInterceptor.
    /// </remarks>
    private async Task<List<BudgetDailyCell>> DailyAsync(
        Guid ledgerId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var result = await _reporting.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Measure = ReportMeasure.Spending,
            GroupBy = ReportGroupBy.Category,
            TimeBucket = ReportTimeBucket.Day,
            Rollup = false,
        }, cancellationToken).ConfigureAwait(false);

        var cells = new List<BudgetDailyCell>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            if (row.GroupId is null || row.Period is null) continue;
            // "yyyy-MM-dd" — the day is the last two characters.
            if (!int.TryParse(row.Period.AsSpan(8, 2), out var day)) continue;
            cells.Add(new BudgetDailyCell(row.GroupId.Value, day, row.Amount, row.RateUnavailable));
        }
        return cells;
    }
}
