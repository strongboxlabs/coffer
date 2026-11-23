using System.Net;
using System.Web;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Frankfurter;
using Coffer.Api.Quotes.MarketSeries;
using Coffer.Api.Quotes.SimpleFin;
using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.MarketSeries;

/// <summary>
/// The market-series fetch (ADR-0114 D6, D8, D10, D11) against the two feeds faked at the
/// HTTP level: consent per destination, where each fetch starts, first stored wins,
/// coverage, concurrent runs, the refresh's audit row, and no egress from the post-sync
/// pull. Synthetic ledgers and values only.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MarketSeriesFetchTests : DatabaseTest
{
    public MarketSeriesFetchTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>A Wednesday. The ledger's history starts two weeks and two days before.</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 6, 3);
    private static readonly DateTime FirstPosted = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly FirstDay = new(2026, 5, 18);

    private readonly TestClock _clock = new(Now);

    private MarketDataFake NewFake() => new(_clock);

    private MarketSeriesFetcher Fetcher(AppDbContext db, MarketDataFake fake) =>
        new(db, fake.Yahoo(), fake.Frankfurter(), new UserPreferencesRepository(db, _clock),
            NullLogger<MarketSeriesFetcher>.Instance, _clock);

    private async Task EnableAsync(SyntheticLedger ledger, params string[] providers)
    {
        await using var db = Db.NewDbContext();
        await new UserPreferencesRepository(db, _clock).SetQuotesAsync(
            ledger.UserId, ledger.LedgerId, new QuotesPrefs { EnabledProviders = providers });
    }

    /// <summary>A USD ledger with a EUR bank account whose first transaction is on
    /// <see cref="FirstDay"/>: it needs EUR→USD from then.</summary>
    private async Task<SyntheticLedger> EuroLedgerAsync(params string[] more)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var groceries = await ledger.AddCategoryAsync("Groceries");
        foreach (var currency in more.Prepend("EUR"))
        {
            var bank = await ledger.AddBankAccountAsync($"Bank {currency}", currencyCode: currency);
            await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -12m, FirstPosted);
        }
        return ledger;
    }

    /// <summary>A benchmark added through the repository's own write, and a trade on
    /// <see cref="FirstDay"/>, the ledger's first posting on an investment account, so the
    /// ledger is first invested then.</summary>
    private async Task AddBenchmarkAndTradeAsync(SyntheticLedger ledger, string symbol = "SYNTH")
    {
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Fund", ticker: "FND");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 1m, 10m, FirstPosted);
        await using var db = Db.NewDbContext();
        var (result, _) = await new MarketSeriesRepository(db, _clock)
            .AddBenchmarkAsync(ledger.LedgerId, symbol, $"Synthetic {symbol}", "USD");
        Assert.Equal(MarketSeriesRepository.AddBenchmarkResult.Ok, result);
    }

    private static int WeekdaysBetween(DateOnly from, DateOnly to) => MarketDataFake.Weekdays(from, to).Count();

    // ---- consent per destination ---------------------------------------------------

    [Fact]
    public async Task Nothing_is_fetched_without_the_users_consent()
    {
        var ledger = await EuroLedgerAsync();
        await AddBenchmarkAndTradeAsync(ledger);
        var fake = NewFake();

        await using var db = Db.NewDbContext();
        var outcome = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.Empty(fake.Requests);
        Assert.Equal(0, outcome.RatesInserted);
        Assert.Equal(0, outcome.BenchmarkDaysInserted);
    }

    [Fact]
    public async Task A_run_with_no_user_sends_nothing()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        var fake = NewFake();

        await using var db = Db.NewDbContext();
        await Fetcher(db, fake).FetchAsync(ledger.LedgerId, userId: null);

        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task The_Yahoo_opt_in_fetches_benchmarks_and_sends_nothing_to_Frankfurter()
    {
        var ledger = await EuroLedgerAsync();
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key);
        var fake = NewFake();

        await using var db = Db.NewDbContext();
        var outcome = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.NotEmpty(fake.YahooRequests);
        Assert.Empty(fake.RateRequests);
        Assert.True(outcome.BenchmarkDaysInserted > 0);
        Assert.Equal(0, outcome.RatesInserted);
        await using var read = Db.NewDbContext();
        Assert.False(await read.FxRates.AnyAsync(r => r.LedgerId == ledger.LedgerId));
    }

    [Fact]
    public async Task The_Frankfurter_opt_in_fetches_rates_and_sends_nothing_to_Yahoo()
    {
        var ledger = await EuroLedgerAsync();
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var fake = NewFake();

        await using var db = Db.NewDbContext();
        var outcome = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.Empty(fake.YahooRequests);
        Assert.NotEmpty(fake.RateRequests);
        Assert.True(outcome.RatesInserted > 0);
        Assert.Equal(0, outcome.BenchmarkDaysInserted);
        await using var read = Db.NewDbContext();
        Assert.False(await read.BenchmarkGrowth.AnyAsync(g => g.LedgerId == ledger.LedgerId));
    }

    // ---- where a fetch starts, and coverage ----------------------------------------

    [Fact]
    public async Task A_first_rate_fetch_reaches_a_week_before_the_first_needed_day_and_sets_coverage()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var fake = NewFake();

        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        var request = Assert.Single(fake.RateRequests);
        var q = HttpUtility.ParseQueryString(request.Query);
        var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
        Assert.Equal(Iso(start), q["from"]);
        Assert.Equal(Iso(Today), q["to"]);
        Assert.Equal("EUR", q["base"]);
        Assert.Equal("USD", q["quotes"]);

        await using var read = Db.NewDbContext();
        var coverage = await read.FxPairCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId);
        Assert.Equal(("EUR", "USD", Today), (coverage.FromCurrency, coverage.ToCurrency, coverage.FetchedThroughDate));
        Assert.Equal(WeekdaysBetween(start, Today),
            await read.FxRates.CountAsync(r => r.LedgerId == ledger.LedgerId && r.Source == FxRateSource.Frankfurter));
    }

    [Fact]
    public async Task A_later_fetch_starts_at_fetched_through_inclusive_and_advances_it()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var fake = NewFake();
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        var before = fake.RateRequests.Count;

        _clock.Advance(TimeSpan.FromDays(2));
        MarketSeriesOutcome outcome;
        await using (var db = Db.NewDbContext())
            outcome = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        var request = fake.RateRequests.Skip(before).Single();
        Assert.Equal(Iso(Today), HttpUtility.ParseQueryString(request.Query)["from"]);
        // Today was already stored, so only the two new days count.
        Assert.Equal(WeekdaysBetween(Today.AddDays(1), Today.AddDays(2)), outcome.RatesInserted);
        await using var read = Db.NewDbContext();
        Assert.Equal(Today.AddDays(2),
            (await read.FxPairCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId)).FetchedThroughDate);
    }

    [Fact]
    public async Task A_benchmark_fetch_starts_a_week_before_the_first_invested_day_then_at_its_last_stored_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key);
        var fake = NewFake();

        MarketSeriesOutcome first;
        await using (var db = Db.NewDbContext())
            first = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
        Assert.Equal(Period1(start), Period1Of(Assert.Single(fake.YahooRequests)));
        // The first trading day in the response is the base, so it has no growth of its own.
        var yesterday = Today.AddDays(-1);
        Assert.Equal(WeekdaysBetween(start, yesterday) - 1, first.BenchmarkDaysInserted);

        _clock.Advance(TimeSpan.FromDays(2));
        MarketSeriesOutcome second;
        await using (var db = Db.NewDbContext())
            second = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.Equal(Period1(yesterday), Period1Of(fake.YahooRequests[^1]));
        Assert.Equal(WeekdaysBetween(Today, Today.AddDays(1)), second.BenchmarkDaysInserted);
    }

    private static long Period1(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    private static long Period1Of(Uri uri) =>
        long.Parse(HttpUtility.ParseQueryString(uri.Query)["period1"]!, System.Globalization.CultureInfo.InvariantCulture);

    private static string Iso(DateOnly day) =>
        day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_benchmark_Yahoo_now_quotes_in_another_currency_stores_nothing_and_says_so()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key);
        var fake = NewFake();
        fake.Currencies["SYNTH"] = "EUR";

        await using var db = Db.NewDbContext();
        var outcome = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.Equal(0, outcome.BenchmarkDaysInserted);
        Assert.Equal(MarketSeriesFetcher.BenchmarkCurrencyChangedCode, Assert.Single(outcome.Errors).Code);
        await using var read = Db.NewDbContext();
        Assert.False(await read.BenchmarkGrowth.AnyAsync(g => g.LedgerId == ledger.LedgerId));
    }

    [Fact]
    public async Task No_day_after_today_is_stored_even_when_a_feed_sends_one()
    {
        var ledger = await EuroLedgerAsync();
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        var fake = NewFake();
        fake.ExtraFutureDays = 10;

        MarketSeriesOutcome outcome;
        await using (var db = Db.NewDbContext())
            outcome = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.True(outcome.RatesInserted > 0 && outcome.BenchmarkDaysInserted > 0);
        await using var read = Db.NewDbContext();
        Assert.Equal(Today, await read.FxRates.Where(r => r.LedgerId == ledger.LedgerId).MaxAsync(r => r.RateDate));
        // Growth stops at yesterday: today's bar is intraday.
        Assert.Equal(Today.AddDays(-1),
            await read.BenchmarkGrowth.Where(g => g.LedgerId == ledger.LedgerId).MaxAsync(g => g.GrowthDate));
        Assert.Equal(Today, (await read.FxPairCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId)).FetchedThroughDate);
        // Nothing past today was asked for, either.
        Assert.All(fake.RateRequests, r => Assert.Equal(Iso(Today), HttpUtility.ParseQueryString(r.Query)["to"]));
    }

    [Fact]
    public async Task A_hand_entered_rate_for_tomorrow_is_refused()
    {
        var ledger = await EuroLedgerAsync();
        await using var db = Db.NewDbContext();
        var (result, _) = await new MarketSeriesRepository(db, _clock)
            .AddManualRateAsync(ledger.LedgerId, ledger.UserId, "EUR", "USD", Today.AddDays(1), 1.1m);

        Assert.Equal(MarketSeriesRepository.AddRateResult.DateInFuture, result);
        Assert.False(await db.FxRates.AnyAsync(r => r.LedgerId == ledger.LedgerId));
    }

    // ---- reaching back when the needed day moves earlier ---------------------------

    private static (string From, string To) RangeOf(Uri request)
    {
        var q = HttpUtility.ParseQueryString(request.Query);
        return (q["from"]!, q["to"]!);
    }

    [Fact]
    public async Task An_older_transaction_in_a_covered_currency_is_fetched_back_once_and_never_again()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var fake = NewFake();
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);

        // An older EUR transaction, entered after the first backfill.
        var older = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);
        var bank = await Db.NewDbContext().Accounts.AsNoTracking()
            .SingleAsync(a => a.LedgerId == ledger.LedgerId && a.CurrencyCode == "EUR");
        var groceries = await Db.NewDbContext().Accounts.AsNoTracking()
            .SingleAsync(a => a.LedgerId == ledger.LedgerId && a.Name == "Groceries");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -5m, older);
        var olderStart = DateOnly.FromDateTime(older).AddDays(-MarketSeriesFetcher.PublishedDayReachBack);

        var asked = fake.RateRequests.Count;
        MarketSeriesOutcome second;
        await using (var db = Db.NewDbContext())
            second = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        var secondAsks = fake.RateRequests.Skip(asked).Select(RangeOf).ToList();
        Assert.Equal(
            [(Iso(olderStart), Iso(start.AddDays(-1))), (Iso(Today), Iso(Today))],
            secondAsks.Order().ToList());
        Assert.Equal(WeekdaysBetween(olderStart, start.AddDays(-1)), second.RatesInserted);
        await using (var read = Db.NewDbContext())
        {
            var coverage = await read.FxPairCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId);
            Assert.Equal((olderStart, Today), (coverage.FetchedFromDate, coverage.FetchedThroughDate));
        }

        // The next refresh asks only for today again: nothing back.
        asked = fake.RateRequests.Count;
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        Assert.Equal([(Iso(Today), Iso(Today))], fake.RateRequests.Skip(asked).Select(RangeOf).ToList());
    }

    [Fact]
    public async Task A_span_before_a_currency_existed_is_asked_once_though_it_holds_no_rate()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var fake = NewFake();
        // The feed has no EUR rate before the 25th: the first days asked come back empty.
        fake.RatesFrom["EUR"] = new DateOnly(2026, 5, 25);

        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        var asked = fake.RateRequests.Count;
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        Assert.Equal([(Iso(Today), Iso(Today))], fake.RateRequests.Skip(asked).Select(RangeOf).ToList());
        await using var read = Db.NewDbContext();
        Assert.Equal(FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack),
            (await read.FxPairCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId)).FetchedFromDate);
        Assert.Equal(new DateOnly(2026, 5, 25), await read.FxRates.Where(r => r.LedgerId == ledger.LedgerId).MinAsync(r => r.RateDate));
    }

    [Fact]
    public async Task A_failed_back_fetch_records_nothing_and_is_asked_again()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var fake = NewFake();
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        var bank = await Db.NewDbContext().Accounts.AsNoTracking()
            .SingleAsync(a => a.LedgerId == ledger.LedgerId && a.CurrencyCode == "EUR");
        var groceries = await Db.NewDbContext().Accounts.AsNoTracking()
            .SingleAsync(a => a.LedgerId == ledger.LedgerId && a.Name == "Groceries");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -5m, new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc));
        var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);

        fake.RateStatus["EUR"] = HttpStatusCode.ServiceUnavailable;
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        await using (var read = Db.NewDbContext())
            Assert.Equal(start, (await read.FxPairCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId)).FetchedFromDate);

        fake.RateStatus.Clear();
        var asked = fake.RateRequests.Count;
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        Assert.Contains((Iso(new DateOnly(2026, 2, 23)), Iso(start.AddDays(-1))),
            fake.RateRequests.Skip(asked).Select(RangeOf));
    }

    [Fact]
    public async Task An_older_first_investment_fetches_benchmark_growth_back_once_through_the_first_stored_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key);
        var fake = NewFake();
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        DateOnly firstStored;
        await using (var read = Db.NewDbContext())
            firstStored = await read.BenchmarkGrowth.Where(g => g.LedgerId == ledger.LedgerId).MinAsync(g => g.GrowthDate);

        // An older trade, entered after the first backfill.
        var brokerage = await Db.NewDbContext().Accounts.AsNoTracking()
            .SingleAsync(a => a.LedgerId == ledger.LedgerId && a.Name == "Brokerage");
        var security = await Db.NewDbContext().Securities.AsNoTracking()
            .SingleAsync(s => s.LedgerId == ledger.LedgerId);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security.Id, 1m, 9m,
            new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc));
        var olderStart = new DateOnly(2026, 3, 2).AddDays(-MarketSeriesFetcher.PublishedDayReachBack);

        var asked = fake.YahooRequests.Count;
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        var back = Assert.Single(fake.YahooRequests.Skip(asked));
        Assert.Equal(Period1(olderStart), Period1Of(back));
        // Through the first stored day, so the day that was the first fetch's base gets its growth.
        Assert.Equal(Period1(firstStored.AddDays(1)), long.Parse(HttpUtility.ParseQueryString(back.Query)["period2"]!,
            System.Globalization.CultureInfo.InvariantCulture));
        await using (var read = Db.NewDbContext())
        {
            var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
            Assert.Equal(WeekdaysBetween(olderStart, Today.AddDays(-1)) - 1,
                await read.BenchmarkGrowth.CountAsync(g => g.LedgerId == ledger.LedgerId));
            var coverage = await read.BenchmarkCoverage.SingleAsync(c => c.LedgerId == ledger.LedgerId);
            Assert.Equal((olderStart, Today.AddDays(-1)), (coverage.FetchedFromDate, coverage.FetchedThroughDate));
            Assert.True(start > olderStart);
        }

        // Asked once: the next refresh the same day sends nothing to Yahoo.
        asked = fake.YahooRequests.Count;
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        Assert.Equal(asked, fake.YahooRequests.Count);
    }

    [Fact]
    public async Task A_span_before_a_fund_launched_is_asked_once()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key);
        var fake = NewFake();
        fake.ListedFrom["SYNTH"] = new DateOnly(2026, 5, 26);

        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        _clock.Advance(TimeSpan.FromDays(1));
        var asked = fake.YahooRequests.Count;
        await using (var db = Db.NewDbContext())
            await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        // Only the new day, from the last stored day as its base: nothing before the launch again.
        var request = Assert.Single(fake.YahooRequests.Skip(asked));
        Assert.Equal(Period1(Today.AddDays(-1)), Period1Of(request));
    }

    // ---- first stored wins ---------------------------------------------------------

    [Fact]
    public async Task A_hand_entered_rate_keeps_its_day_when_a_fetch_brings_the_same_day()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var manualDay = new DateOnly(2026, 5, 20);
        await using (var db = Db.NewDbContext())
        {
            var (result, _) = await new MarketSeriesRepository(db, _clock)
                .AddManualRateAsync(ledger.LedgerId, ledger.UserId, "EUR", "USD", manualDay, 2m);
            Assert.Equal(MarketSeriesRepository.AddRateResult.Ok, result);
        }
        var fake = NewFake();

        MarketSeriesOutcome outcome;
        await using (var db = Db.NewDbContext())
            outcome = await Fetcher(db, fake).FetchAsync(ledger.LedgerId, ledger.UserId);

        var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
        Assert.Equal(WeekdaysBetween(start, Today) - 1, outcome.RatesInserted);
        Assert.Empty(outcome.Errors);
        await using var read = Db.NewDbContext();
        var kept = await read.FxRates.SingleAsync(r => r.LedgerId == ledger.LedgerId && r.RateDate == manualDay);
        Assert.Equal((FxRateSource.Manual, 2m, (Guid?)ledger.UserId), (kept.Source, kept.Rate, kept.EnteredByUserId));
    }

    [Fact]
    public async Task A_hand_entered_rate_is_refused_for_a_day_a_fetch_stored()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        await using (var db = Db.NewDbContext())
            await Fetcher(db, NewFake()).FetchAsync(ledger.LedgerId, ledger.UserId);

        await using var write = Db.NewDbContext();
        var (result, _) = await new MarketSeriesRepository(write, _clock)
            .AddManualRateAsync(ledger.LedgerId, ledger.UserId, "EUR", "USD", new DateOnly(2026, 5, 20), 2m);

        Assert.Equal(MarketSeriesRepository.AddRateResult.DayTaken, result);
    }

    // ---- concurrent runs -----------------------------------------------------------

    [Fact]
    public async Task Two_runs_at_once_store_each_day_once_and_neither_fails()
    {
        var ledger = await EuroLedgerAsync();
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        var fake = NewFake();
        fake.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var db1 = Db.NewDbContext();
        await using var db2 = Db.NewDbContext();
        var run1 = Fetcher(db1, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        var run2 = Fetcher(db2, fake).FetchAsync(ledger.LedgerId, ledger.UserId);
        // Both runs have asked before either answer comes back.
        while (fake.Requests.Count < 2) await Task.Yield();
        fake.Gate.SetResult();
        var outcomes = await Task.WhenAll(run1, run2);

        Assert.All(outcomes, o => Assert.Empty(o.Errors));
        await using var read = Db.NewDbContext();
        var storedRates = await read.FxRates.CountAsync(r => r.LedgerId == ledger.LedgerId);
        var storedDays = await read.BenchmarkGrowth.CountAsync(g => g.LedgerId == ledger.LedgerId);
        Assert.Equal(storedRates, outcomes.Sum(o => o.RatesInserted));
        Assert.Equal(storedDays, outcomes.Sum(o => o.BenchmarkDaysInserted));
        var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
        Assert.Equal(WeekdaysBetween(start, Today), storedRates);
    }

    // ---- the refresh's audit row ---------------------------------------------------

    private QuoteOrchestrator Orchestrator(AppDbContext db, MarketDataFake fake) =>
        new(db, Array.Empty<IQuotePullProvider>(), Array.Empty<IQuotePushProvider>(),
            new UserPreferencesRepository(db, _clock), NullLogger<QuoteOrchestrator>.Instance, _clock,
            Fetcher(db, fake));

    [Fact]
    public async Task A_refresh_records_what_it_stored_and_an_error_row_per_failing_pair()
    {
        var ledger = await EuroLedgerAsync("GBP");
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        var fake = NewFake();
        fake.RateStatus["EUR"] = HttpStatusCode.InternalServerError;

        QuoteRunOutcome outcome;
        await using (var db = Db.NewDbContext())
            outcome = await Orchestrator(db, fake).RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);

        var start = FirstDay.AddDays(-MarketSeriesFetcher.PublishedDayReachBack);
        var gbpDays = WeekdaysBetween(start, Today);
        Assert.Equal(gbpDays, outcome.RatesInserted);
        Assert.True(outcome.BenchmarkDaysInserted > 0);

        await using var read = Db.NewDbContext();
        var run = await read.LedgerOperations.AsNoTracking()
            .SingleAsync(r => r.LedgerId == ledger.LedgerId && r.Family == "quote");
        Assert.Equal((QuoteOrchestrator.QuoteRefreshProviderKey, "manual", "partial"),
            (run.ProviderKey, run.TriggeredVia, run.Status));
        var details = LedgerOperationDetails.Deserialize<QuoteRunDetails>(run.DetailsJson);
        Assert.Equal(gbpDays, details.RatesInserted);
        Assert.Equal(outcome.BenchmarkDaysInserted, details.BenchmarkDaysInserted);
        var error = await read.LedgerOperationErrors.AsNoTracking().SingleAsync(e => e.LedgerOperationId == run.Id);
        Assert.Equal(FrankfurterRatesClient.FetchFailedCode, error.Code);
        Assert.StartsWith("EUR/USD", error.Message);
        // The failing pair has no coverage; the one that answered does.
        Assert.Equal(["GBP"], await read.FxPairCoverage.Where(c => c.LedgerId == ledger.LedgerId)
            .Select(c => c.FromCurrency).ToListAsync());
    }

    [Fact]
    public async Task A_refresh_with_no_held_security_still_fetches_rates()
    {
        var ledger = await EuroLedgerAsync();
        await EnableAsync(ledger, FrankfurterRatesClient.Key);
        var fake = NewFake();

        QuoteRunOutcome outcome;
        await using (var db = Db.NewDbContext())
            outcome = await Orchestrator(db, fake).RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);

        Assert.True(outcome.RatesInserted > 0);
        Assert.NotEmpty(fake.RateRequests);
    }

    [Fact]
    public async Task The_post_sync_pull_sends_nothing_to_either_feed_whatever_the_consent()
    {
        var ledger = await EuroLedgerAsync();
        await AddBenchmarkAndTradeAsync(ledger);
        await EnableAsync(ledger, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        var fake = NewFake();

        await using (var db = Db.NewDbContext())
        {
            var orchestrator = new QuoteOrchestrator(
                db,
                new IQuotePullProvider[] { new SimpleFinHoldingsQuoteProvider(db, NullLogger<SimpleFinHoldingsQuoteProvider>.Instance, _clock) },
                Array.Empty<IQuotePushProvider>(),
                new UserPreferencesRepository(db, _clock), NullLogger<QuoteOrchestrator>.Instance, _clock,
                Fetcher(db, fake));
            var outcome = await orchestrator.RunPullAsync(
                ledger.LedgerId, SimpleFinHoldingsQuoteProvider.Key, "post-sync", ledger.UserId);
            Assert.Equal(0, outcome.RatesInserted);
        }

        Assert.Empty(fake.Requests);
    }
}
