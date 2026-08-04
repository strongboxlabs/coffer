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
/// per group (no time axis).</summary>
public enum ReportTimeBucket { None, Month, Quarter, Year }

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
/// </list>
/// </summary>
public enum ReportGroupBy { Category, Account, Payee }

/// <summary>
/// Parameters for a transaction-aggregation report. Shaped to anticipate the full
/// Moneydance-style settings surface (date range, filters) so canned + memorized
/// reports reuse it.
/// </summary>
public sealed class ReportSpec
{
    public required Guid LedgerId { get; init; }
    /// <summary>Inclusive lower bound (UTC). Null = open-ended.</summary>
    public DateTime? FromUtc { get; init; }
    /// <summary>Exclusive upper bound (UTC). Null = open-ended.</summary>
    public DateTime? ToUtc { get; init; }
    /// <summary>Restrict to transactions carrying this tag (ADR-0077). Null = any.</summary>
    public Guid? TagId { get; init; }
    public ReportMeasure Measure { get; init; } = ReportMeasure.Spending;
    public ReportTimeBucket TimeBucket { get; init; } = ReportTimeBucket.None;
    public ReportGroupBy GroupBy { get; init; } = ReportGroupBy.Category;
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
public sealed record ReportResult(IReadOnlyList<ReportRow> Rows, decimal Total);
