using System.Net;
using System.Text.Json;

namespace Coffer.Api.Quotes.Yahoo;

/// <summary>
/// The one Yahoo v8 chart fetch (ADR-0054 D1) every Yahoo caller goes through: the held-security
/// quote pull and the benchmark client (ADR-0114 D2). HTTP setup, the request, the status
/// mapping and the response's shape live here, so the two callers cannot drift.
/// </summary>
internal static class YahooChart
{
    /// <summary>The chart endpoint's host.</summary>
    public static readonly Uri BaseAddress = new("https://query1.finance.yahoo.com");

    /// <summary>
    /// Base address, timeout and User-Agent for a typed Yahoo <see cref="HttpClient"/>. Every
    /// Yahoo client is registered with this, so they all present the same agent.
    /// </summary>
    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.BaseAddress = BaseAddress;
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CofferQuotes/1.0)");
    }

    /// <summary>
    /// GET one chart path and parse it. A non-success status throws
    /// <see cref="YahooHttpException"/> carrying the status and any <c>Retry-After</c>.
    /// </summary>
    public static async Task<JsonDocument> GetAsync(HttpClient http, string pathAndQuery, CancellationToken ct)
    {
        using var resp = await http.GetAsync(pathAndQuery, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 404 unknown symbol; 429 rate-limited; 5xx upstream.
            throw new YahooHttpException(
                resp.StatusCode,
                resp.Headers.RetryAfter?.Delta,
                $"HTTP {(int)resp.StatusCode} from Yahoo chart endpoint");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>The chart path for <paramref name="symbol"/>, with <paramref name="query"/>
    /// (no leading <c>?</c>).</summary>
    public static string Path(string symbol, string query) =>
        $"/v8/finance/chart/{Uri.EscapeDataString(symbol)}?{query}";

    /// <summary><c>chart.result[0]</c>, or false when the response has none (Yahoo's shape for
    /// an unknown symbol is <c>result: null</c> with <c>error</c> set).</summary>
    public static bool TryGetResult(JsonElement root, out JsonElement result0)
    {
        result0 = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("chart", out var chart) || chart.ValueKind != JsonValueKind.Object) return false;
        if (!chart.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
        {
            return false;
        }
        result0 = result[0];
        return result0.ValueKind == JsonValueKind.Object;
    }

    /// <summary>A string property of <c>meta</c>, or null when absent, not a string, or blank.</summary>
    public static string? MetaString(JsonElement result0, string name)
    {
        if (!result0.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
        if (!meta.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>The <c>timestamp[]</c> array, or false when absent.</summary>
    public static bool TryGetTimestamps(JsonElement result0, out JsonElement timestamps) =>
        result0.TryGetProperty("timestamp", out timestamps) && timestamps.ValueKind == JsonValueKind.Array;

    /// <summary><c>indicators.quote[0].close[]</c> (unadjusted), or false when absent.</summary>
    public static bool TryGetCloses(JsonElement result0, out JsonElement closes) =>
        TryGetIndicatorSeries(result0, "quote", "close", out closes);

    /// <summary><c>indicators.adjclose[0].adjclose[]</c> (split- and dividend-adjusted), or false
    /// when absent.</summary>
    public static bool TryGetAdjustedCloses(JsonElement result0, out JsonElement adjCloses) =>
        TryGetIndicatorSeries(result0, "adjclose", "adjclose", out adjCloses);

    private static bool TryGetIndicatorSeries(
        JsonElement result0, string block, string series, out JsonElement values)
    {
        values = default;
        if (!result0.TryGetProperty("indicators", out var indicators)
            || indicators.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!indicators.TryGetProperty(block, out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() == 0
            || list[0].ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return list[0].TryGetProperty(series, out values) && values.ValueKind == JsonValueKind.Array;
    }

    /// <summary>A bar's UTC calendar day (ADR-0070), or null when its timestamp is unreadable.</summary>
    public static DateOnly? BarDay(JsonElement timestamp) =>
        timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unix)
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime)
            : null;

    /// <summary>A positive decimal value, or null for JSON null, a non-number, zero or a negative.</summary>
    public static decimal? PositiveValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && d > 0m ? d : null;
}

/// <summary>Per-symbol error codes every Yahoo caller reports in the same words.</summary>
public static class YahooErrorCodes
{
    /// <summary>The response carried no usable result or series for the symbol.</summary>
    public const string TickerNotResolved = "ticker-not-resolved";
    /// <summary>Yahoo answered 429.</summary>
    public const string RateLimited = "rate-limited";
    /// <summary>Any other non-success status, or the request itself failed.</summary>
    public const string FetchFailed = "fetch-failed";
    /// <summary>The response was not JSON.</summary>
    public const string ParseFailed = "parse-failed";
}

/// <summary>Non-success HTTP from the Yahoo chart endpoint, carrying the status and any
/// <c>Retry-After</c> so a caller can back off on 429.</summary>
internal sealed class YahooHttpException : Exception
{
    public YahooHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter, string message)
        : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// The Yahoo throttle (ADR-0054): a fixed gap between sequential requests, and after a 429 a
/// backoff honouring <c>Retry-After</c> up to a cap. One gate per run of requests; the first
/// request goes at once.
/// </summary>
internal sealed class YahooRequestGate
{
    private readonly TimeSpan _requestDelay;
    private readonly TimeSpan _maxBackoff;
    private readonly TimeProvider _clock;
    private bool _first = true;

    public YahooRequestGate(YahooQuoteOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _requestDelay = TimeSpan.FromMilliseconds(Math.Max(0, options.RequestDelayMs));
        _maxBackoff = TimeSpan.FromSeconds(Math.Max(0, options.MaxBackoffSeconds));
        _clock = clock;
    }

    /// <summary>Wait the request gap, unless this is the run's first request.</summary>
    public async Task WaitTurnAsync(CancellationToken ct)
    {
        if (!_first && _requestDelay > TimeSpan.Zero)
            await Task.Delay(_requestDelay, _clock, ct).ConfigureAwait(false);
        _first = false;
    }

    /// <summary>Back off after a 429: the response's <c>Retry-After</c>, capped.</summary>
    public async Task BackOffAsync(TimeSpan? retryAfter, CancellationToken ct)
    {
        var backoff = retryAfter is { } ra && ra < _maxBackoff ? ra : _maxBackoff;
        if (backoff > TimeSpan.Zero)
            await Task.Delay(backoff, _clock, ct).ConfigureAwait(false);
    }
}
