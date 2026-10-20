using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// End-to-end reminder import against a real DB (ADR-0047 / mig 124). Proves a
/// reminder materializes as a TEMPLATE txn_header + legs + a slim
/// recurring_transactions row, and that the template NEVER produces a
/// txn_header_account_balances row (the keystone of the balance invariant —
/// live_txn_headers + the mig-124 recompute exclude it). Seed-once (ADR-0052
/// D2): the importer only ever seeds an EMPTY ledger, so a single import is the
/// only path exercised.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class ReminderImportIntegrationTests
{
    private readonly PostgresFixture _fixture;

    public ReminderImportIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    // Weekly reminder on weeklydays=6 (Friday, Java Calendar DOW) with
    // auto-commit 2 days before; single split source -> category.
    private const string ReminderExportJson = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {
              "obj_type":"reminder","id":"rem-1","desc":"Recurring A","memo":"note",
              "type":"weekly","sdt":"20260101","weeklydays":"6","weeklymod":"1",
              "daily":"0","monthlydays":"","yearly":"0","acdays":"2","is_loan_reminder":"0",
              "txn.acctid":"a-checking","txn.desc":"Recurring A",
              "txn.0.acctid":"a-cat","txn.0.samt":"150000","txn.0.pamt":"-150000"
            }
          ]
        }
        """;

    [Fact]
    public async Task Reminder_materializes_a_template_that_never_hits_balances()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId = TestLedger.Id;
        var bankId = Guid.NewGuid();
        var catId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO ledgers (id, name) VALUES (@Id, 'Default') ON CONFLICT (id) DO NOTHING;",
            new { Id = ledgerId });
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code,
                opening_balance, is_active, is_system)
            VALUES (@A, @L, 'Bank A',    'bank',     NULL,      'USD', 0, true, false),
                   (@C, @L, 'Groceries', 'category', 'expense', 'USD', 0, true, false);",
            new { A = bankId, C = catId, L = ledgerId });

        var ctx = new ImportContext(MdItemReader.ReadString(ReminderExportJson), ledgerId);
        ctx.AccountByMdId["a-checking"] = new AccountRef(bankId, "bank");
        ctx.AccountByMdId["a-cat"] = new AccountRef(catId, "category");

        var result = await ReminderImportStep.RunAsync(conn, ctx, "moneydance_export");
        Assert.Equal(1, result.Written);

        // --- the recurring SERIES row ---
        var row = await conn.QuerySingleAsync<(string? Rrule, int? AutoCommit, Guid? TemplateHeaderId, Guid? SourceAccountId, string? SourcePayload, string Origin, bool IsActive)>(
            @"SELECT rrule AS Rrule, auto_commit_days_before AS AutoCommit,
                     template_header_id AS TemplateHeaderId, source_account_id AS SourceAccountId,
                     source_payload AS SourcePayload, origin AS Origin, is_active AS IsActive
              FROM recurring_transactions WHERE external_id = 'rem-1';");
        Assert.Equal("FREQ=WEEKLY;BYDAY=FR", row.Rrule);   // weeklydays=6 -> Friday (Java DOW)
        Assert.Equal(2, row.AutoCommit);                    // acdays=2
        Assert.Equal(bankId, row.SourceAccountId);          // mig 125: originating account (a-checking)
        Assert.Equal("moneydance_import", row.Origin);
        Assert.True(row.IsActive);
        Assert.NotNull(row.SourcePayload);
        Assert.Contains("rem-1", row.SourcePayload!);       // lossless raw reminder JSON
        Assert.NotNull(row.TemplateHeaderId);
        var templateHeaderId = row.TemplateHeaderId!.Value;

        // --- the template header + legs ---
        var isTemplate = await conn.ExecuteScalarAsync<bool>(
            "SELECT is_recurring_template FROM txn_headers WHERE id = @H;", new { H = templateHeaderId });
        Assert.True(isTemplate);
        var legCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_legs WHERE header_id = @H;", new { H = templateHeaderId });
        Assert.Equal(2, legCount);   // origin (bank) + counterpart (category)

        // --- KEYSTONE: recompute, then the template has NO balance row ---
        await conn.ExecuteAsync(
            "SELECT fn_recompute_balances_for_account(@A, '0001-01-01'::timestamptz);", new { A = bankId });
        var balanceRows = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_header_account_balances WHERE header_id = @H;", new { H = templateHeaderId });
        Assert.Equal(0, balanceRows);
    }

    private static async Task ResetAsync(NpgsqlConnection conn) =>
        await conn.ExecuteAsync(@"
            TRUNCATE recurring_transactions, txn_header_account_balances,
                     security_splits, lots, holdings, txn_legs, txn_headers,
                     security_prices, securities,
                     account_external_ids, accounts RESTART IDENTITY CASCADE;");
}
