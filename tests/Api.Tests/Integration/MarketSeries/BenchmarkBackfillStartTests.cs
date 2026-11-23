using System.Web;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.MarketSeries;
using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.MarketSeries;

/// <summary>
/// Where a benchmark's backfill starts (ADR-0114 D3, D11): a week before the first day any
/// returns series of the ledger can be <c>CoveredFrom</c>
/// (<see cref="InvestmentReportingRepository.FirstInvestedDayAsync"/>), not the first trade,
/// so every series the ledger can draw finds its rebase day fetched. Against the Yahoo feed
/// faked at the HTTP level; synthetic ledgers and values only.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BenchmarkBackfillStartTests : DatabaseTest
{
    public BenchmarkBackfillStartTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>A Wednesday.</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 6, 3);

    /// <summary>The cash funding, four weeks before the first trade.</summary>
    private static readonly DateTime FundedAt = new(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly FundedOn = new(2026, 4, 20);
    private static readonly DateTime FirstTradeAt = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly FirstTradeOn = new(2026, 5, 18);

    private readonly TestClock _clock = new(Now);

    private MarketSeriesFetcher Fetcher(AppDbContext db, MarketDataFake fake) =>
        new(db, fake.Yahoo(), fake.Frankfurter(), new UserPreferencesRepository(db, _clock),
            NullLogger<MarketSeriesFetcher>.Instance, _clock);

    private static long Period(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    private static long Param(Uri uri, string name) =>
        long.Parse(HttpUtility.ParseQueryString(uri.Query)[name]!, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The ledger's benchmark SYNTH, added through the repository's own write, with
    /// the owner's Yahoo opt-in on.</summary>
    private async Task AddBenchmarkAsync(SyntheticLedger ledger)
    {
        await using (var db = Db.NewDbContext())
        {
            var (result, _) = await new MarketSeriesRepository(db, _clock)
                .AddBenchmarkAsync(ledger.LedgerId, "SYNTH", "Synthetic SYNTH", "USD");
            Assert.Equal(MarketSeriesRepository.AddBenchmarkResult.Ok, result);
        }
        await using (var db = Db.NewDbContext())
            await new UserPreferencesRepository(db, _clock).SetQuotesAsync(
                ledger.UserId, ledger.LedgerId, new QuotesPrefs { EnabledProviders = [YahooFinanceQuoteProvider.Key] });
    }

    /// <summary>A brokerage funded with cash on <see cref="FundedAt"/> that buys nothing until
    /// <see cref="FirstTradeAt"/>.</summary>
    private async Task<(SyntheticLedger Ledger, AccountRow Brokerage)> CashFirstAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Fund", "FND");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, FundedAt);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 1m, 10m, FirstTradeAt);
        await AddBenchmarkAsync(ledger);
        return (ledger, brokerage);
    }

    private async Task<ReturnsSeriesResult> SeriesAsync(SyntheticLedger ledger, DateTime? from)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, _clock).ReturnsSeriesAsync(
            ledger.LedgerId, null, true, from, null, ReportTimeBucket.Month, Now.UtcDateTime, benchmark: "SYNTH");
    }

    private async Task<IReadOnlyDictionary<DateOnly, decimal>> StoredGrowthAsync(SyntheticLedger ledger)
    {
        await using var read = Db.NewDbContext();
        return await read.BenchmarkGrowth.AsNoTracking()
            .Where(g => g.LedgerId == ledger.LedgerId && g.Symbol == "SYNTH")
            .ToDictionaryAsync(g => g.GrowthDate, g => g.Growth);
    }

    private static double Growth(IReadOnlyDictionary<DateOnly, decimal> stored, DateOnly from, DateOnly to) =>
        stored.Where(kv => kv.Key > from && kv.Key <= to).Aggregate(1.0, (p, kv) => p * (double)kv.Value);

    /// <summary>
    /// A brokerage funded with cash four weeks before its first trade is invested, and its
    /// series <c>CoveredFrom</c>, at the funding. The backfill starts a week before that, so
    /// the benchmark is drawn from there at every point, the ledger's and the account's,
    /// with no <c>benchmark_unavailable</c>. The last point, today, reads the newest stored
    /// day, as a current value's rate does.
    /// </summary>
    /// <remarks>Mutation caught: the backfill starting from the first trade (every point would
    /// be <c>benchmark_unavailable</c>, the funding being before the first stored growth).</remarks>
    [Fact]
    public async Task A_brokerage_funded_with_cash_before_its_first_trade_draws_its_benchmark_from_the_funding()
    {
        var (ledger, _) = await CashFirstAsync();
        var fake = new MarketDataFake(_clock);

        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.Equal(Period(FundedOn.AddDays(-MarketSeriesFetcher.PublishedDayReachBack)),
            Param(Assert.Single(fake.YahooRequests), "period1"));
        var stored = await StoredGrowthAsync(ledger);
        Assert.True(stored.Keys.Min() <= FundedOn);

        var result = await SeriesAsync(ledger, null);
        Assert.Equal(2, result.Series.Count);
        foreach (var series in result.Series)
        {
            Assert.Equal(3, series.Points.Count);   // April, May, and today
            foreach (var p in series.Points)
            {
                Assert.Null(p.BenchmarkUnavailableReason);
                var closeDay = DateOnly.FromDateTime(p.PeriodEnd) >= Today ? Today.AddDays(-1) : DateOnly.FromDateTime(p.PeriodEnd);
                Assert.Equal(100.0 * Growth(stored, FundedOn, closeDay), p.Benchmark!.Value, 9);
            }
        }
        // Drawn from the funding: April has a month of growth before any trade.
        Assert.True(result.Series[0].Points[0].Benchmark > 100.0);
        Assert.True(FundedOn < FirstTradeOn.AddDays(-MarketSeriesFetcher.PublishedDayReachBack));
    }

    /// <summary>
    /// A ledger backfilled from its first trade, as the rule before this one did, asks Yahoo
    /// for the earlier span on its next refresh: from a week before the funding through its
    /// first stored day, so the day that was that fetch's base gets its growth, and never
    /// again after.
    /// </summary>
    /// <remarks>Mutation caught: the backfill starting from the first trade (no request would
    /// be sent).</remarks>
    [Fact]
    public async Task A_ledger_backfilled_from_its_first_trade_fetches_the_earlier_span_once()
    {
        var (ledger, _) = await CashFirstAsync();
        var oldStart = FirstTradeOn.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
        var yesterday = Today.AddDays(-1);

        // What the first-trade backfill stored: every weekday after its base day, and the
        // range it asked for.
        await using (var db = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            foreach (var day in MarketDataFake.Weekdays(oldStart.AddDays(1), yesterday))
            {
                db.BenchmarkGrowth.Add(new BenchmarkGrowthRow
                {
                    LedgerId = ledger.LedgerId, Symbol = "SYNTH", GrowthDate = day, Growth = 1.01m,
                    Source = BenchmarkGrowthSource.Fetch,
                });
            }
            db.BenchmarkCoverage.Add(new BenchmarkCoverageRow
            {
                LedgerId = ledger.LedgerId, Symbol = "SYNTH", FetchedFromDate = oldStart, FetchedThroughDate = yesterday,
            });
            await db.SaveChangesAsync();
        }
        var firstStored = (await StoredGrowthAsync(ledger)).Keys.Min();
        var fake = new MarketDataFake(_clock);

        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        var newStart = FundedOn.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
        var back = Assert.Single(fake.YahooRequests);
        Assert.Equal(Period(newStart), Param(back, "period1"));
        Assert.Equal(Period(firstStored.AddDays(1)), Param(back, "period2"));
        await using (var read = Db.NewDbContext())
        {
            var coverage = await read.BenchmarkCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId);
            Assert.Equal((newStart, yesterday), (coverage.FetchedFromDate, coverage.FetchedThroughDate));
            Assert.Equal(MarketDataFake.Weekdays(newStart, yesterday).Count() - 1,
                await read.BenchmarkGrowth.CountAsync(g => g.LedgerId == ledger.LedgerId));
        }

        // Asked once: the next refresh the same day sends nothing to Yahoo.
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        Assert.Single(fake.YahooRequests);

        // And the series is drawn from the funding.
        var series = (await SeriesAsync(ledger, null)).Series;
        Assert.All(series.SelectMany(s => s.Points), p => Assert.Null(p.BenchmarkUnavailableReason));
    }

    /// <summary>
    /// The backfill's start holds for every series the ledger can draw: since inception and
    /// over windows opening on any day, the ledger's line and each account's, on ledgers
    /// invested first by cash, by an opening balance, and by a dividend paid before any
    /// funding. After one fetch no point is <c>benchmark_unavailable</c>, so no series is
    /// <c>CoveredFrom</c> before the day <see cref="InvestmentReportingRepository.FirstInvestedDayAsync"/>
    /// names.
    /// </summary>
    /// <remarks>Mutation caught: the start taken from the first trade, or from the first cash
    /// flow (the dividend-first and opening-balance ledgers would fail).</remarks>
    [Theory]
    [InlineData("cash")]
    [InlineData("opening-balance")]
    [InlineData("dividend")]
    public async Task No_series_is_invested_before_the_first_invested_day(string firstBy)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var opened = firstBy == "opening-balance";
        var brokerage = await ledger.AddInvestmentAccountAsync(
            "Brokerage", openingBalance: opened ? 500m : 0m, openedOn: opened ? new DateOnly(2026, 4, 6) : null);
        var other = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var sec = await ledger.AddSecurityAsync("Fund", "FND");
        if (firstBy == "cash")
            await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, FundedAt);
        if (firstBy == "dividend")
        {
            var dividends = await ledger.AddCategoryAsync("Dividends", "income");
            await ledger.AddInvestmentDividendAsync(
                brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, dividends.Id, 25m, FundedAt);
        }
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 2_000m, FirstTradeAt.AddDays(-1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 1m, 10m, FirstTradeAt);
        await ledger.AddTransactionPairAsync(other.Id, brokerage.Id, 300m, FirstTradeAt.AddDays(3));
        await AddBenchmarkAsync(ledger);

        await using (var db = Db.NewDbContext())
        {
            var first = await InvestmentReportingRepository.FirstInvestedDayAsync(db, ledger.LedgerId);
            Assert.Equal(opened ? new DateOnly(2026, 4, 6) : FundedOn, first);
        }
        var fake = new MarketDataFake(_clock);
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        DateTime?[] windows =
        [
            null,
            new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc),
        ];
        var drawn = 0;
        foreach (var from in windows)
        {
            var result = await SeriesAsync(ledger, from);
            foreach (var p in result.Series.SelectMany(s => s.Points))
            {
                Assert.True(p.BenchmarkUnavailableReason != ReturnsSeriesReasons.BenchmarkUnavailable,
                    $"{firstBy}, from {from:yyyy-MM-dd}: {p.PeriodEnd:yyyy-MM-dd} needs growth from {p.BenchmarkUnavailableDate}");
                if (p.Benchmark is not null) drawn++;
            }
        }
        Assert.True(drawn > 10, $"only {drawn} points drew a benchmark");
    }
}
