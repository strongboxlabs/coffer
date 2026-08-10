using System.IO;
using Dapper;
using Coffer.Importer.Moneydance;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// End-to-end import of <c>data/samples/moneydance-export-demo.json</c>
/// (the synthetic sample documented in docs/moneydance-investment-actions.md).
/// Asserts the resulting Ledger txn_headers carry only actions from the
/// ADR-0027 9-action set — no pre-A4 actions (`interest`, `misc_income`,
/// `misc_expense`, `split`) survive the import.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class DemoSampleImportTests
{
    private readonly PostgresFixture _fixture;

    public DemoSampleImportTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Import_demo_sample_produces_only_ADR_0027_actions()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId = await ProvisionLedgerAsync(conn);
        var sampleFile = LocateSampleFile();
        var export = MdItemReader.ReadFile(sampleFile);
        var context = new ImportContext(export, ledgerId);
        var importSource = "test:moneydance-export-demo.json";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await SecuritySplitImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, importSource);
        await InvestmentTransactionImportStep.RunAsync(conn, context, importSource);

        // Action distribution post-import.
        var distinctActions = (await conn.QueryAsync<string>(
            "SELECT DISTINCT action FROM txn_headers WHERE action IS NOT NULL ORDER BY action;"))
            .ToList();

        // Only ADR-0027 actions survive. short / cover / exp are sample-only
        // and either deferred (short/cover) or merge into misc (exp); each
        // either skips during import or produces a `misc` header.
        var allowed = new HashSet<string>(new[]
        {
            "buy", "buyx",
            "sell", "sellx",
            "dividend_cash", "dividend_reinvest", "divx",
            "transfer",
            "misc",
        });
        foreach (var action in distinctActions)
        {
            Assert.True(allowed.Contains(action),
                $"Unexpected action '{action}' in imported sample data");
        }

        // None of the dropped pre-A4 actions appear.
        Assert.DoesNotContain("interest",     distinctActions);
        Assert.DoesNotContain("misc_income",  distinctActions);
        Assert.DoesNotContain("misc_expense", distinctActions);
        Assert.DoesNotContain("split",        distinctActions);

        // The MD sample exercises each fee-eligible txntype with and
        // without a fee. Both variants of `inc` map to `misc`, and the
        // `exp` txntype also maps to `misc` — so we expect `misc`
        // present.
        Assert.Contains("misc", distinctActions);
        // Compound txntypes round-trip to their compound action codes.
        Assert.Contains("buyx", distinctActions);
        Assert.Contains("sellx", distinctActions);
        Assert.Contains("divx", distinctActions);
    }

    [Fact]
    public async Task Import_refuses_a_ledger_that_already_has_transactions()
    {
        // ADR-0052 D2: the MD import seeds a fresh ledger exactly once. Run
        // against a ledger that already holds transactions and it must refuse
        // (exit 4) and write nothing - MD re-keys txn.Id on online-merge, so a
        // second import would resurrect hidden/merged rows as duplicates. The
        // guard runs before any step, so no accounts/securities are seeded on
        // the refused run either.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);

        // One bare header is enough to make the ledger non-empty.
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, created_at)
            VALUES (gen_random_uuid(), @LedgerId, 'manual', NOW(),NOW(),
                false, false, NOW());",
            new { LedgerId = ledgerId });
        var repo = new TransactionsRepository(conn);
        Assert.Equal(1, await repo.CountTransactionHeadersAsync(ledgerId));

        // Drive the real CLI against the populated ledger (ExecuteAsync is
        // protected; CommandApp is the supported entry point). It opens its
        // own connection from --db (same container), so it sees the seed.
        var app = new CommandApp();
        app.Configure(config => config.AddCommand<ImportCommand>("import"));
        var exitCode = await app.RunAsync(new[]
        {
            "import", LocateSampleFile(),
            "--db", _fixture.ConnectionString,
            "--ledger-id", ledgerId.ToString(),
        });

        Assert.Equal(4, exitCode);                                    // refused
        Assert.Equal(1, await repo.CountTransactionHeadersAsync(ledgerId));  // nothing imported
    }

    [Fact]
    public async Task Trade_price_seed_step_seeds_trade_source_prices_from_the_sample_buys()
    {
        // ADR-0084: the importer bypasses the EF TradePriceFromLegInterceptor, so
        // TradePriceSeedStep seeds `trade`-source prices from the imported trade
        // legs at end-of-import. Run the pipeline through the price steps and
        // assert every trade row is a real execution observation on the correct
        // UTC day, and that a trade overwrites the same-day csnap `import` seed.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId = await ProvisionLedgerAsync(conn);
        var export = MdItemReader.ReadFile(LocateSampleFile());
        var context = new ImportContext(export, ledgerId);
        var importSource = "test:moneydance-export-demo.json";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await SecuritySplitImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, importSource);
        await InvestmentTransactionImportStep.RunAsync(conn, context, importSource);
        await PriceSnapshotImportStep.RunAsync(conn, context);              // csnap -> import rows
        var seed = await TradePriceSeedStep.RunAsync(conn, context, default);

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
        var importShadowedByTrade = await conn.ExecuteScalarAsync<int>(
            """
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
            """,
            new { LedgerId = ledgerId });
        Assert.Equal(0, importShadowedByTrade);
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

    private static string LocateSampleFile()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "data", "samples", "moneydance-export-demo.json");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate data/samples/moneydance-export-demo.json from " + AppContext.BaseDirectory);
    }

    private static async Task ResetAsync(Npgsql.NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(@"
            TRUNCATE security_splits, lots, holdings, txn_legs, txn_headers,
                     security_prices, securities, accounts
                     RESTART IDENTITY CASCADE;");
    }
}
