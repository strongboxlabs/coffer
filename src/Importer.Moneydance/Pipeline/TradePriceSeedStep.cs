using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Post-import step that seeds <c>trade</c>-source rows into
/// <c>security_prices</c> from the freshly-imported ledger's investment TRADE
/// legs (ADR-0084). The execution price (<c>txn_legs.unit_price</c>) is a real
/// market observation and should feed the price history so a held-but-unfed
/// security isn't valued at 0 by the mig-172 as-of feeder.
/// </summary>
/// <remarks>
/// <para>The API write path runs this via the
/// <c>TradePriceFromLegInterceptor</c> (an EF SaveChangesInterceptor); this
/// importer uses Dapper / raw SQL, so the interceptor never sees its writes and
/// the seed is called explicitly here — the same call-site contract as
/// <see cref="BalanceRecomputeStep"/> and the holdings recompute (ADR-0032).</para>
///
/// <para>Runs AFTER <c>PriceSnapshotImportStep</c> so the MD <c>csnap</c>
/// <c>import</c> prices exist first; this step then upserts a <c>trade</c> row
/// over them on trade days (ADR-0084 D5 — the execution price is the truer
/// observation), rank-gated so a <c>fetch</c>/<c>manual</c> price is never
/// clobbered. One row per <c>(security, UTC-day)</c>, taking the last trade of
/// the day. This is the per-ledger analogue of the one-time migration-177
/// backfill, scoped to this ledger so every FUTURE import is covered too.</para>
/// </remarks>
internal sealed class TradePriceSeedStep
{
    private readonly NpgsqlConnection _connection;

    public TradePriceSeedStep(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    public async Task<ImportStepResult> ExecuteAsync(
        ImportContext context,
        CancellationToken cancellationToken)
    {
        var rows = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO security_prices
                    (id, security_id, ledger_id, price, currency_code, price_date, source)
                SELECT DISTINCT ON (l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date)
                       gen_random_uuid(),
                       l.security_id,
                       l.ledger_id,
                       l.unit_price,
                       'USD',
                       (h.posted_at AT TIME ZONE 'UTC')::date,
                       'trade'
                FROM txn_legs l
                JOIN txn_headers h ON h.id = l.header_id
                WHERE h.ledger_id = @ledgerId
                  AND l.security_id IS NOT NULL
                  AND l.quantity   IS NOT NULL
                  AND l.quantity   <> 0
                  AND l.unit_price IS NOT NULL
                  AND l.unit_price > 0
                  AND h.is_recurring_template = FALSE
                ORDER BY l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date, h.seq DESC
                ON CONFLICT (security_id, price_date) DO UPDATE
                    SET price         = EXCLUDED.price,
                        source        = 'trade',
                        currency_code = EXCLUDED.currency_code
                    WHERE security_prices.source IN ('import', 'simplefin', 'trade');
                """,
                new { ledgerId = context.LedgerId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return new ImportStepResult(
            StepName: "trade-price-seed",
            Read: rows,
            Written: rows,
            Skipped: 0);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        CancellationToken cancellationToken)
    {
        var step = new TradePriceSeedStep(connection);
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
