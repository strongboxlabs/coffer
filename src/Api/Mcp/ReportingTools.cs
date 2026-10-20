using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP tools over the shared reporting layer (ADR-0063 §D5). Read-only: each
/// tool maps a model request onto a <see cref="ReportSpec"/> and returns the
/// deterministic aggregation — the financial math runs here, never in the LLM
/// (ADR-0063 §D4). RLS scopes every read to the bearer's user; an out-of-grant
/// <c>ledgerId</c> simply yields empty rows.
/// </summary>
[McpServerToolType]
public static class ReportingTools
{
    [McpServerTool(Name = "transaction_summary"), Description(
        "Summarize categorized transactions for a ledger: income, expense (spending), " +
        "or net, optionally bucketed by month/quarter/year and grouped by category, " +
        "account, or payee, with an optional top-N cut. 'Spending' = expense-category " +
        "postings only (transfers excluded). groupBy='account' attributes each " +
        "categorized posting to the real account that paid it; groupBy='payee' groups " +
        "by payee text. rollup=true (category only) makes each parent category's total " +
        "include its subcategories, returning the whole tree (rows carry parentId; the " +
        "overall total stays the true sum of postings — don't re-sum parent rows). " +
        "Each row carries groupId (the category/account id; null for payee) and " +
        "parentId. Amounts are positive magnitudes in the ledger's currency (USD). " +
        "Use list_ledgers first to resolve ledgerId.")]
    public static async Task<ReportResult> TransactionSummary(
        ReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Measure: 'spending' (expense), 'income', or 'net'. Default 'spending'.")]
        string measure = "spending",
        [Description("Time bucket: 'none', 'month', 'quarter', or 'year'. Default 'month'.")]
        string timeBucket = "month",
        [Description("Group by: 'category' (default), 'account', or 'payee'.")]
        string groupBy = "category",
        [Description("Roll subcategory totals up into their parent (category grouping only). Default false.")]
        bool rollup = false,
        [Description("Inclusive start (UTC ISO-8601, e.g. 2026-01-01). Omit for no lower bound.")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for no upper bound.")]
        DateTime? toUtc = null,
        [Description("Restrict to transactions carrying this tag (GUID from list_tags). Omit for any.")]
        Guid? tagId = null,
        [Description("Keep only the N largest groups by absolute amount. Omit for all.")]
        int? topN = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = McpArgs.ParseEnum<ReportMeasure>(measure, "measure"),
            TimeBucket = McpArgs.ParseEnum<ReportTimeBucket>(timeBucket, "timeBucket"),
            GroupBy = McpArgs.ParseEnum<ReportGroupBy>(groupBy, "groupBy"),
            Rollup = rollup,
            FromUtc = NormalizeUtc(fromUtc),
            ToUtc = NormalizeUtc(toUtc),
            TagId = tagId,
            TopN = topN,
        };
        return await repository.SummarizeAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "list_transactions"), Description(
        "List individual transaction lines for a ledger — the drill-down behind a " +
        "summary total. Filters compose: accountId (that account's register; a " +
        "category id works too), categoryId (lines categorized as that category), " +
        "payee (exact) / text (substring over payee+memo), minAmount/maxAmount " +
        "(bound the ABSOLUTE amount), direction ('inflow' | 'outflow' | 'all'), and " +
        "fromUtc/toUtc (optional — no date window required). Sort by 'date' (default), " +
        "'amount' (signed), or 'absAmount' (largest movements), descending unless " +
        "ascending=true. Returns up to 'limit' (default 200, max 500) from 'offset', " +
        "with hasMore for paging. When no accountId is given, only money-side legs are " +
        "returned so each transaction appears once. Amounts in the ledger's currency " +
        "(USD); positive = inflow to the line's account. Resolve ids via list_accounts.")]
    public static async Task<TransactionLinesResult> ListTransactions(
        ReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Restrict to this account's register (GUID). Omit for all money-side lines.")]
        Guid? accountId = null,
        [Description("Restrict to lines categorized as this category (GUID).")]
        Guid? categoryId = null,
        [Description("Restrict to transactions carrying this tag (GUID from list_tags). Omit for any.")]
        Guid? tagId = null,
        [Description("Exact payee match. Omit for any.")] string? payee = null,
        [Description("Case-insensitive substring over payee + memo. Omit for any.")]
        string? text = null,
        [Description("Minimum absolute amount. Omit for no lower bound.")] decimal? minAmount = null,
        [Description("Maximum absolute amount. Omit for no upper bound.")] decimal? maxAmount = null,
        [Description("Direction: 'all' (default), 'inflow' (positive), or 'outflow' (negative).")]
        string direction = "all",
        [Description("Inclusive start (UTC ISO-8601). Omit for no lower bound.")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for no upper bound.")]
        DateTime? toUtc = null,
        [Description("Sort field: 'date' (default), 'amount', or 'absAmount'.")]
        string sort = "date",
        [Description("Sort ascending instead of descending. Default false (descending).")]
        bool ascending = false,
        [Description("Max rows to return (default 200, max 500).")] int limit = 200,
        [Description("Rows to skip for paging. Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var query = new TransactionQuery
        {
            LedgerId = ledgerId,
            AccountId = accountId,
            CategoryId = categoryId,
            TagId = tagId,
            Payee = payee,
            Text = text,
            MinAmount = minAmount,
            MaxAmount = maxAmount,
            Direction = McpArgs.ParseEnum<TransactionDirection>(direction, "direction"),
            FromUtc = NormalizeUtc(fromUtc),
            ToUtc = NormalizeUtc(toUtc),
            Sort = McpArgs.ParseEnum<TransactionSort>(sort, "sort"),
            Descending = !ascending,
            Limit = limit,
            Offset = offset,
        };
        return await repository.ListTransactionsAsync(query, cancellationToken).ConfigureAwait(false);
    }

    // The model may pass a bare date ("2026-01-01") that deserializes as Unspecified
    // kind; treat the wall-clock value as UTC so the repository's UTC range filter
    // compares like-for-like.
    private static DateTime? NormalizeUtc(DateTime? value) =>
        value is { } v
            ? DateTime.SpecifyKind(v, DateTimeKind.Utc)
            : null;
}
