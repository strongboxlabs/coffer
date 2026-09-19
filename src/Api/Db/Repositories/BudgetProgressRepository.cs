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
/// growing a parallel one. Until now it had no REST caller at all — the app
/// computed this and would only show it to an MCP client.</para>
///
/// <para><b>Two calls, both with <see cref="ReportTimeBucket.None"/>.</b> One
/// wider call bucketed by month would be one round trip instead of two, and is
/// deliberately not what this does. A bucketed call derives its period label
/// from SQL date parts evaluated in the DATABASE SESSION timezone, and nothing
/// in this repo sets one — it agrees with the C# UTC convention only because
/// postgres defaults to UTC. With <c>None</c> there is no label: the half-open
/// <c>[from, to)</c> bounds passed here define the window and the boundary is
/// unambiguous. Two round trips for a screen load is a cheap price for removing
/// a whole class of off-by-one-month bug.</para>
/// </remarks>
public sealed class BudgetProgressRepository
{
    private readonly ReportingRepository _reporting;
    private readonly AppDbContext _db;

    public BudgetProgressRepository(ReportingRepository reporting, AppDbContext db)
    {
        _reporting = reporting;
        _db = db;
    }

    /// <summary>
    /// The ledger's display currency, derived the same way the overview derives
    /// it — first distinct currency across the net-worth-relevant accounts, and
    /// a "mixed" flag when they disagree.
    ///
    /// <para>Deliberately NOT obtained by calling <see cref="OverviewRepository"/>,
    /// which would be the obvious reuse and is the wrong trade: it computes every
    /// account balance and prices the whole holdings book, and this needs a
    /// three-letter string. Duplicating a one-line derivation is cheaper than
    /// making the budget page cost as much as the dashboard.</para>
    /// </summary>
    private async Task<(string Code, bool Mixed)> DisplayCurrencyAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var codes = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && (AccountClassifier.AssetTypes.Contains(a.AccountType)
                            || AccountClassifier.LiabilityTypes.Contains(a.AccountType)))
            .Select(a => a.CurrencyCode)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (codes.Count > 0 ? codes[0] : "USD", codes.Count > 1);
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
    public async Task<BudgetProgressDto> GetAsync(
        Guid ledgerId,
        DateTime monthStartUtc,
        int windowMonths,
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

        // Divide by the WINDOW LENGTH, never by the number of months that
        // happened to return a cell. A single mattress bought once in three
        // months is a third of a mattress a month, not a mattress a month — and
        // dividing by "months with data" is exactly how a one-off becomes a
        // permanent expectation.
        // A NEGATIVE trailing total is real and has to be dropped, not scaled.
        // Refunds land as credits in an expense category, so a category whose
        // only recent activity was a return sums below zero. Treated as a
        // "normal" it is nonsense in both directions: you cannot overspend a
        // negative budget, and every month with any spending at all would show
        // as over. Null means "no usable history", which the UI already knows
        // how to render.
        var typicalByCategory = history.Rows
            .Where(r => r.GroupId is not null && r.Amount > 0m)
            .ToDictionary(r => r.GroupId!.Value, r => r.Amount / windowMonths);

        // Union both sides: a category can have spend this month and no history
        // (new), or history and no spend this month (stopped). Both are rows the
        // reader wants to see; only the empty intersection is uninteresting.
        var names = new Dictionary<Guid, (string Name, Guid? ParentId)>();
        foreach (var r in actual.Rows.Concat(history.Rows))
            if (r.GroupId is not null) names[r.GroupId.Value] = (r.GroupName, r.ParentId);

        var actualByCategory = actual.Rows
            .Where(r => r.GroupId is not null)
            .ToDictionary(r => r.GroupId!.Value, r => r.Amount);

        // ── Stored targets, and the D1a mark ────────────────────────────
        //
        // Two more round trips, deliberately, on top of the two summaries above.
        // The category tree is needed because a target's ancestors may not appear
        // in EITHER summary — a parent with no direct spend of its own is absent
        // from the leaf rows — and because a targeted category with no activity at
        // all still has to render as a row, or setting a target on a quiet
        // category would look like the write failed.
        var monthFirst = DateOnly.FromDateTime(monthStartUtc);
        var targets = await _db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId && t.TargetMonth == monthFirst)
            .ToDictionaryAsync(t => t.CategoryId, t => t.Amount, cancellationToken)
            .ConfigureAwait(false);

        var categoryTree = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "category")
            .Select(a => new { a.Id, a.ParentId, a.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var parentOf = categoryTree.ToDictionary(c => c.Id, c => c.ParentId);

        // A targeted category that neither spent this month nor has history is
        // still a row: the user typed a number against it and expects to see it.
        foreach (var c in categoryTree)
            if (targets.ContainsKey(c.Id) && !names.ContainsKey(c.Id))
                names[c.Id] = (c.Name, c.ParentId);

        var delta = MarkDeltaByAncestor(parentOf, targets, typicalByCategory);

        var rows = names
            .Select(kv => new BudgetCategoryRow(
                CategoryId: kv.Key,
                Name: kv.Value.Name,
                ParentId: kv.Value.ParentId,
                Actual: actualByCategory.TryGetValue(kv.Key, out var a) ? a : 0m,
                // Null, not zero, when there is no history. A new category
                // showing "typically 0" reads as infinitely over budget.
                Typical: typicalByCategory.TryGetValue(kv.Key, out var t) ? t : null,
                Target: targets.TryGetValue(kv.Key, out var tg) ? tg : null,
                Mark: MarkFor(kv.Key, targets, typicalByCategory, delta)))
            .OrderByDescending(r => r.Actual)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Totals come from the RESULT totals, not from summing the rows: with
        // Rollup the parent rows are subtotals and re-summing them double-counts
        // every descendant. ReportResult.Total is documented as the true sum of
        // the underlying postings for exactly this reason.
        var (currencyCode, mixedCurrency) =
            await DisplayCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // ── The two daily series the hero draws ──────────────────────────
        //
        // Rollup is deliberately OFF here, unlike the two summaries above. A
        // rolled-up daily series double-counts the moment the client sums
        // categories for the all-categories line: the parent cell already
        // contains its children's. Leaf cells sum cleanly and the client can
        // still filter to one category when the user focuses a row.
        var dailyActual = await DailyAsync(
            ledgerId, monthStartUtc, monthEndUtc, cancellationToken).ConfigureAwait(false);

        var dailyWindow = await DailyAsync(
            ledgerId, windowStartUtc, monthStartUtc, cancellationToken).ConfigureAwait(false);

        // Collapse the window's real dates onto a day-of-month profile, divided
        // by the window length — the same rule as `typical`, for the same
        // reason: divide by months that HAD spend and one big day becomes a
        // permanent feature of the shape.
        var normal = dailyWindow
            .GroupBy(c => (c.CategoryId, c.Day))
            .Select(g => new BudgetDailyCell(
                g.Key.CategoryId, g.Key.Day, g.Sum(c => c.Amount) / windowMonths))
            .ToList();

        var daysInMonth = DateTime.DaysInMonth(monthStartUtc.Year, monthStartUtc.Month);
        var nowUtc = DateTime.UtcNow;
        var elapsed =
            nowUtc >= monthEndUtc ? daysInMonth              // a past month is whole
            : nowUtc < monthStartUtc ? 0                     // a future month has not started
            : nowUtc.Day;                                    // the current month, so far

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
            TypicalTotal: history.Total <= 0m ? null : history.Total / windowMonths,
            CurrencyCode: currencyCode,
            MixedCurrency: mixedCurrency,
            DaysInMonth: daysInMonth,
            ElapsedDays: elapsed,
            DailyActual: dailyActual,
            DailyNormal: normal,
            LatestMonthWithSpending: latestWithSpending);
    }

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
            .Where(x => x.Period is not null && x.Amount > 0m)
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
            cells.Add(new BudgetDailyCell(row.GroupId.Value, day, row.Amount));
        }
        return cells;
    }
}
