using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Coffer.Api.Quotes.Yahoo;

/// <summary>
/// The benchmark side of the Yahoo adapter (ADR-0114 D2): checks a symbol can be a benchmark,
/// and reads its daily total-return growth. A pure translator over the shared chart fetch
/// (<see cref="YahooChart"/>): it writes nothing, and every failure comes back as a code.
/// </summary>
/// <remarks>
/// <para>Egress, so a caller runs it only under the acting user's Yahoo opt-in (ADR-0114 D8,
/// ADR-0057). Requests through one instance share one throttle (<see cref="YahooRequestGate"/>):
/// the fixed gap between requests, and after a 429 the capped <c>Retry-After</c> backoff before
/// the next.</para>
/// </remarks>
public sealed class YahooBenchmarkClient
{
    /// <summary>
    /// The <c>meta.instrumentType</c> values a benchmark may have: a fund or an ETF, whose
    /// adjusted close is a total return. The one place the accepted set is spelled.
    /// </summary>
    /// <remarks>
    /// <c>ETF</c> and <c>INDEX</c> are what Yahoo was seen to return. <c>MUTUALFUND</c> for a
    /// mutual fund is NOT yet confirmed against a live response (ADR-0114, "To confirm"); if
    /// Yahoo spells it otherwise, a mutual fund is refused as
    /// <see cref="InstrumentUnsupportedCode"/> until this set is corrected.
    /// </remarks>
    public static readonly IReadOnlySet<string> AcceptedInstrumentTypes =
        new HashSet<string>(StringComparer.Ordinal) { "ETF", "MUTUALFUND" };

    /// <summary>Refusal: an index. Yahoo marks a total-return index and a price index alike
    /// as <c>INDEX</c>, with nothing to tell them apart, so neither is accepted.</summary>
    public const string IndexRefusedCode = "benchmark-is-index";

    /// <summary>Refusal: an instrument type that is not a fund or ETF (a stock, a currency, a
    /// future), or none at all.</summary>
    public const string InstrumentUnsupportedCode = "benchmark-instrument-unsupported";

    /// <summary>Refusal, and growth failure: the response carries no adjusted close, so it
    /// has no total return to read.</summary>
    public const string NoAdjustedCloseCode = "benchmark-no-adjusted-close";

    /// <summary>Decimal places a stored growth value has: <c>benchmark_growth.growth</c> is
    /// <c>NUMERIC(20,12)</c>, so a ratio is rounded to it once, here.</summary>
    public const int GrowthScale = 12;

    private readonly HttpClient _http;
    private readonly ILogger<YahooBenchmarkClient> _logger;
    private readonly TimeProvider _clock;
    private readonly YahooRequestGate _gate;

    public YahooBenchmarkClient(
        HttpClient http,
        IOptions<YahooQuoteOptions> options,
        ILogger<YahooBenchmarkClient> logger,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _logger = logger;
        _clock = clock;
        _gate = new YahooRequestGate(options.Value, clock);
    }

    /// <summary>
    /// Check <paramref name="symbol"/> can be a benchmark, from one chart request: it must be
    /// a fund or ETF (<see cref="AcceptedInstrumentTypes"/>), carry an adjusted close, and name
    /// a currency Coffer recognises (<see cref="YahooCurrency"/>, reported in its major unit).
    /// </summary>
    /// <remarks>
    /// The refusal order is the order a user can act on it: what the symbol is
    /// (<see cref="IndexRefusedCode"/>, <see cref="InstrumentUnsupportedCode"/>), whether it has
    /// a total return (<see cref="NoAdjustedCloseCode"/>), then its currency
    /// (<see cref="YahooCurrency.MissingCode"/>, <see cref="YahooCurrency.UnknownCode"/>). A
    /// fetch failure carries the quote pull's codes (<see cref="YahooErrorCodes"/>). Whatever
    /// the response did say is returned alongside a refusal.
    /// </remarks>
    public async Task<BenchmarkCheck> CheckAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var fetched = await FetchAsync(symbol, "range=1mo&interval=1d&events=div", cancellationToken)
            .ConfigureAwait(false);
        if (fetched.ErrorCode is { } fetchError)
            return BenchmarkCheck.Refused(symbol, fetchError, fetched.Message!);

        using var doc = fetched.Document!;
        if (!YahooChart.TryGetResult(doc.RootElement, out var r0))
        {
            return BenchmarkCheck.Refused(symbol, YahooErrorCodes.TickerNotResolved,
                $"Yahoo returned nothing for '{symbol}'.");
        }

        var instrumentType = YahooChart.MetaString(r0, "instrumentType");
        var metaCurrency = YahooChart.MetaString(r0, "currency");
        var currency = YahooCurrency.Resolve(metaCurrency);
        var name = YahooChart.MetaString(r0, "longName") ?? YahooChart.MetaString(r0, "shortName");
        var hasAdjustedClose = YahooChart.TryGetAdjustedCloses(r0, out var adj)
                               && adj.EnumerateArray().Any(v => YahooChart.PositiveValue(v) is not null);

        BenchmarkCheck Refuse(string code, string message) => new(
            symbol, false, code, message, instrumentType, currency.CurrencyCode, name?.Trim(), hasAdjustedClose);

        if (string.Equals(instrumentType, "INDEX", StringComparison.Ordinal))
        {
            return Refuse(IndexRefusedCode,
                $"{symbol} is an index. Yahoo does not say whether an index includes dividends, "
                + "so use a fund or ETF that tracks it.");
        }
        if (instrumentType is null || !AcceptedInstrumentTypes.Contains(instrumentType))
        {
            return Refuse(InstrumentUnsupportedCode,
                instrumentType is null
                    ? $"Yahoo does not say what {symbol} is. A benchmark is a fund or ETF."
                    : $"{symbol} is {instrumentType}, not a fund or ETF.");
        }
        if (!hasAdjustedClose)
        {
            return Refuse(NoAdjustedCloseCode,
                $"Yahoo has no adjusted close for {symbol}, so its total return cannot be read.");
        }
        if (!currency.Resolved)
        {
            return Refuse(currency.ErrorCode!,
                currency.ErrorCode == YahooCurrency.MissingCode
                    ? $"Yahoo names no currency for {symbol}."
                    : $"Yahoo names currency '{metaCurrency}' for {symbol}, which Coffer does not recognise.");
        }

        return new BenchmarkCheck(
            symbol, true, null, null, instrumentType, currency.CurrencyCode, name?.Trim(), true);
    }

    /// <summary>
    /// The daily growth of <paramref name="symbol"/>'s adjusted close over
    /// [<paramref name="fromDay"/>, <paramref name="toDay"/>], one value per trading day:
    /// that day's adjusted close over the previous trading day's, both from the same response.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a ratio.</b> Yahoo rescales the whole adjusted-close history at every new
    /// dividend or split, so closes from two fetches sit on different bases. Within one
    /// response both closes carry the same factor, which the ratio cancels (ADR-0114 D2).</para>
    ///
    /// <para><b>The first day has no value.</b> A day's growth needs the previous trading day's
    /// close from the same response, so the first trading day in the response has none and is
    /// not returned. The caller therefore always asks from its LAST STORED day (or, for a
    /// first fetch, the day before the first one it needs): that day is the base, and the
    /// first new day gets its previous close. Asking from the day after the last stored one
    /// would leave the first new day with no value at all.</para>
    ///
    /// <para><b>Days.</b> Each bar is its timestamp's UTC calendar day, stored as a
    /// <c>DATE</c> (ADR-0070), the same day rule as the quote pull. When two bars land on the
    /// same day, the later one is that day's. A null, zero or negative adjusted close is
    /// skipped: no value for that day, and the next valid day's growth is taken over the
    /// nearest valid close before it in the same response, so the pair spans the gap.</para>
    ///
    /// <para><b>Today is never returned.</b> While a market is open Yahoo reports today's bar
    /// at the live price, and a stored growth value is never rewritten (ADR-0114 D6), so an
    /// intraday ratio stored today would stand for good. The range is cut at the clock's UTC
    /// yesterday; the next day's fetch picks today up.</para>
    ///
    /// <para>Each value is rounded once to <see cref="GrowthScale"/> places. Nothing is
    /// written here.</para>
    /// </remarks>
    public async Task<BenchmarkGrowthFetch> FetchGrowthAsync(
        string symbol, DateOnly fromDay, DateOnly toDay, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var yesterday = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-1);
        var lastDay = toDay < yesterday ? toDay : yesterday;
        if (fromDay >= lastDay)
            return new BenchmarkGrowthFetch(symbol, [], null, null, null);

        // period2 is the instant after the last day, so the last day's bar is inside it.
        var period1 = new DateTimeOffset(fromDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var fetched = await FetchAsync(
                symbol, $"period1={period1}&period2={period2}&interval=1d&events=div", cancellationToken)
            .ConfigureAwait(false);
        if (fetched.ErrorCode is { } fetchError)
            return new BenchmarkGrowthFetch(symbol, [], null, fetchError, fetched.Message);

        using var doc = fetched.Document!;
        return ReadGrowth(symbol, doc.RootElement, fromDay, lastDay);
    }

    /// <summary>The translation <see cref="FetchGrowthAsync"/> applies to a response, over the
    /// days it was asked for. Separate so the arithmetic is tested on fixtures directly.</summary>
    internal static BenchmarkGrowthFetch ReadGrowth(string symbol, JsonElement root, DateOnly fromDay, DateOnly toDay)
    {
        if (!YahooChart.TryGetResult(root, out var r0) || !YahooChart.TryGetTimestamps(r0, out var timestamps))
        {
            return new BenchmarkGrowthFetch(symbol, [], null, YahooErrorCodes.TickerNotResolved,
                $"Yahoo returned no series for '{symbol}'.");
        }

        var metaCurrency = YahooChart.MetaString(r0, "currency");
        var currency = YahooCurrency.Resolve(metaCurrency);
        if (!currency.Resolved)
        {
            return new BenchmarkGrowthFetch(symbol, [], null, currency.ErrorCode,
                currency.ErrorCode == YahooCurrency.MissingCode
                    ? $"Yahoo names no currency for {symbol}."
                    : $"Yahoo names currency '{metaCurrency}' for {symbol}, which Coffer does not recognise.");
        }

        if (!YahooChart.TryGetAdjustedCloses(r0, out var adjCloses))
        {
            return new BenchmarkGrowthFetch(symbol, [], currency.CurrencyCode, NoAdjustedCloseCode,
                $"Yahoo has no adjusted close for {symbol}.");
        }

        // One valid close per UTC day, the later bar winning a shared day, in day order. A
        // minor-unit currency needs no division here: the factor cancels in the ratio.
        var closes = new SortedDictionary<DateOnly, decimal>();
        var n = Math.Min(timestamps.GetArrayLength(), adjCloses.GetArrayLength());
        for (var i = 0; i < n; i++)
        {
            if (YahooChart.BarDay(timestamps[i]) is not { } day) continue;
            if (YahooChart.PositiveValue(adjCloses[i]) is not { } close) continue;
            closes[day] = close;
        }

        var days = new List<BenchmarkGrowthDay>();
        decimal? previous = null;
        foreach (var (day, close) in closes)
        {
            if (day > toDay) break;
            if (previous is { } prior && day >= fromDay)
                days.Add(new BenchmarkGrowthDay(day, decimal.Round(close / prior, GrowthScale)));
            previous = close;
        }

        return new BenchmarkGrowthFetch(symbol, days, currency.CurrencyCode, null, null);
    }

    /// <summary>One throttled chart request, with the quote pull's failure codes.</summary>
    private async Task<(JsonDocument? Document, string? ErrorCode, string? Message)> FetchAsync(
        string symbol, string query, CancellationToken ct)
    {
        await _gate.WaitTurnAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await YahooChart.GetAsync(_http, YahooChart.Path(symbol, query), ct).ConfigureAwait(false);
            return (doc, null, null);
        }
        catch (YahooHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("Yahoo rate-limited benchmark {Symbol}; backing off.", symbol);
            await _gate.BackOffAsync(ex.RetryAfter, ct).ConfigureAwait(false);
            return (null, YahooErrorCodes.RateLimited, $"{symbol}: Yahoo returned 429 (rate limited).");
        }
        catch (YahooHttpException ex)
        {
            _logger.LogDebug(ex, "Yahoo benchmark fetch failed: {Symbol}", symbol);
            return (null, YahooErrorCodes.FetchFailed, $"{symbol}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Yahoo benchmark request failed: {Symbol}", symbol);
            return (null, YahooErrorCodes.FetchFailed, $"{symbol}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Yahoo benchmark response parse failed: {Symbol}", symbol);
            return (null, YahooErrorCodes.ParseFailed, $"{symbol}: {ex.Message}");
        }
    }
}

/// <summary>
/// The answer to <see cref="YahooBenchmarkClient.CheckAsync"/>: whether the symbol can be a
/// benchmark, the refusal code and message when not, and what the response said.
/// </summary>
/// <param name="CurrencyCode">The symbol's currency in its major unit, or null when it did
/// not resolve.</param>
/// <param name="Name">The fund's long name, else its short name, when the response has one.</param>
public sealed record BenchmarkCheck(
    string Symbol,
    bool Accepted,
    string? RefusalCode,
    string? Message,
    string? InstrumentType,
    string? CurrencyCode,
    string? Name,
    bool HasAdjustedClose)
{
    internal static BenchmarkCheck Refused(string symbol, string code, string message) =>
        new(symbol, false, code, message, null, null, null, false);
}

/// <summary>One trading day's total-return growth: its adjusted close over the previous
/// trading day's, rounded to <see cref="YahooBenchmarkClient.GrowthScale"/> places.</summary>
public sealed record BenchmarkGrowthDay(DateOnly Day, decimal Growth);

/// <summary>
/// The answer to <see cref="YahooBenchmarkClient.FetchGrowthAsync"/>: the days read, oldest
/// first, and the currency the response named (major unit); or an error code and message,
/// with no days.
/// </summary>
public sealed record BenchmarkGrowthFetch(
    string Symbol,
    IReadOnlyList<BenchmarkGrowthDay> Days,
    string? CurrencyCode,
    string? ErrorCode,
    string? Message);
