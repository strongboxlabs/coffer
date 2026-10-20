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
    /// <para>It needed its own dimension because the kind was COMPUTED and then
    /// thrown away. Every cell already carried it (the measure's allow-list is
    /// built on it), but <c>ReportRow</c> did not, so a client had no way to ask
    /// the question — and the first attempt shipped as "Cash flow" grouped by
    /// CATEGORY, which is net-per-category under a familiar name, and was
    /// withdrawn.</para>
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
    /// <summary>Restrict to transactions carrying this tag (ADR-0077). Null = any.</summary>
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
    /// <see cref="ReportResult.Total"/> stays the true sum of underlying postings
    /// — parent rows are subtotals, not to be re-summed.</summary>
    public bool Rollup { get; init; }
    /// <summary>Keep only the top-N groups by absolute total over the range (the
    /// time series is limited to those groups). Null = all.</summary>
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
    public (DateTime? FromUtc, DateTime? ToUtc) ResolveWindow(DateTime nowUtc)
    {
        if (DateMode != ReportDateMode.Sliding) return (FromUtc, ToUtc);

        if (WindowCount is not { } count || count < 1)
            throw new ReportRequestException(
                "A sliding date range needs a WindowCount of at least 1.");

        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var from = WindowAlignToPeriod
            ? StartOfPeriod(Shift(StartOfPeriod(now), -(count - 1)))
            : Shift(now, -count);
        return (from, now);
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
public sealed record ReportRow(
    string? Period, Guid? GroupId, string GroupName, Guid? ParentId, decimal Amount);

/// <summary>Aggregation result: the rows + the overall total of underlying
/// postings across them.</summary>
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
/// </remarks>
public sealed record ReportResult(
    IReadOnlyList<ReportRow> Rows,
    decimal Total,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null);

/// <summary>
/// A report request the reporting code refuses as asked: a daily bucket where only
/// monthly ones exist, a sliding window with no length, more points than a series carries.
/// Its message is written for the caller and is safe to return (FU-095).
/// </summary>
/// <remarks>
/// Its own type, so the endpoints catch exactly these. They caught ArgumentException and
/// InvalidOperationException, which the framework and EF also throw, with messages that
/// name internals; any of those reached the browser as a 400. Derives from
/// ArgumentException so a caller that already treats these as bad arguments still does.
/// </remarks>
public sealed class ReportRequestException(string message) : ArgumentException(message);
