using System.Net.Http.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The investment results convert every figure that combines accounts into the ledger's
/// reporting currency (ADR-0114 D7), and a figure that needs a missing rate is null and
/// names it (ADR-0114 D9). Per-account figures stay in the account's own currency; a
/// position priced in another currency converts into its brokerage's first.
/// </summary>
/// <remarks>
/// Synthetic data only, through the same seeds the single-currency suites use: the trades
/// and dividends in the shapes the write paths build, the prices and the rates as their
/// writers store them.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentConversionTests : DatabaseTest
{
    public InvestmentConversionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private InvestmentReportingRepository NewRepo() => new(Db.NewDbContext(), TimeProvider.System);

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static DateOnly ServerToday => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>The day the fixtures buy on, and the rate a lot bought then converts at.</summary>
    private static readonly DateTime BuyDay = Utc(2026, 1, 5);

    private const decimal EurAtBuy = 1.10m;

    /// <summary>The latest EUR rate: stored three days before the server's today, covered
    /// through today.</summary>
    private const decimal EurLatest = 1.20m;

    private const decimal GbpRate = 1.25m;

    /// <summary>Stores <paramref name="rates"/> for one pair, covered through the server's
    /// UTC today, as the rate fetcher writes them (ADR-0114 D6).</summary>
    private async Task SeedRatesAsync(
        SyntheticLedger ledger, string from, string to, params (DateOnly Day, decimal Rate)[] rates)
    {
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        foreach (var (day, rate) in rates)
        {
            db.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = to,
                RateDate = day, Rate = rate, Source = FxRateSource.Frankfurter,
            });
        }
        db.FxPairCoverage.Add(new FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = to, FetchedThroughDate = ServerToday,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>EUR→USD at the buy day, and the latest a few days back.</summary>
    private Task SeedEurUsdAsync(SyntheticLedger ledger) =>
        SeedRatesAsync(ledger, "EUR", "USD",
            (DateOnly.FromDateTime(BuyDay), EurAtBuy), (ServerToday.AddDays(-3), EurLatest));

    /// <summary>
    /// A USD ledger with a USD brokerage and a EUR one. The USD brokerage holds USDF, priced
    /// in dollars, and GBPF, bought in dollars but closing in pounds; the EUR brokerage holds
    /// EURF, bought and closing in euros. Every buy is on <see cref="BuyDay"/>, every close
    /// on 2026-02-01.
    /// </summary>
    private async Task<Fixture> SeedTwoCurrencyPortfolioAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);   // reporting currency USD
        var usd = await ledger.AddInvestmentAccountAsync("Brokerage USD");
        var eur = await ledger.AddInvestmentAccountAsync("Brokerage EUR", currencyCode: "EUR");
        var usdFund = await ledger.AddSecurityAsync("Fund USD", "USDF");
        var gbpFund = await ledger.AddSecurityAsync("Fund GBP", "GBPF");
        var eurFund = await ledger.AddSecurityAsync("Fund EUR", "EURF");

        await ledger.AddInvestmentBuyAsync(usd.Id, usd.HoldingsAccountId!.Value, usdFund, 5m, 200m, BuyDay);
        await ledger.AddInvestmentBuyAsync(usd.Id, usd.HoldingsAccountId!.Value, gbpFund, 2m, 50m, BuyDay);
        await ledger.AddInvestmentBuyAsync(eur.Id, eur.HoldingsAccountId!.Value, eurFund, 10m, 100m, BuyDay);
        await ledger.AddSecurityPriceAsync(usdFund, 210m, Utc(2026, 2, 1));
        await ledger.AddSecurityPriceAsync(gbpFund, 40m, Utc(2026, 2, 1), currencyCode: "GBP");
        await ledger.AddSecurityPriceAsync(eurFund, 120m, Utc(2026, 2, 1), currencyCode: "EUR");
        await ledger.RecomputeHoldingsAsync();
        return new Fixture(ledger, usd, eur, usdFund, gbpFund, eurFund);
    }

    private sealed record Fixture(
        SyntheticLedger Ledger, AccountRow Usd, AccountRow Eur, Guid UsdFund, Guid GbpFund, Guid EurFund);

    // ----- holdings_snapshot ---------------------------------------------------------

    /// <summary>
    /// The ledger's snapshot is in the reporting currency: each position from its price's
    /// currency into its brokerage's, then into the ledger's, at the latest rate; a foreign
    /// position's cost basis at its lot's acquisition date, so the gain includes the FX move.
    /// </summary>
    [Fact]
    public async Task Holdings_snapshot_converts_values_at_the_latest_rate_and_cost_at_the_buy_date()
    {
        var f = await SeedTwoCurrencyPortfolioAsync();
        await SeedEurUsdAsync(f.Ledger);
        await SeedRatesAsync(f.Ledger, "GBP", "USD", (DateOnly.FromDateTime(BuyDay), GbpRate));

        var snap = await NewRepo().HoldingsSnapshotAsync(f.Ledger.LedgerId);

        Assert.Equal("USD", snap.CurrencyCode);
        Assert.Empty(snap.RateUnavailable);
        var eurRow = snap.Holdings.Single(h => h.SecurityId == f.EurFund);
        Assert.Equal(10m * 120m * EurLatest, eurRow.MarketValue);       // 1440
        Assert.Equal(10m * 100m * EurAtBuy, eurRow.CostBasis);          // 1100, at the buy date
        Assert.Equal(1440m - 1100m, eurRow.UnrealizedGain);
        Assert.Equal(144m, eurRow.LatestPrice);                         // in the snapshot's currency
        var gbpRow = snap.Holdings.Single(h => h.SecurityId == f.GbpFund);
        Assert.Equal(2m * 40m * GbpRate, gbpRow.MarketValue);           // GBP close into the USD brokerage
        Assert.Equal(100m, gbpRow.CostBasis);
        var usdRow = snap.Holdings.Single(h => h.SecurityId == f.UsdFund);
        Assert.Equal(1050m, usdRow.MarketValue);
        Assert.Equal(1000m, usdRow.CostBasis);

        Assert.Equal(1440m + 100m + 1050m, snap.TotalMarketValue);
        Assert.Equal(1100m + 100m + 1000m, snap.TotalCostBasis);
        Assert.Equal(snap.TotalMarketValue - snap.TotalCostBasis, snap.TotalUnrealizedGain);
    }

    /// <summary>A past instant converts at its own day's rate.</summary>
    [Fact]
    public async Task Holdings_snapshot_at_a_past_instant_converts_at_that_day()
    {
        var f = await SeedTwoCurrencyPortfolioAsync();
        await SeedEurUsdAsync(f.Ledger);
        await SeedRatesAsync(f.Ledger, "GBP", "USD", (DateOnly.FromDateTime(BuyDay), GbpRate));

        var snap = await NewRepo().HoldingsSnapshotAsync(
            f.Ledger.LedgerId, accountId: null, asOfUtc: new DateTime(2026, 3, 1, 23, 59, 59, DateTimeKind.Utc));

        var eurRow = snap.Holdings.Single(h => h.SecurityId == f.EurFund);
        Assert.Equal(10m * 120m * EurAtBuy, eurRow.MarketValue);        // the last rate on or before then
        Assert.Equal(10m * 100m * EurAtBuy, eurRow.CostBasis);
    }

    /// <summary>One brokerage's own snapshot is a per-account figure: in its own currency,
    /// unconverted.</summary>
    [Fact]
    public async Task Holdings_snapshot_of_one_brokerage_stays_in_its_currency()
    {
        var f = await SeedTwoCurrencyPortfolioAsync();   // no rate stored at all

        var snap = await NewRepo().HoldingsSnapshotAsync(f.Ledger.LedgerId, f.Eur.Id);

        Assert.Equal("EUR", snap.CurrencyCode);
        Assert.Empty(snap.RateUnavailable);
        var row = Assert.Single(snap.Holdings);
        Assert.Equal(1200m, row.MarketValue);
        Assert.Equal(1000m, row.CostBasis);
        Assert.Equal(1200m, snap.TotalMarketValue);
    }

    /// <summary>
    /// Fail-closed (ADR-0114 D9): a row that needs a missing rate is null with every figure
    /// derived from it and names the pair and day; each total it feeds is null, never a
    /// partial sum; a row that needs no rate keeps its figures.
    /// </summary>
    [Fact]
    public async Task Holdings_snapshot_with_a_missing_rate_is_null_and_names_it()
    {
        var f = await SeedTwoCurrencyPortfolioAsync();
        await SeedRatesAsync(f.Ledger, "GBP", "USD", (DateOnly.FromDateTime(BuyDay), GbpRate));   // no EUR rate

        var snap = await NewRepo().HoldingsSnapshotAsync(f.Ledger.LedgerId);

        var eurRow = snap.Holdings.Single(h => h.SecurityId == f.EurFund);
        Assert.Null(eurRow.MarketValue);
        Assert.Null(eurRow.CostBasis);
        Assert.Null(eurRow.UnrealizedGain);
        Assert.Null(eurRow.UnrealizedGainPct);
        Assert.Null(eurRow.LatestPrice);
        Assert.All(eurRow.RateUnavailable, w => Assert.Equal(("EUR", "USD", RateUnavailable.Code),
            (w.FromCurrency, w.ToCurrency, w.Reason)));
        Assert.Contains(eurRow.RateUnavailable, w => w.Date == DateOnly.FromDateTime(BuyDay));   // the lot
        Assert.Null(snap.TotalMarketValue);
        Assert.Null(snap.TotalCostBasis);
        Assert.Null(snap.TotalUnrealizedGain);
        Assert.NotEmpty(snap.RateUnavailable);
        Assert.Equal(1050m, snap.Holdings.Single(h => h.SecurityId == f.UsdFund).MarketValue);
        // The row that needs a missing rate sorts after every valued one.
        Assert.Equal(f.EurFund, snap.Holdings[^1].SecurityId);
    }

    // ----- allocation ----------------------------------------------------------------

    /// <summary>
    /// Every bucket, the total and the excluded cash are in the reporting currency; an
    /// account bucket is one brokerage's figure, in its own currency, with its percent of
    /// the converted total.
    /// </summary>
    [Fact]
    public async Task Allocation_converts_buckets_and_cash_and_keeps_account_buckets_native()
    {
        var f = await SeedTwoCurrencyPortfolioAsync();
        await SeedEurUsdAsync(f.Ledger);
        await SeedRatesAsync(f.Ledger, "GBP", "USD", (DateOnly.FromDateTime(BuyDay), GbpRate));

        var byClass = await NewRepo().AllocationAsync(f.Ledger.LedgerId);
        Assert.Equal("USD", byClass.CurrencyCode);
        Assert.Empty(byClass.RateUnavailable);
        Assert.Equal(1440m + 100m + 1050m, byClass.TotalMarketValue);
        var equity = Assert.Single(byClass.Buckets);
        Assert.Equal(("equity", (decimal?)2590m, (decimal?)100m, "USD"),
            (equity.Bucket, equity.MarketValue, equity.Percent, equity.CurrencyCode));
        // Cash: each buy spent its brokerage's cash, the EUR one's at the latest rate.
        Assert.Equal(-1100m + (-1000m * EurLatest), byClass.ExcludedBrokerageCash);

        var byAccount = await NewRepo().AllocationAsync(f.Ledger.LedgerId, AllocationDimension.Account);
        var eurBucket = byAccount.Buckets.Single(b => b.Bucket == "Brokerage EUR");
        Assert.Equal(("EUR", (decimal?)1200m), (eurBucket.CurrencyCode, eurBucket.MarketValue));
        Assert.Equal(ReportingScale.Percent(1440m, 2590m), eurBucket.Percent);
        var usdBucket = byAccount.Buckets.Single(b => b.Bucket == "Brokerage USD");
        Assert.Equal(("USD", (decimal?)1150m), (usdBucket.CurrencyCode, usdBucket.MarketValue));
        // Ordered by what each is worth in the reporting currency: 1440 before 1150.
        Assert.Equal(new[] { "Brokerage EUR", "Brokerage USD" }, byAccount.Buckets.Select(b => b.Bucket));
    }

    /// <summary>Fail-closed (ADR-0114 D9): a missing rate nulls every figure that needs it
    /// and names the pair; an account bucket keeps its native value but has no percent of a
    /// total that cannot be formed.</summary>
    [Fact]
    public async Task Allocation_with_a_missing_rate_is_null_and_names_it()
    {
        var f = await SeedTwoCurrencyPortfolioAsync();
        await SeedRatesAsync(f.Ledger, "GBP", "USD", (DateOnly.FromDateTime(BuyDay), GbpRate));   // no EUR rate

        var byClass = await NewRepo().AllocationAsync(f.Ledger.LedgerId);
        Assert.Null(byClass.TotalMarketValue);
        Assert.Null(byClass.ExcludedBrokerageCash);
        var equity = Assert.Single(byClass.Buckets);
        Assert.Null(equity.MarketValue);
        Assert.Null(equity.Percent);
        Assert.Contains(equity.RateUnavailable, w => (w.FromCurrency, w.ToCurrency) == ("EUR", "USD"));
        Assert.Contains(byClass.RateUnavailable, w => (w.FromCurrency, w.ToCurrency) == ("EUR", "USD"));

        var byAccount = await NewRepo().AllocationAsync(f.Ledger.LedgerId, AllocationDimension.Account);
        var eurBucket = byAccount.Buckets.Single(b => b.Bucket == "Brokerage EUR");
        Assert.Equal(1200m, eurBucket.MarketValue);
        Assert.Null(eurBucket.Percent);
        Assert.Equal(1150m, byAccount.Buckets.Single(b => b.Bucket == "Brokerage USD").MarketValue);
    }

    // ----- the scalar returns ---------------------------------------------------------

    /// <summary>The constant EUR→USD rate the returns fixtures convert at.</summary>
    private const decimal ReturnsRate = 1.25m;

    /// <summary>
    /// A USD ledger with two brokerages funded from banks and buying a fund each. The second
    /// brokerage is in <paramref name="secondCurrency"/>: in EUR with its amounts and prices
    /// as given, or in USD with every amount and price of it times <see cref="ReturnsRate"/>,
    /// which is what the EUR one is worth converted at that constant rate.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, AccountRow First, AccountRow Second)> SeedReturnsLedgerAsync(
        string secondCurrency)
    {
        var scale = secondCurrency == "USD" ? ReturnsRate : 1m;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usdBank = await ledger.AddBankAccountAsync("Bank USD");
        var otherBank = await ledger.AddBankAccountAsync("Bank Other", currencyCode: secondCurrency);
        var first = await ledger.AddInvestmentAccountAsync("Brokerage First");
        var second = await ledger.AddInvestmentAccountAsync("Brokerage Second", currencyCode: secondCurrency);
        var usdFund = await ledger.AddSecurityAsync("Fund USD", "USDF");
        var otherFund = await ledger.AddSecurityAsync("Fund Other", "OTHF");

        await ledger.AddTransactionPairAsync(first.Id, usdBank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(first.Id, first.HoldingsAccountId!.Value, usdFund, 100m, 50m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(second.Id, otherBank.Id, 8_000m * scale, Utc(2024, 2, 10));
        await ledger.AddInvestmentBuyAsync(
            second.Id, second.HoldingsAccountId!.Value, otherFund, 40m, 100m * scale, Utc(2024, 2, 10));
        await ledger.AddTransactionPairAsync(otherBank.Id, second.Id, 1_000m * scale, Utc(2024, 5, 15));
        await ledger.AddTransactionPairAsync(first.Id, usdBank.Id, 2_000m, Utc(2024, 7, 1));
        await ledger.AddSecurityPriceAsync(usdFund, 55m, Utc(2024, 3, 1));
        await ledger.AddSecurityPriceAsync(usdFund, 61m, Utc(2024, 8, 1));
        await ledger.AddSecurityPriceAsync(otherFund, 110m * scale, Utc(2024, 4, 1), currencyCode: secondCurrency);
        await ledger.AddSecurityPriceAsync(otherFund, 104m * scale, Utc(2024, 9, 1), currencyCode: secondCurrency);
        if (secondCurrency == "EUR")
            await SeedRatesAsync(ledger, "EUR", "USD", (new DateOnly(2023, 12, 1), ReturnsRate));
        return (ledger, first, second);
    }

    private async Task<ReturnsResult> LedgerReturnsAsync(SyntheticLedger ledger, Guid? accountId = null)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsAsync(
            ledger.LedgerId, accountId, fromUtc: null, toUtc: Utc(2024, 12, 1), today: null);
    }

    /// <summary>
    /// The scalar returns never sums currencies (ADR-0114, the defects fixed): at a constant
    /// rate, a ledger with a EUR brokerage reads exactly as the same ledger with that
    /// brokerage kept in USD at the converted amounts, every figure and both rates; a sum of
    /// the native figures would read the EUR amounts as dollars and differ in each.
    /// </summary>
    [Fact]
    public async Task Scalar_returns_converts_before_adding_and_never_sums_currencies()
    {
        var (mixed, _, mixedSecond) = await SeedReturnsLedgerAsync("EUR");
        var (single, _, _) = await SeedReturnsLedgerAsync("USD");

        var converted = await LedgerReturnsAsync(mixed);
        var expected = await LedgerReturnsAsync(single);

        Assert.Equal("USD", converted.CurrencyCode);
        Assert.Empty(converted.RateUnavailable);
        Assert.Equal(expected.StartValue, converted.StartValue);
        Assert.Equal(expected.EndValue, converted.EndValue);
        Assert.Equal(expected.NetContributions, converted.NetContributions);
        Assert.Equal(expected.Gain, converted.Gain);
        Assert.Equal(expected.ContributionsIn, converted.ContributionsIn);
        Assert.Equal(expected.ContributionsOut, converted.ContributionsOut);
        Assert.NotNull(expected.TimeWeightedReturn);
        Assert.NotNull(expected.MoneyWeightedReturn);
        Assert.Equal(expected.TimeWeightedReturn!.Value, converted.TimeWeightedReturn!.Value, 12);
        Assert.Equal(expected.MoneyWeightedReturn!.Value, converted.MoneyWeightedReturn!.Value, 12);
        // The EUR brokerage's contributions are not dollars: each converts at the rate,
        // and only then joins the dollar brokerage's.
        Assert.Equal(10_000m + 10_000m - 1_250m + 2_000m, converted.NetContributions);

        // Each roster row is that brokerage's own figure, in its own currency.
        var row = converted.Accounts!.Single(a => a.AccountId == mixedSecond.Id);
        Assert.Equal("EUR", row.CurrencyCode);
        var own = await LedgerReturnsAsync(mixed, mixedSecond.Id);
        Assert.Equal("EUR", own.CurrencyCode);
        Assert.Equal(own.EndValue, row.EndValue);
        Assert.Equal(converted.EndValue,
            converted.Accounts!.Sum(a => a.CurrencyCode == "EUR" ? a.EndValue * ReturnsRate : a.EndValue));
    }

    /// <summary>Fail-closed (ADR-0114 D9): with no EUR rate, every figure the EUR brokerage
    /// feeds is null, both rates are null with a reason saying why, and the pair is named;
    /// the start, before the EUR brokerage held anything, needs no rate.</summary>
    [Fact]
    public async Task Scalar_returns_with_a_missing_rate_is_null_and_names_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var second = await ledger.AddInvestmentAccountAsync("Brokerage EUR", currencyCode: "EUR");
        var usdBank = await ledger.AddBankAccountAsync("Bank USD");
        var eurBank = await ledger.AddBankAccountAsync("Bank EUR", currencyCode: "EUR");
        var first = await ledger.AddInvestmentAccountAsync("Brokerage USD");
        await ledger.AddTransactionPairAsync(first.Id, usdBank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(second.Id, eurBank.Id, 8_000m, Utc(2024, 2, 10));

        var result = await LedgerReturnsAsync(ledger);

        Assert.Equal(0m, result.StartValue);   // valued at the first flow, taken back out
        Assert.Null(result.EndValue);
        Assert.Null(result.NetContributions);
        Assert.Null(result.Gain);
        Assert.Null(result.ContributionsIn);
        Assert.Null(result.MoneyWeightedReturn);
        Assert.Null(result.TimeWeightedReturn);
        Assert.Contains("exchange rate", result.MoneyWeightedUnavailableReason, StringComparison.Ordinal);
        Assert.Contains("exchange rate", result.TimeWeightedUnavailableReason, StringComparison.Ordinal);
        Assert.NotEmpty(result.RateUnavailable);
        Assert.All(result.RateUnavailable, w => Assert.Equal(("EUR", "USD"), (w.FromCurrency, w.ToCurrency)));
        // The brokerage's own row needs no rate.
        Assert.Equal(8_000m, result.Accounts!.Single(a => a.AccountId == second.Id).EndValue);
    }

    /// <summary>Counts the statements that read the rate tables.</summary>
    private sealed class RateStatements : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Sent { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("fx_rates", StringComparison.Ordinal)
                || command.CommandText.Contains("fx_pair_coverage", StringComparison.Ordinal))
                Sent++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// The read shape the conversion depends on: every rate a read converts at comes from
    /// one load (ExchangeRateLookup's at most three statements), however many period ends,
    /// instants or series it values. A read that loaded rates per point, per instant or per
    /// series would send more for months than for years, and more with the brokerages'
    /// own series than without.
    /// </summary>
    [Fact]
    public async Task Every_converted_read_loads_its_rates_once_whatever_it_values()
    {
        var (ledger, _, second) = await SeedReturnsLedgerAsync("EUR");

        async Task<int> CountAsync(Func<InvestmentReportingRepository, Task> read)
        {
            var statements = new RateStatements();
            await using var db = Db.NewRequestDbContextAsUser(ledger.UserId, statements);
            await read(new InvestmentReportingRepository(db, TimeProvider.System));
            return statements.Sent;
        }

        var from = Utc(2024, 1, 1);
        var to = Utc(2024, 12, 31);
        var months = await CountAsync(r => r.ReturnsSeriesAsync(
            ledger.LedgerId, null, true, from, to, ReportTimeBucket.Month, today: null));
        var years = await CountAsync(r => r.ReturnsSeriesAsync(
            ledger.LedgerId, null, false, from, to, ReportTimeBucket.Year, today: null));
        var scalar = await CountAsync(r => r.ReturnsAsync(ledger.LedgerId, null, from, to, today: null));
        var snapshot = await CountAsync(r => r.HoldingsSnapshotAsync(ledger.LedgerId));
        var allocation = await CountAsync(r => r.AllocationAsync(ledger.LedgerId));

        // Anchor: each read did convert, so each read rates at all.
        Assert.All(new[] { months, years, scalar, snapshot, allocation }, n => Assert.InRange(n, 1, 3));
        Assert.Equal(years, months);

        // One brokerage's own series converts nothing, so it reads no rate.
        Assert.Equal(0, await CountAsync(r => r.ReturnsSeriesAsync(
            ledger.LedgerId, second.Id, false, from, to, ReportTimeBucket.Month, today: null)));
    }

    // ----- investment_income and realized_gains, written through the API ---------------

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task PostTradeAsync(
        HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.Equal(System.Net.HttpStatusCode.Created, resp.StatusCode);
    }

    private static DateTime Noon(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime Noon(DateOnly day) => Noon(day.Year, day.Month, day.Day);

    private static readonly DateOnly OldLotDay = new(2024, 1, 2);
    private static readonly DateOnly SaleDay = new(2026, 3, 2);
    private static readonly DateOnly AprilDay = new(2026, 4, 1);

    /// <summary>EUR→USD on each day the income and gains fixtures touch, and the latest.</summary>
    private Task SeedEurUsdDailyAsync(SyntheticLedger ledger, bool withOldLot = true)
    {
        var rates = new List<(DateOnly, decimal)>
        {
            (DateOnly.FromDateTime(BuyDay), 1.10m), (SaleDay, 1.15m), (AprilDay, 1.18m),
            (ServerToday.AddDays(-3), EurLatest),
        };
        if (withOldLot) rates.Insert(0, (OldLotDay, 1.05m));
        return SeedRatesAsync(ledger, "EUR", "USD", [.. rates]);
    }

    /// <summary>
    /// Dividends of one fund from a USD brokerage (one, on the sale day) and a EUR one (one
    /// on the sale day and one on 1 April), written as the investment entry form writes
    /// them.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, AccountRow Usd, AccountRow Eur, Guid Fund)> SeedDividendsAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddInvestmentAccountAsync("Brokerage USD");
        var eur = await ledger.AddInvestmentAccountAsync("Brokerage EUR", currencyCode: "EUR");
        var fund = await ledger.AddSecurityAsync("Fund", "FND");
        var dividends = await ledger.AddCategoryAsync("Dividends", "income");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        foreach (var (brokerage, amount, on) in new[]
                 {
                     (usd.Id, 75m, Noon(2026, 3, 2)), (eur.Id, 50m, Noon(2026, 3, 2)), (eur.Id, 30m, Noon(2026, 4, 1)),
                 })
        {
            await PostTradeAsync(client, ledger, new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage, Action = "dividend_cash", SecurityId = fund, Amount = amount,
                CategoryAccountId = dividends.Id, PostedAt = on,
            });
        }
        return (ledger, usd, eur, fund);
    }

    /// <summary>
    /// A security row combines brokerages, so it converts, each leg at its own date; an
    /// account row is that brokerage's own figure and stays native; the total converts.
    /// </summary>
    [Fact]
    public async Task Investment_income_converts_each_leg_at_its_date_and_keeps_account_rows_native()
    {
        var (ledger, usd, eur, fund) = await SeedDividendsAsync();
        await SeedEurUsdDailyAsync(ledger);
        var expected = 75m + (50m * 1.15m) + (30m * 1.18m);   // 167.9

        var bySecurity = await NewRepo().IncomeAsync(
            ledger.LedgerId, null, null, null, null, InvestmentIncomeGroupBy.Security, ReportTimeBucket.None);
        Assert.Equal("USD", bySecurity.CurrencyCode);
        Assert.Empty(bySecurity.RateUnavailable);
        var row = Assert.Single(bySecurity.Rows);
        Assert.Equal((fund, (decimal?)expected, "USD"), (row.GroupId!.Value, row.Amount, row.CurrencyCode));
        Assert.Equal(expected, bySecurity.Total);

        var byAccount = await NewRepo().IncomeAsync(
            ledger.LedgerId, null, null, null, null, InvestmentIncomeGroupBy.Account, ReportTimeBucket.None);
        Assert.Equal(expected, byAccount.Total);
        // Ordered by what each is worth together: the EUR brokerage's dividends, converted,
        // come before the USD brokerage's smaller ones.
        Assert.Equal(new[] { (eur.Id, (decimal?)80m, "EUR"), (usd.Id, (decimal?)75m, "USD") },
            byAccount.Rows.Select(r => (r.GroupId!.Value, r.Amount, r.CurrencyCode)));

        var oneBrokerage = await NewRepo().IncomeAsync(
            ledger.LedgerId, null, null, eur.Id, null, InvestmentIncomeGroupBy.Security, ReportTimeBucket.None);
        Assert.Equal(("EUR", (decimal?)80m), (oneBrokerage.CurrencyCode, oneBrokerage.Total));
    }

    /// <summary>Fail-closed (ADR-0114 D9): with no EUR rate, every figure that combines the
    /// EUR brokerage is null and names the pair and each leg's day; the account rows keep
    /// their own figures.</summary>
    [Fact]
    public async Task Investment_income_with_a_missing_rate_is_null_and_names_it()
    {
        var (ledger, usd, eur, _) = await SeedDividendsAsync();

        var bySecurity = await NewRepo().IncomeAsync(
            ledger.LedgerId, null, null, null, null, InvestmentIncomeGroupBy.Security, ReportTimeBucket.None);
        var row = Assert.Single(bySecurity.Rows);
        Assert.Null(row.Amount);
        Assert.Null(bySecurity.Total);
        Assert.Equal(new[] { SaleDay, AprilDay }, bySecurity.RateUnavailable.Select(w => w.Date).Order());
        Assert.All(bySecurity.RateUnavailable, w => Assert.Equal(("EUR", "USD"), (w.FromCurrency, w.ToCurrency)));

        var byAccount = await NewRepo().IncomeAsync(
            ledger.LedgerId, null, null, null, null, InvestmentIncomeGroupBy.Account, ReportTimeBucket.None);
        Assert.Null(byAccount.Total);
        Assert.Equal(80m, byAccount.Rows.Single(r => r.GroupId == eur.Id).Amount);
        Assert.Equal(75m, byAccount.Rows.Single(r => r.GroupId == usd.Id).Amount);
    }

    /// <summary>
    /// The same fund sold from a USD brokerage and a EUR one, written as the investment
    /// entry form writes them. The EUR brokerage buys an older lot on
    /// <see cref="OldLotDay"/> and a newer one on <see cref="BuyDay"/>, and on
    /// <see cref="SaleDay"/> sells more shares than the older lot holds, so FIFO takes the
    /// whole older lot, long-term, and part of the newer one.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, AccountRow Eur, Guid Fund)> SeedSalesAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddInvestmentAccountAsync("Brokerage USD");
        var eur = await ledger.AddInvestmentAccountAsync("Brokerage EUR", currencyCode: "EUR");
        var fund = await ledger.AddSecurityAsync("Fund", "FND");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var buyDay = DateOnly.FromDateTime(BuyDay);
        foreach (var (brokerage, action, shares, price, on) in new[]
                 {
                     (usd.Id, "buy", 5m, 200m, Noon(buyDay)),
                     (usd.Id, "sell", -1m, 250m, Noon(SaleDay)),
                     (eur.Id, "buy", 2m, 80m, Noon(OldLotDay)),
                     (eur.Id, "buy", 10m, 100m, Noon(buyDay)),
                     (eur.Id, "sell", -4m, 150m, Noon(SaleDay)),
                 })
        {
            await PostTradeAsync(client, ledger, new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage, Action = action, SecurityId = fund, Shares = shares, Price = price,
                PostedAt = on,
            });
        }
        return (ledger, eur, fund);
    }

    /// <summary>
    /// A realized gain converts as a trade does (ADR-0114 D7): the proceeds at the sale's
    /// date, each lot's cost at its acquisition date, so the gain includes the FX move; the
    /// long-term part is the long-term lots' share. One brokerage's own gains stay native.
    /// </summary>
    [Fact]
    public async Task Realized_gains_convert_the_sale_at_its_date_and_each_lot_at_its_acquisition()
    {
        var (ledger, eur, fund) = await SeedSalesAsync();
        await SeedEurUsdDailyAsync(ledger);

        var all = await NewRepo().RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal("USD", all.CurrencyCode);
        Assert.Empty(all.RateUnavailable);
        var row = Assert.Single(all.Rows);
        Assert.Equal(fund, row.SecurityId);
        // The USD brokerage's sale is as stored. The EUR sale's takings convert at the sale
        // day's rate; its cost is the old lots at their day's rate (long-term) plus the new
        // lots at the buy day's (short-term).
        Assert.Equal(250m + 690m, row.Proceeds);
        Assert.Equal(200m + 168m + 220m, row.CostBasisSold);
        Assert.Equal(50m + (690m - 388m), row.RealizedGain);
        Assert.Equal((300m * 1.15m) - 168m, row.RealizedGainLongTerm);   // half the proceeds, the old lot
        Assert.Equal(row.RealizedGain - row.RealizedGainLongTerm, row.RealizedGainShortTerm);
        Assert.Equal(row.RealizedGain, all.TotalRealizedGain);
        Assert.Equal(row.RealizedGainLongTerm, all.TotalRealizedGainLongTerm);

        var native = await NewRepo().RealizedGainsAsync(ledger.LedgerId, null, null, eur.Id, null);
        Assert.Equal("EUR", native.CurrencyCode);
        var own = Assert.Single(native.Rows);
        Assert.Equal((600m, 360m, 240m, 140m),
            (own.Proceeds!.Value, own.CostBasisSold!.Value, own.RealizedGain!.Value, own.RealizedGainLongTerm!.Value));
    }

    /// <summary>Fail-closed (ADR-0114 D9): with no rate on or before the old lot's day, the
    /// cost and the gains are null and name that pair and day; the sale's own rate is there,
    /// so the proceeds still convert.</summary>
    [Fact]
    public async Task Realized_gains_with_a_lot_rate_missing_are_null_and_name_it()
    {
        var (ledger, _, _) = await SeedSalesAsync();
        await SeedEurUsdDailyAsync(ledger, withOldLot: false);

        var all = await NewRepo().RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        var row = Assert.Single(all.Rows);
        Assert.Equal(250m + 690m, row.Proceeds);
        Assert.Null(row.CostBasisSold);
        Assert.Null(row.RealizedGain);
        Assert.Null(row.RealizedGainLongTerm);
        Assert.Null(row.RealizedGainShortTerm);
        var why = Assert.Single(row.RateUnavailable);
        Assert.Equal(("EUR", "USD", OldLotDay), (why.FromCurrency, why.ToCurrency, why.Date));
        Assert.Null(all.TotalRealizedGain);
        Assert.Equal(250m + 690m, all.TotalProceeds);
        Assert.Equal(new[] { why }, all.RateUnavailable);
    }
}
