using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Unit.Quotes;

/// <summary>
/// The benchmark side of the Yahoo adapter (ADR-0114 D2), against chart responses authored
/// here with synthetic values: the add-time check (fund or ETF, adjusted close, currency) and
/// the daily growth read, whose ratios must survive Yahoo rescaling its adjusted-close history.
/// </summary>
public sealed class YahooBenchmarkClientTests
{
    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    /// <summary>The clock the growth tests run at: the bars below are all before it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static YahooBenchmarkClient ClientFor(
        Func<HttpRequestMessage, HttpResponseMessage> handler, DateTimeOffset? now = null)
    {
        var http = new HttpClient(new DelegateHandler(handler)) { BaseAddress = YahooChart.BaseAddress };
        var options = Options.Create(new YahooQuoteOptions { RequestDelayMs = 0, MaxBackoffSeconds = 0 });
        return new YahooBenchmarkClient(
            http, options, NullLogger<YahooBenchmarkClient>.Instance, new TestClock(now ?? Now));
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A US trading day's bar timestamp: 13:30 UTC, the market open.</summary>
    private static long Bar(int month, int day) =>
        new DateTimeOffset(2026, month, day, 13, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>
    /// A chart response. <paramref name="adjCloses"/> null omits the adjclose block; a null
    /// entry is JSON null. <paramref name="currency"/> and <paramref name="instrumentType"/>
    /// null omit the property.
    /// </summary>
    private static string Chart(
        long[] timestamps,
        decimal?[]? adjCloses,
        string? currency = "USD",
        string? instrumentType = "ETF",
        string? longName = "Synthetic World Fund")
    {
        static string Num(decimal? d) => d is null ? "null" : d.Value.ToString(CultureInfo.InvariantCulture);
        var meta = new List<string>();
        if (currency is not null) meta.Add($"\"currency\": \"{currency}\"");
        if (instrumentType is not null) meta.Add($"\"instrumentType\": \"{instrumentType}\"");
        if (longName is not null) meta.Add($"\"longName\": \"{longName}\"");
        meta.Add("\"symbol\": \"SYNTH\"");
        var closes = string.Join(",", (adjCloses ?? timestamps.Select(_ => (decimal?)1m).ToArray()).Select(Num));
        var adj = adjCloses is null
            ? string.Empty
            : $", \"adjclose\": [ {{ \"adjclose\": [{string.Join(",", adjCloses.Select(Num))}] }} ]";
        return $$"""
            {
              "chart": {
                "result": [
                  {
                    "meta": { {{string.Join(", ", meta)}} },
                    "timestamp": [{{string.Join(",", timestamps)}}],
                    "events": { "dividends": {} },
                    "indicators": { "quote": [ { "close": [{{closes}}] } ]{{adj}} }
                  }
                ],
                "error": null
              }
            }
            """;
    }

    private static BenchmarkGrowthFetch Read(string json, DateOnly from, DateOnly to)
    {
        using var doc = JsonDocument.Parse(json);
        return YahooBenchmarkClient.ReadGrowth("SYNTH", doc.RootElement, from, to);
    }

    private static readonly long[] Week =
        [Bar(5, 18), Bar(5, 19), Bar(5, 20), Bar(5, 21), Bar(5, 22)];

    // ---- CheckAsync ----------------------------------------------------------------

    [Theory]
    [InlineData("ETF")]
    [InlineData("MUTUALFUND")]
    public async Task Check_accepts_a_fund_or_ETF_with_an_adjusted_close(string instrumentType)
    {
        var client = ClientFor(_ => Ok(Chart(Week, [10m, 10.1m, 10.2m, 10.3m, 10.4m], instrumentType: instrumentType)));

        var check = await client.CheckAsync("SYNTH");

        Assert.True(check.Accepted);
        Assert.Null(check.RefusalCode);
        Assert.Equal(instrumentType, check.InstrumentType);
        Assert.Equal("USD", check.CurrencyCode);
        Assert.Equal("Synthetic World Fund", check.Name);
        Assert.True(check.HasAdjustedClose);
    }

    [Fact]
    public void The_accepted_instrument_types_are_exactly_fund_and_ETF()
    {
        Assert.Equal(["ETF", "MUTUALFUND"], YahooBenchmarkClient.AcceptedInstrumentTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Check_refuses_an_index_even_with_an_adjusted_close()
    {
        var client = ClientFor(_ => Ok(Chart(Week, [10m, 10.1m, 10.2m, 10.3m, 10.4m], instrumentType: "INDEX")));

        var check = await client.CheckAsync("^SYNTHTR");

        Assert.False(check.Accepted);
        Assert.Equal("benchmark-is-index", check.RefusalCode);
        Assert.Equal("INDEX", check.InstrumentType);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
    }

    [Theory]
    [InlineData("EQUITY")]
    [InlineData("CRYPTOCURRENCY")]
    [InlineData("etf")]
    [InlineData(null)]
    public async Task Check_refuses_anything_that_is_not_a_fund_or_ETF(string? instrumentType)
    {
        var client = ClientFor(_ => Ok(Chart(Week, [10m, 10.1m, 10.2m, 10.3m, 10.4m], instrumentType: instrumentType)));

        var check = await client.CheckAsync("SYNTH");

        Assert.False(check.Accepted);
        Assert.Equal("benchmark-instrument-unsupported", check.RefusalCode);
    }

    [Fact]
    public async Task Check_refuses_a_response_with_no_adjusted_close_block()
    {
        var client = ClientFor(_ => Ok(Chart(Week, adjCloses: null)));

        var check = await client.CheckAsync("SYNTH");

        Assert.False(check.Accepted);
        Assert.Equal("benchmark-no-adjusted-close", check.RefusalCode);
        Assert.False(check.HasAdjustedClose);
    }

    [Fact]
    public async Task Check_refuses_an_adjusted_close_with_no_value_in_it()
    {
        var client = ClientFor(_ => Ok(Chart(Week, [null, null, null, null, null])));

        var check = await client.CheckAsync("SYNTH");

        Assert.Equal("benchmark-no-adjusted-close", check.RefusalCode);
    }

    [Fact]
    public async Task Check_reports_a_minor_unit_fund_in_its_major_currency()
    {
        var client = ClientFor(_ => Ok(Chart(Week, [700m, 701m, 702m, 703m, 704m], currency: "GBp")));

        var check = await client.CheckAsync("SYNTH.L");

        Assert.True(check.Accepted);
        Assert.Equal("GBP", check.CurrencyCode);
    }

    [Theory]
    [InlineData(null, "currency-missing")]
    [InlineData("XYz", "currency-unknown")]
    public async Task Check_fails_a_missing_or_unknown_currency_closed(string? currency, string code)
    {
        var client = ClientFor(_ => Ok(Chart(Week, [10m, 10.1m, 10.2m, 10.3m, 10.4m], currency: currency)));

        var check = await client.CheckAsync("SYNTH");

        Assert.False(check.Accepted);
        Assert.Equal(code, check.RefusalCode);
        Assert.Null(check.CurrencyCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "fetch-failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate-limited")]
    [InlineData(HttpStatusCode.BadGateway, "fetch-failed")]
    public async Task Check_maps_a_failed_request_to_the_quote_pulls_codes(HttpStatusCode status, string code)
    {
        var client = ClientFor(_ => new HttpResponseMessage(status));

        var check = await client.CheckAsync("SYNTH");

        Assert.False(check.Accepted);
        Assert.Equal(code, check.RefusalCode);
    }

    [Fact]
    public async Task Check_maps_an_unknown_symbol_to_ticker_not_resolved()
    {
        var client = ClientFor(_ => Ok("""{ "chart": { "result": null, "error": { "code": "Not Found" } } }"""));

        var check = await client.CheckAsync("NOPE");

        Assert.Equal("ticker-not-resolved", check.RefusalCode);
    }

    [Fact]
    public async Task Check_makes_one_daily_chart_request_with_dividend_events()
    {
        var requests = new List<Uri>();
        var client = ClientFor(req =>
        {
            requests.Add(req.RequestUri!);
            return Ok(Chart(Week, [10m, 10.1m, 10.2m, 10.3m, 10.4m]));
        });

        await client.CheckAsync("SYN TH");

        var uri = Assert.Single(requests);
        Assert.Equal("query1.finance.yahoo.com", uri.Host);
        Assert.Equal("/v8/finance/chart/SYN%20TH", uri.AbsolutePath);
        Assert.Contains("interval=1d", uri.Query, StringComparison.Ordinal);
        Assert.Contains("range=", uri.Query, StringComparison.Ordinal);
        Assert.Contains("events=div", uri.Query, StringComparison.Ordinal);
    }

    // ---- growth --------------------------------------------------------------------

    [Fact]
    public void Growth_is_each_days_adjusted_close_over_the_previous_days_from_the_same_response()
    {
        var growth = Read(Chart(Week, [100m, 101m, 99.99m, 100.9899m, 102m]),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));

        Assert.Null(growth.ErrorCode);
        Assert.Equal("USD", growth.CurrencyCode);
        Assert.Equal(
            [
                new BenchmarkGrowthDay(new DateOnly(2026, 5, 19), 1.01m),
                new BenchmarkGrowthDay(new DateOnly(2026, 5, 20), 0.99m),
                new BenchmarkGrowthDay(new DateOnly(2026, 5, 21), 1.01m),
                new BenchmarkGrowthDay(new DateOnly(2026, 5, 22), decimal.Round(102m / 100.9899m, 12)),
            ],
            growth.Days);
    }

    [Fact]
    public void Two_fetches_on_different_adjustment_bases_give_identical_growth()
    {
        // The same five days, fetched before and after a later dividend: Yahoo has scaled
        // every earlier adjusted close by the same factor, so stored closes would jump where
        // the two fetches meet. The ratios do not move.
        decimal[] before = [100m, 102m, 101m, 103.02m, 104.05m];
        const decimal factor = 0.9731m;
        var after = before.Select(c => (decimal?)(c * factor)).ToArray();

        var first = Read(Chart(Week, before.Select(c => (decimal?)c).ToArray()),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));
        var second = Read(Chart(Week, after), new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));

        Assert.Equal(4, first.Days.Count);
        Assert.Equal(first.Days, second.Days);
        Assert.NotEqual(before[1], after[1]);   // the fixtures really are on different bases
    }

    [Fact]
    public void A_null_or_zero_close_is_skipped_and_the_pair_spans_it()
    {
        var growth = Read(Chart(Week, [100m, null, 0m, 110m, 121m]),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));

        Assert.Equal(
            [
                new BenchmarkGrowthDay(new DateOnly(2026, 5, 21), 1.1m),
                new BenchmarkGrowthDay(new DateOnly(2026, 5, 22), 1.1m),
            ],
            growth.Days);
    }

    [Fact]
    public void A_leading_null_close_moves_the_base_to_the_first_valid_one()
    {
        var growth = Read(Chart(Week, [null, 50m, 55m, 55m, 44m]),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));

        Assert.Equal(new DateOnly(2026, 5, 20), growth.Days[0].Day);
        Assert.Equal(1.1m, growth.Days[0].Growth);
        Assert.Equal(3, growth.Days.Count);
    }

    [Fact]
    public void The_first_day_is_only_the_base_and_earlier_days_are_not_returned()
    {
        // Asked from 5/20: 5/18 and 5/19 are outside the range; 5/19 is still the base 5/20
        // is measured from, since it is the nearest valid close before it.
        var growth = Read(Chart(Week, [100m, 200m, 220m, 231m, 231m]),
            new DateOnly(2026, 5, 20), new DateOnly(2026, 5, 22));

        Assert.Equal(new DateOnly(2026, 5, 20), growth.Days[0].Day);
        Assert.Equal(1.1m, growth.Days[0].Growth);
        Assert.Equal(3, growth.Days.Count);
    }

    [Fact]
    public void Days_after_the_range_are_not_returned()
    {
        var growth = Read(Chart(Week, [100m, 101m, 102m, 103m, 104m]),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 20));

        Assert.Equal([new DateOnly(2026, 5, 19), new DateOnly(2026, 5, 20)], growth.Days.Select(d => d.Day));
    }

    [Fact]
    public void Two_bars_on_one_UTC_day_keep_the_later()
    {
        long[] ts = [Bar(5, 18), Bar(5, 19), Bar(5, 19) + 3600];
        var growth = Read(Chart(ts, [100m, 105m, 110m]), new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 19));

        Assert.Equal(1.1m, Assert.Single(growth.Days).Growth);
    }

    [Fact]
    public void Growth_is_rounded_once_to_twelve_places()
    {
        var growth = Read(Chart([Bar(5, 18), Bar(5, 19)], [3m, 1m]),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 19));

        Assert.Equal(0.333333333333m, Assert.Single(growth.Days).Growth);
        Assert.Equal(12, YahooBenchmarkClient.GrowthScale);
    }

    [Fact]
    public void A_minor_unit_fund_reads_the_same_ratios_in_its_major_currency()
    {
        var growth = Read(Chart([Bar(5, 18), Bar(5, 19)], [700m, 770m], currency: "GBp"),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 19));

        Assert.Equal("GBP", growth.CurrencyCode);
        Assert.Equal(1.1m, Assert.Single(growth.Days).Growth);
    }

    [Theory]
    [InlineData(null, "currency-missing")]
    [InlineData("ZZp", "currency-unknown")]
    public void Growth_fails_a_missing_or_unknown_currency_closed(string? currency, string code)
    {
        var growth = Read(Chart(Week, [100m, 101m, 102m, 103m, 104m], currency: currency),
            new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));

        Assert.Equal(code, growth.ErrorCode);
        Assert.Empty(growth.Days);
    }

    [Fact]
    public void Growth_without_an_adjusted_close_fails_closed()
    {
        var growth = Read(Chart(Week, adjCloses: null), new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));

        Assert.Equal("benchmark-no-adjusted-close", growth.ErrorCode);
        Assert.Empty(growth.Days);
    }

    [Fact]
    public async Task Fetch_asks_for_the_range_by_period_and_never_returns_today()
    {
        // The clock is 5/22 midday: 5/22's bar is a live price, not a close, and a stored
        // growth value is never rewritten, so the range stops at 5/21.
        Uri? asked = null;
        var client = ClientFor(req =>
        {
            asked = req.RequestUri;
            return Ok(Chart(Week, [100m, 101m, 102m, 103m, 104m]));
        }, now: new DateTimeOffset(2026, 5, 22, 15, 0, 0, TimeSpan.Zero));

        var growth = await client.FetchGrowthAsync("SYNTH", new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 31));

        Assert.Equal(new DateOnly(2026, 5, 21), growth.Days[^1].Day);
        Assert.Equal(3, growth.Days.Count);
        var p1 = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var p2 = new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Contains($"period1={p1}", asked!.Query, StringComparison.Ordinal);
        Assert.Contains($"period2={p2}", asked.Query, StringComparison.Ordinal);
        Assert.Contains("events=div", asked.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_with_nothing_after_the_base_day_makes_no_request()
    {
        var called = false;
        var client = ClientFor(_ => { called = true; return Ok(Chart(Week, [1m, 1m, 1m, 1m, 1m])); },
            now: new DateTimeOffset(2026, 5, 22, 15, 0, 0, TimeSpan.Zero));

        var growth = await client.FetchGrowthAsync("SYNTH", new DateOnly(2026, 5, 21), new DateOnly(2026, 5, 22));

        Assert.False(called);
        Assert.Empty(growth.Days);
        Assert.Null(growth.ErrorCode);
    }

    [Fact]
    public async Task Fetch_maps_a_failed_request_to_an_error_with_no_days()
    {
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var growth = await client.FetchGrowthAsync("SYNTH", new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 22));

        Assert.Equal("rate-limited", growth.ErrorCode);
        Assert.Empty(growth.Days);
    }
}
