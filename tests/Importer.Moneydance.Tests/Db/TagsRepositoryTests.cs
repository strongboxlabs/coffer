using System.Text.Json;
using Dapper;
using Coffer.Importer.Moneydance.Db;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// The importer's tag resolution: one tag per name under the database's one key
/// (<c>tag_name_key</c>), whatever case or surrounding white space a name arrives in.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class TagsRepositoryTests : DatabaseTest
{
    private const string VectorFile = "src/Web/src/lib/annotations/annotation-vectors.json";

    public TagsRepositoryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task EnsureTags_inserts_new_names_and_is_idempotent()
    {
        await using var connection = Db.OpenConnection();
        var repo = new TagsRepository(connection);

        var first = await repo.EnsureTagsAsync(TestLedger.Id, ["vacation", "travel"]);
        var second = await repo.EnsureTagsAsync(TestLedger.Id, ["vacation", "travel"]);

        Assert.Equal(first["vacation"], second["vacation"]);
        Assert.Equal(first["travel"], second["travel"]);
        Assert.Equal(2, await repo.CountTagsAsync());
    }

    [Fact]
    public async Task Two_spellings_of_one_new_name_in_one_batch_create_one_tag_with_the_first_spelling()
    {
        await using var connection = Db.OpenConnection();
        var repo = new TagsRepository(connection);

        var ids = await repo.EnsureTagsAsync(TestLedger.Id, ["work", "Work", "WORK"]);

        Assert.Equal(ids["work"], ids["Work"]);
        Assert.Equal(ids["work"], ids["WORK"]);
        Assert.Equal(["work"], await NamesAsync(connection));
    }

    [Fact]
    public async Task A_name_matching_an_existing_tag_under_the_key_resolves_to_it_without_a_new_tag()
    {
        await using var connection = Db.OpenConnection();
        var existing = await InsertTagAsync(connection, "Work", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = new TagsRepository(connection);

        var ids = await repo.EnsureTagsAsync(TestLedger.Id, ["work"]);

        Assert.Equal(existing, ids["work"]);
        Assert.Equal(["Work"], await NamesAsync(connection));
    }

    [Fact]
    public async Task A_name_another_writer_creates_meanwhile_resolves_to_its_tag_whatever_the_spelling()
    {
        // Another transaction holds an uncommitted tag of the same key: the insert here
        // waits on the key index, the other commits, and the name resolves to that tag
        // rather than failing or making a second one.
        await using var connection = Db.OpenConnection();
        await using var other = Db.OpenConnection();
        await using var transaction = await other.BeginTransactionAsync();
        var theirs = Guid.NewGuid();
        await other.ExecuteAsync(
            "INSERT INTO tags (id, ledger_id, name) VALUES (@Id, @L, 'Work')",
            new { Id = theirs, L = TestLedger.Id }, transaction);

        var resolving = new TagsRepository(connection).EnsureTagsAsync(TestLedger.Id, ["WORK"]);
        await Task.Delay(500);
        Assert.False(resolving.IsCompleted, "the insert did not wait for the other writer's tag");
        await transaction.CommitAsync();

        Assert.Equal(theirs, (await resolving)["WORK"]);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM tags WHERE ledger_id = @L", new { L = TestLedger.Id }));
    }

    [Fact]
    public async Task Every_name_folding_vector_resolves_to_the_tag_whose_key_it_shares()
    {
        await using var connection = Db.OpenConnection();
        var repo = new TagsRepository(connection);
        using var vectors = JsonDocument.Parse(File.ReadAllText(Locate(VectorFile)));

        foreach (var vector in vectors.RootElement.GetProperty("nameFolding").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var key = vector.GetProperty("key").GetString()!;
            await ClearOwnTagsAsync(connection);
            // The key itself is already a spelling of the name: a tag stored that way
            // is the one the vector's input must find.
            var existing = await InsertTagAsync(connection, key, DateTimeOffset.UnixEpoch);

            var ids = await repo.EnsureTagsAsync(TestLedger.Id, [input]);

            var resolved = Assert.Single(ids).Value;
            Assert.True(existing == resolved, $"{vector.GetProperty("name").GetString()}: '{input}' did not resolve to '{key}'");
            Assert.Equal(1, await repo.CountTagsAsync());
        }
    }

    [Fact]
    public async Task Tags_in_another_ledger_are_never_matched()
    {
        await using var connection = Db.OpenConnection();
        var otherLedger = Guid.NewGuid();
        await connection.ExecuteAsync("INSERT INTO ledgers (id, name) VALUES (@Id, 'Other');", new { Id = otherLedger });
        await connection.ExecuteAsync(
            "INSERT INTO tags (id, ledger_id, name) VALUES (gen_random_uuid(), @L, 'work');", new { L = otherLedger });
        var repo = new TagsRepository(connection);

        var ids = await repo.EnsureTagsAsync(TestLedger.Id, ["Work"]);

        var ledgerOf = await connection.QuerySingleAsync<Guid>("SELECT ledger_id FROM tags WHERE id = @Id;", new { Id = ids["Work"] });
        Assert.Equal(TestLedger.Id, ledgerOf);
    }

    [Fact]
    public async Task LinkImported_links_headers_and_legs_and_one_name_in_two_cases_to_one_tag()
    {
        await using var connection = Db.OpenConnection();
        var (headerId, legId) = await SeedSampleHeaderAsync(connection);
        var repo = new TagsRepository(connection);

        await repo.LinkImportedAsync(TestLedger.Id,
        [
            new ImportTagSet(headerId, IsHeader: true, ["Groceries", "monthly"]),
            new ImportTagSet(legId, IsHeader: false, ["groceries"]),
        ]);

        Assert.Equal(["Groceries", "monthly"], await NamesAsync(connection));
        var headerTags = (await connection.QueryAsync<string>("""
            SELECT t.name FROM txn_header_tags x JOIN tags t ON t.id = x.tag_id WHERE x.header_id = @H ORDER BY t.name;
            """, new { H = headerId })).ToList();
        var legTags = (await connection.QueryAsync<string>("""
            SELECT t.name FROM txn_leg_tags x JOIN tags t ON t.id = x.tag_id WHERE x.leg_id = @L;
            """, new { L = legId })).ToList();
        Assert.Equal(["Groceries", "monthly"], headerTags);
        Assert.Equal(["Groceries"], legTags);
        await StoredNotesSeed.ComputeImportedNotesAsync(connection, TestLedger.Id);
    }

    /// <summary>Clear the tags this test wrote, between two cases of the same test: each
    /// case needs a ledger holding only its own tags.</summary>
    private static async Task ClearOwnTagsAsync(NpgsqlConnection connection) =>
        await connection.ExecuteAsync(
            "TRUNCATE accounts, txn_headers, txn_legs, tags, txn_header_tags, txn_leg_tags CASCADE;");

    private static async Task<Guid> InsertTagAsync(NpgsqlConnection connection, string name, DateTimeOffset createdAt, Guid? id = null)
    {
        var tagId = id ?? Guid.NewGuid();
        await connection.ExecuteAsync(
            "INSERT INTO tags (id, ledger_id, name, created_at) VALUES (@Id, @L, @Name, @At);",
            new { Id = tagId, L = TestLedger.Id, Name = name, At = createdAt.UtcDateTime });
        return tagId;
    }

    private static async Task<List<string>> NamesAsync(NpgsqlConnection connection) =>
        (await connection.QueryAsync<string>(
            "SELECT name FROM tags WHERE ledger_id = @L ORDER BY name;", new { L = TestLedger.Id })).ToList();

    private static string Locate(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new InvalidOperationException($"Could not locate {relative} from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Insert a minimal account + header + legs graph (one posting, two legs) and
    /// return the header id and the bank leg's id.
    /// </summary>
    private static async Task<(Guid HeaderId, Guid LegId)> SeedSampleHeaderAsync(NpgsqlConnection connection)
    {
        var accounts = new AccountsRepository(connection);
        var bank = await accounts.UpsertByExternalIdAsync(new AccountRow(
            Guid.NewGuid(), TestLedger.Id, null, "Bank", "bank", null, "USD", 0m, true, "tags-bank",
            IsSystem: false, HoldingsAccountId: null,
            Notes: null, AccountNumber: null,
            InstitutionName: null, RoutingNumber: null, AccountUrl: null));
        var cat = await accounts.UpsertByExternalIdAsync(new AccountRow(
            Guid.NewGuid(), TestLedger.Id, null, "Cat", "category", "expense", "USD", 0m, true, "tags-cat",
            IsSystem: false, HoldingsAccountId: null,
            Notes: null, AccountNumber: null,
            InstitutionName: null, RoutingNumber: null, AccountUrl: null));

        var posted = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var headerId = Guid.NewGuid();
        var header = new TxnHeaderRow(
            Id: headerId, LedgerId: TestLedger.Id,
            Origin: "manual", ExternalId: "tags-txn-1",
            Payee: "Grocery Store", Memo: null,
            PostedAt: posted, TransactedAt: posted,
            Status: "cleared", CheckNumber: null,
            IsPending: false, IsHidden: false,
            IsMergedInto: null, ImportSource: "test",
            ClearedAt: posted, ClearedByUserId: null,
            OnlineMatchFitid: null, OnlineMatchFiId: null,
            Action: null, OriginatingAccountId: bank);
        var origin = new TxnLegRow(
            Id: Guid.NewGuid(), HeaderId: headerId, LedgerId: TestLedger.Id, AccountId: bank,
            PostingIndex: 0, LegMemo: null, Amount: -10m,
            SecurityId: null, Quantity: null, UnitPrice: null);
        var counterpart = new TxnLegRow(
            Id: Guid.NewGuid(), HeaderId: headerId, LedgerId: TestLedger.Id, AccountId: cat,
            PostingIndex: 0, LegMemo: null, Amount: 10m,
            SecurityId: null, Quantity: null, UnitPrice: null);

        var repo = new TransactionsRepository(connection);
        await using var tx = await connection.BeginTransactionAsync();
        await repo.BulkUpsertAsync(new[] { header }, new[] { origin, counterpart });
        await tx.CommitAsync();
        return (headerId, origin.Id);
    }
}
