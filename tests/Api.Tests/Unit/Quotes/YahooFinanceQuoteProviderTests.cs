using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Yahoo;

namespace Coffer.Api.Tests.Unit.Quotes;

/// <summary>
/// Unit tests for the Yahoo market-data provider (ADR-0054 D1). A canned
/// <see cref="HttpMessageHandler"/> stands in for the network — the asserts
/// pin the chart-endpoint parse (last non-null close, null walk-back, date
/// normalization) and the best-effort error mapping (404 / unknown symbol →
/// typed <see cref="QuoteError"/>, never a thrown run failure).
/// </summary>
public sealed class YahooFinanceQuoteProviderTests
{
    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private static YahooFinanceQuoteProvider ProviderFor(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new DelegateHandler(handler))
        {
            BaseAddress = new Uri("https://query1.finance.yahoo.com"),
        };
        // Zero delays so the throttle doesn't slow the unit tests.
        var options = Options.Create(new YahooQuoteOptions
        {
            RequestDelayMs = 0,
            MaxBackoffSeconds = 0,
        });
        return new YahooFinanceQuoteProvider(
            http, options, NullLogger<YahooFinanceQuoteProvider>.Instance);
    }

    private static QuotePullContext ContextFor(params (Guid Id, string Ticker)[] securities) =>
        new(
            Guid.NewGuid(),
            Array.Empty<byte>(),
            securities.Select(s => new QuoteSecurityRef(s.Id, s.Ticker, "USD")).ToList());

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Build a chart response with parallel timestamp[]/close[]
    /// arrays. A null entry in <paramref name="closes"/> emits JSON null.</summary>
    private static string ChartJson(long[] timestamps, decimal?[] closes)
    {
        static string Num(decimal? d) =>
            d is null ? "null" : d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ts = string.Join(",", timestamps);
        var cl = string.Join(",", closes.Select(Num));
        return $$"""
            {
              "chart": {
                "result": [
                  {
                    "meta": { "currency": "USD", "symbol": "ETFA" },
                    "timestamp": [{{ts}}],
                    "indicators": { "quote": [ { "close": [{{cl}}] } ] }
                  }
                ],
                "error": null
              }
            }
            """;
    }

    [Fact]
    public async Task PullAsync_returns_last_non_null_close_as_the_price()
    {
        var secId = Guid.NewGuid();
        var json = ChartJson(
            timestamps: new[] { 1779451200L, 1779537600L },
            closes: new decimal?[] { 670.50m, 675.25m });
        var provider = ProviderFor(_ => Ok(json));

        var result = await provider.PullAsync(ContextFor((secId, "ETFA")), CancellationToken.None);

        var entry = Assert.Single(result.Quotes);
        Assert.Equal(secId, entry.SecurityId);
        Assert.Equal(675.25m, entry.Price);
        Assert.Equal("USD", entry.CurrencyCode);
        // Date normalized to the bar's UTC calendar date (midnight).
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1779537600).UtcDateTime.Date,
            entry.PriceAsOfUtc);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PullAsync_walks_back_over_a_trailing_null_close()
    {
        var secId = Guid.NewGuid();
        // Latest bar has a null close (holiday / trading halt) → prior wins.
        var json = ChartJson(
            timestamps: new[] { 1779451200L, 1779537600L },
            closes: new decimal?[] { 670.50m, null });
        var provider = ProviderFor(_ => Ok(json));

        var result = await provider.PullAsync(ContextFor((secId, "ETFA")), CancellationToken.None);

        var entry = Assert.Single(result.Quotes);
        Assert.Equal(670.50m, entry.Price);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1779451200).UtcDateTime.Date,
            entry.PriceAsOfUtc);
    }

    [Fact]
    public async Task PullAsync_maps_a_404_to_a_fetch_failed_error()
    {
        var secId = Guid.NewGuid();
        var provider = ProviderFor(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await provider.PullAsync(ContextFor((secId, "NOPE")), CancellationToken.None);

        Assert.Empty(result.Quotes);
        var err = Assert.Single(result.Errors);
        Assert.Equal(secId, err.SecurityId);
        Assert.Equal("NOPE", err.Ticker);
        Assert.Equal("fetch-failed", err.Code);
    }

    [Fact]
    public async Task PullAsync_maps_a_429_to_rate_limited()
    {
        // Rate limited → distinct 'rate-limited' code (not 'fetch-failed'), so
        // the outcome can tell throttling apart from a dead symbol.
        var secId = Guid.NewGuid();
        var provider = ProviderFor(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await provider.PullAsync(ContextFor((secId, "ETFA")), CancellationToken.None);

        Assert.Empty(result.Quotes);
        var err = Assert.Single(result.Errors);
        Assert.Equal(secId, err.SecurityId);
        Assert.Equal("rate-limited", err.Code);
    }

    [Fact]
    public async Task PullAsync_maps_an_empty_result_to_ticker_not_resolved()
    {
        var secId = Guid.NewGuid();
        // Yahoo's shape for an unknown symbol: result null + error set.
        const string json = """{ "chart": { "result": null, "error": { "code": "Not Found" } } }""";
        var provider = ProviderFor(_ => Ok(json));

        var result = await provider.PullAsync(ContextFor((secId, "NOPE")), CancellationToken.None);

        Assert.Empty(result.Quotes);
        var err = Assert.Single(result.Errors);
        Assert.Equal("ticker-not-resolved", err.Code);
    }

    [Fact]
    public async Task PullAsync_requests_the_symbol_in_the_chart_path()
    {
        var secId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var provider = ProviderFor(req =>
        {
            captured = req;
            return Ok(ChartJson(new[] { 1779537600L }, new decimal?[] { 100m }));
        });

        await provider.PullAsync(ContextFor((secId, "ETFA")), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("/v8/finance/chart/ETFA", captured!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("interval=1d", captured.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_iterates_all_requested_securities()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var provider = ProviderFor(req =>
        {
            var sym = req.RequestUri!.AbsolutePath.Split('/')[^1];
            return Ok(ChartJson(new[] { 1779537600L },
                new decimal?[] { sym == "AAA" ? 11m : 22m }));
        });

        var result = await provider.PullAsync(
            ContextFor((a, "AAA"), (b, "BBB")), CancellationToken.None);

        Assert.Equal(2, result.Quotes.Count);
        Assert.Contains(result.Quotes, q => q.SecurityId == a && q.Price == 11m);
        Assert.Contains(result.Quotes, q => q.SecurityId == b && q.Price == 22m);
    }
}
