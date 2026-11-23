using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Coffer.Api.Quotes.Frankfurter;

/// <summary>
/// Exchange rates from Frankfurter's blended feed (ADR-0114 D4): a pure translator from its
/// <c>/v2/rates</c> response to one rate per (day, pair). It writes nothing, and every failure
/// comes back as a code.
/// </summary>
/// <remarks>
/// <para>Egress, and opt-in: a caller runs it only when the acting user's <c>quotes</c>
/// preference lists <see cref="Key"/> (ADR-0114 D8). No key and no quota.</para>
///
/// <para><b>Request.</b> <c>GET {base}/v2/rates?from=&lt;day&gt;&amp;to=&lt;day&gt;&amp;base=&lt;from
/// currency&gt;&amp;quotes=&lt;to currencies, comma separated&gt;</c>. The blended feed is the
/// default, so no <c>providers</c> parameter is sent. The account's currency is the
/// <c>base</c>, so no stored rate is the inverse of another (ADR-0114 D4).</para>
///
/// <para><b>Paging, assumed.</b> Whether <c>/v2/rates</c> limits a range is not confirmed
/// (ADR-0114, "To confirm"), so every span is asked for one calendar year at a time, oldest
/// first, whether the service needs it or not. A year that fails stops the fetch: what came
/// before it is returned, with <see cref="FxRatesFetch.FetchedThrough"/> at the last year that
/// succeeded, so coverage never claims a day the fetch did not read.</para>
///
/// <para><b>Response, assumed.</b> The blended response's exact shape is not confirmed
/// (ADR-0114, "To confirm"). The parser takes either of these and refuses anything else:</para>
/// <list type="bullet">
/// <item>a list of rows, at the root or under <c>rates</c>:
/// <c>[{"date":"2026-05-18","base":"EUR","quote":"USD","rate":&lt;rate&gt;,"provider":"ECB"}]</c>;
/// a row's <c>base</c> may instead sit once at the root, and may be absent (it is then the
/// base asked for);</item>
/// <item>the v1 map: <c>{"base":"EUR","rates":{"2026-05-18":{"USD":&lt;rate&gt;}}}</c>.</item>
/// </list>
/// <para>The upstream source is a row's <c>provider</c> or <c>source</c> string, else the
/// root's, else none; an array of names is joined with commas. A row for a currency not asked
/// for, or a day outside the year asked for, is ignored. A row whose day, quote or rate
/// cannot be read, whose rate is not positive, or whose base is not the one asked for, fails
/// the whole year (<see cref="ParseFailedCode"/>): dropping it would leave a gap the coverage
/// claims is filled. A day's second row for the same pair is ignored, the first winning as it
/// does in storage (ADR-0114 D6).</para>
///
/// <para><b>Days.</b> The feed has no weekend or holiday rows, and none are invented: a day
/// converts at the last rate on or before it, which is the reader's rule (ADR-0114 D4).</para>
/// </remarks>
public sealed class FrankfurterRatesClient
{
    /// <summary>The opt-in provider key in the <c>quotes</c> preference's
    /// <c>enabledProviders</c>.</summary>
    public const string Key = "frankfurter";

    /// <summary>Name shown on the Quotes settings tab.</summary>
    public const string DisplayName = "Frankfurter (exchange rates)";

    /// <summary>Decimal places a stored rate has: <c>fx_rates.rate</c> is
    /// <c>NUMERIC(28,12)</c>, so a rate is rounded to it once, here.</summary>
    public const int RateScale = 12;

    /// <summary>The request failed: a non-success status, or no response.</summary>
    public const string FetchFailedCode = "fx-fetch-failed";

    /// <summary>The response was not JSON, or not a shape the parser reads, or a row in it
    /// could not be read.</summary>
    public const string ParseFailedCode = "fx-parse-failed";

    /// <summary>The configured base URL is not an absolute http(s) URL.</summary>
    public const string BaseUrlInvalidCode = "fx-base-url-invalid";

    private readonly HttpClient _http;
    private readonly Uri? _baseUri;
    private readonly ILogger<FrankfurterRatesClient> _logger;
    private readonly TimeProvider _clock;

    public FrankfurterRatesClient(
        HttpClient http,
        IOptions<FrankfurterOptions> options,
        ILogger<FrankfurterRatesClient> logger,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _baseUri = options.Value.ResolveBaseUri();
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// The rates from <paramref name="fromCurrency"/> into each of <paramref name="toCurrencies"/>
    /// for every published day in [<paramref name="fromDay"/>, <paramref name="toDay"/>], oldest
    /// first. A day after the clock's UTC today is not asked for.
    /// </summary>
    /// <remarks>
    /// The currencies must already be in stored form (three upper-case characters); that is
    /// a caller's contract, so a bad one throws. A rate published after this fetch for a day
    /// it covered is read by the next fetch only if that one starts AT
    /// <see cref="FxRatesFetch.FetchedThrough"/>, not after it; starting there costs nothing,
    /// since the first stored rate for a day is kept.
    /// </remarks>
    public async Task<FxRatesFetch> FetchRatesAsync(
        string fromCurrency,
        IReadOnlyCollection<string> toCurrencies,
        DateOnly fromDay,
        DateOnly toDay,
        CancellationToken cancellationToken = default)
    {
        RequireStoredCode(fromCurrency, nameof(fromCurrency));
        ArgumentNullException.ThrowIfNull(toCurrencies);
        foreach (var c in toCurrencies) RequireStoredCode(c, nameof(toCurrencies));

        var quotes = new SortedSet<string>(
            toCurrencies.Where(c => !string.Equals(c, fromCurrency, StringComparison.Ordinal)),
            StringComparer.Ordinal);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var lastDay = toDay < today ? toDay : today;
        if (quotes.Count == 0 || fromDay > lastDay)
            return new FxRatesFetch([], null, null, null);

        if (_baseUri is null)
        {
            return new FxRatesFetch([], null, BaseUrlInvalidCode,
                $"{FrankfurterOptions.SectionName}:BaseUrl is not an absolute http(s) URL.");
        }

        var rates = new List<FxRate>();
        DateOnly? fetchedThrough = null;
        foreach (var (chunkFrom, chunkTo) in YearChunks(fromDay, lastDay))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = new Uri(_baseUri,
                "v2/rates"
                + $"?from={chunkFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                + $"&to={chunkTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                + $"&base={Uri.EscapeDataString(fromCurrency)}"
                + $"&quotes={string.Join(",", quotes.Select(Uri.EscapeDataString))}");

            string? error;
            string? message;
            try
            {
                using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return Stop(FetchFailedCode,
                        $"HTTP {(int)resp.StatusCode} from the exchange-rate service for {fromCurrency} "
                        + $"{chunkFrom:yyyy-MM-dd}..{chunkTo:yyyy-MM-dd}.");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                (error, message) = Parse(doc.RootElement, fromCurrency, quotes, chunkFrom, chunkTo, rates);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Exchange-rate request failed: {From}", fromCurrency);
                return Stop(FetchFailedCode, $"{fromCurrency}: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                // The HttpClient timeout, not the caller's token.
                _logger.LogDebug(ex, "Exchange-rate request timed out: {From}", fromCurrency);
                return Stop(FetchFailedCode, $"{fromCurrency}: the exchange-rate service did not answer in time.");
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Exchange-rate response was not JSON: {From}", fromCurrency);
                return Stop(ParseFailedCode, $"{fromCurrency}: {ex.Message}");
            }

            if (error is not null) return Stop(error, message!);
            fetchedThrough = chunkTo;
        }

        return new FxRatesFetch(Ordered(rates), fetchedThrough, null, null);

        FxRatesFetch Stop(string code, string why) => new(Ordered(rates), fetchedThrough, code, why);
    }

    /// <summary>[from, to] cut at each calendar year's end.</summary>
    internal static IEnumerable<(DateOnly From, DateOnly To)> YearChunks(DateOnly from, DateOnly to)
    {
        var start = from;
        while (start <= to)
        {
            var yearEnd = new DateOnly(start.Year, 12, 31);
            var end = yearEnd < to ? yearEnd : to;
            yield return (start, end);
            start = end.AddDays(1);
        }
    }

    /// <summary>
    /// Read one year's response into <paramref name="sink"/>: rows for <paramref name="baseCurrency"/>
    /// into a currency in <paramref name="quotes"/>, dated inside [<paramref name="from"/>,
    /// <paramref name="to"/>]. On failure nothing from this response is added, and the code and
    /// message say why.
    /// </summary>
    internal static (string? Code, string? Message) Parse(
        JsonElement root,
        string baseCurrency,
        IReadOnlySet<string> quotes,
        DateOnly from,
        DateOnly to,
        List<FxRate> sink)
    {
        var read = new List<FxRate>();
        var seen = sink.Select(r => (r.Day, r.ToCurrency)).ToHashSet();
        string? rootBase = null;
        string? rootSource = null;
        JsonElement rows;

        if (root.ValueKind == JsonValueKind.Array)
        {
            rows = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rates", out var rates))
        {
            rootBase = ReadString(root, "base");
            rootSource = ReadSource(root);
            if (rates.ValueKind == JsonValueKind.Object)
                return ReadMap(rates, rootBase ?? baseCurrency, rootSource);
            if (rates.ValueKind != JsonValueKind.Array)
                return (ParseFailedCode, "The exchange-rate response's rates are neither a list nor a map.");
            rows = rates;
        }
        else
        {
            return (ParseFailedCode, "The exchange-rate response is not a shape Coffer reads.");
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                return (ParseFailedCode, "An exchange-rate row is not an object.");
            var rowBase = ReadString(row, "base") ?? rootBase ?? baseCurrency;
            var quote = ReadString(row, "quote");
            if (quote is null || ReadDay(row, "date") is not { } day || ReadRate(row, "rate") is not { } rate)
                return (ParseFailedCode, "An exchange-rate row has no readable date, quote or positive rate.");
            var failure = Add(day, rowBase, quote, rate, ReadSource(row) ?? rootSource);
            if (failure is not null) return failure.Value;
        }
        sink.AddRange(read);
        return (null, null);

        (string?, string?) ReadMap(JsonElement map, string mapBase, string? source)
        {
            foreach (var dated in map.EnumerateObject())
            {
                if (ParseDay(dated.Name) is not { } day || dated.Value.ValueKind != JsonValueKind.Object)
                    return (ParseFailedCode, "An exchange-rate map entry has no readable date.");
                foreach (var pair in dated.Value.EnumerateObject())
                {
                    if (PositiveDecimal(pair.Value) is not { } rate)
                        return (ParseFailedCode, $"The {pair.Name} rate for {dated.Name} is not a positive number.");
                    var failure = Add(day, mapBase, pair.Name, rate, source);
                    if (failure is not null) return failure.Value;
                }
            }
            sink.AddRange(read);
            return (null, null);
        }

        (string?, string?)? Add(DateOnly day, string rowBase, string quote, decimal rate, string? source)
        {
            // A rate from another base answers a question nobody asked; skipping it would let
            // the fetch report a year covered that it never read, so it fails the year.
            if (!string.Equals(rowBase, baseCurrency, StringComparison.Ordinal))
                return (ParseFailedCode, $"The exchange-rate service answered for base {rowBase}, not {baseCurrency}.");
            // A currency not asked for, or a day outside the year asked for: not this request's.
            if (!quotes.Contains(quote)) return null;
            if (day < from || day > to) return null;
            if (!seen.Add((day, quote))) return null;
            read.Add(new FxRate(day, baseCurrency, quote, decimal.Round(rate, RateScale), source));
            return null;
        }
    }

    private static List<FxRate> Ordered(List<FxRate> rates) =>
        [.. rates.OrderBy(r => r.Day).ThenBy(r => r.ToCurrency, StringComparer.Ordinal)];

    private static void RequireStoredCode(string code, string parameter)
    {
        if (!CurrencyCodes.TryNormalize(code, out var normal) || !string.Equals(normal, code, StringComparison.Ordinal))
            throw new ArgumentException($"'{code}' is not a currency code in stored form.", parameter);
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    /// <summary>The upstream source a row or response names: <c>provider</c>, else <c>source</c>,
    /// as a string or a list of strings.</summary>
    private static string? ReadSource(JsonElement el)
    {
        foreach (var name in (ReadOnlySpan<string>)["provider", "source", "providers"])
        {
            if (!el.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString()!.Trim();
            if (v.ValueKind == JsonValueKind.Array)
            {
                var names = v.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    .Select(x => x.GetString()!.Trim())
                    .ToList();
                if (names.Count > 0) return string.Join(",", names);
            }
        }
        return null;
    }

    private static DateOnly? ReadDay(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? ParseDay(v.GetString()!) : null;

    /// <summary>A <c>yyyy-MM-dd</c> day, or the day part of an ISO timestamp.</summary>
    private static DateOnly? ParseDay(string text)
    {
        var s = text.Length > 10 && text[10] == 'T' ? text[..10] : text;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static decimal? ReadRate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? PositiveDecimal(v) : null;

    private static decimal? PositiveDecimal(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d > 0m ? d : null;
}

/// <summary>One day's rate: one unit of <paramref name="FromCurrency"/> in
/// <paramref name="ToCurrency"/>, rounded to <see cref="FrankfurterRatesClient.RateScale"/>
/// places, with the upstream source the response named for it, if any.</summary>
public sealed record FxRate(DateOnly Day, string FromCurrency, string ToCurrency, decimal Rate, string? UpstreamSource);

/// <summary>
/// The answer to <see cref="FrankfurterRatesClient.FetchRatesAsync"/>: the rates read, oldest
/// first; the last day the fetch read through (null when it read nothing); and, when it
/// stopped early, the code and message for why. Rates before a failure are kept, and
/// <see cref="FetchedThrough"/> stops before the year that failed.
/// </summary>
public sealed record FxRatesFetch(
    IReadOnlyList<FxRate> Rates,
    DateOnly? FetchedThrough,
    string? ErrorCode,
    string? Message);
