using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Post-import step that re-derives <c>txn_header_account_balances</c>
/// for every account in the freshly-imported ledger. Mirrors the
/// ADR-0034 / ADR-0032 contract: every writer that mutates
/// <c>txn_legs</c> / <c>txn_headers</c> / overrides explicitly invokes
/// the recompute. The API path runs this via
/// <c>BalanceRecomputeInterceptor</c> (an EF SaveChangesInterceptor);
/// this importer uses Dapper / raw SQL, so the interceptor doesn't
/// see its writes and the recompute is called explicitly here.
/// </summary>
/// <remarks>
/// <para>Approach: iterate every account that has at least one leg
/// in this ledger and call <c>fn_recompute_balances_for_account</c>
/// from the dawn of time. Each call wipes the account's window and
/// re-inserts fresh balance rows in canonical
/// <c>(posted_at, seq)</c> order.</para>
///
/// <para>One-shot, N+1 by design — the importer typically touches a
/// few hundred accounts; per-account round-trip cost is dominated
/// by the recompute itself (a CTE + UPSERT). Wrapping into a single
/// DO block would shave maybe ~5% off the wall clock and lose the
/// per-account progress feedback.</para>
/// </remarks>
internal sealed class BalanceRecomputeStep
{
    private readonly NpgsqlConnection _connection;

    public BalanceRecomputeStep(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    public async Task<ImportStepResult> ExecuteAsync(
        ImportContext context,
        CancellationToken cancellationToken)
    {
        // Distinct accounts in this ledger that have at least one leg.
        // Categories, holdings siblings, etc. are included — the
        // recompute is a no-op on accounts with no balance-affecting
        // headers, so over-iteration is safe.
        var accountIds = (await _connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                SELECT DISTINCT l.account_id
                  FROM txn_legs l
                  JOIN txn_headers h ON h.id = l.header_id
                 WHERE h.ledger_id = @ledgerId;
                """,
                new { ledgerId = context.LedgerId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        foreach (var accountId in accountIds)
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    "SELECT fn_recompute_balances_for_account(@accountId, '0001-01-01'::timestamptz);",
                    new { accountId },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        return new ImportStepResult(
            StepName: "balance-recompute",
            Read: accountIds.Count,
            Written: accountIds.Count,
            Skipped: 0);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        CancellationToken cancellationToken)
    {
        var step = new BalanceRecomputeStep(connection);
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
