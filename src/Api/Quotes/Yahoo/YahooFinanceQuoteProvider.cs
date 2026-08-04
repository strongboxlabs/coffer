using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Quotes.Yahoo;

/// <summary>
/// Market-data quote provider (ADR-0054 D1). HTTP EOD-close fetch against
/// Yahoo Finance's public chart endpoint
/// (<c>query1.finance.yahoo.com/v8/finance/chart/{symbol}</c>). No API key.
/// </summary>
/// <remarks>
/// <para>Unofficial, best-effort source (ADR-0054 D1 caveat): Yahoo's chart
/// endpoint is undocumented, ToS-gray, and breaks periodically. The provider
/// degrades gracefully — a symbol it can't resolve becomes a typed
/// <see cref="QuoteError"/> and surfaces as "unresolved"; manual entry
/// remains. Egress is OPT-IN per ledger: the orchestrator runs this provider
/// only when the acting ledger's <c>quotes</c> preference enables it
/// (ADR-0057), so a default install makes no external calls.</para>
///
/// <para>Price = the last non-null daily CLOSE from
/// <c>indicators.quote[0].close[]</c>, stored UNADJUSTED (ADR-0054 D1 —
/// <c>adjclose</c> folds in splits/dividends, which Coffer models in
/// <c>security_splits</c> / lots). The price date is normalized to the bar's
/// UTC calendar date so daily closes dedupe cleanly and a same-day manual
/// price still wins under the source-aware upsert.</para>
///
/// <para>GETs are sequential per symbol — Yahoo rate-limits bursts, and a
/// 404 / 429 / 5xx on one symbol becomes a per-symbol error, not a run
/// failure. Bounded concurrency is a future optimization if book sizes
/// warrant.</para>
/// </remarks>
public sealed class YahooFinanceQuoteProvider : IQuotePullProvider
{
    public const string Key = "yahoo";

    private readonly HttpClient _http;
    private readonly ILogger<YahooFinanceQuoteProvider> _logger;
    private readonly TimeSpan _requestDelay;
    private readonly TimeSpan _maxBackoff;

    public YahooFinanceQuoteProvider(
        HttpClient http,
        IOptions<YahooQuoteOptions> options,
        ILogger<YahooFinanceQuoteProvider> logger)
    {
        _http = http;
        _logger = logger;
        var opts = options.Value;
        _requestDelay = TimeSpan.FromMilliseconds(Math.Max(0, opts.RequestDelayMs));
        _maxBackoff = TimeSpan.FromSeconds(Math.Max(0, opts.MaxBackoffSeconds));
    }

    public string ProviderKey => Key;
    public string DisplayName => "Yahoo Finance";
    // External HTTP egress — opt-in per ledger via the `quotes` pref (ADR-0057).
    public bool RequiresOptIn => true;

    public async Task<QuoteResult> PullAsync(
        QuotePullContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var quotes = new List<QuoteEntry>();
        var errors = new List<QuoteError>();
        var first = true;

        foreach (var sec in context.Securities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Throttle (ADR-0054): a fixed gap between sequential requests
            // keeps the burst rate polite to Yahoo's unofficial endpoint.
            if (!first && _requestDelay > TimeSpan.Zero)
            {
                await Task.Delay(_requestDelay, cancellationToken).ConfigureAwait(false);
            }
            first = false;

            try
            {
                var entry = await FetchOneAsync(sec, cancellationToken)
                    .ConfigureAwait(false);
                if (entry is not null)
                {
                    quotes.Add(entry);
                }
                else
                {
                    errors.Add(new QuoteError(
                        sec.SecurityId, sec.Ticker, "ticker-not-resolved",
                        $"Yahoo returned no usable close for '{sec.Ticker}'."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (YahooHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Rate limited: surface a distinct code and back off before the
                // next symbol so we don't keep hammering a limiter that's
                // already pushing back — honor Retry-After, capped.
                _logger.LogWarning(
                    "Yahoo rate-limited {Ticker}; backing off before next symbol.",
                    sec.Ticker);
                errors.Add(new QuoteError(
                    sec.SecurityId, sec.Ticker, "rate-limited",
                    $"{sec.Ticker}: Yahoo returned 429 (rate limited)."));
                var backoff = ex.RetryAfter is { } ra && ra < _maxBackoff ? ra : _maxBackoff;
                if (backoff > TimeSpan.Zero)
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (YahooHttpException ex)
            {
                _logger.LogDebug(ex, "Yahoo fetch failed: {Ticker}", sec.Ticker);
                errors.Add(new QuoteError(
                    sec.SecurityId, sec.Ticker, "fetch-failed",
                    $"{sec.Ticker}: {ex.Message}"));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Yahoo request failed: {Ticker}", sec.Ticker);
                errors.Add(new QuoteError(
                    sec.SecurityId, sec.Ticker, "fetch-failed",
                    $"{sec.Ticker}: {ex.Message}"));
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Yahoo response parse failed: {Ticker}", sec.Ticker);
                errors.Add(new QuoteError(
                    sec.SecurityId, sec.Ticker, "parse-failed",
                    $"{sec.Ticker}: {ex.Message}"));
            }
        }

        return new QuoteResult(quotes, errors);
    }

    private async Task<QuoteEntry?> FetchOneAsync(
        QuoteSecurityRef sec, CancellationToken ct)
    {
        // 5-day window @ 1d granularity → take the most recent complete close.
        var url = $"/v8/finance/chart/{Uri.EscapeDataString(sec.Ticker)}"
                  + "?interval=1d&range=5d";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 404 unknown symbol; 429 rate-limited; 5xx upstream. Carry the
            // status (+ Retry-After on 429) so the pull loop can throttle and
            // tag the per-symbol error in QuoteResult.Errors.
            throw new YahooHttpException(
                resp.StatusCode,
                resp.Headers.RetryAfter?.Delta,
                $"HTTP {(int)resp.StatusCode} from Yahoo chart endpoint");
        }

        await using var stream = await resp.Content
            .ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument
            .ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!TryGetChartSeries(doc.RootElement, out var timestamps, out var closes))
        {
            return null;
        }

        // Walk newest → oldest; first usable close wins (a holiday/halt bar
        // can carry a null close).
        var n = Math.Min(timestamps.GetArrayLength(), closes.GetArrayLength());
        for (var i = n - 1; i >= 0; i--)
        {
            var c = closes[i];
            if (c.ValueKind != JsonValueKind.Number) continue;
            if (!c.TryGetDecimal(out var price)) continue;
            if (price <= 0m) continue;

            var asOfDate = timestamps[i].ValueKind == JsonValueKind.Number
                           && timestamps[i].TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Date
                : DateTime.UtcNow.Date;

            return new QuoteEntry(
                sec.SecurityId,
                decimal.Round(price, 12),
                DateTime.SpecifyKind(asOfDate, DateTimeKind.Utc),
                sec.CurrencyCode,
                PriceSource.Fetch);
        }

        return null;
    }

    /// <summary>
    /// Pull the <c>timestamp[]</c> + <c>indicators.quote[0].close[]</c> arrays
    /// out of a chart response. Returns false on any shape mismatch (Yahoo
    /// <c>chart.error</c>, empty result, missing series).
    /// </summary>
    private static bool TryGetChartSeries(
        JsonElement root, out JsonElement timestamps, out JsonElement closes)
    {
        timestamps = default;
        closes = default;

        if (!root.TryGetProperty("chart", out var chart)) return false;
        if (!chart.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
        {
            return false;
        }

        var r0 = result[0];
        if (!r0.TryGetProperty("timestamp", out timestamps)
            || timestamps.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        if (!r0.TryGetProperty("indicators", out var indicators)) return false;
        if (!indicators.TryGetProperty("quote", out var quote)
            || quote.ValueKind != JsonValueKind.Array
            || quote.GetArrayLength() == 0)
        {
            return false;
        }
        if (!quote[0].TryGetProperty("close", out closes)
            || closes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return true;
    }

    /// <summary>Non-success HTTP from the Yahoo chart endpoint, carrying the
    /// status + any <c>Retry-After</c> so the pull loop can throttle on 429.</summary>
    private sealed class YahooHttpException : Exception
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
}
