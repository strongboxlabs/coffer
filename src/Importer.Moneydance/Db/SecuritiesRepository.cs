using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to the <c>securities</c> table. Per ADR-0005, hot-
/// path / merge-evaluator code uses Dapper directly. The importer uses the
/// same library for consistency, plus because the upsert pattern below is
/// SQL-shaped enough that EF Core would obscure it.
/// </summary>
public sealed class SecuritiesRepository
{
    private readonly NpgsqlConnection _connection;

    public SecuritiesRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Insert or update a security keyed by <c>(ledger_id, external_id)</c>.
    /// Returns the persisted row's id (the existing one on conflict, the
    /// supplied id on insert).
    /// </summary>
    /// <remarks>
    /// On conflict we update the data fields but preserve the original
    /// <c>id</c>, <c>created_at</c>, <c>ledger_id</c>, and <c>external_id</c>.
    /// Callers that supply a freshly-generated <see cref="SecurityRow.Id"/>
    /// should use the returned id (not the one they passed) for FK references.
    /// Multi-ledger (ADR-0020 Phase A): the conflict key is per-ledger, so
    /// two ledgers can both import the same MD security UUID without
    /// colliding.
    /// </remarks>
    public async Task<Guid> UpsertByExternalIdAsync(SecurityRow row, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(row.ExternalId))
            throw new ArgumentException(
                "UpsertByExternalIdAsync requires a non-empty ExternalId on the row.", nameof(row));

        // Classification (asset_class, vehicle_type, source, confidence) is SEEDED
        // on insert but NOT refreshed on re-import — Coffer owns it once the user
        // curates it in the editor (import-once, ADR-0050 D10 / ADR-0067).
        const string sql = """
            INSERT INTO securities (id, ledger_id, ticker, cusip, name, asset_class, vehicle_type, classification_source, classification_confidence, exchange, is_active, external_id, share_decimals)
            VALUES (@Id, @LedgerId, @Ticker, @Cusip, @Name, @AssetClass, @VehicleType, @ClassificationSource, @ClassificationConfidence, @Exchange, @IsActive, @ExternalId, @ShareDecimals)
            ON CONFLICT (ledger_id, external_id) WHERE external_id IS NOT NULL
            DO UPDATE SET
                ticker         = EXCLUDED.ticker,
                cusip          = EXCLUDED.cusip,
                name           = EXCLUDED.name,
                exchange       = EXCLUDED.exchange,
                is_active      = EXCLUDED.is_active,
                share_decimals = EXCLUDED.share_decimals
            RETURNING id;
            """;
        var command = new CommandDefinition(sql, row, cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<Guid>(command).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM securities;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    public async Task<SecurityRow?> GetByExternalIdAsync(Guid ledgerId, string externalId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id                        AS Id,
                   ledger_id                 AS LedgerId,
                   ticker                    AS Ticker,
                   cusip                     AS Cusip,
                   name                      AS Name,
                   asset_class               AS AssetClass,
                   vehicle_type              AS VehicleType,
                   classification_source     AS ClassificationSource,
                   classification_confidence AS ClassificationConfidence,
                   exchange                  AS Exchange,
                   is_active                 AS IsActive,
                   external_id               AS ExternalId,
                   share_decimals            AS ShareDecimals
            FROM securities
            WHERE ledger_id = @ledgerId AND external_id = @externalId;
            """;
        var command = new CommandDefinition(sql, new { ledgerId, externalId }, cancellationToken: cancellationToken);
        return await _connection.QuerySingleOrDefaultAsync<SecurityRow>(command).ConfigureAwait(false);
    }
}
