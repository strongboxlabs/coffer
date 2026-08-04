namespace Coffer.Api.Contracts;

/// <summary>
/// Investment reporting read shapes (ADR-0063 §D5). Decimal money/quantities;
/// USD (v1). Market value = qty × latest price; positions with no price are
/// carried at cost basis (the OverviewRepository convention). Income + realized
/// gains land in a later increment (realized gains needs FIFO lot consumption).
/// </summary>
/// <summary>Which account(s) a rolled-up holding sits in (ADR-0063 v2) — the
/// owning brokerage, not the internal holdings-sibling.</summary>
public sealed record HeldInSlice(Guid AccountId, string AccountName, decimal Quantity);

public sealed record HoldingSnapshotRow(
    Guid SecurityId,
    string? Ticker,
    string Name,
    string? AssetClass,
    decimal Quantity,
    decimal CostBasis,
    decimal? LatestPrice,
    decimal MarketValue,
    decimal UnrealizedGain,
    decimal? UnrealizedGainPct,
    IReadOnlyList<HeldInSlice> HeldIn);

public sealed record HoldingsSnapshot(
    IReadOnlyList<HoldingSnapshotRow> Holdings,
    decimal TotalMarketValue,
    decimal TotalCostBasis,
    decimal TotalUnrealizedGain);

/// <summary>The dimension an allocation breakdown buckets by (ADR-0067).</summary>
public enum AllocationDimension { AssetClass, Region, VehicleType, Security, Account }

/// <summary>One allocation bucket: a value of the chosen dimension, its market
/// value, and percent of portfolio. <see cref="Bucket"/> is the dimension value
/// (e.g. 'equity', 'us', 'mutual_fund'); 'Unclassified' when unset.</summary>
public sealed record AllocationRow(string Bucket, decimal MarketValue, decimal Percent);

public sealed record AllocationResult(IReadOnlyList<AllocationRow> Buckets, decimal TotalMarketValue);

/// <summary>One aggregated investment event for the activity feed (ADR-0080): a
/// header collapsed via <c>InvestmentEventProjector</c> into a single row — the same
/// aggregation the register renders. <see cref="Amount"/> is the net cash impact on
/// the brokerage; <see cref="Fee"/> is the fee leg's magnitude;
/// <see cref="Category"/> / <see cref="TransferAccount"/> are the projected slots
/// (null when that role leg is absent).</summary>
public sealed record InvestmentActivityRow(
    Guid HeaderId,
    DateTime PostedAt,
    Guid AccountId,
    string AccountName,
    string? Action,
    Guid? SecurityId,
    string? SecurityTicker,
    string? SecurityName,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal Amount,
    decimal? Fee,
    string? Category,
    string? TransferAccount);

public sealed record InvestmentActivityResult(IReadOnlyList<InvestmentActivityRow> Events);

/// <summary>Security catalog entry with the full classification (ADR-0067):
/// economic asset class, vehicle, region, the split style axes (equity size/style
/// or fixed-income duration/credit — only the relevant pair set), and the
/// security's own tax character. A 'multi_asset' asset class is the look-through
/// signal (its sleeves live in security_components).</summary>
public sealed record SecurityInfo(
    Guid Id,
    string? Ticker,
    string Name,
    string? AssetClass,
    string? VehicleType,
    string? Region,
    string? EquitySize,
    string? EquityStyle,
    string? FiDuration,
    string? FiCredit,
    string? TaxCharacter,
    bool IsActive);

public sealed record PricePoint(DateOnly PriceDate, decimal Price, decimal? High, decimal? Low, long? Volume);

/// <summary>Group dimension for investment income.</summary>
public enum InvestmentIncomeGroupBy { Security, Account }

/// <summary>One income bucket (ADR-0063 v2 <c>investment_income</c>): dividend /
/// interest / misc income from <c>posting_role='income'</c> legs, normalized to a
/// positive magnitude. <see cref="GroupId"/> is the security or brokerage id (null
/// for misc income with no security); <see cref="Period"/> is set when bucketed.</summary>
public sealed record InvestmentIncomeRow(
    string? Period, Guid? GroupId, string? Ticker, string GroupName, decimal Amount);

public sealed record InvestmentIncomeResult(IReadOnlyList<InvestmentIncomeRow> Rows, decimal Total);

/// <summary>One security's realized gains over the window (ADR-0064 FIFO):
/// proceeds, FIFO cost basis consumed, and realized gain.</summary>
/// <summary>Per-security realized gains (FIFO). <see cref="RealizedGain"/> is the
/// total; <see cref="RealizedGainShortTerm"/> / <see cref="RealizedGainLongTerm"/>
/// split it by the holding period of the lots each sale consumed (long-term = held
/// more than one year; mig 169 / ADR-0064 D2). A single sale straddling the 1-year
/// line contributes to both.</summary>
public sealed record RealizedGainSummaryRow(
    Guid SecurityId, string? Ticker, string Name,
    decimal Proceeds, decimal CostBasisSold, decimal RealizedGain,
    decimal RealizedGainShortTerm, decimal RealizedGainLongTerm);

public sealed record RealizedGainsResult(
    IReadOnlyList<RealizedGainSummaryRow> Rows,
    decimal TotalProceeds,
    decimal TotalCostBasisSold,
    decimal TotalRealizedGain,
    decimal TotalRealizedGainShortTerm,
    decimal TotalRealizedGainLongTerm);

/// <summary>
/// Investment returns (ADR-0063 v2). <see cref="MoneyWeightedReturn"/> (XIRR) is
/// the reliable headline — annualized fraction (0.1 = 10%/yr), null when it can't
/// be solved (e.g. single-signed flows). <see cref="TimeWeightedReturn"/> is null
/// with <see cref="TimeWeightedUnavailableReason"/> when it can't be computed
/// honestly. Values are in the ledger's currency (USD); start value is 0 for a
/// since-inception window.
/// </summary>
public sealed record ReturnsResult(
    string Scope,
    DateTime StartDate,
    DateTime EndDate,
    decimal StartValue,
    decimal EndValue,
    decimal NetContributions,
    double? MoneyWeightedReturn,
    double? TimeWeightedReturn,
    string? TimeWeightedUnavailableReason);

/// <summary>
/// A detected in-kind-transfer candidate (ADR-0065 D4): a disposal (sell/sellx)
/// in one investment account paired with an acquisition (buy/buyx) in another, of
/// the SAME security, SAME calendar date, and EQUAL share quantity — the shape an
/// in-kind rollover/ACATS takes when it was (mis-)recorded as sell+buy. The user
/// reviews each against a brokerage statement, then converts it to a single
/// <c>transfer_shares</c> (zero realized gain, basis carried).
/// <see cref="SourceHadFee"/>/<see cref="DestHadFee"/> warn that the original pair
/// carried a fee leg the in-kind transfer will drop.
/// </summary>
public sealed record InKindTransferCandidate(
    Guid SellHeaderId,
    Guid BuyHeaderId,
    Guid SourceAccountId,
    string SourceAccountName,
    Guid DestAccountId,
    string DestAccountName,
    Guid SecurityId,
    string? SecurityTicker,
    string? SecurityName,
    decimal Quantity,
    DateTime Date,
    bool SourceHadFee,
    bool DestHadFee);
