using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Post-import step that re-derives the denormalized posting counts
/// (<c>txn_legs.account_postings_on_header</c> /
/// <c>header_total_postings</c>, mig 120 / ADR-0046) for the
/// freshly-imported ledger. Same ADR-0032/0034 contract as
/// <see cref="BalanceRecomputeStep"/>: the API path maintains these via
/// <c>LegDerivedRecomputeInterceptor</c>, but this importer writes legs
/// with Dapper / raw SQL (and the multi-row <c>insert_*</c> helpers),
/// which the interceptor never sees — so the recompute is called
/// explicitly here.
/// </summary>
/// <remarks>
/// <para>Without this step every multi-posting header the importer
/// creates (paycheck splits, investment events) would keep the columns'
/// <c>DEFAULT 1</c>, so the originating-vs-target discriminator
/// (ADR-0036: <c>account_postings_on_header &lt; header_total_postings</c>)
/// would misfire and target rows would collapse. The mig-120 backfill
/// only corrected rows that existed at migration time; a fresh import
/// needs its own recompute.</para>
///
/// <para>One set-based UPDATE over the whole ledger — the counts are a
/// pure per-(header[, account]) aggregate, so a single pass (mirroring
/// the mig-120 backfill, scoped to this ledger) is both correct and far
/// cheaper than the per-header function call.</para>
/// </remarks>
internal sealed class PostingCountRecomputeStep
{
    private readonly NpgsqlConnection _connection;

    public PostingCountRecomputeStep(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    public async Task<ImportStepResult> ExecuteAsync(
        ImportContext context,
        CancellationToken cancellationToken)
    {
        var updated = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                WITH htp AS (
                    SELECT header_id, COUNT(DISTINCT posting_index) AS total
                      FROM txn_legs WHERE ledger_id = @ledgerId GROUP BY header_id
                ), aph AS (
                    SELECT header_id, account_id, COUNT(DISTINCT posting_index) AS cnt
                      FROM txn_legs WHERE ledger_id = @ledgerId GROUP BY header_id, account_id
                )
                UPDATE txn_legs l
                   SET header_total_postings      = htp.total,
                       account_postings_on_header = aph.cnt
                  FROM htp, aph
                 WHERE l.ledger_id = @ledgerId
                   AND htp.header_id = l.header_id
                   AND aph.header_id = l.header_id
                   AND aph.account_id = l.account_id;
                """,
                new { ledgerId = context.LedgerId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return new ImportStepResult(
            StepName: "posting-count-recompute",
            Read: updated,
            Written: updated,
            Skipped: 0);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        CancellationToken cancellationToken)
    {
        var step = new PostingCountRecomputeStep(connection);
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
