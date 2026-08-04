using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Bulk-write surface for <c>holdings</c> and <c>lots</c>. Per-transaction
/// security details (security_id, quantity, unit_price, commission) live
/// on <c>txn_legs</c> directly (the holdings-side leg of each posting,
/// per ADR-0019 / ADR-0022); <c>inv_txn_securities</c> was dropped in
/// migration 011.
/// </summary>
public sealed class InvestmentRepository
{
    private readonly NpgsqlConnection _connection;

    public InvestmentRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Bulk-upsert holdings keyed by <c>(account_id, security_id)</c>. On
    /// conflict the data fields refresh and the existing id is preserved.
    /// Returns a map from <c>(account_id, security_id)</c> to the persisted
    /// holding id, used by the lots writer to wire FKs.
    /// </summary>
    public async Task<IReadOnlyDictionary<(Guid AccountId, Guid SecurityId), Guid>> BulkUpsertHoldingsAsync(
        IReadOnlyList<HoldingRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return new Dictionary<(Guid, Guid), Guid>();

        const string sql = """
            INSERT INTO holdings (id, ledger_id, account_id, security_id, quantity, cost_basis, as_of)
            SELECT * FROM unnest(
                @Ids::uuid[],
                @LedgerIds::uuid[],
                @AccountIds::uuid[],
                @SecurityIds::uuid[],
                @Quantities::numeric[],
                @CostBases::numeric[],
                @AsOfs::timestamptz[]
            )
            ON CONFLICT (account_id, security_id) DO UPDATE SET
                quantity   = EXCLUDED.quantity,
                cost_basis = EXCLUDED.cost_basis,
                as_of      = EXCLUDED.as_of
            RETURNING id, account_id, security_id;
            """;

        var ids         = new Guid[rows.Count];
        var ledgerIds   = new Guid[rows.Count];
        var accountIds  = new Guid[rows.Count];
        var securityIds = new Guid[rows.Count];
        var quantities  = new decimal[rows.Count];
        var costBases   = new decimal[rows.Count];
        var asOfs       = new DateTime[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ids[i]         = row.Id;
            ledgerIds[i]   = row.LedgerId;
            accountIds[i]  = row.AccountId;
            securityIds[i] = row.SecurityId;
            quantities[i]  = row.Quantity;
            costBases[i]   = row.CostBasis;
            asOfs[i]       = row.AsOf.UtcDateTime;
        }

        var command = new CommandDefinition(sql, new
        {
            Ids         = ids,
            LedgerIds   = ledgerIds,
            AccountIds  = accountIds,
            SecurityIds = securityIds,
            Quantities  = quantities,
            CostBases   = costBases,
            AsOfs       = asOfs,
        }, cancellationToken: cancellationToken);

        var result = new Dictionary<(Guid, Guid), Guid>(rows.Count);
        var returned = await _connection.QueryAsync<(Guid Id, Guid AccountId, Guid SecurityId)>(command)
                                        .ConfigureAwait(false);
        foreach (var row in returned)
            result[(row.AccountId, row.SecurityId)] = row.Id;
        return result;
    }

    /// <summary>
    /// Replace every <c>lots</c> row owned by the supplied legs, then
    /// bulk-insert the new set. Lots are owned by their opening leg
    /// (the holdings-side leg of a buy or dividend-reinvest); the
    /// replacement pattern keeps re-runs idempotent. ADR-0022 Phase 2
    /// retargeted the FK from <c>transaction_id</c> to <c>leg_id</c>.
    /// </summary>
    public async Task BulkReplaceLotsAsync(
        IReadOnlyCollection<Guid> legIdsToReset,
        IReadOnlyList<LotRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (legIdsToReset.Count > 0)
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM lots WHERE leg_id = ANY(@ids);",
                new { ids = legIdsToReset.ToArray() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (rows.Count == 0) return;

        const string sql = """
            INSERT INTO lots (id, ledger_id, holding_id, leg_id, quantity, unit_cost, acquired_at, is_closed)
            SELECT * FROM unnest(
                @Ids::uuid[],
                @LedgerIds::uuid[],
                @HoldingIds::uuid[],
                @LegIds::uuid[],
                @Quantities::numeric[],
                @UnitCosts::numeric[],
                @AcquiredAts::timestamptz[],
                @IsCloseds::bool[]
            );
            """;

        var ids         = new Guid[rows.Count];
        var ledgerIds   = new Guid[rows.Count];
        var holdingIds  = new Guid[rows.Count];
        var legIds      = new Guid[rows.Count];
        var quantities  = new decimal[rows.Count];
        var unitCosts   = new decimal[rows.Count];
        var acquiredAts = new DateTime[rows.Count];
        var isCloseds   = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ids[i]         = row.Id;
            ledgerIds[i]   = row.LedgerId;
            holdingIds[i]  = row.HoldingId;
            legIds[i]      = row.LegId;
            quantities[i]  = row.Quantity;
            unitCosts[i]   = row.UnitCost;
            acquiredAts[i] = row.AcquiredAt.UtcDateTime;
            isCloseds[i]   = row.IsClosed;
        }

        await _connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Ids         = ids,
            LedgerIds   = ledgerIds,
            HoldingIds  = holdingIds,
            LegIds      = legIds,
            Quantities  = quantities,
            UnitCosts   = unitCosts,
            AcquiredAts = acquiredAts,
            IsCloseds   = isCloseds,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountHoldingsAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM holdings;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Recompute every <c>holdings.cost_basis</c> in the ledger using the
    /// average-cost method (migration 052). The HoldingDelta aggregation
    /// in <see cref="InvestmentTransactionImportStep"/> only ADDS basis
    /// on Buy / DivReinvest; it leaves Sells alone per ADR-0018 rule 4,
    /// which means a user who has sold anything ends up with a cost basis
    /// equal to lifetime gross acquisition cost, not the basis of
    /// currently-held shares. This finaliser corrects that by walking
    /// every (account, security)'s holdings-side legs in posted_at order
    /// and applying avg-cost basis reduction on each disposition.
    /// </summary>
    public async Task<int> RecomputeCostBasisAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition(
            "SELECT recompute_holdings_cost_basis(@LedgerId);",
            new { LedgerId = ledgerId },
            cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    public async Task<int> CountLotsAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM lots;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }
}
