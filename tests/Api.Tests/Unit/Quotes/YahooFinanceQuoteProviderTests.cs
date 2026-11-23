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
            http, options, NullLogger<YahooFinanceQuoteProvider>.Instance, TimeProvider.System);
    }

    private static QuotePullContext ContextFor(params (Guid Id, string Ticker)[] securities) =>
        new(
            Guid.NewGuid(),
            Array.Empty<byte>(),
            securities.Select(s => new QuoteSecurityRef(s.Id, s.Ticker)).ToList());

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Build a chart response with parallel timestamp[]/close[]
    /// arrays. A null entry in <paramref name="closes"/> emits JSON null.</summary>
    private static string ChartJson(long[] timestamps, decimal?[] closes, string? currency = "USD")
    {
        static string Num(decimal? d) =>
            d is null ? "null" : d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ts = string.Join(",", timestamps);
        var cl = string.Join(",", closes.Select(Num));
        var currencyProperty = currency is null ? string.Empty : $"\"currency\": \"{currency}\", ";
        return $$"""
            {
              "chart": {
                "result": [
                  {
                    "meta": { {{currencyProperty}}"symbol": "ETFA" },
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

    // ---- currency (ADR-0114 D5) ---------------------------------------------------

    [Fact]
    public async Task PullAsync_stamps_the_currency_the_response_names()
    {
        // The defect: every quote was stored as USD whatever the symbol traded in.
        var secId = Guid.NewGuid();
        var provider = ProviderFor(_ =>
            Ok(ChartJson(new[] { 1779537600L }, new decimal?[] { 41.25m }, currency: "EUR")));

        var result = await provider.PullAsync(ContextFor((secId, "FUNDE")), CancellationToken.None);

        var entry = Assert.Single(result.Quotes);
        Assert.Equal("EUR", entry.CurrencyCode);
        Assert.Equal(41.25m, entry.Price);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("GBp", "GBP", "1234.5", "12.345")]
    [InlineData("ZAc", "ZAR", "8150", "81.5")]
    [InlineData("ILA", "ILS", "2702.37", "27.0237")]
    public async Task PullAsync_stores_a_minor_unit_quote_in_its_major_unit(
        string quoted, string major, string closeText, string expectedText)
    {
        var close = decimal.Parse(closeText, System.Globalization.CultureInfo.InvariantCulture);
        var expected = decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture);
        var secId = Guid.NewGuid();
        var provider = ProviderFor(_ =>
            Ok(ChartJson(new[] { 1779537600L }, new decimal?[] { close }, currency: quoted)));

        var result = await provider.PullAsync(ContextFor((secId, "MINOR")), CancellationToken.None);

        var entry = Assert.Single(result.Quotes);
        Assert.Equal(major, entry.CurrencyCode);
        Assert.Equal(expected, entry.Price);
    }

    [Fact]
    public async Task PullAsync_rounds_a_minor_unit_quote_once_after_dividing()
    {
        // A pence quote finer than the column divides to pounds still finer, and is rounded
        // once to the column's scale. Rounding the pence to 4dp first and dividing would
        // leave a figure past that scale for the column to round again.
        var secId = Guid.NewGuid();
        var provider = ProviderFor(_ =>
            Ok(ChartJson(new[] { 1779537600L }, new decimal?[] { 1234.56789m }, currency: "GBp")));

        var result = await provider.PullAsync(ContextFor((secId, "MINOR")), CancellationToken.None);

        Assert.Equal(12.3457m, Assert.Single(result.Quotes).Price);
    }

    [Theory]
    [InlineData("XYz")]   // a lower-case-suffixed minor unit the table does not list
    [InlineData("gbp")]   // the right letters in the wrong case
    [InlineData("US")]    // not three letters
    [InlineData("USDT")]
    [InlineData("U$D")]
    public async Task PullAsync_fails_an_unrecognised_currency_closed(string currency)
    {
        var secId = Guid.NewGuid();
        var provider = ProviderFor(_ =>
            Ok(ChartJson(new[] { 1779537600L }, new decimal?[] { 100m }, currency: currency)));

        var result = await provider.PullAsync(ContextFor((secId, "ODD")), CancellationToken.None);

        Assert.Empty(result.Quotes);
        var err = Assert.Single(result.Errors);
        Assert.Equal(secId, err.SecurityId);
        Assert.Equal("ODD", err.Ticker);
        Assert.Equal("currency-unknown", err.Code);
    }

    [Fact]
    public async Task PullAsync_fails_a_missing_currency_closed()
    {
        var secId = Guid.NewGuid();
        var provider = ProviderFor(_ =>
            Ok(ChartJson(new[] { 1779537600L }, new decimal?[] { 100m }, currency: null)));

        var result = await provider.PullAsync(ContextFor((secId, "NOCCY")), CancellationToken.None);

        Assert.Empty(result.Quotes);
        var err = Assert.Single(result.Errors);
        Assert.Equal(secId, err.SecurityId);
        Assert.Equal("currency-missing", err.Code);
    }

    [Fact]
    public async Task PullAsync_fails_one_symbol_closed_and_still_prices_the_next()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var provider = ProviderFor(req =>
        {
            var sym = req.RequestUri!.AbsolutePath.Split('/')[^1];
            return Ok(ChartJson(new[] { 1779537600L }, new decimal?[] { 10m },
                currency: sym == "AAA" ? null : "CAD"));
        });

        var result = await provider.PullAsync(
            ContextFor((a, "AAA"), (b, "BBB")), CancellationToken.None);

        var entry = Assert.Single(result.Quotes);
        Assert.Equal(b, entry.SecurityId);
        Assert.Equal("CAD", entry.CurrencyCode);
        Assert.Equal(a, Assert.Single(result.Errors).SecurityId);
    }
}
