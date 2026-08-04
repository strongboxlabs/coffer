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
        "Current holdings for a ledger rolled up per security: quantity, cost basis " +
        "(FIFO), latest price, market value, unrealized gain (amount + percent), and " +
        "heldIn (which brokerage account(s) hold it, with quantity). Positions with no " +
        "recorded price are carried at cost basis. Totals included. Pass accountId to " +
        "scope to one brokerage. USD. Use list_accounts to resolve ids.")]
    public static async Task<HoldingsSnapshot> HoldingsSnapshot(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Scope to one brokerage account (GUID). Omit for the whole ledger.")]
        Guid? accountId = null,
        CancellationToken cancellationToken = default) =>
        await repository.HoldingsSnapshotAsync(ledgerId, accountId, cancellationToken).ConfigureAwait(false);

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
        "TWR is null with a reason only when a sub-period can't be valued honestly (a " +
        "fully-withdrawn base, or too many cash-flow dates). With no dates it's " +
        "since-inception to now. Pass accountId for one brokerage (scope='account'); " +
        "omit for the whole ledger (scope='ledger'). USD.")]
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

    [McpServerTool(Name = "allocation"), Description(
        "Portfolio allocation for a ledger, bucketed by 'asset_class' (default), " +
        "'region', 'vehicle', 'security', or 'account': market value + percent per " +
        "bucket. For asset_class/region, multi-asset funds (target-date, balanced, " +
        "529) are decomposed via look-through into their sleeves rather than counted " +
        "as one 'multi_asset' bucket; vehicle/security are leaf attributes. 'account' " +
        "attributes each position's value to the brokerage account(s) holding it " +
        "(apportioned by share quantity). Unclassified positions bucket as " +
        "'Unclassified'. USD. Use list_ledgers first to resolve ledgerId.")]
    public static async Task<AllocationResult> Allocation(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Bucket by: 'asset_class' (default), 'region', 'vehicle', 'security', or 'account'.")]
        string dimension = "asset_class",
        CancellationToken cancellationToken = default) =>
        await repository.AllocationAsync(ledgerId, ParseAllocationDimension(dimension), cancellationToken)
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
