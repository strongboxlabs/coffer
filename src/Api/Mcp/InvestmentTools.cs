using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

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
        "so the three agree at any instant; omit it for now, the end of today as the " +
        "register counts it (what account_portfolio and list_accounts value). " +
        "costBasis is FIFO and is exact at ANY instant, not just now — the walk that " +
        "produces it is as-of-bounded and is the same one the recompute persists, so a " +
        "past basis is the basis as it stood then, never today's basis against a past " +
        "market value. unrealizedGain and its percentage follow from it. " +
        "Note latestPrice is the per-share price VALUATION USED, back-adjusted onto that " +
        "instant's split basis — for a past instant it is not the newest price on file. " +
        "Money is in currencyCode: the ledger's reporting currency, or with accountId that " +
        "brokerage's own currency. Each position converts from its price's currency into its " +
        "brokerage's, then into currencyCode, at the instant's day's rate (the latest rate " +
        "when asOfUtc is omitted); the cost basis of a position kept in another currency " +
        "converts lot by lot at each lot's acquisition date, so unrealizedGain includes the " +
        "currency move since each purchase. A figure that needs a missing exchange rate is " +
        "null, never a partial sum, and rateUnavailable (on the row and on the result) names " +
        "each missing pair and date; a null is never zero. Use list_accounts to resolve ids.")]
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
        "bucketed by month/quarter/year. Amounts are positive magnitudes. total and every " +
        "row grouped by security are in currencyCode: the ledger's reporting currency, or with " +
        "accountId that brokerage's own; each income leg converts from its brokerage's currency " +
        "at its transaction date's rate. A row grouped by account is that brokerage's own " +
        "figure and stays in its currency (the row's currencyCode). A figure that needs a " +
        "missing exchange rate is null, never a partial sum, and rateUnavailable names each " +
        "missing pair and date. Filter by accountId (brokerage) and/or securityId. " +
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
        "accountId (brokerage) and/or securityId. Every figure is in currencyCode: the ledger's " +
        "reporting currency, or with accountId that brokerage's own. A sale in an account kept " +
        "in another currency converts as a trade does: its proceeds at the sale's trade-date " +
        "rate and the cost of each lot it consumed at that lot's acquisition-date rate, so a " +
        "realized gain includes the currency move between purchase and sale. A figure that " +
        "needs a missing exchange rate is null, never a partial sum, and rateUnavailable names " +
        "each missing pair and date. Resolve ids via list_accounts / list_securities.")]
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
        "net contributions and gain. Both value the portfolio the same way at each boundary " +
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
        "portfolio's own earnings and costs, so they stay inside the return. " +
        "gain is the window's change in value that contributions and withdrawals do not " +
        "explain: price movement, realized or not, plus dividends and interest, less fees and " +
        "investment expenses; not annualized; for the whole scope. It is endValue - startValue - " +
        "netContributions exactly, measured from the start value rather than from cost, so it is " +
        "neither the FIFO realized gain (realized_gains) nor the unrealized gain against cost, and " +
        "like netContributions it depends on the scope. With no dates it's " +
        "since-inception to now. Pass accountId for one brokerage (scope='account'); " +
        "omit for the whole ledger (scope='ledger'). " +
        "At ledger scope the result also carries 'accounts': EVERY brokerage the report " +
        "covered, with its start and end value on the same basis, so those columns SUM to " +
        "the report's own. Use it to decide which accounts a window spans — do NOT pick " +
        "accounts by current balance, which silently drops any account that held money when " +
        "the window opened and was emptied before it closed (a rollover source, for example). " +
        "The roster carries no per-account rate or " +
        "contribution figure because both are scope-relative; call returns again with " +
        "accountId for those. " +
        "netContributions is a NET and can hide large offsetting movements, so do NOT " +
        "describe it as a single event: contributionsIn + contributionsOut == " +
        "netContributions exactly, and contributionsBySource splits both halves by origin " +
        "(external_accounts, other_investment_accounts, category_transfers, " +
        "in_kind_transfers, opening_balances). Characterize the composition from those fields, never by " +
        "matching the net against an event you already know about. " +
        "An account's opening balance is its money from its Start Date (or from its first " +
        "transaction, when that is earlier), never before: a window that opens before then, or " +
        "whose fromUtc is that day, values the account without it and counts it as a flow " +
        "(opening_balances) at that instant, money in for a positive balance and money out for " +
        "a negative one; a window that opens later holds it in startValue. Since-inception " +
        "starts no later than the first balance to arrive, holds that one in startValue, and takes " +
        "any that arrives later as a flow. " +
        "Every money figure and both rates are in currencyCode: the ledger's reporting currency " +
        "at ledger scope, the brokerage's own at account scope. Each brokerage's value at each " +
        "boundary converts at that day's rate (the latest rate for a value at now), a position " +
        "priced in another currency into its brokerage's first, and each flow at its own date's " +
        "rate, BEFORE anything is added; so neither rate is computed over a sum of currencies, " +
        "and the ledger figure is unhedged (it includes the currency moves). Each 'accounts' row " +
        "is that brokerage's own figure, in its own currencyCode. A figure that needs a missing " +
        "exchange rate is null, never a partial sum; a rate it nulls carries a reason saying so, " +
        "and rateUnavailable names each missing pair and date.")]
    public static async Task<ReturnsResult> Returns(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("One brokerage account (GUID) for account scope. Omit for the whole ledger.")]
        Guid? accountId = null,
        [Description("Inclusive start (UTC ISO-8601). startValue is the portfolio just before it, so " +
                     "anything dated at it (a dividend, a fee, a contribution) is inside the window. " +
                     "Omit for since-inception (exact).")]
        DateTime? fromUtc = null,
        [Description("Exclusive end (UTC ISO-8601). endValue is the portfolio just before it, so " +
                     "anything dated at it belongs to the next window. Omit for now, the end of today " +
                     "as the register counts it, which is itself counted.")]
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default) =>
        // No date: an MCP caller gets the register's own fallback (ScheduledBoundary).
        await repository.ReturnsAsync(
            ledgerId, accountId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc), today: null, cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "activity"), Description(
        "Investment transaction activity for a ledger — each investment event " +
        "(Buy / Sell / Dividend / Reinvest / Transfer / Misc, including any fee) " +
        "collapsed into ONE row, newest first. Fields per event: date, brokerage " +
        "account, action, security (ticker + name), quantity, unit price, net cash " +
        "amount, fee, and the category / transfer counterparty when present. Filter " +
        "by accountId (brokerage), securityId, and/or a fromUtc/toUtc window; pass a " +
        "window for a large ledger. Each event's amounts are in its brokerage's currency, " +
        "unconverted. On a transfer between accounts in two currencies, transferAmount is " +
        "the amount that arrived in (or left) the other account, in that account's " +
        "currency, named by transferCurrencyCode; both are null on a transfer in one " +
        "currency (the other side is the amount negated) and when there is no transfer. " +
        "Resolve ids via list_accounts / list_securities.")]
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
        [Description("Exclusive end (UTC ISO-8601). Omit for now, the end of today as the register counts it.")]
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default) =>
        await repository.ReturnsCostEstimateAsync(
            ledgerId, accountId, NormalizeUtc(fromUtc), NormalizeUtc(toUtc), today: null, cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "returns_series"), Description(
        "Investment returns OVER TIME for a ledger or one brokerage account: one point at the end " +
        "of each period (interval 'month' | 'quarter' | 'year'), the last clamped to now, the end of " +
        "today as the register counts it. Each point carries index (the cumulative time-weighted " +
        "growth from the first invested point, base 100; CUMULATIVE, not annualized: 112.4 is 12.4% " +
        "grown over coveredDays of invested time), periodReturn (growth over the invested part of " +
        "that period, derived on the server: never sum or chain it), gain (value less the window's " +
        "start value less net money in since, in the series' own currency) and mwrTrailingYear (the " +
        "money-weighted return of the 12 months ending at that point, read from the history before " +
        "fromUtc where the year reaches back past it: the 'returns' money-weighted figure for that " +
        "year as its window). " +
        "Every figure that is null carries its own reason beside it (indexUnavailableReason, " +
        "periodReturnUnavailableReason, gainUnavailableReason, mwrUnavailableReason): nothing " +
        "invested yet (before_first_funding), an emptied account (no_invested_base), and so on. " +
        "Never treat a null as zero or as the last value carried forward. mwrTrailingYear is null " +
        "with the reason under_a_year_covered when nothing was invested as its 12 months opened, " +
        "because annualizing a shorter holding magnifies it. " +
        "Omit fromUtc for since-inception: the series starts at the scope's own inception, the same " +
        "start 'returns' uses with no dates. Pass accountId for one brokerage (scope 'account'), or " +
        "set includeAccounts to add one series per brokerage that held something, after the ledger's. " +
        "Each series is in its currencyCode. The ledger series is in the ledger's reporting currency " +
        "and unhedged: each brokerage's value and flows are converted into it at each instant's rate " +
        "(the day's rate; the latest rate at now), a position priced in another currency into its " +
        "brokerage's first, before the chain runs, so no figure adds currencies. Each brokerage's own " +
        "series stays in its own currency. A point that needs a missing exchange rate is null in every " +
        "figure with the reason rate_unavailable, and its rateUnavailable list names each missing pair " +
        "and date; a rate missing only for the year before fromUtc leaves mwrTrailingYear alone null " +
        "that way. " +
        "Pass benchmark (one of the ledger's benchmark symbols, as Settings > Quotes lists them) to " +
        "compare: every point of every series then carries benchmark, that fund's total return over " +
        "the SAME invested time as the series' index, on the same base (100 at the series' first " +
        "invested point), paused wherever the series has nothing invested and resumed from the value " +
        "it held, and converted into the series' currency at each day's rate, unhedged. It arrives " +
        "already chained and rebased: compare it with index point by point, never chain it. A null " +
        "benchmark carries benchmarkUnavailableReason: the index's own reason where the series has no " +
        "invested time (before_first_funding, no_invested_base, rate_unavailable), benchmark_unavailable " +
        "when the fund has no stored growth for a day the point needs (benchmarkUnavailableDate names " +
        "the first such day; never chained across), or rate_unavailable for its own conversion " +
        "(benchmarkRateUnavailable names each pair and date). The result's benchmark names the fund " +
        "and its currency. A benchmark that is not one of the ledger's symbols is refused with " +
        "benchmark-not-in-ledger, and one that is not a symbol with benchmark-symbol-invalid. " +
        "The last point's index is the time-weighted growth 'returns' reports annualized, and " +
        "sampling by month or year never changes a figure. Every response carries computedAt and " +
        "engineVersion. Prefer a wider interval over long ranges; a range of more than 600 period " +
        "ends is refused.")]
    public static async Task<ReturnsSeriesResult> ReturnsSeries(
        InvestmentReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("One brokerage account (GUID) for account scope. Omit for the whole ledger.")]
        Guid? accountId = null,
        [Description("With no accountId, also return one series per brokerage that held something " +
                     "in the window. Default false.")]
        bool includeAccounts = false,
        [Description("Interval between points: 'month' (default), 'quarter', or 'year'.")]
        string interval = "month",
        [Description("Window start (UTC ISO-8601). Omit for since-inception (exact).")]
        DateTime? fromUtc = null,
        [Description("Window end (UTC ISO-8601). Omit for now; the last point never passes it.")]
        DateTime? toUtc = null,
        [Description("One of the ledger's benchmark symbols (for example VT) to draw beside every " +
                     "series. Omit for none.")]
        string? benchmark = null,
        CancellationToken cancellationToken = default)
    {
        var bucket = McpArgs.ParseEnum<ReportTimeBucket>(interval, "interval");
        if (benchmark is not null)
        {
            if (!BenchmarkSymbol.IsValid(benchmark))
                throw new ArgumentException(
                    $"{BusinessError.Codes.BenchmarkSymbolInvalid}: '{benchmark}' is not a benchmark symbol. "
                    + "Pass one of the ledger's benchmark symbols, upper-case, such as VT.");
            var symbols = await repository.BenchmarkSymbolsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
            if (!symbols.Contains(benchmark, StringComparer.Ordinal))
                throw NotInLedger(benchmark, symbols);
        }

        // No date: an MCP caller gets the register's own fallback (ScheduledBoundary).
        var result = await repository.ReturnsSeriesAsync(
                ledgerId, accountId, includeAccounts, NormalizeUtc(fromUtc), NormalizeUtc(toUtc),
                bucket, today: null, cancellationToken, benchmark)
            .ConfigureAwait(false);
        // Removed between the check and the read: refused the same way, not answered.
        if (result.Benchmark?.UnavailableReason == ReturnsSeriesReasons.BenchmarkNotInLedger)
            throw NotInLedger(benchmark!, null);
        return result;
    }

    private static ArgumentException NotInLedger(string benchmark, IReadOnlyList<string>? symbols) =>
        new($"{BusinessError.Codes.BenchmarkNotInLedger}: '{benchmark}' is not one of this ledger's benchmarks. "
            + (symbols is null
                ? "It was removed while the series was read."
                : symbols.Count == 0
                    ? "The ledger has none; owners and editors add them in Settings > Quotes."
                    : $"Its benchmarks are: {string.Join(", ", symbols)}."));

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
        "effect — when one such fund holds most of a portfolio, equity can read as a small " +
        "fraction of its true share. When the list is non-empty, say so rather than presenting the " +
        "buckets as complete; set_security_components is the fix. " +
        "totalMarketValue is SECURITIES ONLY; 'excludedBrokerageCash' is cash that has no " +
        "asset class to bucket, and the two together equal the endValue 'returns' reports " +
        "when both value the same instant: with neither asOfUtc nor toUtc, both value now; " +
        "a returns window with an explicit toUtc is valued just before it, so pass asOfUtc " +
        "one microsecond before that toUtc. asOfUtc values a PAST instant through the same " +
        "feeder returns uses; omit for now, the end of today as the register counts it. " +
        "Money is in the ledger's reporting currency (currencyCode): each position converts " +
        "from its price's currency into its brokerage's, then into currencyCode, and each " +
        "brokerage's cash into it, at the instant's day's rate (the latest rate when asOfUtc " +
        "is omitted). The one exception is an 'account' bucket, which is one brokerage's " +
        "figure and stays in that brokerage's currency (the bucket's own currencyCode); its " +
        "percent is still its share of the converted total. A figure that needs a missing " +
        "exchange rate is null, never a partial sum, and rateUnavailable names each missing " +
        "pair and date; a null is never zero. Use list_ledgers first to resolve ledgerId.")]
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
        "same calendar date, equal share quantity, distinct accounts in one currency (a " +
        "pair whose accounts are in different currencies is not listed, ADR-0114 D13). " +
        "These fabricate a " +
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
        "(date, close, high, low, volume, source), oldest first. Resolve " +
        "securityId via list_securities. `source` says where the figure came " +
        "from — manual, a quote feed, an import or a trade — and they are not " +
        "equally trustworthy. `unbacked` marks a price that says a trade " +
        "produced it while no trade currently does, usually because that trade " +
        "was deleted or edited; it MAY still be correct, so treat it as a " +
        "caution and not as an error.")]
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
