using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Quotes.Frankfurter;
using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MarketSeries;

/// <summary>
/// The two outside feeds, faked at the HTTP message level (ADR-0114 D4): Yahoo's chart
/// endpoint and Frankfurter's <c>/v2/rates</c>. The real clients run over it, so a test
/// asserts what reached the wire. Every value is synthetic and generated from the request:
/// a bar or rate for each weekday asked for, so a response fits whatever range the code
/// under test computed from the clock.
/// </summary>
internal sealed class MarketDataFake : HttpMessageHandler
{
    private readonly TimeProvider _clock;

    public MarketDataFake(TimeProvider clock) => _clock = clock;

    /// <summary>Every request that reached the fake, in order.</summary>
    public ConcurrentQueue<Uri> Requests { get; } = new();

    public IReadOnlyList<Uri> YahooRequests =>
        Requests.Where(u => u.AbsolutePath.StartsWith("/v8/finance/chart/", StringComparison.Ordinal)).ToList();

    public IReadOnlyList<Uri> RateRequests =>
        Requests.Where(u => u.AbsolutePath.EndsWith("/v2/rates", StringComparison.Ordinal)).ToList();

    /// <summary>Per symbol: the instrument type the chart reports (default ETF).</summary>
    public ConcurrentDictionary<string, string> InstrumentTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Per symbol: the currency the chart reports (default USD).</summary>
    public ConcurrentDictionary<string, string> Currencies { get; } = new(StringComparer.Ordinal);

    /// <summary>Per base currency: the HTTP status the rates endpoint answers (default 200).</summary>
    public ConcurrentDictionary<string, HttpStatusCode> RateStatus { get; } = new(StringComparer.Ordinal);

    /// <summary>The rate a pair not in <see cref="Rates"/> carries every day.</summary>
    public const decimal DefaultRate = 1.25m;

    /// <summary>Per (base, quote): the rate every day of a response carries (default
    /// <see cref="DefaultRate"/>).</summary>
    public ConcurrentDictionary<(string Base, string Quote), decimal> Rates { get; } = new();

    /// <summary>Per symbol: the first day the fund has bars (it had not launched before).</summary>
    public ConcurrentDictionary<string, DateOnly> ListedFrom { get; } = new(StringComparer.Ordinal);

    /// <summary>Per base currency: the first day the feed has rates for it (it did not
    /// exist before).</summary>
    public ConcurrentDictionary<string, DateOnly> RatesFrom { get; } = new(StringComparer.Ordinal);

    /// <summary>Days past the range asked for (and past today) that every response also
    /// carries, as a misbehaving feed might: a bar or rate dated in the future.</summary>
    public int ExtraFutureDays { get; set; }

    /// <summary>Hold every response until released, to line two runs up (concurrency tests).</summary>
    public TaskCompletionSource? Gate { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue(uri);
        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (uri.AbsolutePath.StartsWith("/v8/finance/chart/", StringComparison.Ordinal))
            return Chart(Uri.UnescapeDataString(uri.AbsolutePath["/v8/finance/chart/".Length..]), query);
        if (uri.AbsolutePath.EndsWith("/v2/rates", StringComparison.Ordinal))
            return RatesResponse(query);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Chart(string symbol, System.Collections.Specialized.NameValueCollection query)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        DateOnly from, to;
        if (query["period1"] is { } p1 && query["period2"] is { } p2)
        {
            from = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(long.Parse(p1, CultureInfo.InvariantCulture)).UtcDateTime);
            to = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(long.Parse(p2, CultureInfo.InvariantCulture)).UtcDateTime).AddDays(-1);
        }
        else
        {
            from = today.AddDays(-30);
            to = today.AddDays(-1);
        }
        if (ListedFrom.TryGetValue(symbol, out var listed) && listed > from) from = listed;
        var days = Weekdays(from, Later(to, today)).ToList();
        var stamps = days.Select(d => new DateTimeOffset(d.ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero).ToUnixTimeSeconds());
        // A close that rises one unit a day from where the response starts: synthetic.
        var closes = days.Select((_, i) => (100m + i).ToString(CultureInfo.InvariantCulture)).ToList();
        var type = InstrumentTypes.GetValueOrDefault(symbol, "ETF");
        var currency = Currencies.GetValueOrDefault(symbol, "USD");
        var series = string.Join(",", closes);
        var json =
            "{\"chart\":{\"result\":[{"
            + $"\"meta\":{{\"currency\":\"{currency}\",\"instrumentType\":\"{type}\","
            + $"\"longName\":\"Synthetic {symbol} Fund\",\"symbol\":\"{symbol}\"}},"
            + $"\"timestamp\":[{string.Join(",", stamps)}],"
            + $"\"indicators\":{{\"quote\":[{{\"close\":[{series}]}}],\"adjclose\":[{{\"adjclose\":[{series}]}}]}}"
            + "}],\"error\":null}}";
        return Json(json);
    }

    private HttpResponseMessage RatesResponse(System.Collections.Specialized.NameValueCollection query)
    {
        var baseCurrency = query["base"]!;
        if (RateStatus.TryGetValue(baseCurrency, out var status) && status != HttpStatusCode.OK)
            return new HttpResponseMessage(status);
        var from = DateOnly.ParseExact(query["from"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateOnly.ParseExact(query["to"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var quotes = query["quotes"]!.Split(',');
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (RatesFrom.TryGetValue(baseCurrency, out var exists) && exists > from) from = exists;
        var rows = Weekdays(from, Later(to, today)).SelectMany(d => quotes.Select(q =>
            $$"""{"date":"{{d:yyyy-MM-dd}}","base":"{{baseCurrency}}","quote":"{{q}}","rate":{{Rates.GetValueOrDefault((baseCurrency, q), DefaultRate).ToString(CultureInfo.InvariantCulture)}},"provider":"SYNTH"}"""));
        return Json("[" + string.Join(",", rows) + "]");
    }

    /// <summary>The range's end, or <see cref="ExtraFutureDays"/> past today when set.</summary>
    private DateOnly Later(DateOnly to, DateOnly today) =>
        ExtraFutureDays > 0 ? today.AddDays(ExtraFutureDays) : to;

    /// <summary>Every Monday-to-Friday in [from, to].</summary>
    public static IEnumerable<DateOnly> Weekdays(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                yield return d;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A Yahoo benchmark client over this fake, with no throttle delay.</summary>
    public YahooBenchmarkClient Yahoo() =>
        new(new HttpClient(this, disposeHandler: false) { BaseAddress = YahooChart.BaseAddress },
            Options.Create(new YahooQuoteOptions { RequestDelayMs = 0, MaxBackoffSeconds = 0 }),
            NullLogger<YahooBenchmarkClient>.Instance, _clock);

    /// <summary>A Frankfurter client over this fake, at the default base URL.</summary>
    public FrankfurterRatesClient Frankfurter() =>
        new(new HttpClient(this, disposeHandler: false),
            Options.Create(new FrankfurterOptions()),
            NullLogger<FrankfurterRatesClient>.Instance, _clock);

    /// <summary>Route the app's benchmark and rate clients through this fake.</summary>
    public ApiFactory Install(ApiFactory factory) =>
        factory.WithServices(s =>
        {
            s.RemoveAll<YahooBenchmarkClient>();
            s.AddTransient(_ => Yahoo());
            s.RemoveAll<FrankfurterRatesClient>();
            s.AddTransient(_ => Frankfurter());
        });
}
