using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to <c>security_splits</c>. Stock-split events
/// imported from Moneydance <c>csplit</c> objects upsert per-row keyed on
/// <c>(ledger_id, external_id)</c> so re-imports are idempotent.
/// </summary>
public sealed class SecuritySplitsRepository
{
    private readonly NpgsqlConnection _connection;

    public SecuritySplitsRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Bulk-upsert split events. The unique index
    /// <c>uq_security_splits_external_id_per_ledger</c> keys idempotency;
    /// rows without an external_id (manual entries) always insert.
    /// </summary>
    public async Task<int> BulkUpsertAsync(
        IReadOnlyList<SecuritySplitRow> rows,
        int chunkSize = 1000,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) return 0;

        const string sql = """
            INSERT INTO security_splits
                (id, ledger_id, security_id, split_at, ratio, old_shares, new_shares, external_id)
            SELECT * FROM unnest(
                @Ids::uuid[],
                @LedgerIds::uuid[],
                @SecurityIds::uuid[],
                @SplitAts::timestamptz[],
                @Ratios::numeric[],
                @OldShares::numeric[],
                @NewShares::numeric[],
                @ExternalIds::text[]
            )
            ON CONFLICT (ledger_id, external_id) WHERE external_id IS NOT NULL
            DO UPDATE SET
                security_id = EXCLUDED.security_id,
                split_at    = EXCLUDED.split_at,
                ratio       = EXCLUDED.ratio,
                old_shares  = EXCLUDED.old_shares,
                new_shares  = EXCLUDED.new_shares;
            """;

        var written = 0;
        for (var offset = 0; offset < rows.Count; offset += chunkSize)
        {
            var chunkLength = Math.Min(chunkSize, rows.Count - offset);
            var parameters = BuildUnnestParameters(rows, offset, chunkLength);
            var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
            written += await _connection.ExecuteAsync(command).ConfigureAwait(false);
        }
        return written;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM security_splits;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    private static object BuildUnnestParameters(IReadOnlyList<SecuritySplitRow> rows, int offset, int length)
    {
        var ids         = new Guid[length];
        var ledgerIds   = new Guid[length];
        var securityIds = new Guid[length];
        var splitAts    = new DateTime[length];
        var ratios      = new decimal[length];
        var oldShares   = new decimal?[length];
        var newShares   = new decimal?[length];
        var externalIds = new string?[length];

        for (var i = 0; i < length; i++)
        {
            var row = rows[offset + i];
            ids[i]         = row.Id;
            ledgerIds[i]   = row.LedgerId;
            securityIds[i] = row.SecurityId;
            splitAts[i]    = row.SplitAt.UtcDateTime;
            ratios[i]      = row.Ratio;
            oldShares[i]   = row.OldShares;
            newShares[i]   = row.NewShares;
            externalIds[i] = row.ExternalId;
        }

        return new
        {
            Ids         = ids,
            LedgerIds   = ledgerIds,
            SecurityIds = securityIds,
            SplitAts    = splitAts,
            Ratios      = ratios,
            OldShares   = oldShares,
            NewShares   = newShares,
            ExternalIds = externalIds,
        };
    }
}
