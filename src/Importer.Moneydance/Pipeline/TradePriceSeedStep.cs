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
/// the day by the rule every writer and the consistency check apply
/// (<c>fn_trade_execution_for_day</c>): only an execution prices a day, never a share
/// movement (ADR-0084 D6), and a hidden or merged-away row is no trade. This is
/// the per-ledger analogue of the one-time migration-177 backfill, scoped to this
/// ledger so every FUTURE import is covered too.</para>
///
/// <para>Each price is in the currency of the brokerage that made the picked trade,
/// read through the rule's own <c>fn_trade_currency</c> (migration 274), so the choice
/// of currency is stated once. Each imported account is in its own Moneydance currency
/// (ADR-0114), so a trade's price is in its brokerage's.</para>
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
                       -- The currency of the brokerage that made the trade (migration
                       -- 274's fn_trade_currency, the rule's).
                       c.currency_code,
                       (h.posted_at AT TIME ZONE 'UTC')::date,
                       'trade'
                FROM txn_legs l
                JOIN live_txn_headers h ON h.id = l.header_id
                CROSS JOIN LATERAL fn_trade_currency(l.account_id) c
                WHERE h.ledger_id = @ledgerId
                  AND l.security_id IS NOT NULL
                  -- Identical to fn_trade_execution_for_day (migration 274): an execution
                  -- (fn_leg_prices_a_day, so never a ShrsIn/ShrsOut share movement), on
                  -- a header that is no template, not hidden and not merged into another.
                  AND h.is_hidden = FALSE
                  AND h.is_merged_into IS NULL
                  AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                                          h.provider_raw_payload->>'qif_invst_action')
                -- Without the last two columns a header carrying several security legs
                -- ties on seq and Postgres picks arbitrarily, so the importer could seed
                -- a price the checker would never derive.
                ORDER BY l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date,
                         h.seq DESC, abs(l.amount) DESC, l.id DESC
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
