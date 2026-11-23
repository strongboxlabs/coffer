using Dapper;
using Coffer.Api.Db;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// ADR-0091, amendment of 2026-11-18: Moneydance has no third category kind, so
/// an export marks an adjustment category as a top-level type <c>e</c> row with
/// <c>"coffer_category_kind": "adjustment"</c>. That one value on that one shape
/// imports as an adjustment category; anything else fails the import.
/// </summary>
/// <remarks>
/// Fail closed matters here because the alternatives fail open: an unknown type
/// code is dropped and its postings skipped, and an ignored key would quietly
/// file an adjustment as spending.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class CategoryKindOverrideTests : DatabaseTest
{
    public CategoryKindOverrideTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Base = """
        {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},
        {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
        {"obj_type":"acct","id":"a-house","type":"a","name":"House","parentid":"a-root","currid":"c-usd","sbal":"0"},
        {"obj_type":"acct","id":"c-bills","type":"e","name":"Bills","parentid":"a-root","currid":"c-usd"},
        {"obj_type":"acct","id":"c-pay","type":"i","name":"Paycheck","parentid":"a-root","currid":"c-usd"}
        """;

    [Fact]
    public async Task A_top_level_expense_row_with_the_key_imports_as_an_adjustment_category()
    {
        await using var conn = Db.OpenConnection();

        var result = await ImportAsync(conn, """
            ,{"obj_type":"acct","id":"c-adj","type":"e","name":"Valuation Adjustment","parentid":"a-root",
              "currid":"c-usd","coffer_category_kind":"adjustment"},
             {"obj_type":"acct","id":"c-cable","type":"e","name":"Cable","parentid":"c-bills","currid":"c-usd"},
             {"obj_type":"txn","id":"t-1","acctid":"a-house","desc":"Revaluation","dt":"20260101",
              "0.id":"s-1","0.acctid":"c-adj","0.samt":"-500000","0.pamt":"500000"}
            """, $"Adjustment {Guid.NewGuid():N}");

        Assert.True(result.Validation.AllPassed,
            string.Join("; ", result.Validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}")));

        var kinds = (await conn.QueryAsync<(string ExternalId, string Kind, Guid? ParentId)>(
            """
            SELECT external_id, category_kind, parent_id FROM accounts
             WHERE ledger_id = @L AND account_type = 'category' AND external_id IS NOT NULL;
            """, new { L = result.LedgerId })).ToDictionary(r => r.ExternalId);

        Assert.Equal("adjustment", kinds["c-adj"].Kind);
        Assert.Null(kinds["c-adj"].ParentId);
        // The key changes only the row that carries it.
        Assert.Equal("expense", kinds["c-bills"].Kind);
        Assert.Equal("expense", kinds["c-cable"].Kind);
        Assert.Equal("income", kinds["c-pay"].Kind);

        // Its posting landed on it: the row was not dropped as an unknown type.
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_legs l JOIN accounts a ON a.id = l.account_id
             WHERE a.ledger_id = @L AND a.external_id = 'c-adj';
            """, new { L = result.LedgerId }));
    }

    public static TheoryData<string, string, string> Refused => new()
    {
        // label, the offending row, the value the message quotes
        { "another value", """{"obj_type":"acct","id":"x-1","type":"e","name":"Odd","parentid":"a-root","currid":"c-usd","coffer_category_kind":"income"}""", "income" },
        { "wrong case", """{"obj_type":"acct","id":"x-1","type":"e","name":"Odd","parentid":"a-root","currid":"c-usd","coffer_category_kind":"Adjustment"}""", "Adjustment" },
        { "empty", """{"obj_type":"acct","id":"x-1","type":"e","name":"Odd","parentid":"a-root","currid":"c-usd","coffer_category_kind":""}""", "" },
        { "not a string", """{"obj_type":"acct","id":"x-1","type":"e","name":"Odd","parentid":"a-root","currid":"c-usd","coffer_category_kind":1}""", "1" },
        { "income row", """{"obj_type":"acct","id":"x-1","type":"i","name":"Odd","parentid":"a-root","currid":"c-usd","coffer_category_kind":"adjustment"}""", "adjustment" },
        { "bank row", """{"obj_type":"acct","id":"x-1","type":"b","name":"Odd","parentid":"a-root","currid":"c-usd","sbal":"0","coffer_category_kind":"adjustment"}""", "adjustment" },
        { "security row", """{"obj_type":"acct","id":"x-1","type":"s","name":"Odd","parentid":"a-house","currid":"c-usd","coffer_category_kind":"adjustment"}""", "adjustment" },
        { "child category", """{"obj_type":"acct","id":"x-1","type":"e","name":"Odd","parentid":"c-bills","currid":"c-usd","coffer_category_kind":"adjustment"}""", "adjustment" },
        // Top level means no parent or MD's root, not merely "parent is not a
        // category"; starter-categories.gen.mjs uses the same rule.
        { "under a real account", """{"obj_type":"acct","id":"x-1","type":"e","name":"Odd","parentid":"a-house","currid":"c-usd","coffer_category_kind":"adjustment"}""", "adjustment" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Any_other_value_or_shape_fails_the_import(string label, string row, string value)
    {
        await using var conn = Db.OpenConnection();
        var name = $"Refused {Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(conn, "," + row, name));

        Assert.True(
            ex.Message == $"acct x-1 'Odd': coffer_category_kind '{value}' is not supported; only \"adjustment\" on a top-level expense category",
            $"{label}: {ex.Message}");
        // Nothing of the import survives it.
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ledgers WHERE name = @N;", new { N = name }));
    }

    [Fact]
    public async Task A_blank_named_row_with_a_bad_key_is_refused_not_sent_to_uncategorized()
    {
        // A blank-named top-level leaf is otherwise folded into the system
        // Uncategorized before mapping, which would skip the check.
        await using var conn = Db.OpenConnection();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(conn, """
            ,{"obj_type":"acct","id":"x-1","type":"e","name":"","parentid":"a-root","currid":"c-usd","coffer_category_kind":"income"}
            """, $"Refused {Guid.NewGuid():N}"));

        Assert.StartsWith("acct x-1 '': coffer_category_kind 'income' is not supported", ex.Message);
    }

    private static Task<MoneydanceImportResult> ImportAsync(
        Npgsql.NpgsqlConnection conn, string extra, string ledgerName)
    {
        var json = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{Base}}{{extra}} ]
            }
            """;
        return new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(json), ledgerName: ledgerName,
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: LegNotes.RecomputeLedgerOnAsync);
    }
}
