using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>One header selected for pruning, with its twin classification.</summary>
internal sealed record PruneTarget(
    Guid Id, DateTime PostedAt, string? Payee, string Origin,
    bool IsRecurringTemplate, bool HasTwin);

/// <summary>A (holdings-account, security) pair whose holdings need re-deriving.</summary>
internal sealed record PruneHoldingPair(Guid AccountId, Guid SecurityId);

/// <summary>
/// The set of rows a prune would remove plus the recompute scope it implies.
/// Computed read-only by <see cref="PruneImportBatch.PlanAsync"/> so it can be
/// previewed (dry-run) before <see cref="PruneImportBatch.ApplyAsync"/> mutates.
/// </summary>
internal sealed record PrunePlan(
    IReadOnlyList<PruneTarget> Targets,
    IReadOnlyList<Guid> AffectedAccounts,
    IReadOnlyList<PruneHoldingPair> HoldingPairs)
{
    public int RegisterRowCount => Targets.Count(t => !t.IsRecurringTemplate);
    public int TemplateCount => Targets.Count(t => t.IsRecurringTemplate);
    public int TwinCount => Targets.Count(t => t.HasTwin && !t.IsRecurringTemplate);

    /// <summary>Register rows with no pre-batch counterpart — the data-loss risk.</summary>
    public IReadOnlyList<PruneTarget> NoTwin =>
        Targets.Where(t => !t.HasTwin && !t.IsRecurringTemplate).ToList();
}

/// <summary>
/// Read/apply logic for <c>prune-batch</c>, factored out of the CLI command so
/// it is testable against a real schema (recompute functions + FK cascades need
/// the migrated DB). See <see cref="PruneImportBatchCommand"/> for the why.
/// </summary>
internal static class PruneImportBatch
{
    /// <summary>
    /// Select the headers in the (ledger, import_source, created_at window)
    /// batch and classify each by whether a pre-batch row covers the same
    /// (account, posted-date, amount) — a "twin" a delete would fall back to.
    /// Also returns the accounts + (holdings-account, security) pairs the
    /// removal would touch, captured here because the legs cascade away with
    /// the headers on apply. Read-only.
    /// </summary>
    public static async Task<PrunePlan> PlanAsync(
        NpgsqlConnection connection,
        Guid ledgerId,
        string importSource,
        DateTimeOffset createdFrom,
        DateTimeOffset createdTo,
        bool includeTemplates,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var templateClause = includeTemplates ? "" : " AND NOT h.is_recurring_template";
        var targetSql = $"""
            SELECT h.id                    AS "Id",
                   h.posted_at             AS "PostedAt",
                   h.payee                 AS "Payee",
                   h.origin                AS "Origin",
                   h.is_recurring_template AS "IsRecurringTemplate",
                   EXISTS (
                       SELECT 1
                         FROM txn_legs tl
                         JOIN txn_legs pl
                           ON pl.account_id = tl.account_id AND pl.amount = tl.amount AND pl.amount <> 0
                         JOIN txn_headers ph ON ph.id = pl.header_id
                        WHERE tl.header_id = h.id
                          AND ph.ledger_id = @LedgerId
                          AND ph.id <> h.id
                          AND ph.created_at < @CreatedFrom
                          AND ph.posted_at::date = h.posted_at::date
                   )                       AS "HasTwin"
              FROM txn_headers h
             WHERE h.ledger_id = @LedgerId
               AND h.import_source = @ImportSource
               AND h.created_at >= @CreatedFrom
               AND h.created_at <  @CreatedTo
               {templateClause}
             ORDER BY h.posted_at, h.payee;
            """;

        var targets = (await connection.QueryAsync<PruneTarget>(new CommandDefinition(
            targetSql,
            new { LedgerId = ledgerId, ImportSource = importSource, CreatedFrom = createdFrom, CreatedTo = createdTo },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        if (targets.Count == 0)
            return new PrunePlan(targets, Array.Empty<Guid>(), Array.Empty<PruneHoldingPair>());

        var ids = targets.Select(t => t.Id).ToArray();

        var affectedAccounts = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT l.account_id FROM txn_legs l WHERE l.header_id = ANY(@Ids);",
            new { Ids = ids }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var holdingPairs = (await connection.QueryAsync<PruneHoldingPair>(new CommandDefinition("""
            SELECT DISTINCT l.account_id AS "AccountId", l.security_id AS "SecurityId"
              FROM txn_legs l
             WHERE l.header_id = ANY(@Ids) AND l.security_id IS NOT NULL;
            """,
            new { Ids = ids }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        return new PrunePlan(targets, affectedAccounts, holdingPairs);
    }

    /// <summary>
    /// Delete the planned headers (legs/lots/overrides/tags/balances cascade)
    /// and re-derive holdings + balances for the affected scope, all on the
    /// supplied transaction. Returns the number of headers deleted.
    /// </summary>
    public static async Task<int> ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ledgerId,
        PrunePlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Targets.Count == 0) return 0;
        var ids = plan.Targets.Select(t => t.Id).ToArray();

        // ledger_id in the predicate is a scope guard: a wrong id list can
        // never reach another ledger.
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM txn_headers WHERE ledger_id = @ledgerId AND id = ANY(@Ids);",
            new { ledgerId, Ids = ids },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Holdings + lots: re-derive per affected (holdings-account, security)
        // pair — the API's canonical post-mutation recompute (HoldingsRecomputeService).
        foreach (var pair in plan.HoldingPairs)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT recompute_holdings_for_account_security(@AccountId, @SecurityId);",
                new { pair.AccountId, pair.SecurityId },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // Balances: re-derive each affected account's window from the dawn of
        // time (matches BalanceRecomputeStep's per-account contract).
        foreach (var accountId in plan.AffectedAccounts)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT fn_recompute_balances_for_account(@AccountId, '0001-01-01'::timestamptz);",
                new { AccountId = accountId },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return deleted;
    }
}
