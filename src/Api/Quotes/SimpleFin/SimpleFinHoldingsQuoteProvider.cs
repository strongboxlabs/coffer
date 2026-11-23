using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Quotes.SimpleFin;

/// <summary>
/// First concrete quote provider (ADR-0033). Extracts per-position
/// prices from SimpleFIN's <c>holdings[]</c> block on every
/// brokerage account the orchestrator captured during the ingest
/// sync — no external HTTP, no rate limits.
/// </summary>
/// <remarks>
/// <para>Data source: <c>feed_connection_accounts.last_provider_raw_payload</c>
/// (migration 080). The SimpleFIN ingest orchestrator stores the
/// verbatim per-account JSON on every successful sync; that JSON
/// includes a <c>holdings[]</c> array whose entries pair a
/// <c>symbol</c> (ticker) with <c>shares</c>, <c>market_value</c>,
/// and <c>purchase_price</c>. We compute price as
/// <c>market_value / shares</c> (the SimpleFIN-reported per-share
/// value as of the account's <c>balance_date_unix</c>).</para>
///
/// <para>Pull only (no push). Returns one
/// <see cref="QuoteEntry"/> per (security, account) pair the
/// matcher resolves; multiple brokerages holding the same
/// security produce duplicate entries that the orchestrator
/// dedupes by <c>(security, price_date)</c>.</para>
///
/// <para>Ticker resolution: case-insensitive ticker match on
/// <c>securities.ticker</c> against <c>holdings[].symbol</c>.
/// A plan-internal portfolio number (a bare numeric code, such as an
/// education-savings plan reports in place of a ticker) matches the
/// security the user added with that code as its ticker.</para>
/// </remarks>
public sealed class SimpleFinHoldingsQuoteProvider : IQuotePullProvider
{
    public const string Key = "simplefin-holdings";

    /// <summary>Per-account error code: the feed account gave no currency.</summary>
    public const string CurrencyMissingCode = CurrencyCodes.MissingErrorCode;

    /// <summary>Per-account error code: the feed account's currency is not a currency code.</summary>
    public const string CurrencyUnknownCode = CurrencyCodes.UnknownErrorCode;

    /// <summary>The <see cref="CurrencyMissingCode"/> message. A feed account that names
    /// no currency errors only when it is mapped to no Coffer account, since a mapped one
    /// is priced in its Coffer account's currency, so the fix is to map it.</summary>
    public const string MissingUnmappedMessage =
        "SimpleFIN sent no currency for this account and it is not mapped to a Coffer account, "
        + "so its holdings were not priced. Map it in Settings → Bank feeds.";

    private readonly AppDbContext _db;
    private readonly ILogger<SimpleFinHoldingsQuoteProvider> _logger;
    private readonly TimeProvider _clock;

    public SimpleFinHoldingsQuoteProvider(
        AppDbContext db,
        ILogger<SimpleFinHoldingsQuoteProvider> logger,
        TimeProvider clock)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
    }

    public string ProviderKey => Key;
    public string DisplayName => "SimpleFIN holdings";
    // Reads stored sync payloads — no external egress, so always on.
    public bool RequiresOptIn => false;

    public async Task<QuoteResult> PullAsync(
        QuotePullContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Pull every SimpleFIN-bound feed-connection-account row
        // for this ledger that has a captured raw payload. One
        // row per (mapped + unmapped) account on every SimpleFIN
        // connection. The raw payload is the verbatim account
        // JSON SimpleFIN returned on the last sync.
        var raws = await _db.FeedConnectionAccounts.AsNoTracking()
            .Where(a => a.LedgerId == context.LedgerId
                        && a.LastProviderRawPayload != null)
            .Select(a => new { a.FeedConnectionId, a.ExternalId, a.LastProviderRawPayload })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (raws.Count == 0)
        {
            return new QuoteResult(
                Array.Empty<QuoteEntry>(),
                Array.Empty<QuoteError>());
        }

        // The currency of the Coffer account each feed account is mapped to, by the
        // binding the sync uses (accounts.feed_connection_id + external_id). A feed
        // account that reports no currency is priced in it (ADR-0114 D5, amendment).
        var mappedCurrency = (await _db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == context.LedgerId
                            && a.FeedConnectionId != null
                            && a.ExternalId != null)
                .Select(a => new { FeedConnectionId = a.FeedConnectionId!.Value, ExternalId = a.ExternalId!, a.CurrencyCode })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(a => (a.FeedConnectionId, a.ExternalId))
            .ToDictionary(g => g.Key, g => g.First().CurrencyCode);

        // Build a case-insensitive ticker → security map from the
        // orchestrator's pre-loaded security set. The orchestrator
        // already narrowed to securities with non-null tickers.
        var tickerToSecurity = context.Securities
            .GroupBy(s => s.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                StringComparer.OrdinalIgnoreCase);

        var quotes = new List<QuoteEntry>();
        var errors = new List<QuoteError>();
        var seen = new HashSet<(Guid securityId, DateTime asOf)>();

        foreach (var raw in raws)
        {
            ParseAccount(
                raw.LastProviderRawPayload!,
                raw.ExternalId,
                mappedCurrency.GetValueOrDefault((raw.FeedConnectionId, raw.ExternalId)),
                tickerToSecurity,
                quotes,
                errors,
                seen);
        }

        return new QuoteResult(quotes, errors);
    }

    /// <summary>
    /// Walk one account's raw JSON, extract every resolvable
    /// holding's price, append to the running quote / error
    /// lists. Tolerates malformed JSON (skips the account with a
    /// typed error rather than throwing).
    /// </summary>
    /// <param name="mappedCurrency">The currency of the Coffer account this feed account
    /// is mapped to; null when it is mapped to none.</param>
    private void ParseAccount(
        string rawJson,
        string externalId,
        string? mappedCurrency,
        IReadOnlyDictionary<string, QuoteSecurityRef> tickerToSecurity,
        List<QuoteEntry> quotes,
        List<QuoteError> errors,
        HashSet<(Guid, DateTime)> seen)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "SimpleFIN raw payload failed to parse: account={ExternalId}",
                externalId);
            errors.Add(new QuoteError(
                SecurityId: null,
                Ticker: string.Empty,
                Code: "raw-payload-parse",
                Message: ex.Message,
                FeedAccountId: externalId));
            return;
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("holdings", out var holdings))
                return;
            if (holdings.ValueKind != JsonValueKind.Array)
                return;

            // Account-level "as of" timestamp — when SimpleFIN
            // says this balance was accurate. Falls back to "now"
            // if absent (degraded; lets the provider still write
            // a quote with today's date).
            var asOf = ReadUnixSeconds(doc.RootElement, "balance-date")
                ?? ReadUnixSeconds(doc.RootElement, "balance_date")
                ?? _clock.GetUtcNow().UtcDateTime;

            // A holding's market_value is in its account's currency (ADR-0114 D5): the
            // `currency` the feed reports on the account the holdings sit in. Read once
            // per account. When the feed names none, the Coffer account the feed account
            // is mapped to says what currency it holds, as it already does for the
            // account's transactions, which a sync does not judge when the feed names no
            // currency (ADR-0114 D13). A feed account with no currency that is mapped to no
            // Coffer account, or one whose currency is not a currency code (SimpleFIN
            // names a custom currency by URL), fails its holdings closed: none of them is
            // stored, and the account gets one error. Checked only when a holding matched,
            // so a cash account with no positions stays silent.
            var rawCurrency = ReadString(doc.RootElement, "currency");
            var missing = string.IsNullOrWhiteSpace(rawCurrency);
            var currencyOk = CurrencyCodes.TryNormalize(missing ? mappedCurrency : rawCurrency, out var currency);
            var currencyErrorReported = false;

            foreach (var h in holdings.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object) continue;

                var symbol = ReadString(h, "symbol");
                if (string.IsNullOrWhiteSpace(symbol)) continue;
                if (!tickerToSecurity.TryGetValue(symbol, out var security)) continue;

                var shares = ReadDecimalLike(h, "shares");
                var marketValue = ReadDecimalLike(h, "market_value");
                if (shares is null || marketValue is null) continue;
                if (shares <= 0m)
                {
                    // SimpleFIN's market_value / shares yields no
                    // useful price when shares is zero — provider
                    // abstains. Not an error per se; the position
                    // is just zero.
                    continue;
                }

                var price = marketValue.Value / shares.Value;
                if (price < 0m) continue;

                if (!currencyOk)
                {
                    if (!currencyErrorReported)
                    {
                        currencyErrorReported = true;
                        // The account is named by FeedAccountId: the Activity log shows
                        // its name when the run is opened, so the message does not
                        // repeat it.
                        errors.Add(new QuoteError(
                            SecurityId: null,
                            Ticker: string.Empty,
                            Code: missing ? CurrencyMissingCode : CurrencyUnknownCode,
                            Message: missing
                                ? MissingUnmappedMessage
                                : $"SimpleFIN named currency '{rawCurrency!.Trim()}', which is not a currency code, so its holdings were not priced.",
                            FeedAccountId: externalId));
                    }
                    continue;
                }

                if (!seen.Add((security.SecurityId, asOf))) continue;

                quotes.Add(new QuoteEntry(
                    SecurityId: security.SecurityId,
                    // Round to 4dp (ADR-0070 D8): security_prices.price is
                    // NUMERIC(19,4) (migration 155), so the DB enforces 4dp anyway
                    // — rounding here matches the importer (PriceSnapshotMapper) and
                    // keeps the value clean at the source. (market_value / shares
                    // can carry sub-cent precision a valuation doesn't need.)
                    Price: decimal.Round(price, 4),
                    PriceAsOfUtc: asOf,
                    CurrencyCode: currency,
                    Source: PriceSource.Simplefin));
            }
        }
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>SimpleFIN sends numbers as strings; tolerate
    /// either shape.</summary>
    private static decimal? ReadDecimalLike(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            return decimal.TryParse(
                v.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d)
                ? d
                : null;
        }
        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.TryGetDecimal(out var d) ? d : null;
        }
        return null;
    }

    private static DateTime? ReadUnixSeconds(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        long? unix = v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : null,
            JsonValueKind.String => long.TryParse(v.GetString(), out var n) ? n : null,
            _ => null,
        };
        return unix is null ? null : DateTimeOffset.FromUnixTimeSeconds(unix.Value).UtcDateTime;
    }
}
