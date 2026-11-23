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
        "Each row carries groupId (the category/account id; null for payee), " +
        "parentId, and groupPath: a category's names from its top-level category down " +
        "to its own (groupName is only the last of them, which can be ambiguous — name a " +
        "category to the user by its path), or the one name for any other groupBy; " +
        "group totals and rest.only carry groupPath too. With topN, every row also carries drawn, and the result carries " +
        "rest {count, total, byPeriod}: the drawn rows never overlap, and for each " +
        "period they plus rest.byPeriod equal periodTotals, so read 'everything else' " +
        "from rest rather than summing rows. Without rollup every kept row is drawn; " +
        "with rollup a kept row is drawn only when no kept row lies below it, and a " +
        "kept parent with a kept row below it gets extra rows with isDirect=true (its " +
        "own groupId) holding its own postings, one per period. rest.count is how many " +
        "groups the rest folds together (with rollup, a dropped parent and its " +
        "children count once); when it is 1, rest.only {groupId, groupName, isDirect} " +
        "names that group (isDirect: the own postings of a category with a kept row " +
        "below it; otherwise a whole group, or a dropped subtree named at its highest " +
        "category). Each row and group total carries currencyCode, the currency its " +
        "amount is in. With groupBy='account' a row and its group total are that " +
        "account's own figure, in the account's own currency, unconverted, so rows of " +
        "accounts in different currencies do not sum. Every other figure combines " +
        "accounts and is in the ledger's reporting currency (the result's currencyCode): " +
        "the total, periodTotals and rest always, and the rows and group totals of every " +
        "other groupBy. A posting paid from an account in another currency converts at " +
        "the exchange rate for its own date (today's or a later posting at the latest " +
        "rate), then is summed and rounded once. With groupBy='account', drawn rows plus " +
        "rest.byPeriod equal periodTotals only after each row is converted, so read the " +
        "combined figures from the totals. A converted figure that needs a missing " +
        "exchange rate is null and carries rateUnavailable naming the currency pair and " +
        "day; it is never a partial sum. Amounts are positive magnitudes. " +
        "Use list_ledgers first to resolve ledgerId.")]
    public static async Task<ReportSummaryDto> TransactionSummary(
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
        [Description("Restrict to postings this tag counts for (GUID from list_tags): the tags the paying " +
                     "account's register shows for the posting; on a split entered from that account, the " +
                     "transaction's tags count for every line. A posting between two categories counts the " +
                     "tags of the category it is grouped under. Omit for any.")]
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
        // Through the same wrapper as the app's endpoint, so a figure withheld for a
        // missing rate is withheld here too. No local date is passed, so a
        // sliding window resolves against the clock, as it always has over MCP.
        return await repository.SummaryAsync(spec, today: null, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "list_transactions"), Description(
        "List individual transaction lines for a ledger — the drill-down behind a " +
        "summary total. Filters compose: accountId (that account's register; a " +
        "category id works too), categoryId (lines categorized as that category), " +
        "payee (exact) / text (substring over payee and the line's memo), minAmount/maxAmount " +
        "(bound the ABSOLUTE amount), direction ('inflow' | 'outflow' | 'all'), and " +
        "fromUtc/toUtc (optional — no date window required). Sort by 'date' (default), " +
        "'amount' (signed), or 'absAmount' (largest movements), descending unless " +
        "ascending=true. Returns up to 'limit' (default 200, max 500) from 'offset', " +
        "with hasMore for paging. When no accountId is given, only money-side legs are " +
        "returned: a transaction against categories appears once per line (a split with " +
        "three category lines appears three times), a transfer between two real accounts " +
        "once per account. Each line is read as the register of the leg in " +
        "scope shows it: accountId's; else categoryId's; else the line's own, and for a " +
        "transfer between two real accounts the outflow leg's (for a zero amount, the leg " +
        "of the account the transaction belongs to), so both of its lines read the same. Each line carries memo (what that register shows), transactionMemo (the " +
        "transaction's memo, only when that register is the account that split it), " +
        "tags (the tags in play there), legId (the line's own leg, which names it: " +
        "a split's lines share headerId and accountId) and currencyCode (the ISO 4217 " +
        "currency of the line's account; a category holds no money of its own, so with a " +
        "category as accountId each line is in the currency of the account on its other " +
        "side, and only a line between two categories takes the category's). Each amount " +
        "is in its line's currencyCode, unconverted, so lines in different currencies do " +
        "not sum; positive = inflow to the line's account. Resolve ids via list_accounts.")]
    public static async Task<TransactionLinesResult> ListTransactions(
        ReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Restrict to this account's register (GUID). Omit for all money-side lines.")]
        Guid? accountId = null,
        [Description("Restrict to lines categorized as this category (GUID).")]
        Guid? categoryId = null,
        [Description("Restrict to lines this tag is in play for (GUID from list_tags): the tags the " +
                     "register the line is read from shows, and on a split's own lines the transaction's " +
                     "tags too. Omit for any.")]
        Guid? tagId = null,
        [Description("Exact payee match. Omit for any.")] string? payee = null,
        [Description("Case-insensitive substring over payee, the memo the line is shown with, and " +
                     "its transactionMemo. Omit for any.")]
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
