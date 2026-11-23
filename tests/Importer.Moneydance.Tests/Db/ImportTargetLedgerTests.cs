using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Every import creates the ledger it fills. The name typed for it is a label,
/// never a lookup: ledger names are not unique, and the import runs as
/// <c>coffer_service</c>, which bypasses row-level security, so resolving the
/// name could write into, or report on, a ledger the importing person cannot see.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class ImportTargetLedgerTests : DatabaseTest
{
    private const string Export = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-shop","type":"e","name":"Shopping","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"}
          ]
        }
        """;

    public ImportTargetLedgerTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task An_import_creates_its_own_ledger_whatever_other_ledgers_share_its_name()
    {
        await using var conn = Db.OpenConnection();
        var name = $"Shared name {Guid.NewGuid():N}";
        var firstUser = await NewUserAsync(conn);
        var secondUser = await NewUserAsync(conn);

        // The first user's ledger, created in the app and still empty.
        var existing = await new LedgersRepository(conn).CreateAsync(name);
        await conn.ExecuteAsync(
            "INSERT INTO user_ledger_grants (user_id, ledger_id, role) VALUES (@U, @L, 'owner');",
            new { U = firstUser, L = existing.Id });

        // Another user names an import the same: a new ledger, theirs alone.
        var second = await ImportAsync(conn, name, secondUser);
        Assert.NotEqual(existing.Id, second.LedgerId);
        Assert.Equal(0, await HeaderCountAsync(conn, existing.Id));
        Assert.Equal([(secondUser, "owner")], await GrantsAsync(conn, second.LedgerId));
        Assert.Equal([(firstUser, "owner")], await GrantsAsync(conn, existing.Id));

        // The first user imports under that name too, now held by a populated ledger
        // and by two ledgers at once: a third ledger, and the populated one is untouched.
        var secondHeaders = await HeaderCountAsync(conn, second.LedgerId);
        Assert.True(secondHeaders > 0, "the import wrote nothing; the checks below would prove nothing");
        var third = await ImportAsync(conn, name, firstUser);
        Assert.DoesNotContain(third.LedgerId, new[] { existing.Id, second.LedgerId });
        Assert.Equal(secondHeaders, await HeaderCountAsync(conn, second.LedgerId));
        Assert.Equal(secondHeaders, await HeaderCountAsync(conn, third.LedgerId));
        Assert.Equal([(firstUser, "owner")], await GrantsAsync(conn, third.LedgerId));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ledgers WHERE name = @name;", new { name }));
    }

    /// <summary>
    /// The import's one transaction creates the ledger it fills, so having written every row
    /// it may still take that ledger's write lock: migration 275 exempts the transaction that
    /// created the ledger from the rule that the lock comes before any write.
    /// </summary>
    [Fact]
    public async Task The_import_may_take_its_ledgers_write_lock_after_writing_because_it_created_the_ledger()
    {
        await using var conn = Db.OpenConnection();
        var owner = await NewUserAsync(conn);
        bool? locked = null;
        var result = await new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(Export), ledgerName: $"Locked {Guid.NewGuid():N}", ownerUserId: owner,
            progress: null, cancellationToken: default,
            beforeCommit: async (tx, ledgerId, ct) =>
            {
                await Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync(tx, ledgerId, ct);
                locked = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT locked FROM fn_ledger_write_lock(@ledgerId);", new { ledgerId }, tx, cancellationToken: ct));
            });

        Assert.True(await HeaderCountAsync(conn, result.LedgerId) > 0, "the import wrote nothing before the lock; the check proves nothing");
        Assert.True(locked);
    }

    private static Task<MoneydanceImportResult> ImportAsync(NpgsqlConnection conn, string name, Guid owner) =>
        new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(Export), ledgerName: name, ownerUserId: owner, progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);

    private static Task<Guid> NewUserAsync(NpgsqlConnection conn) =>
        conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (display_name) VALUES (@n) RETURNING id;",
            new { n = $"importer-{Guid.NewGuid():N}" });

    private static Task<int> HeaderCountAsync(NpgsqlConnection conn, Guid ledgerId) =>
        conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM txn_headers WHERE ledger_id = @ledgerId;", new { ledgerId });

    private static async Task<(Guid UserId, string Role)[]> GrantsAsync(NpgsqlConnection conn, Guid ledgerId) =>
        (await conn.QueryAsync<(Guid, string)>(
            "SELECT user_id, role FROM user_ledger_grants WHERE ledger_id = @ledgerId ORDER BY user_id;",
            new { ledgerId })).ToArray();
}
