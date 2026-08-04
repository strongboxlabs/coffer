using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to <c>security_prices</c>. The Moneydance importer
/// produces one row per <c>csnap</c> item that resolves to a known security;
/// the export's 30k+ snapshots go through <see cref="BulkUpsertAsync"/> in a
/// single multi-row INSERT per chunk.
/// </summary>
public sealed class SecurityPricesRepository
{
    private readonly NpgsqlConnection _connection;

    public SecurityPricesRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Bulk-upsert price snapshots keyed by <c>(security_id, price_date)</c>.
    /// On conflict the data fields refresh and the existing id is preserved.
    /// </summary>
    public async Task<int> BulkUpsertAsync(
        IReadOnlyList<SecurityPriceRow> rows,
        int chunkSize = 5000,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) return 0;

        const string sql = """
            INSERT INTO security_prices (id, ledger_id, security_id, price, currency_code, price_date,
                                         high, low, volume, source)
            SELECT *, 'import' FROM unnest(
                @Ids::uuid[],
                @LedgerIds::uuid[],
                @SecurityIds::uuid[],
                @Prices::numeric[],
                @CurrencyCodes::text[],
                @PriceDates::date[],
                @Highs::numeric[],
                @Lows::numeric[],
                @Volumes::bigint[]
            )
            -- ADR-0070: import is the rank floor. On a (security, day) conflict
            -- it refreshes ONLY an existing 'import' row; a live 'fetch' /
            -- 'simplefin' fetch or a hand-entered 'manual' price for that day is
            -- left untouched (source is never changed on conflict).
            ON CONFLICT (security_id, price_date) DO UPDATE SET
                price         = EXCLUDED.price,
                currency_code = EXCLUDED.currency_code,
                high          = EXCLUDED.high,
                low           = EXCLUDED.low,
                volume        = EXCLUDED.volume
            WHERE security_prices.source = 'import';
            """;

        // ADR-0070: price_date is now a calendar DATE, so multiple same-day
        // csnaps for one security collapse to one key. Dedup to one row per
        // (security, UTC day) — keeping the latest snapshot — before the
        // multi-row INSERT, or a chunk holding two same-day rows would trip
        // "ON CONFLICT cannot affect row a second time".
        var deduped = rows
            .GroupBy(r => (r.SecurityId, Day: r.PriceDate.UtcDateTime.Date))
            .Select(g => g.OrderBy(r => r.PriceDate).Last())
            .ToList();

        var written = 0;
        for (var offset = 0; offset < deduped.Count; offset += chunkSize)
        {
            var chunkLength = Math.Min(chunkSize, deduped.Count - offset);
            var parameters = BuildUnnestParameters(deduped, offset, chunkLength);
            var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
            written += await _connection.ExecuteAsync(command).ConfigureAwait(false);
        }
        return written;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM security_prices;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    private static object BuildUnnestParameters(IReadOnlyList<SecurityPriceRow> rows, int offset, int length)
    {
        var ids           = new Guid[length];
        var ledgerIds     = new Guid[length];
        var securityIds   = new Guid[length];
        var prices        = new decimal[length];
        var currencyCodes = new string[length];
        var priceDates    = new DateOnly[length];
        var highs         = new decimal?[length];
        var lows          = new decimal?[length];
        var volumes       = new long?[length];

        for (var i = 0; i < length; i++)
        {
            var row = rows[offset + i];
            ids[i]           = row.Id;
            ledgerIds[i]     = row.LedgerId;
            securityIds[i]   = row.SecurityId;
            prices[i]        = row.Price;
            currencyCodes[i] = row.CurrencyCode;
            priceDates[i]    = DateOnly.FromDateTime(row.PriceDate.UtcDateTime);
            highs[i]         = row.High;
            lows[i]          = row.Low;
            volumes[i]       = row.Volume;
        }

        return new
        {
            Ids           = ids,
            LedgerIds     = ledgerIds,
            SecurityIds   = securityIds,
            Prices        = prices,
            CurrencyCodes = currencyCodes,
            PriceDates    = priceDates,
            Highs         = highs,
            Lows          = lows,
            Volumes       = volumes,
        };
    }
}
