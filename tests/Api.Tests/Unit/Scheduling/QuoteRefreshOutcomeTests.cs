using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Scheduling;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Tests.Unit.Scheduling;

/// <summary>
/// The trap this whole outcome channel exists for.
/// </summary>
/// <remarks>
/// <para>
/// <c>QuoteOrchestrator</c> catches every provider exception into its error list and
/// returns normally. So a scheduled quote refresh in which EVERY provider is down
/// completes without throwing, and without an outcome channel the scheduler would record
/// a clean run and a dead-man's switch wired to "the handler did not throw" would ping
/// its check green over a ledger whose prices have stopped moving.
/// </para>
/// <para>
/// That is worse than having no monitor: it manufactures exactly the confidence the
/// monitor was supposed to earn. These tests pin the classification that prevents it,
/// and the boundary that keeps it from crying wolf.
/// </para>
/// </remarks>
public sealed class QuoteRefreshOutcomeTests
{
    private static QuoteRunOutcome Outcome(string[] providerKeys, params QuoteError[] errors) =>
        new(
            ProviderKeys: providerKeys,
            PricesInserted: 0,
            PricesUpdated: 0,
            PricesWrittenBySource: new Dictionary<string, int>(),
            SecuritiesUnresolved: Array.Empty<Guid>(),
            Errors: errors);

    private static QuoteError ProviderDown(string key) => new(
        SecurityId: null,
        Ticker: string.Empty,
        Code: QuoteRefreshJobHandler.ProviderExceptionCode,
        Message: $"{key}: connection refused");

    [Fact]
    public void A_run_where_every_provider_threw_is_not_a_success()
    {
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo", "simplefin-holdings"],
            ProviderDown("yahoo"),
            ProviderDown("simplefin-holdings")));

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.True(result.NothingDone);   // nothing refreshed: escalates after a day (FU-071)
        Assert.Contains("No quote provider succeeded", result.Message);
        Assert.Contains("2 of 2", result.Message);

        // The provider's own words do NOT travel. QuoteOrchestrator builds these as
        // "{providerKey}: {ex.Message}", so joining them put raw exception text into a
        // summary that goes out to a configured webhook. The orchestrator logs each one
        // in full on this side of the wire.
        Assert.DoesNotContain("connection refused", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_provider_down_out_of_several_is_still_worth_saying()
    {
        // The run did work, so this is not a failure. It is still reported, because the
        // same provider failing every day is how prices go stale while the schedule
        // keeps reporting healthy runs.
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo", "simplefin-holdings"],
            ProviderDown("yahoo")));

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.False(result.NothingDone);  // some prices were refreshed: a partial run
        Assert.Contains("A quote provider failed", result.Message);
    }

    [Fact]
    public void A_security_no_provider_covers_is_an_ordinary_healthy_run()
    {
        // The boundary that stops the monitor crying wolf. A delisted ticker produces an
        // error on a perfectly healthy refresh; classifying that as degraded every day
        // is how a monitor teaches its owner to ignore it. Only provider-exception counts.
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo"],
            new QuoteError(
                SecurityId: Guid.NewGuid(),
                Ticker: "DELISTED",
                Code: "not-found",
                Message: "no quote for DELISTED")));

        Assert.Equal(JobRunResult.Ok, result.Result);
        Assert.Null(result.Message);
    }

    [Fact]
    public void A_clean_run_is_ok()
    {
        Assert.Equal(JobRunResult.Ok, QuoteRefreshJobHandler.Classify(Outcome(["yahoo"])).Result);
    }

    // ----- exchange rates (ADR-0114 D11: they ride this job, so its switch watches them) -----

    private static QuoteError PairFailed(string pair, string code = Coffer.Api.Quotes.Frankfurter.FrankfurterRatesClient.FetchFailedCode) =>
        new(SecurityId: null, Ticker: pair, Code: code, Message: $"{pair}: HTTP 503 from the exchange-rate service");

    [Fact]
    public void Prices_refreshed_but_no_rate_stored_is_not_a_success()
    {
        // The trap again, one step over: the rate source down while every quote provider
        // answers used to read Ok, so the switch stayed green while conversions went stale.
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo"],
            PairFailed("EUR/USD"),
            PairFailed("GBP/USD")));

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.False(result.NothingDone);   // prices did land
        Assert.Contains("Exchange rates were not stored (2 errors)", result.Message);
        Assert.DoesNotContain("HTTP 503", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Coffer.Api.Quotes.Frankfurter.FrankfurterRatesClient.FetchFailedCode)]
    [InlineData(Coffer.Api.Quotes.Frankfurter.FrankfurterRatesClient.ParseFailedCode)]
    [InlineData(Coffer.Api.Quotes.Frankfurter.FrankfurterRatesClient.BaseUrlInvalidCode)]
    [InlineData(QuoteOrchestrator.MarketSeriesExceptionCode)]
    [InlineData(Coffer.Api.Quotes.MarketSeries.MarketSeriesFetcher.LedgerWriteBusyCode)]
    public void Every_code_that_means_rates_were_not_stored_degrades_the_run(string code)
    {
        var result = QuoteRefreshJobHandler.Classify(Outcome(["yahoo"], PairFailed("EUR/USD", code)));

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.Contains("Exchange rates were not stored (1 error)", result.Message);
    }

    [Fact]
    public void A_rates_only_ledger_whose_rates_all_failed_did_nothing()
    {
        // A ledger of bank accounts in two currencies has no quote work, only rates. If
        // none landed, the run did none of its work and must escalate like one.
        var result = QuoteRefreshJobHandler.Classify(Outcome([], PairFailed("EUR/USD")));

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.True(result.NothingDone);
    }

    [Fact]
    public void A_rates_only_ledger_that_stored_some_rates_did_part_of_its_work()
    {
        var result = QuoteRefreshJobHandler.Classify(
            Outcome([], PairFailed("EUR/USD")) with { RatesInserted = 3 });

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.False(result.NothingDone);
    }

    [Fact]
    public void Every_provider_and_the_rates_down_says_both_and_keeps_the_quote_message_first()
    {
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo"], ProviderDown("yahoo"), PairFailed("EUR/USD")));

        Assert.True(result.NothingDone);
        // Migration 252 matches stored messages on this prefix.
        Assert.StartsWith("No quote provider succeeded", result.Message, StringComparison.Ordinal);
        Assert.Contains("Exchange rates were not stored", result.Message);
    }

    [Theory]
    [InlineData(Coffer.Api.Quotes.Yahoo.YahooBenchmarkClient.IndexRefusedCode)]
    [InlineData(Coffer.Api.Quotes.Yahoo.YahooBenchmarkClient.InstrumentUnsupportedCode)]
    [InlineData(Coffer.Api.Quotes.Yahoo.YahooBenchmarkClient.NoAdjustedCloseCode)]
    [InlineData(Coffer.Api.Quotes.MarketSeries.MarketSeriesFetcher.BenchmarkCurrencyChangedCode)]
    public void A_benchmark_refused_for_what_it_is_is_an_ordinary_healthy_run(string code)
    {
        // The per-symbol boundary, for benchmarks: an index or an unsupported instrument
        // is refused every day by design, and must not turn the switch red every day.
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo"],
            new QuoteError(SecurityId: null, Ticker: "^EXAMPLE", Code: code, Message: "refused")));

        Assert.Equal(JobRunResult.Ok, result.Result);
    }
}
