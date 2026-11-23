using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The benchmark beside a returns series (ADR-0114 D1, D3): the fund's total return chained
/// over the series' own invested time, rebased to 100 at its first invested point, paused
/// with it, converted into its currency at each day's rate, and null with a reason wherever a
/// day or a rate it needs is missing (ADR-0114 D9).
/// </summary>
/// <remarks>
/// <para>Every expected value is worked out here from the growth and the rates the case
/// stored (<see cref="BenchmarkSeed.Growth"/>), never read back from the engine. The growth of
/// each case's first invested day, and of every day a pause spans, is far from the rest, so
/// a line that counted the rebase day or chained across a pause is off by a visible factor.</para>
/// <para>Synthetic data only: the trades and flows through the seeding helpers, the benchmark
/// history and the rates through EF in the shapes their fetchers write.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsSeriesBenchmarkTests : DatabaseTest
{
    public ReturnsSeriesBenchmarkTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly DateTime Later = Utc(2030, 1, 1);

    private static readonly DateOnly FundedOn = D(2024, 1, 10);

    /// <summary>An ordinary day's growth, a little up or down by weekday.</summary>
    private static decimal Ordinary(DateOnly day) => 1m + ((day.DayNumber % 7) - 3) * 0.001m;

    /// <summary>The first invested day's own growth: it ends at that day's close, before the
    /// money arrived, so it is no part of the line.</summary>
    private const decimal FundingDayGrowth = 1.5m;

    private static decimal WithFundingDay(DateOnly day) => day == FundedOn ? FundingDayGrowth : Ordinary(day);

    /// <summary>EUR→USD, a different rate every day: it climbs a hundred-thousandth a day.</summary>
    private static decimal EurUsd(DateOnly day) => 1.05m + (day.DayNumber - 738_000) / 100_000m;

    private async Task<ReturnsSeriesResult> SeriesAsync(
        SyntheticLedger ledger, Guid? accountId, bool includeAccounts, DateTime from, DateTime to, string? benchmark)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
            ledger.LedgerId, accountId, includeAccounts, from, to, ReportTimeBucket.Month, Later,
            benchmark: benchmark);
    }

    private static ReturnsSeriesPoint At(ReturnsSeries series, int y, int m) =>
        series.Points.Single(p => p.PeriodEnd.Year == y && p.PeriodEnd.Month == m);

    private static DateOnly DayOf(ReturnsSeriesPoint p) => DateOnly.FromDateTime(p.PeriodEnd);

    /// <summary>One USD brokerage funded on <see cref="FundedOn"/>, its fund up 10% by March.</summary>
    private async Task<(SyntheticLedger Ledger, AccountRow Brokerage)> FundedBrokerageAsync(string currency = "USD")
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking", currencyCode: currency);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage", currencyCode: currency);
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 110m, Utc(2024, 3, 1), currencyCode: currency);
        return (ledger, brokerage);
    }

    // ----- rebase ---------------------------------------------------------------------

    /// <summary>
    /// The benchmark is 100 at the series' first invested point and grows by the fund's
    /// stored days after it: the funding day's own growth, which ends at that day's close, is
    /// not counted. Before the first funding it is null with the index's reason. Each series
    /// is rebased at its own first invested point, so a window that opens later starts at 100
    /// there.
    /// </summary>
    /// <remarks>Mutation caught: counting the rebase day's growth (every point off by 1.5);
    /// rebasing at the first stored growth day instead of the series' (the later window would
    /// not start at 100); dropping the growth since the last boundary (every point would read
    /// 100).</remarks>
    [Fact]
    public async Task The_benchmark_is_100_at_the_series_first_invested_point_and_grows_by_the_days_after_it()
    {
        var (ledger, brokerage) = await FundedBrokerageAsync();
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        var stored = await BenchmarkSeed.SeedGrowthAsync(
            Db, ledger, "BMK", D(2024, 1, 1), D(2024, 6, 30), WithFundingDay);

        var result = await SeriesAsync(ledger, null, true, Utc(2023, 12, 1), Utc(2024, 6, 30), "BMK");

        Assert.Equal(new ReturnsBenchmark("BMK", "BMK Total Market Fund", "USD", null), result.Benchmark);
        Assert.Equal(2, result.Series.Count);
        foreach (var series in result.Series)
        {
            var december = At(series, 2023, 12);
            Assert.Null(december.Benchmark);
            Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, december.BenchmarkUnavailableReason);

            foreach (var p in series.Points.Skip(1))
            {
                Assert.NotNull(p.Index);
                Assert.Null(p.BenchmarkUnavailableReason);
                Assert.Null(p.BenchmarkUnavailableDate);
                Assert.Empty(p.BenchmarkRateUnavailable);
                Assert.Equal(100.0 * BenchmarkSeed.Growth(stored, FundedOn, DayOf(p)), p.Benchmark!.Value, 9);
            }
        }

        // A window that opens on March 1 starts there, at 100, whatever came before it.
        var later = Assert.Single((await SeriesAsync(ledger, brokerage.Id, false, Utc(2024, 3, 1), Utc(2024, 6, 30), "BMK")).Series);
        Assert.Equal(100.0 * BenchmarkSeed.Growth(stored, D(2024, 3, 1), D(2024, 3, 31)), At(later, 2024, 3).Benchmark!.Value, 9);
        Assert.Equal(100.0 * BenchmarkSeed.Growth(stored, D(2024, 3, 1), D(2024, 6, 30)), At(later, 2024, 6).Benchmark!.Value, 9);
    }

    /// <summary>With no benchmark asked for, the result names none and no point carries one.</summary>
    [Fact]
    public async Task With_no_benchmark_asked_for_no_point_carries_one()
    {
        var (ledger, _) = await FundedBrokerageAsync();
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMK", D(2024, 1, 1), D(2024, 6, 30), Ordinary);

        var result = await SeriesAsync(ledger, null, true, Utc(2024, 1, 1), Utc(2024, 6, 30), null);

        Assert.Null(result.Benchmark);
        Assert.All(result.Series.SelectMany(s => s.Points), p =>
        {
            Assert.Null(p.Benchmark);
            Assert.Null(p.BenchmarkUnavailableReason);
            Assert.Null(p.BenchmarkUnavailableDate);
            Assert.Empty(p.BenchmarkRateUnavailable);
        });
    }

    // ----- pause and resume -------------------------------------------------------------

    /// <summary>
    /// An account emptied in April and funded again in August, with the fund rising hard
    /// while it held nothing. The benchmark pauses with the portfolio: null with
    /// <c>no_invested_base</c> while nothing is invested, then resumed from the value it held,
    /// so the months the account sat out are not in it. In another currency the rate's move
    /// over the pause is skipped as the market's is, so the converted line resumes from the
    /// value it held too.
    /// </summary>
    /// <remarks>Mutation caught: chaining across the pause (the August point is off by the
    /// pause's growth, compounded every trading day for three months); converting the whole
    /// stretch at rate_t over rate_CoveredFrom (the August point is off by the rate's move
    /// over the pause).</remarks>
    [Fact]
    public async Task Across_a_stretch_with_nothing_invested_the_benchmark_pauses_and_resumes_from_the_value_it_held()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 110m, Utc(2024, 3, 1));
        await ledger.AddInvestmentSellAsync(brokerage.Id, holdings, sec, 10m, 110m, Utc(2024, 4, 15));
        await ledger.AddTransactionPairAsync(bank.Id, brokerage.Id, 1_100m, Utc(2024, 4, 15));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_100m, Utc(2024, 8, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 110m, Utc(2024, 8, 10));
        await ledger.AddSecurityPriceAsync(sec, 121m, Utc(2024, 10, 1));

        var emptied = D(2024, 4, 15);
        var refunded = D(2024, 8, 10);
        decimal PauseRises(DateOnly day) =>
            day > emptied && day <= refunded ? 1.01m : WithFundingDay(day);
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMKE", "EUR", position: 2);
        var stored = await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMK", D(2024, 1, 1), D(2024, 10, 31), PauseRises);
        var storedEur = await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMKE", D(2024, 1, 1), D(2024, 10, 31), PauseRises);
        var rates = await BenchmarkSeed.SeedRatesAsync(
            Db, ledger, "EUR", "USD", D(2023, 12, 25), D(2024, 12, 31), D(2024, 12, 31), EurUsd);

        var series = Assert.Single((await SeriesAsync(ledger, brokerage.Id, false, Utc(2024, 1, 1), Utc(2024, 10, 31), "BMK")).Series);

        Assert.Equal(100.0 * BenchmarkSeed.Growth(stored, FundedOn, D(2024, 3, 31)), At(series, 2024, 3).Benchmark!.Value, 9);
        foreach (var month in new[] { 4, 5, 6, 7 })
        {
            var p = At(series, 2024, month);
            Assert.Equal(ReturnsSeriesReasons.NoInvestedBase, p.IndexUnavailableReason);
            Assert.Null(p.Benchmark);
            Assert.Equal(ReturnsSeriesReasons.NoInvestedBase, p.BenchmarkUnavailableReason);
        }

        var held = BenchmarkSeed.Growth(stored, FundedOn, emptied);
        Assert.Equal(100.0 * held * BenchmarkSeed.Growth(stored, refunded, D(2024, 8, 31)), At(series, 2024, 8).Benchmark!.Value, 9);
        Assert.Equal(100.0 * held * BenchmarkSeed.Growth(stored, refunded, D(2024, 10, 31)), At(series, 2024, 10).Benchmark!.Value, 9);
        // The pause's growth is large enough that chaining across it could not pass for this.
        Assert.True(BenchmarkSeed.Growth(stored, FundedOn, D(2024, 8, 31)) > 1.5 * held);

        // Converted: each invested stretch at its own end rates, the pause's rate move skipped.
        var converted = Assert.Single((await SeriesAsync(ledger, brokerage.Id, false, Utc(2024, 1, 1), Utc(2024, 10, 31), "BMKE")).Series);
        double R(DateOnly day) => (double)rates[day];
        var heldUsd = BenchmarkSeed.Growth(storedEur, FundedOn, emptied) * R(emptied) / R(FundedOn);
        var august = 100.0 * heldUsd * BenchmarkSeed.Growth(storedEur, refunded, D(2024, 8, 31)) * R(D(2024, 8, 31)) / R(refunded);
        Assert.Equal(august, At(converted, 2024, 8).Benchmark!.Value, 9);
        Assert.NotEqual(R(emptied), R(refunded));
        Assert.Equal(ReturnsSeriesReasons.NoInvestedBase, At(converted, 2024, 6).BenchmarkUnavailableReason);
    }

    // ----- conversion -------------------------------------------------------------------

    /// <summary>
    /// A EUR fund drawn beside a USD ledger whose one brokerage is in EUR: the ledger line is
    /// in the reporting currency, so the benchmark converts into it at each day's rate,
    /// unhedged (its growth times rate_t over the rate at the first invested point); the
    /// brokerage's own line is in EUR, so the same fund needs no conversion there.
    /// </summary>
    /// <remarks>Mutation caught: no conversion on the ledger line, a conversion on the account
    /// line, or the rates the wrong way up (each point off by the rate's move since January).</remarks>
    [Fact]
    public async Task A_benchmark_in_another_currency_converts_at_each_days_rate_into_each_series_currency()
    {
        var (ledger, brokerage) = await FundedBrokerageAsync("EUR");
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMKE", "EUR");
        var stored = await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMKE", D(2024, 1, 1), D(2024, 6, 30), WithFundingDay);
        var rates = await BenchmarkSeed.SeedRatesAsync(
            Db, ledger, "EUR", "USD", D(2023, 12, 25), D(2024, 12, 31), DateOnly.FromDateTime(DateTime.UtcNow), EurUsd);
        double R(DateOnly day) => (double)rates[day];

        var result = await SeriesAsync(ledger, null, true, Utc(2024, 1, 1), Utc(2024, 6, 30), "BMKE");

        var ledgerLine = result.Series.Single(s => s.Scope == "ledger");
        var accountLine = result.Series.Single(s => s.AccountId == brokerage.Id);
        Assert.Equal(("USD", "EUR"), (ledgerLine.CurrencyCode, accountLine.CurrencyCode));
        Assert.Equal(6, ledgerLine.Points.Count);
        foreach (var (inUsd, inEur) in ledgerLine.Points.Zip(accountLine.Points))
        {
            var day = DayOf(inUsd);
            var growth = BenchmarkSeed.Growth(stored, FundedOn, day);
            Assert.Equal(100.0 * growth * R(day) / R(FundedOn), inUsd.Benchmark!.Value, 9);
            Assert.Equal(100.0 * growth, inEur.Benchmark!.Value, 9);
            Assert.Empty(inUsd.BenchmarkRateUnavailable);
        }
        // The rate moves between the points, so a constant rate would not pass.
        Assert.Equal(6, ledgerLine.Points.Select(p => rates[DayOf(p)]).Distinct().Count());
    }

    // ----- fail closed --------------------------------------------------------------------

    /// <summary>
    /// A point after the last day the fetches asked for has no benchmark: null with
    /// <c>benchmark_unavailable</c> and the first day with no value, never chained across;
    /// the points before it keep theirs.
    /// </summary>
    /// <remarks>Mutation caught: treating a day past the coverage as a non-trading day (the
    /// March to June points would read the March 15 value).</remarks>
    [Fact]
    public async Task A_point_past_the_fetched_days_has_no_benchmark_and_names_the_first_missing_day()
    {
        var (ledger, brokerage) = await FundedBrokerageAsync();
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        var stored = await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMK", D(2024, 1, 1), D(2024, 3, 15), Ordinary);

        var series = Assert.Single((await SeriesAsync(ledger, brokerage.Id, false, Utc(2024, 1, 1), Utc(2024, 6, 30), "BMK")).Series);

        Assert.Equal(100.0 * BenchmarkSeed.Growth(stored, FundedOn, D(2024, 2, 29)), At(series, 2024, 2).Benchmark!.Value, 9);
        foreach (var month in new[] { 3, 4, 5, 6 })
        {
            var p = At(series, 2024, month);
            Assert.NotNull(p.Index);
            Assert.Null(p.Benchmark);
            Assert.Equal(ReturnsSeriesReasons.BenchmarkUnavailable, p.BenchmarkUnavailableReason);
            Assert.Equal(D(2024, 3, 16), p.BenchmarkUnavailableDate);
        }
    }

    /// <summary>
    /// A fund whose stored growth starts after the series' first invested day has nothing to
    /// rebase on: every point is null with <c>benchmark_unavailable</c> naming that day, even
    /// once growth is stored, because chaining from the first stored day would start the line
    /// at the wrong point.
    /// </summary>
    /// <remarks>Mutation caught: reading the days before the first stored row as non-trading
    /// days (the line would start flat at 100).</remarks>
    [Fact]
    public async Task A_first_invested_day_before_the_first_stored_growth_leaves_every_point_without_a_benchmark()
    {
        var (ledger, brokerage) = await FundedBrokerageAsync();
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        await BenchmarkSeed.SeedGrowthAsync(
            Db, ledger, "BMK", D(2024, 1, 1), D(2024, 6, 30), Ordinary, firstRow: D(2024, 2, 1));

        var series = Assert.Single((await SeriesAsync(ledger, brokerage.Id, false, Utc(2024, 1, 1), Utc(2024, 6, 30), "BMK")).Series);

        Assert.All(series.Points, p =>
        {
            Assert.NotNull(p.Index);
            Assert.Null(p.Benchmark);
            Assert.Equal(ReturnsSeriesReasons.BenchmarkUnavailable, p.BenchmarkUnavailableReason);
            Assert.Equal(FundedOn, p.BenchmarkUnavailableDate);
        });
    }

    /// <summary>
    /// A EUR fund beside a USD series with rates only through February: from March every
    /// point needs a rate the ledger does not hold, so its benchmark is null with
    /// <c>rate_unavailable</c> and names the pair and day (ADR-0114 D9). The series itself
    /// converts nothing and keeps its index.
    /// </summary>
    /// <remarks>Mutation caught: a missing rate read as one, or the rate list left empty.</remarks>
    [Fact]
    public async Task A_benchmark_point_that_needs_a_missing_rate_is_null_and_names_it()
    {
        var (ledger, brokerage) = await FundedBrokerageAsync();
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMKE", "EUR");
        var stored = await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMKE", D(2024, 1, 1), D(2024, 6, 30), Ordinary);
        var rates = await BenchmarkSeed.SeedRatesAsync(
            Db, ledger, "EUR", "USD", D(2023, 12, 25), D(2024, 2, 29), D(2024, 2, 29), EurUsd);

        var series = Assert.Single((await SeriesAsync(ledger, brokerage.Id, false, Utc(2024, 1, 1), Utc(2024, 6, 30), "BMKE")).Series);

        var february = At(series, 2024, 2);
        Assert.Equal(
            100.0 * BenchmarkSeed.Growth(stored, FundedOn, D(2024, 2, 29)) * (double)rates[D(2024, 2, 29)] / (double)rates[FundedOn],
            february.Benchmark!.Value, 9);
        foreach (var month in new[] { 3, 4, 5, 6 })
        {
            var p = At(series, 2024, month);
            Assert.NotNull(p.Index);
            Assert.Empty(p.RateUnavailable);
            Assert.Null(p.Benchmark);
            Assert.Equal(ReturnsSeriesReasons.RateUnavailable, p.BenchmarkUnavailableReason);
            Assert.Contains(new RateUnavailable("EUR", "USD", DayOf(p)), p.BenchmarkRateUnavailable);
            Assert.All(p.BenchmarkRateUnavailable, w => Assert.Equal(("EUR", "USD"), (w.FromCurrency, w.ToCurrency)));
        }
    }

    // ----- a removed symbol -------------------------------------------------------------

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>
    /// A saved Returns report keeps its benchmark pick after the symbol is removed from the
    /// ledger (ADR-0114 D1): the spec still reads it, and the series it asks for is answered,
    /// not refused, with <c>benchmark_not_in_ledger</c> on the result and on every point and no
    /// benchmark value. Adding the symbol back draws the line again from the history kept.
    /// </summary>
    [Fact]
    public async Task A_saved_report_whose_benchmark_was_removed_keeps_its_pick_and_draws_nothing_with_the_reason()
    {
        var (ledger, _) = await FundedBrokerageAsync();
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMK", D(2024, 1, 1), D(2024, 6, 30), Ordinary);
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var saved = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reports/saved", new
        {
            name = "Returns against BMK",
            spec = new { kind = "returns", benchmark = "BMK" },
        });
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var reportId = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var seriesUrl = $"/api/ledgers/{ledger.LedgerId}/reports/returns-series"
                        + "?benchmark=BMK&from=2024-01-01T00:00:00Z&to=2024-06-30T00:00:00Z&today=2024-07-15";
        var before = JsonDocument.Parse(await client.GetStringAsync(seriesUrl)).RootElement;
        Assert.Equal(JsonValueKind.Null, before.GetProperty("benchmark").GetProperty("unavailableReason").ValueKind);
        Assert.Equal(JsonValueKind.Number,
            before.GetProperty("series")[0].GetProperty("points")[0].GetProperty("benchmark").ValueKind);

        await using (var db = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            var id = await db.LedgerBenchmarks.Where(b => b.LedgerId == ledger.LedgerId).Select(b => b.Id).SingleAsync();
            Assert.True(await new MarketSeriesRepository(db, TimeProvider.System).RemoveBenchmarkAsync(ledger.LedgerId, id));
        }

        var report = JsonDocument.Parse(
            await client.GetStringAsync($"/api/ledgers/{ledger.LedgerId}/reports/saved/{reportId}")).RootElement;
        Assert.Equal("BMK", report.GetProperty("spec").GetProperty("benchmark").GetString());

        var response = await client.GetAsync(seriesUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var benchmark = after.GetProperty("benchmark");
        Assert.Equal("BMK", benchmark.GetProperty("symbol").GetString());
        Assert.Equal(JsonValueKind.Null, benchmark.GetProperty("name").ValueKind);
        Assert.Equal(ReturnsSeriesReasons.BenchmarkNotInLedger, benchmark.GetProperty("unavailableReason").GetString());
        var points = after.GetProperty("series")[0].GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(6, points.Count);
        Assert.All(points, p =>
        {
            Assert.Equal(JsonValueKind.Number, p.GetProperty("index").ValueKind);
            Assert.Equal(JsonValueKind.Null, p.GetProperty("benchmark").ValueKind);
            Assert.Equal(ReturnsSeriesReasons.BenchmarkNotInLedger, p.GetProperty("benchmarkUnavailableReason").GetString());
        });

        // Re-saving the report with its pick still succeeds: the pick is a symbol, not a reference.
        var resaved = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reports/saved/{reportId}",
            new { spec = new { kind = "returns", benchmark = "BMK" } });
        Assert.Equal(HttpStatusCode.OK, resaved.StatusCode);

        // Added back, it draws again from the history kept, with no fetch.
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        var again = JsonDocument.Parse(await client.GetStringAsync(seriesUrl)).RootElement;
        Assert.Equal(
            before.GetProperty("series")[0].GetProperty("points")[5].GetProperty("benchmark").GetDouble(),
            again.GetProperty("series")[0].GetProperty("points")[5].GetProperty("benchmark").GetDouble());
    }

    /// <summary>A spec's benchmark is a symbol or null; anything else is refused before it is
    /// stored, on create and on patch, and the series refuses a value that is not a symbol.</summary>
    [Theory]
    [InlineData("\"vt\"")]
    [InlineData("\"V T\"")]
    [InlineData("\"\"")]
    [InlineData("5")]
    [InlineData("{\"symbol\":\"VT\"}")]
    public async Task A_benchmark_that_is_not_a_symbol_is_refused_by_the_spec_and_the_series(string benchmarkJson)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/reports/saved";
        var spec = JsonDocument.Parse($"{{\"kind\":\"returns\",\"benchmark\":{benchmarkJson}}}").RootElement;

        var created = await client.PostAsJsonAsync(url, new { name = "Bad", spec });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains("benchmark", await created.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var ok = await client.PostAsJsonAsync(url, new { name = "Good", spec = new { kind = "returns", benchmark = (string?)null } });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var id = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var patched = await client.PatchAsJsonAsync($"{url}/{id}", new { spec });
        Assert.Equal(HttpStatusCode.BadRequest, patched.StatusCode);

        if (spec.GetProperty("benchmark").ValueKind == JsonValueKind.String
            && spec.GetProperty("benchmark").GetString() is { Length: > 0 } raw)
        {
            var series = await client.GetAsync(
                $"/api/ledgers/{ledger.LedgerId}/reports/returns-series?benchmark={Uri.EscapeDataString(raw)}");
            Assert.Equal(HttpStatusCode.BadRequest, series.StatusCode);
        }
    }

    // ----- MCP --------------------------------------------------------------------------

    /// <summary>
    /// The MCP tool takes one of the ledger's symbols and returns what the repository draws;
    /// anything else is refused with a code: a symbol not in the ledger with
    /// <c>benchmark-not-in-ledger</c>, naming the ones it has, and a value that is not a symbol
    /// with <c>benchmark-symbol-invalid</c>.
    /// </summary>
    [Fact]
    public async Task The_mcp_tool_draws_a_ledger_symbol_and_refuses_anything_else_with_a_code()
    {
        var (ledger, brokerage) = await FundedBrokerageAsync();
        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMK");
        var stored = await BenchmarkSeed.SeedGrowthAsync(Db, ledger, "BMK", D(2024, 1, 1), D(2024, 6, 30), Ordinary);
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var repo = new InvestmentReportingRepository(db, TimeProvider.System);

        var drawn = await InvestmentTools.ReturnsSeries(
            repo, ledger.LedgerId, accountId: brokerage.Id, fromUtc: Utc(2024, 1, 1), toUtc: Utc(2024, 6, 30), benchmark: "BMK");
        Assert.Equal("BMK", drawn.Benchmark!.Symbol);
        var june = Assert.Single(drawn.Series).Points.Single(p => p.PeriodEnd.Month == 6 && p.PeriodEnd.Year == 2024);
        Assert.Equal(100.0 * BenchmarkSeed.Growth(stored, FundedOn, D(2024, 6, 30)), june.Benchmark!.Value, 9);

        var notInLedger = await Assert.ThrowsAsync<ArgumentException>(
            () => InvestmentTools.ReturnsSeries(repo, ledger.LedgerId, benchmark: "VT"));
        Assert.StartsWith("benchmark-not-in-ledger: ", notInLedger.Message, StringComparison.Ordinal);
        Assert.Contains("BMK", notInLedger.Message, StringComparison.Ordinal);

        var invalid = await Assert.ThrowsAsync<ArgumentException>(
            () => InvestmentTools.ReturnsSeries(repo, ledger.LedgerId, benchmark: "bmk"));
        Assert.StartsWith("benchmark-symbol-invalid: ", invalid.Message, StringComparison.Ordinal);
    }
}
