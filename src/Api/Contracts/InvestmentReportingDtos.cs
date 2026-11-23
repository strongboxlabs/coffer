namespace Coffer.Api.Contracts;

/// <summary>
/// Investment reporting read shapes (ADR-0063 §D5). Decimal money/quantities. A
/// figure that combines accounts is in the ledger's reporting currency, converted; a
/// per-account figure stays in its account's currency (ADR-0114 D7); a figure that needs
/// a missing rate is null and named (ADR-0114 D9). Market value = qty × latest
/// price; positions with no price are carried at cost basis (the
/// OverviewRepository convention).
/// </summary>
/// <summary>
/// When a reporting figure was produced and by which build. Every reporting
/// response carries both, because a consumer that assembles one report from
/// several calls cannot otherwise tell which numbers are current.
/// <para>
/// This is not decoration. A consumer that carries rows over from an older run
/// beside freshly-fetched ones can show "n/a" for a figure the engine now returns,
/// and without a stamp nothing in either payload distinguishes the two. A stamp
/// does not stop a consumer reusing stale rows, but
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
/// null when basis is zero.
/// </para>
/// <para>
/// Every money figure is in the snapshot's <see cref="HoldingsSnapshot.CurrencyCode"/>
/// (ADR-0114 D7): each position from its price's currency into its brokerage's, then into
/// the snapshot's, at the instant's rate (the latest rate for now); the cost basis of a
/// position in another currency lot by lot at each lot's acquisition date, so the
/// unrealized gain includes the currency move since each purchase. A figure that needs a
/// missing rate is null, with every figure derived from it, and
/// <see cref="RateUnavailable"/> names each missing pair and day (ADR-0114 D9).
/// </para>
/// </summary>
public sealed record HoldingSnapshotRow(
    Guid SecurityId,
    string? Ticker,
    string Name,
    string? AssetClass,
    decimal Quantity,
    decimal? CostBasis,
    decimal? LatestPrice,
    decimal? MarketValue,
    decimal? UnrealizedGain,
    decimal? UnrealizedGainPct,
    IReadOnlyList<HeldInSlice> HeldIn,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>
/// Holdings as of an instant, valued through the SAME feeder <c>returns</c> and
/// <c>allocation</c> use.
/// <para>
/// That shared feeder is the point, rather than the current <c>holdings</c>
/// projection. The projection is kept in step with <c>txn_legs</c> by an EF
/// <c>SaveChangesInterceptor</c> (<c>HoldingsRecomputeInterceptor</c>, mig 104 —
/// NOT a trigger; mig 104 dropped those), and the API writes only through EF, so
/// the two agree. The reason is simpler than a disagreement: the projection only
/// ever describes NOW. There is no past-dated form of it, so no
/// as-of report can be built on it. The feeder replays the legs directly and
/// answers at any instant.
/// </para>
/// <para>
/// Everything here is exact at <see cref="AsOf"/>, cost basis included — the FIFO
/// walk is as-of-bounded (mig 202) and shared with the recompute, so there is no
/// "current basis against a past valuation" compromise anywhere in this shape.
/// </para>
/// <para>
/// <see cref="CurrencyCode"/> is the ledger's reporting currency, or with one brokerage
/// asked for, that brokerage's own currency: a per-account figure stays native (ADR-0114
/// D7). The totals are the rows' sums, so a row that needs a missing rate makes each total
/// it feeds null, and <see cref="RateUnavailable"/> names every missing pair and day.
/// </para>
/// </summary>
public sealed record HoldingsSnapshot(
    IReadOnlyList<HoldingSnapshotRow> Holdings,
    decimal? TotalMarketValue,
    decimal? TotalCostBasis,
    decimal? TotalUnrealizedGain,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable,
    DateTime AsOf,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>The dimension an allocation breakdown buckets by (ADR-0067).</summary>
public enum AllocationDimension { AssetClass, Region, VehicleType, Security, Account }

/// <summary>One allocation bucket: a value of the chosen dimension, its market
/// value, and percent of portfolio. <see cref="Bucket"/> is the dimension value
/// (e.g. 'equity', 'us', 'mutual_fund'); 'Unclassified' when unset.</summary>
/// <remarks>
/// <see cref="MarketValue"/> is in <see cref="CurrencyCode"/>: the ledger's reporting
/// currency, except an <c>account</c> bucket, which is one brokerage's figure and stays in
/// that brokerage's currency (ADR-0114 D7). <see cref="Percent"/> is always the bucket's
/// share of the converted total. Either is null when it needs a missing rate, named in
/// <see cref="RateUnavailable"/> (ADR-0114 D9).
/// </remarks>
public sealed record AllocationRow(
    string Bucket,
    decimal? MarketValue,
    decimal? Percent,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>
/// A security classified <c>multi_asset</c> that has no component weights
/// configured, so look-through cannot decompose it and its whole market value
/// sits in an opaque bucket.
/// <para>
/// This is reported because the failure is otherwise SILENT and the resulting
/// chart is confidently wrong. If such a fund is a large part of a portfolio, every
/// bucket it actually holds — equity included — is understated by its whole weight,
/// and without this list nothing in the response would say the number was a
/// placeholder. <c>set_security_components</c> is the fix; until then, the
/// buckets are understated by whatever this security actually holds.
/// </para>
/// </summary>
/// <remarks>In the allocation's reporting currency; null when it needs a missing rate,
/// named in <see cref="AllocationResult.RateUnavailable"/>.</remarks>
public sealed record UndecomposedMultiAsset(
    Guid SecurityId,
    string? Ticker,
    string Name,
    decimal? MarketValue,
    decimal? PercentOfTotal);

/// <summary>
/// An allocation breakdown (ADR-0067) as of an instant.
/// <para>
/// <see cref="TotalMarketValue"/> is SECURITIES ONLY. Brokerage cash that is not
/// held as a money-market position is not a holding and has no asset class, so it
/// cannot be bucketed — but it is still part of the portfolio, and leaving it
/// silently out is what makes an allocation total disagree with a returns total
/// for no visible reason. <see cref="ExcludedBrokerageCash"/> states it, and the
/// identity <c>TotalMarketValue + ExcludedBrokerageCash == returns.endValue</c>
/// holds exactly when both value the same instant. With no instant given, both
/// value the end of the caller's today. A returns window with an explicit end is
/// exclusive of it and valued at the instant before it, one microsecond earlier,
/// so it matches an allocation at that instant; an allocation AT the end also counts
/// whatever is dated there.
/// </para>
/// <para>
/// <see cref="UndecomposedMultiAssets"/> is empty when every multi-asset holding
/// could be looked through. A non-empty list means the buckets below understate
/// whatever those securities hold — see <see cref="UndecomposedMultiAsset"/>.
/// </para>
/// <para>
/// <see cref="TotalMarketValue"/>, <see cref="ExcludedBrokerageCash"/> and every bucket but
/// an account one are in the ledger's reporting currency, <see cref="CurrencyCode"/>
/// (ADR-0114 D7): each position from its price's currency into its brokerage's, then into
/// the reporting currency, and each brokerage's cash into it, at the instant's day's rate
/// (the latest rate for now). A figure that needs a missing rate is null, never a partial
/// sum, and <see cref="RateUnavailable"/> names every missing pair and day (ADR-0114 D9).
/// </para>
/// </summary>
public sealed record AllocationResult(
    IReadOnlyList<AllocationRow> Buckets,
    decimal? TotalMarketValue,
    DateTime AsOf,
    decimal? ExcludedBrokerageCash,
    IReadOnlyList<UndecomposedMultiAsset> UndecomposedMultiAssets,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>One aggregated investment event for the activity feed (ADR-0080): a
/// header collapsed via <c>InvestmentEventProjector</c> into a single row — the same
/// aggregation the register renders. <see cref="Amount"/> is the net cash impact on
/// the brokerage — which is structurally ZERO for a cash-neutral event (a
/// reinvestment, an in-kind transfer, a sale a fee consumes), so
/// <see cref="Settled"/> carries the trade value those events actually moved
/// (ADR-0073 D1). Read <see cref="Settled"/> when <see cref="Amount"/> is zero;
/// null means no leg carried one. <see cref="Fee"/> is the fee leg's magnitude;
/// <see cref="Category"/> / <see cref="TransferAccount"/> are the projected slots
/// (null when that role leg is absent). On a transfer between currencies,
/// <see cref="TransferAmount"/> is the other side's own figure, in
/// <see cref="TransferCurrencyCode"/>, as the investment register's transfer slot shows
/// it (ADR-0114 D14, "The register row"); both are null on a transfer in one currency,
/// where the other side is <see cref="Amount"/> negated, and when there is no transfer.</summary>
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
    decimal? Settled,
    decimal? Fee,
    string? Category,
    string? TransferAccount,
    decimal? TransferAmount,
    string? TransferCurrencyCode);

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

/// <param name="Source">Where the price came from (ADR-0054 D2 / ADR-0070):
/// <c>import</c>, <c>fetch</c>, <c>manual</c>, <c>simplefin</c> or
/// <c>trade</c>. Without it a reader cannot tell a figure someone typed from a
/// quote-feed close from one derived off a transaction, which are not equally
/// trustworthy.</param>
/// <param name="Unbacked">The row says a trade produced it and no trade
/// currently does. ADVISORY — it may still be correct, since ADR-0084 D4 keeps
/// the price a deleted trade left behind, and nothing records WHICH transaction
/// wrote a price, so this is inferred.</param>
public sealed record PricePoint(
    DateOnly PriceDate, decimal Price, decimal? High, decimal? Low, long? Volume,
    string Source, bool Unbacked);

/// <summary>Group dimension for investment income.</summary>
public enum InvestmentIncomeGroupBy { Security, Account }

/// <summary>One income bucket (ADR-0063 v2 <c>investment_income</c>): dividend /
/// interest / misc income from <c>posting_role='income'</c> legs, normalized to a
/// positive magnitude. <see cref="GroupId"/> is the security or brokerage id (null
/// for misc income with no security); <see cref="Period"/> is set when bucketed.</summary>
/// <remarks>
/// <see cref="Amount"/> is in <see cref="CurrencyCode"/>. A row grouped by account is that
/// brokerage's own figure and stays in its currency; a row grouped by security combines
/// brokerages and is in the result's currency, each leg converted at its transaction's
/// date (ADR-0114 D7). Null, with <see cref="RateUnavailable"/> naming each missing pair
/// and day, when a rate is missing (ADR-0114 D9).
/// </remarks>
public sealed record InvestmentIncomeRow(
    string? Period,
    Guid? GroupId,
    string? Ticker,
    string GroupName,
    decimal? Amount,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>Investment income: the rows and their total.</summary>
/// <remarks>
/// <see cref="CurrencyCode"/> is the ledger's reporting currency, or with one brokerage
/// asked for, that brokerage's. <see cref="Total"/> is every leg in it, each converted at
/// its transaction's date, summed and rounded once; null when a rate is missing, named in
/// <see cref="RateUnavailable"/> (ADR-0114 D7, D9).
/// </remarks>
public sealed record InvestmentIncomeResult(
    IReadOnlyList<InvestmentIncomeRow> Rows,
    decimal? Total,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>One security's realized gains over the window (ADR-0064 FIFO):
/// proceeds, FIFO cost basis consumed, and realized gain.</summary>
/// <summary>Per-security realized gains (FIFO). <see cref="RealizedGain"/> is the
/// total; <see cref="RealizedGainShortTerm"/> / <see cref="RealizedGainLongTerm"/>
/// split it by the holding period of the lots each sale consumed (long-term = held
/// more than one year; mig 169 / ADR-0064 D2). A single sale straddling the 1-year
/// line contributes to both.</summary>
/// <remarks>
/// In the result's <see cref="RealizedGainsResult.CurrencyCode"/> (ADR-0114 D7). A sale in
/// an account kept in another currency converts as a trade does: its proceeds at its trade
/// date's rate, the cost of each lot it consumed at that lot's acquisition date's, so the
/// realized gain includes the currency move between the two. A figure that needs a missing
/// rate is null, with every figure derived from it, and <see cref="RateUnavailable"/> names
/// each missing pair and day (ADR-0114 D9).
/// </remarks>
public sealed record RealizedGainSummaryRow(
    Guid SecurityId, string? Ticker, string Name,
    decimal? Proceeds, decimal? CostBasisSold, decimal? RealizedGain,
    decimal? RealizedGainShortTerm, decimal? RealizedGainLongTerm,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>Realized gains per security and their totals.</summary>
/// <remarks>
/// <see cref="CurrencyCode"/> is the ledger's reporting currency, or with one brokerage
/// asked for, that brokerage's own: a per-account figure stays native (ADR-0114 D7). The
/// totals are the rows' sums, so a row that needs a missing rate makes each total it feeds
/// null, and <see cref="RateUnavailable"/> names every missing pair and day.
/// </remarks>
public sealed record RealizedGainsResult(
    IReadOnlyList<RealizedGainSummaryRow> Rows,
    decimal? TotalProceeds,
    decimal? TotalCostBasisSold,
    decimal? TotalRealizedGain,
    decimal? TotalRealizedGainShortTerm,
    decimal? TotalRealizedGainLongTerm,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>
/// One brokerage's contribution to a LEDGER-scope returns report: what it was
/// worth at each end of the window, valued on exactly the same basis as the
/// report's own totals, so the rows SUM to them.
/// <para>
/// This exists because a report cannot otherwise know which accounts a window
/// covers. Selecting by current balance drops every account that held money at
/// the window's open and was emptied before its close — that hides the whole
/// opening value of every account a rollover drained, in a table whose rows
/// read as summing to the total beneath them. An account appears here when it
/// was worth something at EITHER
/// end of the window or saw any flow within it — so one emptied mid-window stays,
/// and only accounts that held nothing throughout and saw nothing move are
/// omitted, which changes no column's total.
/// </para>
/// <para>
/// Deliberately no per-account return or net-contribution figure. Both are
/// SCOPE-RELATIVE — a rollover is internal to the ledger and external to each
/// account — so producing them here would mean a second implementation of the
/// flow-classification rules running beside the first, and two copies of those
/// rules drift apart. Call returns again with accountId for those; the account-scope
/// path already gets them right, in-kind transfers included.
/// </para>
/// <para>
/// Each row is that brokerage's own figure, in its own <see cref="CurrencyCode"/> (ADR-0114
/// D7): a position priced in another currency converts into it at the instant's rate, and
/// the rows sum to the report's totals once each is converted into the report's currency
/// at the same instant's rate. A value that needs a missing rate is null, named in
/// <see cref="RateUnavailable"/> (ADR-0114 D9).
/// </para>
/// </summary>
public sealed record ReturnsAccountValue(
    Guid AccountId,
    string AccountName,
    decimal? StartValue,
    decimal? EndValue,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable);

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
/// <c>in_kind_transfers</c> (securities moved with no cash leg at all, valued at
/// market at the transfer instant, ADR-0065 D5), and <c>opening_balances</c> (a
/// brokerage's funded opening balance arriving inside the window on its start date, or
/// at its first posting when that is earlier: a brokerage is valued without it before
/// then; money in, or money out for a negative balance).
/// </para>
/// <para>In the report's currency, each flow converted at its date; null when one of them
/// needs a missing rate (ADR-0114 D9).</para>
/// </summary>
public sealed record ContributionSourceTotals(
    string Source,
    decimal? In,
    decimal? Out);

/// <summary>
/// Investment returns (ADR-0063 v2). Both figures are annualized fractions
/// (0.1 = 10%/yr), and each is paired with its own reason string: a null rate
/// ALWAYS carries a non-null explanation, and a non-null rate never does. Neither
/// figure is a reliable headline on its own — <see cref="MoneyWeightedReturn"/>
/// (XIRR) answers "what did my money earn", <see cref="TimeWeightedReturn"/>
/// answers "how did the holdings perform", and they diverge whenever cash flows
/// are large relative to the balance.
/// <para>
/// <b>Currency.</b> Every money figure, and both rates, are in <see cref="CurrencyCode"/>:
/// the ledger's reporting currency at ledger scope, the brokerage's own at account scope
/// (ADR-0114 D7). Each brokerage's value at each boundary converts at that boundary's day's
/// rate (the latest rate for a value at now), a position priced in another currency into
/// its brokerage's first, and each flow converts at its own date's rate, before anything
/// adds them; so neither rate ever runs over a sum of currencies, and the ledger figure is
/// unhedged. A figure that needs a missing rate is null, with every figure derived from
/// it, a null rate carries a reason saying so, and <see cref="RateUnavailable"/> names
/// every missing pair and day (ADR-0114 D9).
/// </para>
/// <para>
/// <see cref="Gain"/> is the window's change in value that contributions and
/// withdrawals do not explain: price movement, realized or not, plus dividends and
/// interest, less fees and investment expenses; not annualized; for the whole scope.
/// It is <see cref="EndValue"/> − <see cref="StartValue"/> −
/// <see cref="NetContributions"/> exactly. Measured from the start value rather than
/// from cost, it is neither the FIFO realized gain nor the unrealized gain against
/// cost basis, and like <see cref="NetContributions"/> it depends on the scope, so the
/// per-account rows do not carry it.
/// </para>
/// <para>
/// <b>The window's edges.</b> <see cref="StartDate"/> is inclusive and an explicit
/// end is exclusive, compared at the microsecond, the database's resolution.
/// <see cref="StartValue"/> is the portfolio at the last instant before the start,
/// and <see cref="EndValue"/> the portfolio at the last instant the window counts:
/// the instant before an explicit end, or, with no end given, the end of the
/// caller's today as the register counts it, which is counted. An event dated on an
/// edge therefore lands in exactly one window, and every flow valued into
/// <see cref="EndValue"/> is in <see cref="NetContributions"/>: a dividend or fee
/// dated the first day is this window's return, a contribution dated the start is
/// this window's flow, and one dated an explicit end is the next window's.
/// Since-inception is valued AT its anchor instead, so what arrived there without a
/// flow is opening capital; a flow landing on the anchor is in that valuation and is
/// taken back out of <see cref="StartValue"/>, so it too counts once, as a flow. A
/// since-inception start value is therefore zero only when the scope began with a
/// flow and held nothing before it; one that began with a funded opening
/// balance, or with assets that arrived without a flow, starts from that capital.
/// </para>
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
/// reliably misleads: a net outflow is equally consistent with a single withdrawal
/// of that size and with a larger outflow partly offset by money coming in. A reader
/// holding one salient event will bind the figure to it, reading a net outflow as
/// one withdrawal when a larger withdrawal was partly offset by contributions. So
/// the parts travel with the total:
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
    decimal? StartValue,
    decimal? EndValue,
    decimal? NetContributions,
    decimal? Gain,
    double? MoneyWeightedReturn,
    string? MoneyWeightedUnavailableReason,
    double? TimeWeightedReturn,
    string? TimeWeightedUnavailableReason,
    DateTime? TimeWeightedCoveredFrom,
    DateTime? TimeWeightedCoveredTo,
    double? TimeWeightedCoveredYears,
    int? TimeWeightedCoveredDays,
    IReadOnlyList<ReturnsAccountValue>? Accounts,
    decimal? ContributionsIn,
    decimal? ContributionsOut,
    IReadOnlyList<ContributionSourceTotals> ContributionsBySource,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable,
    DateTime ComputedAt,
    string EngineVersion) : IReportProvenance;

/// <summary>
/// What a time-weighted return would COST for a window, without computing one.
/// TWR values the portfolio once per instant at which money crossed the scope's
/// boundary, and that count — not the window's length or the number of trades —
/// is what the work scales with.
/// <para>
/// This exists so a caller can size a request before spending it. There is no
/// ceiling to compare against — migrations 200 and 201 batch both halves of a
/// boundary valuation, so an instant cap has nothing to protect — but the count
/// still tells a caller how much work a
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
/// Why a returns-series figure is null. Every null on a <see cref="ReturnsSeriesPoint"/>
/// names one of these, so a blank point always says why it is blank.
/// </summary>
public static class ReturnsSeriesReasons
{
    /// <summary>Nothing has been invested yet: no part of the window up to this point
    /// held a positive base. There is no performance to measure, and the index starts
    /// at its first invested point.</summary>
    public const string BeforeFirstFunding = "before_first_funding";

    /// <summary>Something was invested earlier, but at this point the account holds
    /// nothing and is not funded again yet. The index is not carried across the
    /// stretch; it resumes from its last value when money returns.</summary>
    public const string NoInvestedBase = "no_invested_base";

    /// <summary>The holdings were worth less than nothing (borrowed against the
    /// position), and a cumulative growth below zero is not an index.</summary>
    public const string NegativeCumulativeGrowth = "negative_cumulative_growth";

    /// <summary>The point needs an exchange rate that is missing (ADR-0114 D9): its own
    /// value, or a boundary or flow at or before it, converts at a rate the ledger does not
    /// hold. The point's <see cref="ReturnsSeriesPoint.RateUnavailable"/> names each pair and
    /// day.</summary>
    public const string RateUnavailable = global::Coffer.Api.Contracts.RateUnavailable.Code;

    /// <summary>The previous index is zero (a total loss), so a return from it has no
    /// base.</summary>
    public const string PriorIndexZero = "prior_index_zero";

    /// <summary>Nothing was invested when the 12 months ending at the point opened, so the
    /// money in them has been held for less than a year, and an annualized money-weighted
    /// rate would be a short stretch magnified.</summary>
    public const string UnderAYearCovered = "under_a_year_covered";

    /// <summary>The money-weighted solver had fewer than two dated flows.</summary>
    public const string TooFewFlows = "too_few_flows";

    /// <summary>Every flow ran one way, so no rate reconciles them.</summary>
    public const string SingleSignedFlows = "single_signed_flows";

    /// <summary>Every flow falls on one instant, so there is no time to annualize over.</summary>
    public const string ZeroLengthWindow = "zero_length_window";

    /// <summary>The flows offset exactly, so every rate satisfies them equally.</summary>
    public const string Indeterminate = "indeterminate";

    /// <summary>No rate between -99.99% and 10000% a year satisfies the flows.</summary>
    public const string NoRootInRange = "no_root_in_range";

    /// <summary>The benchmark asked for is not one of the ledger's benchmarks (ADR-0114 D1):
    /// it was removed after a report saved it, so the report keeps its pick and draws no
    /// line until it is added again. Given on <see cref="ReturnsBenchmark"/> and on every
    /// point's benchmark.</summary>
    public const string BenchmarkNotInLedger = "benchmark_not_in_ledger";

    /// <summary>The benchmark has no stored growth for a day the point needs (ADR-0114 D3):
    /// the point's first invested day is before the symbol's first stored growth day, or the
    /// point is after the last day its fetches have asked for. A point on the server's UTC
    /// today is a current value: it reads the newest stored day while the fetches reach
    /// yesterday, as a current value's rate does (ADR-0114 D9). The point's
    /// <see cref="ReturnsSeriesPoint.BenchmarkUnavailableDate"/> names the first day it needs
    /// with no value.</summary>
    public const string BenchmarkUnavailable = "benchmark_unavailable";
}

/// <summary>
/// The benchmark a returns series was asked to compare with (ADR-0114 D1, D3): the symbol as
/// asked, and, when it is one of the ledger's benchmarks, the name the footnote gives it and
/// the currency its total return is quoted in. When it is not one of them,
/// <see cref="Name"/> and <see cref="CurrencyCode"/> are null, no point has a benchmark
/// value, and <see cref="UnavailableReason"/> is
/// <see cref="ReturnsSeriesReasons.BenchmarkNotInLedger"/>.
/// </summary>
public sealed record ReturnsBenchmark(
    string Symbol,
    string? Name,
    string? CurrencyCode,
    string? UnavailableReason);

/// <summary>
/// One period end of a <see cref="ReturnsSeries"/>. <see cref="Index"/> is the
/// cumulative time-weighted growth from the first invested point of the window, on a
/// base of 100 and NOT annualized: 112.4 is 12.4% grown over <see cref="CoveredDays"/>
/// of invested time. <see cref="PeriodReturn"/> is the growth over the invested part
/// of this period, derived on the server from the index (this period's over the last
/// reported one), and is never to be summed or chained by a client. It is a decimal
/// fraction (one hundredth is 1%), as is <see cref="MwrTrailingYear"/>.
/// <para>
/// <see cref="Gain"/> is value minus the window's start value minus the net money in
/// since, in the series' own currency, for the whole scope; <see cref="MwrTrailingYear"/> is
/// the money-weighted return of the 12 months ending at this point, read from the scope's
/// history before the window where the year reaches back past its start (ADR-0104 D6,
/// amended 2026-11-21). It is the money-weighted return the scalar report gives for that
/// year as its window. A year is a year, so nothing is magnified; but a year that opened with
/// nothing invested is null with <see cref="ReturnsSeriesReasons.UnderAYearCovered"/>.
/// A null figure carries the reason beside it (<see cref="ReturnsSeriesReasons"/>) and a
/// present one carries none. A null is never a zero and never the last value carried
/// forward.
/// </para>
/// <para>
/// A point that needs a missing exchange rate (ADR-0114 D9) is null in every figure, each
/// reason is <see cref="ReturnsSeriesReasons.RateUnavailable"/>, and
/// <see cref="RateUnavailable"/> names each missing pair and day. A rate missing only for
/// the year before the window leaves <see cref="MwrTrailingYear"/> alone null with that
/// reason, and <see cref="RateUnavailable"/> names it. It is empty on every other point.
/// </para>
/// <para>
/// <see cref="Benchmark"/> is the requested benchmark's total return over the same invested
/// time as <see cref="Index"/>, on the same base: 100 at the series' first invested point,
/// paused wherever the series has no invested base and resumed from the value it held, and
/// converted into the series' currency at each day's rate, unhedged (ADR-0114 D3). It is
/// already chained and rebased; no client chains or rebases it. With no benchmark asked
/// for, it and its reason are null. A null benchmark otherwise carries
/// <see cref="BenchmarkUnavailableReason"/>: the index's own reason where the series has no
/// invested time to chain over (<c>before_first_funding</c>, <c>no_invested_base</c>, or the
/// index's <c>rate_unavailable</c>, whose pairs <see cref="RateUnavailable"/> names);
/// <c>benchmark_not_in_ledger</c>; <c>benchmark_unavailable</c>, with
/// <see cref="BenchmarkUnavailableDate"/>; or <c>rate_unavailable</c> for the benchmark's
/// own conversion, with <see cref="BenchmarkRateUnavailable"/> naming each missing pair and
/// day (ADR-0114 D9). <see cref="BenchmarkUnavailableDate"/> is null for every other reason,
/// and <see cref="BenchmarkRateUnavailable"/> is empty.
/// </para>
/// </summary>
public sealed record ReturnsSeriesPoint(
    DateTime PeriodEnd,
    double? Index,
    string? IndexUnavailableReason,
    double? PeriodReturn,
    string? PeriodReturnUnavailableReason,
    int CoveredDays,
    decimal? Gain,
    string? GainUnavailableReason,
    double? MwrTrailingYear,
    string? MwrUnavailableReason,
    IReadOnlyList<RateUnavailable> RateUnavailable,
    double? Benchmark,
    string? BenchmarkUnavailableReason,
    DateOnly? BenchmarkUnavailableDate,
    IReadOnlyList<RateUnavailable> BenchmarkRateUnavailable);

/// <summary>
/// One line of a returns series: the ledger's brokerages together (<c>ledger</c>) or
/// one brokerage on its own (<c>account</c>). Each is computed from its own perimeter,
/// so a rollover between two brokerages is a flow to each account and nothing at
/// ledger scope. <see cref="CurrencyCode"/> is the ledger's reporting currency for the
/// ledger line, which is unhedged: each brokerage's value and flows are converted into it
/// at each instant's rate before the chain runs (ADR-0114 D7). A brokerage's own line is in
/// its own currency. <see cref="Points"/> has one entry per period end, the same period ends
/// for every series of a result.
/// </summary>
public sealed record ReturnsSeries(
    string Scope,
    Guid? AccountId,
    string? AccountName,
    string CurrencyCode,
    IReadOnlyList<ReturnsSeriesPoint> Points);

/// <summary>
/// Returns over time (ADR-0104 D6): the time-weighted index, the cumulative gain and
/// the money-weighted return of the 12 months ending there, at the end of each period of
/// <see cref="Interval"/> (<c>month</c>, <c>quarter</c> or <c>year</c>) in
/// [<see cref="From"/>, <see cref="To"/>], the last clamped to the caller's today.
/// <para>
/// Every series is the same time-weighted chain <c>returns</c> reports, run once over
/// the instants money crossed the scope's edge; a period end only READS it. Sampling
/// by month or by year therefore cannot change a figure, and the last point's index
/// is the window's time-weighted growth that <c>returns</c> reports annualized.
/// Money is in each series' <see cref="ReturnsSeries.CurrencyCode"/>.
/// </para>
/// <para>
/// <see cref="Benchmark"/> is the benchmark the request named, or null when it named none;
/// each point of every series then carries its value (<see cref="ReturnsSeriesPoint.Benchmark"/>).
/// </para>
/// </summary>
public sealed record ReturnsSeriesResult(
    DateTime From,
    DateTime To,
    string Interval,
    IReadOnlyList<ReturnsSeries> Series,
    ReturnsBenchmark? Benchmark,
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
