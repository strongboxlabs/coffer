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
/// <para>The price is stamped with the currency the response names,
/// <c>meta.currency</c>, read by <see cref="YahooCurrency"/> (ADR-0114 D5): a minor
/// unit is divided into its major one, and a missing or unrecognised currency fails
/// that symbol closed with its own error code. Nothing the orchestrator asked for
/// says what currency a symbol trades in, so the response is the only source.</para>
///
/// <para>GETs are sequential per symbol — Yahoo rate-limits bursts, and a
/// 404 / 429 / 5xx on one symbol becomes a per-symbol error, not a run
/// failure. Bounded concurrency is a future optimization if book sizes
/// warrant.</para>
/// </remarks>
public sealed class YahooFinanceQuoteProvider : IQuotePullProvider, IQuoteCurrencyLookup
{
    public const string Key = "yahoo";

    private readonly HttpClient _http;
    private readonly ILogger<YahooFinanceQuoteProvider> _logger;
    private readonly YahooQuoteOptions _options;
    private readonly TimeProvider _clock;

    public YahooFinanceQuoteProvider(
        HttpClient http,
        IOptions<YahooQuoteOptions> options,
        ILogger<YahooFinanceQuoteProvider> logger,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _logger = logger;
        _options = options.Value;
        _clock = clock;
    }

    public string ProviderKey => Key;
    public string DisplayName => "Yahoo Finance";
    // External HTTP egress — opt-in per ledger via the `quotes` pref (ADR-0057).
    public bool RequiresOptIn => true;
    // The chart host every Yahoo caller is configured with (YahooChart.Configure).
    public string? DestinationHost => YahooChart.BaseAddress.Host;

    public async Task<QuoteResult> PullAsync(
        QuotePullContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (quotes, errors) = await ForEachSymbolAsync(context.Securities, FetchOneAsync, cancellationToken)
            .ConfigureAwait(false);
        return new QuoteResult(quotes, errors);
    }

    /// <summary>
    /// The currency-only lookup (ADR-0114 D5, amendment): one chart request per symbol,
    /// the same request, throttle and error codes as <see cref="PullAsync"/>, reading only
    /// <c>meta.currency</c> through <see cref="YahooCurrency"/>. No price is read or
    /// returned, so nothing is stored but the currency.
    /// </summary>
    public async Task<QuoteCurrencyLookupResult> LookupCurrenciesAsync(
        IReadOnlyList<QuoteSecurityRef> securities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(securities);
        var (currencies, errors) = await ForEachSymbolAsync(securities, LookupOneAsync, cancellationToken)
            .ConfigureAwait(false);
        return new QuoteCurrencyLookupResult(currencies, errors);
    }

    /// <summary>
    /// One request per security, in order, under one throttle (ADR-0054): a fixed gap
    /// between requests, and after a 429 the capped backoff before the next. A failure is
    /// that security's error, never the run's.
    /// </summary>
    private async Task<(List<T> Found, List<QuoteError> Errors)> ForEachSymbolAsync<T>(
        IReadOnlyList<QuoteSecurityRef> securities,
        Func<QuoteSecurityRef, CancellationToken, Task<(T? Value, QuoteError? Error)>> fetchOne,
        CancellationToken cancellationToken)
        where T : class
    {
        var quotes = new List<T>();
        var errors = new List<QuoteError>();
        // Throttle (ADR-0054): a fixed gap between sequential requests
        // keeps the burst rate polite to Yahoo's unofficial endpoint.
        var gate = new YahooRequestGate(_options, _clock);

        foreach (var sec in securities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await gate.WaitTurnAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var fetched = await fetchOne(sec, cancellationToken)
                    .ConfigureAwait(false);
                if (fetched.Value is { } entry)
                    quotes.Add(entry);
                else
                    errors.Add(fetched.Error!);
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
                    sec.SecurityId, sec.Ticker, YahooErrorCodes.RateLimited,
                    $"{sec.Ticker}: Yahoo returned 429 (rate limited)."));
                await gate.BackOffAsync(ex.RetryAfter, cancellationToken).ConfigureAwait(false);
            }
            catch (YahooHttpException ex)
            {
                _logger.LogDebug(ex, "Yahoo fetch failed: {Ticker}", sec.Ticker);
                errors.Add(new QuoteError(
                    sec.SecurityId, sec.Ticker, YahooErrorCodes.FetchFailed,
                    $"{sec.Ticker}: {ex.Message}"));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Yahoo request failed: {Ticker}", sec.Ticker);
                errors.Add(new QuoteError(
                    sec.SecurityId, sec.Ticker, YahooErrorCodes.FetchFailed,
                    $"{sec.Ticker}: {ex.Message}"));
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Yahoo response parse failed: {Ticker}", sec.Ticker);
                errors.Add(new QuoteError(
                    sec.SecurityId, sec.Ticker, YahooErrorCodes.ParseFailed,
                    $"{sec.Ticker}: {ex.Message}"));
            }
        }

        return (quotes, errors);
    }

    private async Task<(QuoteCurrency? Currency, QuoteError? Error)> LookupOneAsync(
        QuoteSecurityRef sec, CancellationToken ct)
    {
        using var doc = await YahooChart.GetAsync(
                _http, YahooChart.Path(sec.Ticker, "interval=1d&range=5d"), ct)
            .ConfigureAwait(false);
        if (!YahooChart.TryGetResult(doc.RootElement, out var r0))
            return (null, NotResolved(sec));

        var metaCurrency = YahooChart.MetaString(r0, "currency");
        var currency = YahooCurrency.Resolve(metaCurrency);
        return currency.Resolved
            ? (new QuoteCurrency(sec.SecurityId, currency.CurrencyCode!, currency.Factor), null)
            : (null, CurrencyError(sec, currency, metaCurrency));
    }

    private static QuoteError CurrencyError(
        QuoteSecurityRef sec, YahooCurrencyResolution currency, string? metaCurrency) =>
        new(sec.SecurityId, sec.Ticker, currency.ErrorCode!,
            currency.ErrorCode == YahooCurrency.MissingCode
                ? $"{sec.Ticker}: Yahoo named no currency for this symbol."
                : $"{sec.Ticker}: Yahoo named currency '{metaCurrency}', which Coffer does not recognise.");

    private async Task<(QuoteEntry? Entry, QuoteError? Error)> FetchOneAsync(
        QuoteSecurityRef sec, CancellationToken ct)
    {
        // 5-day window @ 1d granularity → take the most recent complete close.
        using var doc = await YahooChart.GetAsync(
                _http, YahooChart.Path(sec.Ticker, "interval=1d&range=5d"), ct)
            .ConfigureAwait(false);

        if (!YahooChart.TryGetResult(doc.RootElement, out var r0)
            || !YahooChart.TryGetTimestamps(r0, out var timestamps)
            || !YahooChart.TryGetCloses(r0, out var closes))
        {
            return (null, NotResolved(sec));
        }

        // The currency the response names, before any price is read from it: a
        // symbol whose currency is missing or unrecognised stores nothing, so no
        // pence figure is ever stored as pounds and no other currency as USD.
        var metaCurrency = YahooChart.MetaString(r0, "currency");
        var currency = YahooCurrency.Resolve(metaCurrency);
        if (!currency.Resolved)
            return (null, CurrencyError(sec, currency, metaCurrency));

        // Walk newest → oldest; first usable close wins (a holiday/halt bar
        // can carry a null close).
        var n = Math.Min(timestamps.GetArrayLength(), closes.GetArrayLength());
        for (var i = n - 1; i >= 0; i--)
        {
            if (YahooChart.PositiveValue(closes[i]) is not { } quoted) continue;

            var asOfDate = YahooChart.BarDay(timestamps[i])
                ?? DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

            return (new QuoteEntry(
                sec.SecurityId,
                // Into the major unit first, then 4dp, matching security_prices.price
                // NUMERIC(19,4) — not 12. Divided before rounding so the stored value is
                // rounded once, at the destination scale.
                //
                // Rounding finer than the destination column rounds TWICE, and here the
                // second rounding happens where nobody can see it. The visible cost is
                // not a wrong price but change detection that never converges:
                // QuoteOrchestrator compares this value against the one read back from
                // the column, so a 12dp value never equals its own stored 4dp form, and
                // every refetch rewrites the row and counts an update that changed
                // nothing. Yahoo closes are float-derived (mig 155 cites a close from
                // this same `fetch` source carrying a float tail out to the twelfth
                // decimal), so the 5th decimal is normally
                // non-zero and this fired essentially every time.
                //
                // Mig 155 narrowed the column on the stated premise that "current
                // producers already round to 4dp" — true of the SimpleFIN provider and
                // the Moneydance importer, but this provider predates it and was never
                // revisited. Same shape as mig 205 vs realized_gains (see mig 209).
                decimal.Round(currency.ToMajor(quoted), 4),
                DateTime.SpecifyKind(asOfDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                currency.CurrencyCode!,
                PriceSource.Fetch,
                currency.Factor), null);
        }

        return (null, NotResolved(sec));
    }

    private static QuoteError NotResolved(QuoteSecurityRef sec) =>
        new(sec.SecurityId, sec.Ticker, YahooErrorCodes.TickerNotResolved,
            $"Yahoo returned no usable close for '{sec.Ticker}'.");
}
