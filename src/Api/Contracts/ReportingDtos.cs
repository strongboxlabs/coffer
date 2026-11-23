using System.Text.Json.Serialization;

namespace Coffer.Api.Contracts;

/// <summary>
/// Reusable reporting layer (ADR-0063 + follow-ups "Reporting"). A serializable
/// report spec + result that BOTH the MCP tools and a future in-app Reports /
/// "memorized reports" feature consume — one aggregation engine, not a parallel
/// one. v2 generalizes the dimension (category / account / payee) and adds
/// category-tree rollup; balances/investment reports are separate repositories.
/// </summary>
public enum ReportMeasure
{
    /// <summary>Outflows to expense-category accounts (positive magnitude).</summary>
    Spending,
    /// <summary>Inflows to income-category accounts (positive magnitude).</summary>
    Income,
    /// <summary>Income − spending (net savings); income rows positive, expense negative.</summary>
    Net,
}

/// <summary>Time granularity for the series. <see cref="None"/> = a single total
/// per group (no time axis).
/// <para><see cref="Day"/> is supported for the CATEGORY dimension only — it is
/// there to feed a daily spending chart, and adding the day to the SQL grouping
/// key multiplies the row count by up to 31 for every caller that does not want
/// it. Asking for a daily series by account or payee throws rather than
/// silently returning monthly buckets.</para>
/// <para>Day buckets are only trustworthy because the session timezone is
/// pinned to UTC (see AppUserDbConnectionInterceptor): Postgres extracts date
/// parts from a timestamptz in the session zone, so an unpinned session would
/// put a late-evening transaction on the wrong DAY for anyone west of UTC —
/// visible every day, unlike the month-boundary version of the same bug.</para>
/// </summary>
public enum ReportTimeBucket { None, Month, Quarter, Year, Day }

/// <summary>
/// The dimension a transaction summary groups by. The measure (spending/income/
/// net) always selects category-leg postings by kind; <see cref="ReportGroupBy"/>
/// only changes the grouping key:
/// <list type="bullet">
///   <item><see cref="Category"/> — the expense/income category (the default;
///   nests via parent_id, so <see cref="ReportSpec.Rollup"/> applies here).</item>
///   <item><see cref="Account"/> — the real account paired with the categorized
///   posting (the cash/credit-card account that "paid"), via the view's
///   counterparty. Real accounts are flat (no parent).</item>
///   <item><see cref="Payee"/> — the transaction payee text.</item>
///   <item><see cref="CategoryKind"/> — income vs expense vs adjustment, which
///   is the cash-flow question.</item>
/// </list>
/// </summary>
public enum ReportGroupBy
{
    Category,
    Account,
    Payee,
    /// <summary>
    /// One row per tag, spanning BOTH levels (ADR-0103). A posting is counted
    /// under its header's tags and its own leg's tags, so a transaction can
    /// appear under more than one row and the column total can exceed the same
    /// period's spending total. That is correct and has to be said on the
    /// report, or it reads as a bug.
    /// </summary>
    Tag,

    /// <summary>
    /// One row per category KIND — income, expense, adjustment — which is what
    /// a cash-flow report is: money in against money out, per period.
    /// </summary>
    /// <remarks>
    /// <para>It is its own dimension because <c>ReportRow</c> carries no kind.
    /// Every cell computes it (the measure's allow-list is built on it), and
    /// grouping by it is the only way a client can ask the question. "Cash flow" grouped by CATEGORY instead is net-per-category
    /// under a familiar name.</para>
    ///
    /// <para><see cref="ReportSpec.Rollup"/> does not apply: a kind has no
    /// parent, so there is nothing to roll up into.</para>
    /// </remarks>
    CategoryKind,
}

/// <summary>
/// How a report's date range is expressed (ADR-0104 D5).
/// </summary>
public enum ReportDateMode
{
    /// <summary>Fixed bounds — <see cref="ReportSpec.FromUtc"/> / <see cref="ReportSpec.ToUtc"/>.</summary>
    Static,

    /// <summary>
    /// A window measured back from the moment the report RUNS, resolved at
    /// query time and never at save time. "Groceries, 12 months" means the last
    /// twelve from today; a spec that froze Oct–Sep would return a
    /// progressively staler answer every month while looking like it worked.
    /// </summary>
    Sliding,
}

/// <summary>The unit a sliding window counts in.</summary>
public enum ReportWindowUnit { Day, Month, Quarter, Year }

/// <summary>
/// Parameters for a transaction-aggregation report. Shaped to anticipate the full
/// Moneydance-style settings surface (date range, filters) so canned + memorized
/// reports reuse it.
/// </summary>
public sealed class ReportSpec
{
    public required Guid LedgerId { get; init; }
    /// <summary>Inclusive lower bound (UTC). Null = open-ended. Ignored when
    /// <see cref="DateMode"/> is <see cref="ReportDateMode.Sliding"/>.</summary>
    public DateTime? FromUtc { get; init; }
    /// <summary>Exclusive upper bound (UTC). Null = open-ended. Ignored when
    /// <see cref="DateMode"/> is <see cref="ReportDateMode.Sliding"/>.</summary>
    public DateTime? ToUtc { get; init; }
    /// <summary>Restrict to postings this tag counts for (ADR-0077), as the paying account's register shows them. Null = any.</summary>
    public Guid? TagId { get; init; }
    public ReportMeasure Measure { get; init; } = ReportMeasure.Spending;
    public ReportTimeBucket TimeBucket { get; init; } = ReportTimeBucket.None;
    public ReportGroupBy GroupBy { get; init; } = ReportGroupBy.Category;

    /// <summary>
    /// Static by default, so every caller written before ADR-0104 keeps the
    /// behaviour it had and a saved row with no mode reads as the fixed range
    /// it was written with.
    /// </summary>
    public ReportDateMode DateMode { get; init; } = ReportDateMode.Static;

    /// <summary>How many <see cref="WindowUnit"/>s the sliding window spans.
    /// Required (and >= 1) when <see cref="DateMode"/> is Sliding.</summary>
    public int? WindowCount { get; init; }

    public ReportWindowUnit WindowUnit { get; init; } = ReportWindowUnit.Month;

    /// <summary>
    /// Snap the window's start to a period boundary. False gives a rolling
    /// window — exactly N units back from this instant. True gives the
    /// calendar reading: "12 months" becomes the start of the month 11 back
    /// through now, which is the one that makes a monthly series show twelve
    /// whole columns instead of thirteen partial ones.
    /// </summary>
    public bool WindowAlignToPeriod { get; init; }

    /// <summary>Category-tree rollup: when true (and <see cref="GroupBy"/> is
    /// <see cref="ReportGroupBy.Category"/>), each parent category's amount
    /// includes all its descendants', and rows carry the full set of ancestor
    /// nodes. Ignored for the flat account/payee dimensions. The result
    /// <see cref="ReportResult.Total"/> stays the sum of the cells before rollup
    /// (for every dimension but Tag, the true sum of underlying postings) —
    /// parent rows are subtotals, not to be re-summed.</summary>
    public bool Rollup { get; init; }
    /// <summary>Keep only the top-N groups by absolute total over the range (the
    /// time series is limited to those groups). Null = all. With a cut (a value of
    /// 1 or more), every row carries <see cref="ReportRow.Drawn"/> and the result
    /// carries <see cref="ReportResult.Rest"/>, so a chart can draw the kept groups
    /// and one series for everything else without summing rows.</summary>
    public int? TopN { get; init; }

    /// <summary>
    /// The bounds this spec means at <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// Resolution happens HERE, at query time, and not in the client before a
    /// save. Resolving at save time is the same defect as a frozen range one
    /// layer up: the row would record absolute dates and there would be no way
    /// left to tell a window the author wanted to slide from one they wanted
    /// fixed.
    /// </remarks>
    public (DateTime? FromUtc, DateTime? ToUtc) ResolveWindow(DateTime nowUtc) =>
        ResolveWindow(nowUtc, nowUtc);

    /// <summary>
    /// The bounds this spec means when the window ends at <paramref name="endUtc"/>
    /// and its current period is the one holding <paramref name="snapAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// Two instants because a caller's day ends at an exclusive boundary: the
    /// window ends AT the boundary (the caller's tomorrow at midnight) and a
    /// rolling window counts back from it, while an aligned window snaps from the
    /// last instant BEFORE it. Snapping from the boundary itself would put an
    /// aligned window a period late on a period's last day, and every day for
    /// <see cref="ReportWindowUnit.Day"/>.
    /// </remarks>
    public (DateTime? FromUtc, DateTime? ToUtc) ResolveWindow(DateTime snapAtUtc, DateTime endUtc)
    {
        if (DateMode != ReportDateMode.Sliding) return (FromUtc, ToUtc);

        if (WindowCount is not { } count || count < 1)
            throw new ReportRequestException(
                "A sliding date range needs a WindowCount of at least 1.");

        var snapAt = DateTime.SpecifyKind(snapAtUtc, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc);
        var from = WindowAlignToPeriod
            ? StartOfPeriod(Shift(StartOfPeriod(snapAt), -(count - 1)))
            : Shift(end, -count);
        return (from, end);
    }

    private DateTime Shift(DateTime at, int units) => WindowUnit switch
    {
        ReportWindowUnit.Day => at.AddDays(units),
        ReportWindowUnit.Month => at.AddMonths(units),
        ReportWindowUnit.Quarter => at.AddMonths(units * 3),
        _ => at.AddYears(units),
    };

    private DateTime StartOfPeriod(DateTime at) => WindowUnit switch
    {
        ReportWindowUnit.Day => new DateTime(at.Year, at.Month, at.Day, 0, 0, 0, DateTimeKind.Utc),
        ReportWindowUnit.Month => new DateTime(at.Year, at.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        ReportWindowUnit.Quarter => new DateTime(at.Year, ((at.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(at.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}

/// <summary>One aggregated cell: a (period?, group) bucket with its total.
/// <see cref="Period"/> is null when <see cref="ReportSpec.TimeBucket"/> is None;
/// otherwise an ISO-ish label (<c>2026-05</c>, <c>2026-Q2</c>, <c>2026</c>).
/// <see cref="GroupId"/> is the category/account id, or null for the payee
/// dimension. <see cref="ParentId"/> is the category's parent (null for roots /
/// the flat dimensions).</summary>
/// <param name="Drawn">
/// Set only when the spec has a top-N cut; null, and absent from the JSON, without
/// one. True on the rows a chart draws. Drawn rows never overlap, so with
/// <see cref="ReportResult.Rest"/> they come to each period's total. Without
/// rollup every kept row is drawn. Under rollup a kept row holds its whole
/// subtree, so it is drawn only when no kept row lies below it; otherwise its
/// kept descendants and its <see cref="IsDirect"/> rows draw it.
/// </param>
/// <param name="IsDirect">
/// True on a row holding a rolled-up kept parent's own postings, added by a
/// top-N cut when a kept row lies below the parent: the parent's subtree row is
/// then not drawn, and without these rows its own postings would be drawn
/// nowhere. One per period the parent has postings in, with the parent's
/// <see cref="GroupId"/>, name and <see cref="ParentId"/>, and always drawn.
/// Absent from the JSON when false.
/// </param>
/// <param name="Amount">In <see cref="CurrencyCode"/>. A row grouped by account is a
/// per-account figure, so it stays in that account's own currency, unconverted, and never
/// needs a rate. Every other row combines accounts, so it is in the ledger's reporting
/// currency: each posting converted at its own date's rate, summed, rounded once
/// (ADR-0114 D7), and null when a posting needs a missing rate (ADR-0114 D9).</param>
/// <param name="RateUnavailable">Every missing rate <see cref="Amount"/> needed; absent
/// from the JSON when none is.</param>
/// <param name="CurrencyCode">The currency <see cref="Amount"/> is in: the account's own
/// for a row grouped by account, else the ledger's reporting currency. Set on every row a
/// summary returns.</param>
/// <param name="GroupPath">The group's full name, as a list (ADR-0104 amendment
/// 2026-11-20): for a category, each name from its top-level category down to its own
/// (<c>["Salary", "Base"]</c>); for every other dimension, the one name. Set on every
/// row a summary returns. A list rather than a joined string, so a name that contains the
/// separator cannot be split wrongly; the client chooses whether to show the leaf or the
/// whole path. <see cref="GroupName"/> stays the leaf.</param>
public sealed record ReportRow(
    string? Period, Guid? GroupId, string GroupName, Guid? ParentId, decimal? Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Drawn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsDirect = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RateUnavailable>? RateUnavailable = null,
    string? CurrencyCode = null,
    IReadOnlyList<string>? GroupPath = null);

/// <summary>Aggregation result: the rows, the overall total, and the totals per
/// period and per group. For every dimension but Tag the total is the sum of the
/// underlying postings; a Tag result counts a posting once per tag it carries, so
/// its total (and each period's) is the sum of the tag rows.</summary>
/// <param name="FromUtc">
/// The RESOLVED lower bound this result covers — the sliding window worked out
/// against the instant it ran, not the spec's literal field. Null when the
/// range was open-ended.
/// </param>
/// <param name="ToUtc">
/// The resolved exclusive upper bound. Null when open-ended.
/// </param>
/// <remarks>
/// The bounds are here so a caller can draw the whole axis. A period with no
/// postings produces no row, so a client that took its columns from the rows
/// alone silently dropped empty months: a gap read as no column rather than as
/// zero, and adjacent columns then looked like adjacent months. Only the
/// server knows where a sliding window landed, so only the server can say how
/// many columns the answer has.
/// <para><see cref="PeriodTotals"/> and <see cref="GroupTotals"/> are taken
/// before top-N, like <see cref="Total"/>, so a chart's column heights and a
/// legend's figures never come from re-summing rows that overlap (rollup) or
/// were cut (top-N). Period totals are also taken before rollup; group totals
/// after it, so a parent's figure is its subtree's, as its rows are.</para>
/// <para><see cref="Rest"/> is set only when the spec has a top-N cut, and is
/// absent from the JSON otherwise.</para>
/// </remarks>
/// <param name="Total">Null when a posting needs a missing rate; <see cref="RateUnavailable"/>
/// names every one.</param>
public sealed record ReportResult(
    IReadOnlyList<ReportRow> Rows,
    decimal? Total,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    IReadOnlyList<ReportPeriodTotal>? PeriodTotals = null,
    IReadOnlyList<ReportGroupTotal>? GroupTotals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReportRest? Rest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>One period's total, labelled as <see cref="ReportRow.Period"/> is.
/// Every period the window spans when it has both bounds, zero where nothing
/// posted; empty for a result with no time bucket. Null, with
/// <see cref="RateUnavailable"/>, when a posting in it needs a missing rate
/// (ADR-0114 D9).</summary>
public sealed record ReportPeriodTotal(
    string Period, decimal? Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>
/// What a top-N cut leaves undrawn: the postings no <see cref="ReportRow.Drawn"/>
/// row holds, which a chart shows as one series.
/// </summary>
/// <remarks>
/// For every period, the drawn rows plus <see cref="ByPeriod"/> equal
/// <see cref="ReportResult.PeriodTotals"/>, with rollup and without. The rest is
/// summed from the undrawn postings themselves rather than taken as the period
/// total less the drawn rows, so that equality is a check that drawn rows and
/// the rest partition the money, not arithmetic that holds whatever is drawn.
/// For Tag it is on the same footing as the totals: sums of tag cells, so a
/// posting with two tags counts under each.
/// <para>Its figures combine groups, so they are in the reporting currency, converted
/// (ADR-0114 D7), even where the rows are accounts kept in their own currencies; a
/// figure that needs a missing rate is null, named in <see cref="RateUnavailable"/>
/// (ADR-0114 D9). The partition above holds in the reporting currency: a row of an
/// account in another currency counts at its converted value.</para>
/// </remarks>
/// <param name="Count">
/// How many series the rest folds together, at the level the drawn rows are
/// drawn at: without rollup, each group the cut dropped. Under rollup, each
/// category whose whole subtree is in the rest, counted once at the highest such
/// category, so a dropped parent and its children are one series; and each
/// category that is not kept but has a kept row below it, whose own postings are
/// in the rest. Zero when nothing is left out.
/// </param>
/// <param name="Total">The rest across the window; null when it needs a missing rate.</param>
/// <param name="ByPeriod">
/// The rest per period, one entry for each of <see cref="ReportResult.PeriodTotals"/>
/// in the same order, zero where everything was drawn. Empty with no time bucket.
/// </param>
/// <param name="Only">
/// The one series the rest holds, set only when <see cref="Count"/> is 1 and absent
/// from the JSON otherwise, so a chart can name it. Under rollup the browser cannot
/// work out which group that is.
/// </param>
public sealed record ReportRest(
    int Count,
    decimal? Total,
    IReadOnlyList<ReportPeriodTotal>? ByPeriod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReportRestGroup? Only = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>The group that names a rest holding one series.</summary>
/// <param name="GroupId">The group's id, as <see cref="ReportRow.GroupId"/> carries it; null where that is.</param>
/// <param name="GroupName">The group's name.</param>
/// <param name="IsDirect">
/// True when the series is the own postings of a category that is not kept but has
/// a kept row below it. False when it is a whole group, or under rollup a dropped
/// subtree, named at its highest category.
/// </param>
/// <param name="GroupPath">The group's full name, as <see cref="ReportRow.GroupPath"/> carries it.</param>
public sealed record ReportRestGroup(
    Guid? GroupId, string GroupName, bool IsDirect, IReadOnlyList<string>? GroupPath = null);

/// <summary>One group's total across the window, whether or not top-N kept its
/// rows. <see cref="GroupId"/> is null where <see cref="ReportRow.GroupId"/> is. In
/// <see cref="CurrencyCode"/>, as the group's rows are: the account's own currency for a
/// group by account, else the reporting currency (ADR-0114 D7). Set on every group total
/// a summary returns, as is <see cref="GroupPath"/>, the group's full name as
/// <see cref="ReportRow.GroupPath"/> carries it.</summary>
public sealed record ReportGroupTotal(
    Guid? GroupId, string GroupName, decimal? Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RateUnavailable>? RateUnavailable = null,
    string? CurrencyCode = null,
    IReadOnlyList<string>? GroupPath = null);

/// <summary>
/// A summary report as the app receives it: the result, and the ledger's reporting
/// currency, which every figure that combines accounts is in (ADR-0114 D7).
/// </summary>
/// <remarks>
/// The endpoint and the MCP tool both build this through
/// <see cref="Coffer.Api.Db.Repositories.ReportingRepository.SummaryAsync"/>. The total,
/// period totals and rest are converted, and so are the rows and group totals of every
/// dimension but Account: a row or group total by account is a per-account figure, so it
/// stays in the account's own currency, named on it. A converted figure that needs a
/// missing rate is null and names it (ADR-0114 D9), on the row or total that carries it.
/// </remarks>
public sealed record ReportSummaryDto(
    IReadOnlyList<ReportRow> Rows,
    decimal? Total,
    DateTime? FromUtc,
    DateTime? ToUtc,
    IReadOnlyList<ReportPeriodTotal> PeriodTotals,
    IReadOnlyList<ReportGroupTotal> GroupTotals,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReportRest? Rest,
    string CurrencyCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RateUnavailable>? RateUnavailable = null)
{
    public static ReportSummaryDto From(ReportResult result, string currencyCode)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ReportSummaryDto(
            result.Rows, result.Total, result.FromUtc, result.ToUtc,
            result.PeriodTotals ?? [], result.GroupTotals ?? [], result.Rest, currencyCode,
            result.RateUnavailable);
    }
}

/// <summary>
/// A report request the reporting code refuses as asked: a daily bucket where only
/// monthly ones exist, a sliding window with no length, more points than a series carries.
/// Its message is written for the caller and is safe to return (FU-095).
/// </summary>
/// <remarks>
/// Its own type, so the endpoints catch exactly these. Catching ArgumentException and
/// InvalidOperationException, which the framework and EF also throw with messages that
/// name internals, would send any of those to the browser as a 400. Derives from
/// ArgumentException so a caller that already treats these as bad arguments still does.
/// </remarks>
public sealed class ReportRequestException(string message) : ArgumentException(message);
