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
/// MD 529 portfolio numbers (<c>"8918"</c>, <c>"8920"</c>) match
/// the securities the user added with those tickers.</para>
/// </remarks>
public sealed class SimpleFinHoldingsQuoteProvider : IQuotePullProvider
{
    public const string Key = "simplefin-holdings";

    private readonly AppDbContext _db;
    private readonly ILogger<SimpleFinHoldingsQuoteProvider> _logger;

    public SimpleFinHoldingsQuoteProvider(
        AppDbContext db,
        ILogger<SimpleFinHoldingsQuoteProvider> logger)
    {
        _db = db;
        _logger = logger;
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
            .Select(a => new { a.ExternalId, a.LastProviderRawPayload })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (raws.Count == 0)
        {
            return new QuoteResult(
                Array.Empty<QuoteEntry>(),
                Array.Empty<QuoteError>());
        }

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
    private void ParseAccount(
        string rawJson,
        string externalId,
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
                Message: $"Account {externalId}: {ex.Message}"));
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
                ?? DateTime.UtcNow;

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
                    CurrencyCode: security.CurrencyCode,
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
