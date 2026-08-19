namespace Coffer.Api.Contracts;

/// <summary>
/// Investment reporting read shapes (ADR-0063 §D5). Decimal money/quantities;
/// USD (v1). Market value = qty × latest price; positions with no price are
/// carried at cost basis (the OverviewRepository convention). Income + realized
/// gains land in a later increment (realized gains needs FIFO lot consumption).
/// </summary>
/// <summary>
/// When a reporting figure was produced and by which build. Every reporting
/// response carries both, because a consumer that assembles one report from
/// several calls cannot otherwise tell which numbers are current.
/// <para>
/// This is not decoration. A report was published in which four accounts showed
/// "n/a" for a figure the engine had returned minutes earlier — rows carried over
/// from an older run beside freshly-fetched ones, with nothing in either payload
/// to distinguish them. A stamp does not stop a consumer reusing stale rows, but
/// it makes the mistake detectable afterwards instead of invisible.
/// </para>
/// <para>
/// <c>ComputedAt</c> is when the computation RAN — distinct from a report's
/// as-of or window-end date, which the caller chooses and may set in the past.
/// <c>EngineVersion</c> is <c>semver+sha</c>, exact enough to tell two builds
/// apart when a figure's definition changes between them.
/// </para>
/// </summary>
public interface IReportProvenance
{
    DateTime ComputedAt { get; }
    string EngineVersion { get; }
}

/// <summary>Which account(s) a rolled-up holding sits in (ADR-0063 v2) — the
/// owning brokerage, not the internal holdings-sibling.</summary>
public sealed record HeldInSlice(Guid AccountId, string AccountName, decimal Quantity);

/// <summary>
/// One rolled-up position. <see cref="Quantity"/> and <see cref="MarketValue"/> are
/// split-adjusted as of the snapshot's instant; <see cref="LatestPrice"/> is the
/// per-share price that valuation actually used, back-adjusted onto that instant's
/// split basis, which is not necessarily the newest row in security_prices.
/// <para>
/// <see cref="CostBasis"/> is FIFO (ADR-0064) and is exact at ANY instant, not just
/// now: migration 202 made the FIFO walk pure and as-of-bounded, so the read uses
/// the same algorithm the recompute persists. <see cref="UnrealizedGainPct"/> is
/// null only when basis is zero.
/// </para>
/// </summary>
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

/// <summary>
/// Holdings as of an instant, valued through the SAME feeder <c>returns</c> and
/// <c>allocation</c> use.
/// <para>
/// That shared feeder is the point. This report previously read the current
/// <c>holdings</c> projection while allocation valued through the as-of feeder.
/// The projection is kept in step with <c>txn_legs</c> by an EF
/// <c>SaveChangesInterceptor</c> (<c>HoldingsRecomputeInterceptor</c>, mig 104 —
/// NOT a trigger; mig 104 dropped those), and the API writes only through EF, so
/// the two do agree. The reason to move is simpler than a disagreement: the
/// projection only ever describes NOW. There is no past-dated form of it, so no
/// as-of report can be built on it. The feeder replays the legs directly and
/// answers at any instant.
/// </para>
/// <para>
/// Everything here is exact at <see cref="AsOf"/>, cost basis included — the FIFO
/// walk is as-of-bounded (mig 202) and shared with the recompute, so there is no
/// "current basis against a past valuation" compromise anywhere in this shape.
/// </para>
/// </summary>
public sealed record HoldingsSnapshot(
    IReadOnlyList<HoldingSnapshotRow> Holdings,
    decimal TotalMarketValue,
    decimal TotalCostBasis,
    decimal TotalUnrealizedGain,
    DateTime AsOf,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>The dimension an allocation breakdown buckets by (ADR-0067).</summary>
public enum AllocationDimension { AssetClass, Region, VehicleType, Security, Account }

/// <summary>One allocation bucket: a value of the chosen dimension, its market
/// value, and percent of portfolio. <see cref="Bucket"/> is the dimension value
/// (e.g. 'equity', 'us', 'mutual_fund'); 'Unclassified' when unset.</summary>
public sealed record AllocationRow(string Bucket, decimal MarketValue, decimal Percent);

/// <summary>
/// A security classified <c>multi_asset</c> that has no component weights
/// configured, so look-through cannot decompose it and its whole market value
/// sits in an opaque bucket.
/// <para>
/// This is reported because the failure is otherwise SILENT and the resulting
/// chart is confidently wrong. One such fund at 66% of a portfolio put equity at
/// 8.5% when the true figure was 35% — nothing in the response said the number
/// was a placeholder. <c>set_security_components</c> is the fix; until then, the
/// buckets are understated by whatever this security actually holds.
/// </para>
/// </summary>
public sealed record UndecomposedMultiAsset(
    Guid SecurityId,
    string? Ticker,
    string Name,
    decimal MarketValue,
    decimal PercentOfTotal);

/// <summary>
/// An allocation breakdown (ADR-0067) as of an instant.
/// <para>
/// <see cref="TotalMarketValue"/> is SECURITIES ONLY. Brokerage cash that is not
/// held as a money-market position is not a holding and has no asset class, so it
/// cannot be bucketed — but it is still part of the portfolio, and leaving it
/// silently out is what makes an allocation total disagree with a returns total
/// for no visible reason. <see cref="ExcludedBrokerageCash"/> states it, and the
/// identity holds exactly at the same instant:
/// <c>TotalMarketValue + ExcludedBrokerageCash == returns.endValue</c>.
/// </para>
/// <para>
/// <see cref="UndecomposedMultiAssets"/> is empty when every multi-asset holding
/// could be looked through. A non-empty list means the buckets below understate
/// whatever those securities hold — see <see cref="UndecomposedMultiAsset"/>.
/// </para>
/// </summary>
public sealed record AllocationResult(
    IReadOnlyList<AllocationRow> Buckets,
    decimal TotalMarketValue,
    DateTime AsOf,
    decimal ExcludedBrokerageCash,
    IReadOnlyList<UndecomposedMultiAsset> UndecomposedMultiAssets,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

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

public sealed record InvestmentIncomeResult(
    IReadOnlyList<InvestmentIncomeRow> Rows,
    decimal Total,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

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
    decimal TotalRealizedGainLongTerm,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>
/// One brokerage's contribution to a LEDGER-scope returns report: what it was
/// worth at each end of the window, valued on exactly the same basis as the
/// report's own totals, so the rows SUM to them.
/// <para>
/// This exists because a report cannot otherwise know which accounts a window
/// covers. Selecting by current balance drops every account that held money at
/// the window's open and was emptied before its close — on the ledger this was
/// built for, that hid $2.18M of opening value across accounts drained by
/// rollovers, in a table whose rows were naturally read as summing to the total
/// beneath them. An account appears here when it was worth something at EITHER
/// end of the window or saw any flow within it — so one emptied mid-window stays,
/// and only accounts that held nothing throughout and saw nothing move are
/// omitted, which changes no column's total.
/// </para>
/// <para>
/// Deliberately no per-account return or net-contribution figure. Both are
/// SCOPE-RELATIVE — a rollover is internal to the ledger and external to each
/// account — so producing them here would mean a second implementation of the
/// flow-classification rules running beside the first, which is how the original
/// defects arose. Call returns again with accountId for those; the account-scope
/// path already gets them right, in-kind transfers included.
/// </para>
/// </summary>
public sealed record ReturnsAccountValue(
    Guid AccountId,
    string AccountName,
    decimal StartValue,
    decimal EndValue);

/// <summary>
/// Gross money in and gross money out for one source of contributions, in the
/// same sign convention as net contributions: <see cref="In"/> is positive,
/// <see cref="Out"/> is zero or negative, and the two ADD to that source's net.
/// <para>
/// <see cref="Source"/> is one of: <c>external_accounts</c> (banks, cash, assets,
/// liabilities — money entering or leaving the investment world),
/// <c>other_investment_accounts</c> (a rollover to or from a brokerage outside
/// this report's scope), <c>category_transfers</c> (employer retirement
/// contributions arriving, withdrawals leaving — NOT dividends, interest,
/// expenses or fees, which are the portfolio's own return), and
/// <c>in_kind_transfers</c> (securities moved with no cash leg at all).
/// </para>
/// </summary>
public sealed record ContributionSourceTotals(
    string Source,
    decimal In,
    decimal Out);

/// <summary>
/// Investment returns (ADR-0063 v2). Both figures are annualized fractions
/// (0.1 = 10%/yr), and each is paired with its own reason string: a null rate
/// ALWAYS carries a non-null explanation, and a non-null rate never does. Neither
/// figure is a reliable headline on its own — <see cref="MoneyWeightedReturn"/>
/// (XIRR) answers "what did my money earn", <see cref="TimeWeightedReturn"/>
/// answers "how did the holdings perform", and they diverge whenever cash flows
/// are large relative to the balance. Values are in the ledger's currency (USD);
/// start value is 0 for a since-inception window.
/// <para>
/// <see cref="TimeWeightedReturn"/> is annualized over the time the account was
/// actually invested, which is SHORTER than the requested window whenever the
/// account was empty at either end of it or in the middle — a rollover
/// destination funded two months ago has a time-weighted return, and it covers
/// two months, not the five years asked for. <see cref="TimeWeightedCoveredYears"/>
/// carries that span and must be shown wherever the rate is: annualizing a short
/// stretch magnifies it, exactly as it does for the money-weighted figure, so the
/// two are only comparable across accounts once the spans are known to match.
/// </para>
/// <para>
/// <see cref="Accounts"/> is present at LEDGER scope and null at account scope:
/// every brokerage the report covered, with its start and end value on the same
/// valuation basis, so those two columns sum to the report's own. It is the only
/// reliable way to know which accounts a window spans — see
/// <see cref="ReturnsAccountValue"/>.
/// </para>
/// <para>
/// <see cref="NetContributions"/> is a NET, and a net is lossy in a way that
/// reliably misleads: −653,611 is equally consistent with a single withdrawal of
/// that size and with 688,759 out against 35,148 in. A reader holding one salient
/// event will bind the figure to it — a real report described exactly this number
/// as "the rollover" when the rollover was 678,803 and continued employer
/// contributions offset the rest. So the parts travel with the total:
/// <see cref="ContributionsIn"/> + <see cref="ContributionsOut"/> ==
/// <see cref="NetContributions"/> exactly, and
/// <see cref="ContributionsBySource"/> says where each half came from. Describe
/// the composition from those, never from the net.
/// </para>
/// </summary>
public sealed record ReturnsResult(
    string Scope,
    DateTime StartDate,
    DateTime EndDate,
    decimal StartValue,
    decimal EndValue,
    decimal NetContributions,
    double? MoneyWeightedReturn,
    string? MoneyWeightedUnavailableReason,
    double? TimeWeightedReturn,
    string? TimeWeightedUnavailableReason,
    DateTime? TimeWeightedCoveredFrom,
    DateTime? TimeWeightedCoveredTo,
    double? TimeWeightedCoveredYears,
    int? TimeWeightedCoveredDays,
    IReadOnlyList<ReturnsAccountValue>? Accounts,
    decimal ContributionsIn,
    decimal ContributionsOut,
    IReadOnlyList<ContributionSourceTotals> ContributionsBySource,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>
/// What a time-weighted return would COST for a window, without computing one.
/// TWR values the portfolio once per instant at which money crossed the scope's
/// boundary, and that count — not the window's length or the number of trades —
/// is what the work scales with.
/// <para>
/// This exists so a caller can size a request before spending it. There is no
/// ceiling to compare against — migrations 200 and 201 batched both halves of a
/// boundary valuation, so the cap that used to refuse a time-weighted figure past
/// 400 instants is gone — but the count still tells a caller how much work a
/// window implies before it asks for it.
/// </para>
/// <para>
/// The count is SCOPE-RELATIVE for the same reason net contributions are: a
/// rollover between two brokerages is one boundary at account scope and none at
/// ledger scope. Trades are never boundaries — a trade faces the holdings
/// sibling, which is inside the perimeter, so an account trading a hundred times
/// a day adds nothing here.
/// </para>
/// </summary>
public sealed record ReturnsCostEstimate(
    string Scope,
    DateTime StartDate,
    DateTime EndDate,
    int FlowInstants,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

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
