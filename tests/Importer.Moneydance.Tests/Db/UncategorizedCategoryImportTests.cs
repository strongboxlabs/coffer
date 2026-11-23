using Dapper;
using Coffer.Api.Db;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Moneydance's blank-named leaf category lands on the ledger's system
/// Uncategorized — the account bank feeds already put unclassified postings in
/// (<c>IngestOrchestrator.EnsureUncategorizedAsync</c>) — instead of becoming a
/// user category called "(unnamed)".
/// </summary>
/// <remarks>
/// Fresh imports only: nothing here rewrites a ledger imported before. A blank
/// category that parents others keeps its own "(unnamed)" row (fail closed), and
/// named categories and real accounts are untouched.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class UncategorizedCategoryImportTests : DatabaseTest
{
    public UncategorizedCategoryImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Accounts = """
        {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
        {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
        {"obj_type":"acct","id":"c-blank-e","type":"e","name":"","parentid":"a-root","currid":"c-usd"},
        {"obj_type":"acct","id":"c-blank-i","type":"i","name":" ","parentid":"a-root","currid":"c-usd"},
        {"obj_type":"acct","id":"c-blank-parent","type":"e","name":"","parentid":"a-root","currid":"c-usd"},
        {"obj_type":"acct","id":"c-child","type":"e","name":"Sub","parentid":"c-blank-parent","currid":"c-usd"},
        {"obj_type":"acct","id":"c-named","type":"e","name":"Groceries","parentid":"a-root","currid":"c-usd"},
        {"obj_type":"acct","id":"c-bills","type":"e","name":"Bills","parentid":"a-root","currid":"c-usd"},
        {"obj_type":"acct","id":"c-blank-nested","type":"e","name":"","parentid":"c-bills","currid":"c-usd"}
        """;

    [Fact]
    public async Task Blank_leaf_categories_all_resolve_to_one_system_uncategorized()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var context = await ImportAccountsAsync(conn, ledgerId);

        var uncategorizedId = context.AccountByMdId["c-blank-e"].Id;
        Assert.Equal(uncategorizedId, context.AccountByMdId["c-blank-i"].Id);
        Assert.True(context.AccountByMdId["c-blank-e"].IsCategory);

        // Exactly EnsureUncategorizedAsync's row.
        var row = await conn.QuerySingleAsync<AccountRow>(
            SelectAccount + " WHERE id = @Id;", new { Id = uncategorizedId });
        Assert.Equal(ledgerId,        row.LedgerId);
        Assert.Equal("Uncategorized", row.Name);
        Assert.Equal("category",      row.AccountType);
        Assert.Equal("expense",       row.CategoryKind);
        Assert.Equal("USD",           row.CurrencyCode);
        Assert.Equal(0m,              row.OpeningBalance);
        Assert.True(row.IsActive);
        Assert.True(row.IsSystem);
        Assert.Null(row.ExternalId);
        Assert.Null(row.ParentId);

        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @L AND is_system AND name = 'Uncategorized';",
            new { L = ledgerId }));

        // The blank leaves left no account and no junction row of their own.
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @L AND external_id IN ('c-blank-e', 'c-blank-i');",
            new { L = ledgerId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM account_external_ids WHERE ledger_id = @L AND external_id IN ('c-blank-e', 'c-blank-i');",
            new { L = ledgerId }));
    }

    [Fact]
    public async Task A_blank_category_with_children_keeps_its_own_unnamed_row()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var context = await ImportAccountsAsync(conn, ledgerId);

        var parent = await conn.QuerySingleAsync<AccountRow>(
            SelectAccount + " WHERE ledger_id = @L AND external_id = 'c-blank-parent';", new { L = ledgerId });
        Assert.Equal("(unnamed)", parent.Name);
        Assert.False(parent.IsSystem);
        Assert.Equal(parent.Id, context.AccountByMdId["c-blank-parent"].Id);
        Assert.NotEqual(context.AccountByMdId["c-blank-e"].Id, parent.Id);

        var child = await conn.QuerySingleAsync<AccountRow>(
            SelectAccount + " WHERE ledger_id = @L AND external_id = 'c-child';", new { L = ledgerId });
        Assert.Equal(parent.Id, child.ParentId);

        // Named categories and real accounts are named as before.
        var names = (await conn.QueryAsync<string>(
            "SELECT name FROM accounts WHERE ledger_id = @L AND external_id IN ('c-named', 'a-bank') ORDER BY name;",
            new { L = ledgerId })).ToList();
        Assert.Equal(["Checking", "Groceries"], names);
    }

    [Fact]
    public async Task The_created_uncategorized_is_not_counted_as_a_written_account()
    {
        // Written counts Moneydance accounts written, as for the Holdings
        // siblings the step also creates: one per junction row.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var json = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{Accounts}} ]
            }
            """;
        var result = await AccountImportStep.RunAsync(
            conn, new ImportContext(MdItemReader.ReadString(json), ledgerId));

        var junctions = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM account_external_ids WHERE ledger_id = @L AND source = 'moneydance';",
            new { L = ledgerId });
        Assert.Equal(junctions, result.Written);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @L AND is_system AND name = 'Uncategorized';",
            new { L = ledgerId }));
    }

    [Fact]
    public async Task A_blank_category_under_a_named_parent_keeps_its_own_unnamed_row_in_place()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var context = await ImportAccountsAsync(conn, ledgerId);

        var nested = await conn.QuerySingleAsync<AccountRow>(
            SelectAccount + " WHERE ledger_id = @L AND external_id = 'c-blank-nested';", new { L = ledgerId });
        Assert.Equal("(unnamed)", nested.Name);
        Assert.False(nested.IsSystem);
        Assert.Equal(context.AccountByMdId["c-bills"].Id, nested.ParentId);
        Assert.Equal(nested.Id, context.AccountByMdId["c-blank-nested"].Id);
        Assert.DoesNotContain("c-blank-nested", context.MdIdsOnUncategorized);
    }

    [Fact]
    public async Task An_existing_system_uncategorized_is_reused_rather_than_duplicated()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        // The row a bank feed would already have created in this ledger.
        var existingId = await new AccountsRepository(conn).InsertSystemAccountAsync(new AccountRow(
            Id: Guid.NewGuid(), LedgerId: ledgerId, ParentId: null, Name: "Uncategorized",
            AccountType: "category", CategoryKind: "expense", CurrencyCode: "USD",
            OpeningBalance: 0m, IsActive: true, ExternalId: null, IsSystem: true,
            HoldingsAccountId: null, Notes: null, AccountNumber: null,
            InstitutionName: null, RoutingNumber: null, AccountUrl: null));

        var context = await ImportAccountsAsync(conn, ledgerId);

        Assert.Equal(existingId, context.AccountByMdId["c-blank-e"].Id);
        Assert.Equal(existingId, context.AccountByMdId["c-blank-i"].Id);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @L AND is_system AND name = 'Uncategorized';",
            new { L = ledgerId }));
    }

    [Fact]
    public async Task A_users_own_category_named_uncategorized_is_not_the_system_row()
    {
        // The lookup keys on is_system, as the API's does. A Moneydance category
        // the user happened to name "Uncategorized" is theirs, keeps its own row,
        // and does not absorb the blank one.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var context = await ImportAccountsAsync(conn, ledgerId, extra: """
            ,{"obj_type":"acct","id":"c-users","type":"e","name":"Uncategorized","parentid":"a-root","currid":"c-usd"}
            """);

        var users = context.AccountByMdId["c-users"].Id;
        var blank = context.AccountByMdId["c-blank-e"].Id;
        Assert.NotEqual(users, blank);
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT is_system FROM accounts WHERE id = @Id;", new { Id = users }));
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT is_system FROM accounts WHERE id = @Id;", new { Id = blank }));
    }

    [Fact]
    public async Task A_users_own_uncategorized_beside_the_blank_category_passes_validation()
    {
        // The full service, so the post-import validator runs. The ledger then
        // holds two categories named "Uncategorized": the user's, with its
        // Moneydance external id, and the system row, which has none. That is
        // not the re-import drift the name-uniqueness check looks for.
        await using var conn = Db.OpenConnection();

        var json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"c-blank","type":"e","name":"","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"acct","id":"c-users","type":"e","name":"Uncategorized","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
                 "0.id":"s-1","0.acctid":"c-blank","0.samt":"1000","0.pamt":"-1000"},
                {"obj_type":"txn","id":"t-2","acctid":"a-bank","desc":"Shop","dt":"20260102",
                 "0.id":"s-2","0.acctid":"c-users","0.samt":"1000","0.pamt":"-1000"}
              ]
            }
            """;

        var result = await new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(json), ledgerName: $"Uncategorized {Guid.NewGuid():N}",
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: LegNotes.RecomputeLedgerOnAsync);
        var ledgerId = result.LedgerId;

        Assert.True(result.Validation.AllPassed,
            string.Join("; ", result.Validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}")));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @L AND account_type = 'category' AND name = 'Uncategorized';",
            new { L = ledgerId }));
    }

    private static async Task<ImportContext> ImportAccountsAsync(
        NpgsqlConnection conn, Guid ledgerId, string extra = "")
    {
        var json = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{Accounts}}{{extra}} ]
            }
            """;
        var context = new ImportContext(MdItemReader.ReadString(json), ledgerId);
        await AccountImportStep.RunAsync(conn, context);
        return context;
    }


    private const string SelectAccount = """
        SELECT id AS Id, ledger_id AS LedgerId, parent_id AS ParentId, name AS Name,
               account_type AS AccountType, category_kind AS CategoryKind,
               currency_code AS CurrencyCode, opening_balance AS OpeningBalance,
               is_active AS IsActive, external_id AS ExternalId, is_system AS IsSystem,
               holdings_account_id AS HoldingsAccountId, notes AS Notes,
               account_number AS AccountNumber, institution_name AS InstitutionName,
               routing_number AS RoutingNumber, account_url AS AccountUrl,
               provider_raw_payload::text AS ProviderRawPayload, tax_status AS TaxStatus,
               opened_on AS OpenedOn
          FROM accounts
        """;
}
