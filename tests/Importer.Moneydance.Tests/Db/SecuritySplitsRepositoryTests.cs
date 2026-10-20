using Dapper;
using Coffer.Importer.Moneydance.Db;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Integration tests for <see cref="SecuritySplitsRepository"/>. Stock-split
/// rows imported from Moneydance <c>csplit</c> objects upsert keyed on
/// <c>(ledger_id, external_id)</c> so re-imports stay idempotent.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class SecuritySplitsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public SecuritySplitsRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BulkUpsert_inserts_new_rows()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var securityA = Guid.NewGuid();
        var securityB = Guid.NewGuid();
        await InsertSecurityAsync(conn, ledgerId, securityA, "AAA");
        await InsertSecurityAsync(conn, ledgerId, securityB, "BBB");

        var repo = new SecuritySplitsRepository(conn);
        var written = await repo.BulkUpsertAsync(new[]
        {
            new SecuritySplitRow(
                Id: Guid.NewGuid(), LedgerId: ledgerId, SecurityId: securityA,
                SplitAt: new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.Zero),
                Ratio: 2.0m, OldShares: 1m, NewShares: 2m, ExternalId: "ext-aaa"),
            new SecuritySplitRow(
                Id: Guid.NewGuid(), LedgerId: ledgerId, SecurityId: securityB,
                SplitAt: new DateTimeOffset(2024, 6, 7, 0, 0, 0, TimeSpan.Zero),
                Ratio: 10.0m, OldShares: 1m, NewShares: 10m, ExternalId: "ext-bbb"),
        });

        Assert.Equal(2, written);
        Assert.Equal(2, await repo.CountAsync());
    }

    [Fact]
    public async Task BulkUpsert_updates_existing_row_when_external_id_matches()
    {
        // MD re-export of the same csplit with an adjusted ratio (operator
        // corrected the entry) should refresh in place, not duplicate.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId   = TestLedger.Id;
        var securityId = Guid.NewGuid();
        await InsertSecurityAsync(conn, ledgerId, securityId, "IDXC");

        var repo = new SecuritySplitsRepository(conn);
        var initialId = Guid.NewGuid();
        await repo.BulkUpsertAsync(new[]
        {
            new SecuritySplitRow(
                Id: initialId, LedgerId: ledgerId, SecurityId: securityId,
                SplitAt: new DateTimeOffset(2024, 6, 7, 0, 0, 0, TimeSpan.Zero),
                Ratio: 2.0m, OldShares: 1m, NewShares: 2m, ExternalId: "csplit-xyz"),
        });

        // Same external_id, different ratio + a different proposed id.
        await repo.BulkUpsertAsync(new[]
        {
            new SecuritySplitRow(
                Id: Guid.NewGuid(), LedgerId: ledgerId, SecurityId: securityId,
                SplitAt: new DateTimeOffset(2024, 6, 7, 0, 0, 0, TimeSpan.Zero),
                Ratio: 3.0m, OldShares: 1m, NewShares: 3m, ExternalId: "csplit-xyz"),
        });

        Assert.Equal(1, await repo.CountAsync());

        var (id, ratio, newShares) = await conn.QueryFirstAsync<(Guid id, decimal ratio, decimal newShares)>(@"
            SELECT id, ratio, new_shares FROM security_splits WHERE external_id = 'csplit-xyz';");
        Assert.Equal(initialId, id);    // original id preserved
        Assert.Equal(3.0m, ratio);
        Assert.Equal(3m, newShares);
    }

    [Fact]
    public async Task BulkUpsert_inserts_separate_rows_when_external_id_is_null()
    {
        // User-entered splits (no MD source) all have NULL external_id;
        // the partial unique index doesn't apply, so they don't collide.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId   = TestLedger.Id;
        var securityId = Guid.NewGuid();
        await InsertSecurityAsync(conn, ledgerId, securityId, "IDXC");

        var repo = new SecuritySplitsRepository(conn);
        await repo.BulkUpsertAsync(new[]
        {
            new SecuritySplitRow(
                Id: Guid.NewGuid(), LedgerId: ledgerId, SecurityId: securityId,
                SplitAt: new DateTimeOffset(2024, 6, 7, 0, 0, 0, TimeSpan.Zero),
                Ratio: 2.0m, OldShares: null, NewShares: null, ExternalId: null),
            new SecuritySplitRow(
                Id: Guid.NewGuid(), LedgerId: ledgerId, SecurityId: securityId,
                SplitAt: new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
                Ratio: 1.5m, OldShares: null, NewShares: null, ExternalId: null),
        });

        Assert.Equal(2, await repo.CountAsync());
    }

    [Fact]
    public async Task BulkUpsert_returns_zero_for_empty_input()
    {
        await using var conn = _fixture.OpenConnection();
        var repo = new SecuritySplitsRepository(conn);
        var written = await repo.BulkUpsertAsync(Array.Empty<SecuritySplitRow>());
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task Ratio_must_be_positive()
    {
        // Migration 060 CHECK (ratio > 0). A zero or negative ratio is
        // never a valid split.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId   = TestLedger.Id;
        var securityId = Guid.NewGuid();
        await InsertSecurityAsync(conn, ledgerId, securityId, "IDXC");

        var repo = new SecuritySplitsRepository(conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => repo.BulkUpsertAsync(new[]
        {
            new SecuritySplitRow(
                Id: Guid.NewGuid(), LedgerId: ledgerId, SecurityId: securityId,
                SplitAt: new DateTimeOffset(2024, 6, 7, 0, 0, 0, TimeSpan.Zero),
                Ratio: 0m, OldShares: null, NewShares: null, ExternalId: null),
        }));
        Assert.Equal("23514", ex.SqlState);    // check_violation
    }

    private static async Task InsertSecurityAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid securityId, string ticker)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO securities (id, ledger_id, ticker, name, asset_class,
                is_active, share_decimals)
            VALUES (@Id, @LedgerId, @Ticker, @Name, 'equity', true, 4);",
            new { Id = securityId, LedgerId = ledgerId, Ticker = ticker,
                  Name = ticker + " Test Security" });
    }

    private static async Task ResetAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(@"
            TRUNCATE security_splits, lots, holdings, txn_legs, txn_headers,
                     securities, accounts RESTART IDENTITY CASCADE;");
    }
}
