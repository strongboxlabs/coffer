using Dapper;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Tests.Db;

[Collection(DbCollection.Name)]
public sealed class TagsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public TagsRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureTag_inserts_a_new_tag_and_is_idempotent()
    {
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE tags CASCADE;");

        var repo = new TagsRepository(connection);
        var firstId = await repo.EnsureTagAsync(TestLedger.Id, "vacation");
        var secondId = await repo.EnsureTagAsync(TestLedger.Id, "vacation");

        Assert.Equal(firstId, secondId);
        Assert.Equal(1, await repo.CountTagsAsync());
    }

    [Fact]
    public async Task SetTagsForHeader_attaches_the_supplied_set_and_replaces_on_re_run()
    {
        await using var connection = _fixture.OpenConnection();
        await ResetAsync(connection);

        var headerId = await SeedSampleHeaderAsync(connection);
        var tagsRepo = new TagsRepository(connection);

        var groceries = await tagsRepo.EnsureTagAsync(TestLedger.Id, "groceries");
        var monthly   = await tagsRepo.EnsureTagAsync(TestLedger.Id, "monthly");
        var travel    = await tagsRepo.EnsureTagAsync(TestLedger.Id, "travel");

        await tagsRepo.SetTagsForHeaderAsync(TestLedger.Id, headerId, new[] { groceries, monthly });
        Assert.Equal(2, await CountHeaderTagsAsync(connection, headerId));

        // Re-run with a different set: the old links go away.
        await tagsRepo.SetTagsForHeaderAsync(TestLedger.Id, headerId, new[] { travel });
        var current = (await connection.QueryAsync<Guid>(
            "SELECT tag_id FROM txn_header_tags WHERE header_id = @headerId;",
            new { headerId })).ToArray();
        Assert.Single(current);
        Assert.Equal(travel, current[0]);
    }

    [Fact]
    public async Task SetTagsForHeader_with_empty_set_clears_links()
    {
        await using var connection = _fixture.OpenConnection();
        await ResetAsync(connection);

        var headerId = await SeedSampleHeaderAsync(connection);
        var tagsRepo = new TagsRepository(connection);
        var tag = await tagsRepo.EnsureTagAsync(TestLedger.Id, "temp");
        await tagsRepo.SetTagsForHeaderAsync(TestLedger.Id, headerId, new[] { tag });
        Assert.Equal(1, await CountHeaderTagsAsync(connection, headerId));

        await tagsRepo.SetTagsForHeaderAsync(TestLedger.Id, headerId, []);
        Assert.Equal(0, await CountHeaderTagsAsync(connection, headerId));
    }

    private static async Task ResetAsync(Npgsql.NpgsqlConnection connection)
    {
        await connection.ExecuteAsync(
            "TRUNCATE accounts, txn_headers, txn_legs, tags, txn_header_tags CASCADE;");
    }

    private static async Task<int> CountHeaderTagsAsync(Npgsql.NpgsqlConnection connection, Guid headerId)
    {
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_header_tags WHERE header_id = @headerId;",
            new { headerId });
    }

    /// <summary>
    /// Insert a minimal account + header + legs graph (one posting, two
    /// legs) and return the header id. Tags attach to the header.
    /// </summary>
    private static async Task<Guid> SeedSampleHeaderAsync(Npgsql.NpgsqlConnection connection)
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
            Payee: "Whole Foods", Memo: null,
            PostedAt: posted, TransactedAt: posted,
            Status: "cleared", CheckNumber: null,
            IsPending: false, IsHidden: false,
            IsMergedInto: null, ImportSource: "test",
            ClearedAt: posted, ClearedByUserId: null,
            OnlineMatchFitid: null, OnlineMatchFiId: null,
            Action: null);
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
        return headerId;
    }
}
