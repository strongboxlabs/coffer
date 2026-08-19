using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP tools over the investment reporting layer (ADR-0063 §D5): holdings
/// snapshot, asset-class allocation, securities catalog, price history. All
/// read-only and deterministic; valuation = qty × latest price, no-price
/// positions carried at cost (the OverviewRepository convention). Income +
/// returns (IRR/TWR) are MCP v2. RLS scopes every read to the bearer's user.
/// </summary>
[McpServerToolType]
public static class InvestmentTools
{
    [McpServerTool(Name = "holdings_snapshot"), Description(
        "Holdings for a ledger rolled up per security, as of an instant: quantity, cost " +
        "basis (FIFO), the per-share price used, market value, unrealized gain (amount + " +
        "percent), and heldIn (which brokerage account(s) hold it, with quantity). Totals " +
        "included. Pass accountId to scope to one brokerage. " +
        "asOfUtc values a PAST instant through the same feeder returns and allocation use, " +
        "so the three agree at any instant; omit it for now. " +
        "costBasis is FIFO and is exact at ANY instant, not just now — the walk that " +
        "produces it is as-of-bounded and is the same one the recompute persists, so a " +
        "past basis is the basis as it stood then, never today's basis against a past " +
        "market value. unrealizedGain and its percentage follow from it. " +
        "Note latestPrice is the per-share price VALUATION USED, back-adjusted onto that " +
        "instant's split basis — for a past instant it is not the newest price on file. " +
        "USD. Use list_accounts to resolve ids.")]
    public static async Task<HoldingsSnapshot> HoldingsSnapshot(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Scope to one brokerage account (GUID). Omit for the whole ledger.")]
        Guid? accountId = null,
        [Description("Value as of this instant (UTC ISO-8601). Omit for now.")]
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default) =>
        await repository.HoldingsSnapshotAsync(
            ledgerId, accountId, NormalizeUtc(asOfUtc), cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "investment_income"), Description(
        "Dividend / interest / misc investment income for a ledger over an optional " +
        "period, grouped by 'security' (default) or 'account' (brokerage), optionally " +
        "bucketed by month/quarter/year. Amounts are positive magnitudes in the " +
        "ledger's currency (USD). Filter by accountId (brokerage) and/or securityId. " +
        "Resolve ids via list_accounts / list_securities.")]
    public static async Task<InvestmentIncomeResult> InvestmentIncome(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Inclusive start (UTC ISO-8601). Omit for no lower bound.")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for no upper bound.")]
        DateTime? toUtc = null,
        [Description("Filter to one brokerage account (GUID). Omit for all.")]
        Guid? accountId = null,
        [Description("Filter to one security (GUID). Omit for all.")]
        Guid? securityId = null,
        [Description("Group by 'security' (default) or 'account'.")]
        string groupBy = "security",
        [Description("Time bucket: 'none' (default), 'month', 'quarter', or 'year'.")]
        string timeBucket = "none",
        CancellationToken cancellationToken = default) =>
        await repository.IncomeAsync(
            ledgerId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc), accountId, securityId,
            McpArgs.ParseEnum<InvestmentIncomeGroupBy>(groupBy, "groupBy"),
            McpArgs.ParseEnum<ReportTimeBucket>(timeBucket, "timeBucket"), cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "realized_gains"), Description(
        "Realized capital gains for a ledger over an optional period, grouped by " +
        "security (FIFO cost basis): proceeds, cost basis sold, and realized gain — " +
        "each gain split into short-term vs long-term by the holding period of the " +
        "lots the sale consumed (long-term = held more than one year; a sale " +
        "straddling the 1-year line contributes to both), with totals. Filter by " +
        "accountId (brokerage) and/or securityId. USD. Resolve ids via list_accounts " +
        "/ list_securities.")]
    public static async Task<RealizedGainsResult> RealizedGains(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Inclusive start (UTC ISO-8601). Omit for no lower bound.")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for no upper bound.")]
        DateTime? toUtc = null,
        [Description("Filter to one brokerage account (GUID). Omit for all.")]
        Guid? accountId = null,
        [Description("Filter to one security (GUID). Omit for all.")]
        Guid? securityId = null,
        CancellationToken cancellationToken = default) =>
        await repository.RealizedGainsAsync(
            ledgerId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc), accountId, securityId, cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "returns"), Description(
        "Investment returns for a ledger or one brokerage account over an optional " +
        "window. Returns BOTH the money-weighted return (annualized IRR, e.g. 0.12 = " +
        "12%/yr) and the true time-weighted return (TWR), plus start value, end value, " +
        "and net contributions. Both value the portfolio the same way at each boundary " +
        "(cash + split-adjusted holdings), so a contribution never distorts the figure. " +
        "Either figure can be null, and a null ALWAYS carries its own reason string " +
        "(moneyWeightedUnavailableReason / timeWeightedUnavailableReason) saying why — " +
        "an account that held nothing all window, or a window with no elapsed time. " +
        "Never treat a null as zero. There is no longer any limit on window size or flow " +
        "count: a whole-ledger multi-year time-weighted return is computed, not declined. " +
        "TWR is annualized over the time the account was actually INVESTED, which is " +
        "shorter than the requested window whenever the account was empty at either end " +
        "of it or in the middle: a rollover destination funded two months ago reports a " +
        "TWR covering two months, not the five years asked for. timeWeightedCoveredYears " +
        "and timeWeightedCoveredDays give that span (with timeWeightedCoveredFrom/To as its " +
        "outer bounds) and MUST be reported wherever the rate is — annualizing a short " +
        "stretch magnifies it, so two accounts' TWRs are comparable only once their covered " +
        "spans are known to match. Use the DAYS figure when stating the span in words: " +
        "covered time is the sum of the invested stretches, so it is not the difference " +
        "between the from/to bounds and cannot be re-derived from them. " +
        "Every response carries computedAt and " +
        "engineVersion — when assembling one report from several calls, check them rather " +
        "than reusing rows from an earlier run. " +
        "Net contributions " +
        "count transfers crossing the REPORTED SCOPE's boundary: a rollover between two " +
        "brokerages is internal at ledger scope but a withdrawal from the source and a " +
        "contribution to the destination at account scope — including an IN-KIND share " +
        "transfer, which moves securities with no cash leg at all. Money facing a CATEGORY counts " +
        "only when the posting is a transfer (an employer retirement contribution arriving, " +
        "a withdrawal leaving); dividends, interest, investment expenses and fees are the " +
        "portfolio's own earnings and costs, so they stay inside the return. With no dates it's " +
        "since-inception to now. Pass accountId for one brokerage (scope='account'); " +
        "omit for the whole ledger (scope='ledger'). " +
        "At ledger scope the result also carries 'accounts': EVERY brokerage the report " +
        "covered, with its start and end value on the same basis, so those columns SUM to " +
        "the report's own. Use it to decide which accounts a window spans — do NOT pick " +
        "accounts by current balance, which silently drops any account that held money when " +
        "the window opened and was emptied before it closed (rollover sources), and those are " +
        "often the largest movements in the window. The roster carries no per-account rate or " +
        "contribution figure because both are scope-relative; call returns again with " +
        "accountId for those. " +
        "netContributions is a NET and can hide large offsetting movements, so do NOT " +
        "describe it as a single event: contributionsIn + contributionsOut == " +
        "netContributions exactly, and contributionsBySource splits both halves by origin " +
        "(external_accounts, other_investment_accounts, category_transfers, " +
        "in_kind_transfers). Characterize the composition from those fields, never by " +
        "matching the net against an event you already know about. USD.")]
    public static async Task<ReturnsResult> Returns(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("One brokerage account (GUID) for account scope. Omit for the whole ledger.")]
        Guid? accountId = null,
        [Description("Inclusive start (UTC ISO-8601). Omit for since-inception (exact).")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for now.")]
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default) =>
        await repository.ReturnsAsync(
            ledgerId, accountId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc), DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "activity"), Description(
        "Investment transaction activity for a ledger — each investment event " +
        "(Buy / Sell / Dividend / Reinvest / Transfer / Misc, including any fee) " +
        "collapsed into ONE row, newest first. Fields per event: date, brokerage " +
        "account, action, security (ticker + name), quantity, unit price, net cash " +
        "amount, fee, and the category / transfer counterparty when present. Filter " +
        "by accountId (brokerage), securityId, and/or a fromUtc/toUtc window; pass a " +
        "window for a large ledger. Amounts in the ledger's currency (USD). Resolve " +
        "ids via list_accounts / list_securities.")]
    public static async Task<InvestmentActivityResult> Activity(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Inclusive start (UTC ISO-8601). Omit for no lower bound.")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for no upper bound.")]
        DateTime? toUtc = null,
        [Description("Filter to one brokerage account (GUID). Omit for all.")]
        Guid? accountId = null,
        [Description("Filter to one security (GUID). Omit for all.")]
        Guid? securityId = null,
        [Description("Max events to return (default 100, max 500).")] int limit = 100,
        CancellationToken cancellationToken = default) =>
        await repository.ActivityAsync(
            ledgerId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc), accountId, securityId, limit,
            cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "returns_cost_estimate"), Description(
        "How expensive a 'returns' call would be for a window, WITHOUT computing it. " +
        "Returns flowInstants (the number of portfolio valuations a time-weighted return " +
        "would need — one per instant money crossed the scope's boundary). Useful for " +
        "understanding how much history a window covers before asking for it; there is no " +
        "ceiling, and 'returns' will compute a time-weighted figure at any count. " +
        "The count is scope-relative, exactly as net contributions are: a rollover between " +
        "two of your brokerages is a boundary at account scope and none at ledger scope. " +
        "Trades are never boundaries. Same arguments as 'returns'.")]
    public static async Task<ReturnsCostEstimate> ReturnsCostEstimateTool(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("One brokerage account (GUID) for account scope. Omit for the whole ledger.")]
        Guid? accountId = null,
        [Description("Inclusive start (UTC ISO-8601). Omit for since-inception (exact).")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for now.")]
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default) =>
        await repository.ReturnsCostEstimateAsync(
            ledgerId, accountId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc),
            DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "allocation"), Description(
        "Portfolio allocation for a ledger, bucketed by 'asset_class' (default), " +
        "'region', 'vehicle', 'security', or 'account': market value + percent per " +
        "bucket. For asset_class/region, multi-asset funds (target-date, balanced, " +
        "529) are decomposed via look-through into their sleeves rather than counted " +
        "as one 'multi_asset' bucket; vehicle/security are leaf attributes. 'account' " +
        "attributes each position's value to the brokerage account(s) holding it " +
        "(apportioned by share quantity). Unclassified positions bucket as " +
        "'Unclassified'. " +
        "Check 'undecomposedMultiAssets' BEFORE reporting any bucket: a multi-asset fund " +
        "with no components configured cannot be looked through, so its whole value sits " +
        "in one opaque bucket and every other bucket is understated. That is not a small " +
        "effect — one such fund at 66% of a portfolio showed equity as 8.5% when the true " +
        "figure was 35%. When the list is non-empty, say so rather than presenting the " +
        "buckets as complete; set_security_components is the fix. " +
        "totalMarketValue is SECURITIES ONLY; 'excludedBrokerageCash' is cash that has no " +
        "asset class to bucket, and the two together equal the portfolio value 'returns' " +
        "reports for the same instant. asOfUtc values a PAST instant through the same " +
        "feeder returns uses; omit for now. USD. Use list_ledgers first to resolve ledgerId.")]
    public static async Task<AllocationResult> Allocation(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Bucket by: 'asset_class' (default), 'region', 'vehicle', 'security', or 'account'.")]
        string dimension = "asset_class",
        [Description("Value as of this instant (UTC ISO-8601). Omit for now.")]
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default) =>
        await repository.AllocationAsync(
            ledgerId, ParseAllocationDimension(dimension), NormalizeUtc(asOfUtc), cancellationToken)
            .ConfigureAwait(false);

    private static AllocationDimension ParseAllocationDimension(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "asset_class" or "assetclass" => AllocationDimension.AssetClass,
        "region" => AllocationDimension.Region,
        "vehicle" or "vehicle_type" => AllocationDimension.VehicleType,
        "security" => AllocationDimension.Security,
        "account" => AllocationDimension.Account,
        // Fail loud (ADR-0063 §D4) rather than silently returning asset_class.
        _ => throw new ArgumentException(
            $"Unknown dimension '{value}'. Valid values: asset_class, region, vehicle, security, account."),
    };

    [McpServerTool(Name = "list_securities"), Description(
        "List the securities defined in a ledger with full classification (ADR-0067): " +
        "asset class (equity/fixed_income/multi_asset/cash/real_assets/alternative), " +
        "vehicle type (mutual_fund/etf/stock/...), region (us/developed_ex_us/emerging/" +
        "global), the style axes (equitySize+equityStyle for equities, fiDuration+fiCredit " +
        "for fixed income), and taxCharacter (taxable/tax_managed/tax_exempt). " +
        "An assetClass of 'multi_asset' is the look-through signal (its sleeves drive " +
        "allocation decomposition). Fields are null when unclassified. Use for " +
        "allocation/exposure analysis and to resolve a securityId for price_history.")]
    public static async Task<IReadOnlyList<SecurityInfo>> ListSecurities(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        await repository.SecuritiesAsync(ledgerId, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "find_in_kind_transfer_candidates"), Description(
        "Detect likely in-kind transfers (rollover / ACATS) that were recorded as a " +
        "sell in one investment account + a buy in another (ADR-0065 D4): same security, " +
        "same calendar date, equal share quantity, distinct accounts. These fabricate a " +
        "realized gain in the source and reset the destination cost basis — review each " +
        "against a brokerage statement, then (when writes are enabled) convert it to a " +
        "single transfer_shares (zero realized gain, original basis carried) with the " +
        "convert_in_kind_transfer tool, passing the returned sellHeaderId + buyHeaderId. " +
        "sourceHadFee/destHadFee flag a fee leg the transfer will drop. Read-only; " +
        "conversion is a separate write step.")]
    public static async Task<IReadOnlyList<InKindTransferCandidate>> FindInKindTransferCandidates(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        await repository.FindInKindTransferCandidatesAsync(ledgerId, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "price_history"), Description(
        "Recorded price points for one security over an optional date window " +
        "(date, close, high, low, volume), oldest first. Resolve securityId via " +
        "list_securities.")]
    public static async Task<IReadOnlyList<PricePoint>> PriceHistory(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Security id (GUID) from list_securities.")] Guid securityId,
        [Description("Inclusive start (UTC ISO-8601). Omit for no lower bound.")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). Omit for no upper bound.")]
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default) =>
        await repository.PriceHistoryAsync(
            ledgerId, securityId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc), cancellationToken)
            .ConfigureAwait(false);

    private static DateTime? NormalizeUtc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
