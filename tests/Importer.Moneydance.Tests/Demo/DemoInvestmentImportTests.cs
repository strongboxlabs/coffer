using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Pipeline;
using Coffer.Importer.Moneydance.Tests.Db;
using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The generated investment route imports through the importer's own steps with
/// nothing skipped: every security with the vehicle its holding accounts' codes
/// give it, the split, a price per business day, and every row in one of
/// ADR-0027's actions.
/// </summary>
/// <remarks>
/// The export is the product's own, with the whole household: every bank-route row
/// imports with nothing skipped as well, and both reminders.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class DemoInvestmentImportTests : DatabaseTest
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    public DemoInvestmentImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task The_investment_route_imports_with_nothing_skipped()
    {
        var build = DemoExportGenerator.Generate(DemoSpec.Full(AsOf));
        using var export = build.ToExport();

        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var context = new ImportContext(export, ledgerId);
        const string source = "test:generated-demo";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        var splits = await SecuritySplitImportStep.RunAsync(conn, context);
        var bank = await TransactionImportStep.RunAsync(conn, context, source);
        var investment = await InvestmentTransactionImportStep.RunAsync(conn, context, source);
        var prices = await PriceSnapshotImportStep.RunAsync(conn, context);
        var reminders = await ReminderImportStep.RunAsync(conn, context, source);
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        Assert.Equal(0, bank.Skipped);
        Assert.True(bank.Written > 6_000, $"only {bank.Written} bank rows");
        Assert.Equal(0, reminders.Skipped);
        Assert.Equal(2, reminders.Written);
        Assert.Equal(0, investment.Skipped);
        Assert.Empty(investment.Skips ?? []);
        Assert.Equal(0, splits.Skipped);
        Assert.Equal(0, prices.Skipped);
        Assert.True(investment.Written > 300, $"only {investment.Written} investment rows");

        var vehicles = (await conn.QueryAsync<(string? Ticker, string? Vehicle)>(
            "SELECT ticker, vehicle_type FROM securities WHERE ledger_id = @L", new { L = ledgerId }))
            .ToDictionary(s => s.Ticker ?? "(none)", s => s.Vehicle, StringComparer.Ordinal);
        Assert.Equal("etf", vehicles["VTI"]);
        Assert.Equal("etf", vehicles["VNQ"]);
        Assert.Equal("mutual_fund", vehicles["VFFVX"]);
        Assert.Equal("money_market", vehicles["VMFXX"]);
        Assert.Equal("stock", vehicles["NVDA"]);
        Assert.Equal("mutual_fund", vehicles["(none)"]);
        Assert.Equal(9, vehicles.Count);

        var ratio = await conn.ExecuteScalarAsync<decimal>(
            """
            SELECT x.ratio FROM security_splits x JOIN securities s ON s.id = x.security_id
             WHERE x.ledger_id = @L AND s.ticker = 'NVDA'
            """, new { L = ledgerId });
        Assert.Equal(10m, ratio);

        var businessDays = new DemoCalendar(AsOf).BusinessDaysInWindow().Count();
        var priceRows = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM security_prices WHERE ledger_id = @L AND source = 'import'", new { L = ledgerId });
        Assert.Equal(8 * businessDays, priceRows);

        var actions = (await conn.QueryAsync<string>(
            "SELECT DISTINCT action FROM txn_headers WHERE ledger_id = @L AND action IS NOT NULL", new { L = ledgerId }))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(
            new HashSet<string>(["buy", "buyx", "sell", "sellx", "dividend_cash", "dividend_reinvest", "divx", "misc", "transfer"]),
            actions);
        Assert.Contains("buyx", actions);
        Assert.Contains("sellx", actions);
        Assert.Contains("divx", actions);
        Assert.Contains("transfer", actions);
    }

    private static async Task<Guid> ProvisionLedgerAsync(NpgsqlConnection conn)
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
