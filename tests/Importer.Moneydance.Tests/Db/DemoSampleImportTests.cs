using Dapper;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// End-to-end import of the generated Demo's short span
/// (<see cref="DemoTestExports.Short"/>: the trailing year of bank-route rows and the
/// whole investment route, whose shapes docs/moneydance-investment-actions.md
/// documents). Asserts the resulting Ledger txn_headers carry exactly the
/// ADR-0027 actions the Demo exercises, and no pre-A4 action (`interest`,
/// `misc_income`, `misc_expense`, `split`) survives the import.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class DemoSampleImportTests : DatabaseTest
{
    public DemoSampleImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Import_demo_sample_owns_every_header_by_its_home_inside_the_263_invariants()
    {
        // Migration 263: every route stamps the Moneydance home, and the home is on a
        // leg of the header, is the brokerage on every posting of each actioned event,
        // and is on every line of each split — the validator's originating check.
        await using var conn = Db.OpenConnection();

        var ledgerId = await ProvisionLedgerAsync(conn);
        using var export = DemoTestExports.ShortExport();
        var context = new ImportContext(export, ledgerId);
        var importSource = "test:generated-demo-short";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await SecuritySplitImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, importSource);
        await InvestmentTransactionImportStep.RunAsync(conn, context, importSource);
        await ReminderImportStep.RunAsync(conn, context, importSource);
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        var withLegs = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM txn_headers h
             WHERE h.ledger_id = @LedgerId
               AND EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id)
            """, new { LedgerId = ledgerId });
        Assert.True(withLegs > 0, "the demo import wrote no headers; the check below would prove nothing");

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);
        var check = report.Checks.Single(c => c.Name == "originating-account");
        Assert.True(check.Passed, check.Message);
    }

    [Fact]
    public async Task Import_demo_sample_produces_only_ADR_0027_actions()
    {
        await using var conn = Db.OpenConnection();

        var ledgerId = await ProvisionLedgerAsync(conn);
        using var export = DemoTestExports.ShortExport();
        var context = new ImportContext(export, ledgerId);
        var importSource = "test:generated-demo-short";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await SecuritySplitImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, importSource);
        var investment = await InvestmentTransactionImportStep.RunAsync(conn, context, importSource);
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        // Every investment row the generator wrote imports: none is skipped.
        Assert.Empty(investment.Skips ?? []);
        Assert.Equal(0, investment.Skipped);

        // Action distribution post-import.
        var distinctActions = (await conn.QueryAsync<string>(
            "SELECT DISTINCT action FROM txn_headers WHERE ledger_id = @L AND action IS NOT NULL ORDER BY action;",
            new { L = ledgerId }))
            .ToList();

        // Exactly the ADR-0027 actions the Demo exercises: each buy and sell with and
        // without its transfer, both dividend forms and the transferred one, the
        // brokerage's own cash move, and `misc` (the employer match's `inc` and the
        // HSA's `exp` both map to it).
        Assert.Equal(
            new[]
            {
                "buy", "buyx", "dividend_cash", "dividend_reinvest", "divx", "misc", "sell", "sellx", "transfer",
            },
            distinctActions.Order(StringComparer.Ordinal));

        // A sale carries its commission: at least one `sell` header has a fee leg.
        var sellsWithFee = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM txn_headers h
             WHERE h.ledger_id = @L AND h.action = 'sell'
               AND EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id AND l.posting_role = 'fee')
            """, new { L = ledgerId });
        Assert.True(sellsWithFee > 0, "no imported sell carries a fee leg");

        // None of the dropped pre-A4 actions appear.
        Assert.DoesNotContain("interest",     distinctActions);
        Assert.DoesNotContain("misc_income",  distinctActions);
        Assert.DoesNotContain("misc_expense", distinctActions);
        Assert.DoesNotContain("split",        distinctActions);
    }

    [Fact]
    public async Task Trade_price_seed_step_seeds_trade_source_prices_from_the_sample_buys()
    {
        // ADR-0084: the importer bypasses the EF TradePriceFromLegInterceptor, so
        // TradePriceSeedStep seeds `trade`-source prices from the imported trade
        // legs at end-of-import. Run the pipeline through the price steps and
        // assert every trade row is a real execution observation on the correct
        // UTC day, and that a trade overwrites the same-day csnap `import` seed.
        await using var conn = Db.OpenConnection();

        var ledgerId = await ProvisionLedgerAsync(conn);
        using var export = DemoTestExports.ShortExport();
        var context = new ImportContext(export, ledgerId);
        var importSource = "test:generated-demo-short";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await SecuritySplitImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, importSource);
        await InvestmentTransactionImportStep.RunAsync(conn, context, importSource);
        await PriceSnapshotImportStep.RunAsync(conn, context);              // csnap -> import rows

        // Anchor for the rank gate below: before the seed runs, (security, day) pairs
        // with both an `import` seed and a priced trade exist. The Demo prices every
        // business day and trades on business days, so without this count the gate's
        // zero could only mean there was nothing for it to overwrite.
        var importAndTradeDays = await conn.ExecuteScalarAsync<int>(ImportShadowedByTradeSql, new { LedgerId = ledgerId });
        Assert.True(importAndTradeDays > 0, "no day has both an import price and a priced trade; the rank gate is vacuous");

        var seed = await TradePriceSeedStep.RunAsync(conn, context, default);
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        // The sample exercises priced buys/reinvests, so the seed writes rows.
        Assert.True(seed.Written > 0,
            "TradePriceSeedStep should seed at least one trade price from the sample's priced investment legs");

        var tradeCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM security_prices WHERE source = 'trade' AND ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        Assert.True(tradeCount > 0);

        // Every trade row corresponds to a real priced leg's execution price on
        // that security's UTC posted day. security_prices.price is NUMERIC(19,4)
        // (ADR-0070 D8 / mig 155), so the seed stores unit_price rounded to 4dp —
        // compare at that scale, not against the raw (25,12) unit_price.
        var orphanTradeRows = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM security_prices sp
            WHERE sp.source = 'trade' AND sp.ledger_id = @LedgerId
              AND NOT EXISTS (
                  SELECT 1
                  FROM txn_legs l
                  JOIN txn_headers h ON h.id = l.header_id
                  WHERE l.security_id = sp.security_id
                    AND (h.posted_at AT TIME ZONE 'UTC')::date = sp.price_date
                    AND round(l.unit_price, 4) = sp.price);
            """,
            new { LedgerId = ledgerId });
        Assert.Equal(0, orphanTradeRows);

        // Rank gate (ADR-0084 D5): on a (security, day) that has a priced trade,
        // no stale `import` row survives — the trade overwrote the csnap seed.
        var importShadowedByTrade = await conn.ExecuteScalarAsync<int>(ImportShadowedByTradeSql, new { LedgerId = ledgerId });
        Assert.Equal(0, importShadowedByTrade);
    }

    /// <summary>(security, day) pairs whose `import` price shares the day with a priced trade.</summary>
    private const string ImportShadowedByTradeSql = """
        SELECT COUNT(*)
        FROM security_prices sp
        WHERE sp.source = 'import' AND sp.ledger_id = @LedgerId
          AND EXISTS (
              SELECT 1
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
              WHERE l.security_id = sp.security_id
                AND (h.posted_at AT TIME ZONE 'UTC')::date = sp.price_date
                AND l.unit_price IS NOT NULL AND l.unit_price > 0
                AND l.quantity IS NOT NULL AND l.quantity <> 0
                AND h.is_recurring_template = FALSE);
        """;

    [Fact]
    public async Task Import_leaves_no_trade_price_check_finding()
    {
        // The seed states the trade-price rule in its own SQL; the consistency check
        // (fn_trade_price_check) states it again and, since migration 274, compares the
        // stored currency with the currency of the brokerage that made the day's trade as
        // well as the price. After an import the two must agree on every day.
        //
        // The import names every account in dollars, so these assertions cannot tell the
        // seed's reading of the brokerage's currency apart from a 'USD' literal until the
        // importer carries currencies; they catch any other currency, and any other price.
        await using var conn = Db.OpenConnection();

        var ledgerId = await ProvisionLedgerAsync(conn);
        using var export = DemoTestExports.ShortExport();
        var context = new ImportContext(export, ledgerId);
        var importSource = "test:generated-demo-short";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await SecuritySplitImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, importSource);
        await InvestmentTransactionImportStep.RunAsync(conn, context, importSource);
        await PriceSnapshotImportStep.RunAsync(conn, context);
        await TradePriceSeedStep.RunAsync(conn, context, default);
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        // Anchor: the check examines days, so a count of none reported is its verdict.
        var examined = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM fn_trade_price_check(@LedgerId);",
            new { LedgerId = ledgerId });
        Assert.True(examined > 0, "the check examined no day of the import");

        var reported = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM fn_trade_price_check(@LedgerId) WHERE reason IS NOT NULL;",
            new { LedgerId = ledgerId });
        Assert.Equal(0, reported);

        // Every seeded day is in the currency the rule's execution names.
        var otherCurrency = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
              FROM security_prices sp
              CROSS JOIN LATERAL fn_trade_execution_for_day(sp.ledger_id, sp.security_id, sp.price_date) e
             WHERE sp.ledger_id = @LedgerId AND sp.source = 'trade'
               AND sp.currency_code <> e.currency_code;
            """,
            new { LedgerId = ledgerId });
        Assert.Equal(0, otherCurrency);
    }

    private static async Task<Guid> ProvisionLedgerAsync(Npgsql.NpgsqlConnection conn)
    {
        var existing = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM ledgers WHERE id = @Id;",
            new { Id = TestLedger.Id });
        if (existing is null)
        {
            await conn.ExecuteAsync(
                "INSERT INTO ledgers (id, name) VALUES (@Id, 'Default');",
                new { Id = TestLedger.Id });
        }
        return TestLedger.Id;
    }
}
