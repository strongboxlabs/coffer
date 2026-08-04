using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// Filesystem persistence + retention for stored backup artifacts (ADR-0060).
/// Pure filesystem — a fake writer delegate stands in for the pg_dump→encrypt
/// pipeline, so these run without postgresql-client. Each test owns a unique
/// temp directory it cleans up.
/// </summary>
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _dir;

    public BackupStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "coffer-backup-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private BackupStore NewStore() => new(_dir, NullLogger<BackupStore>.Instance);

    // Retention is now passed per-create (ADR-0074: the policy lives in
    // backup_settings, resolved by BackupManager and handed to CreateAsync).
    private static readonly RetentionPolicy Policy =
        new(DailyDays: 7, WeeklyWeeks: 8, MonthlyMonths: 12);

    private static Func<Stream, CancellationToken, Task> Writes(byte[] bytes) =>
        async (stream, ct) => await stream.WriteAsync(bytes, ct);

    [Fact]
    public async Task Create_then_List_round_trips_metadata()
    {
        var store = NewStore();
        var payload = Encoding.UTF8.GetBytes("COFFERBAK-fake-encrypted-bytes");

        var info = await store.CreateAsync(Writes(payload), Policy);

        Assert.Equal(payload.Length, info.SizeBytes);
        Assert.StartsWith("coffer-", info.Id);
        var list = store.List();
        var only = Assert.Single(list);
        Assert.Equal(info.Id, only.Id);
        Assert.Equal(payload.Length, only.SizeBytes);
    }

    [Fact]
    public async Task OpenRead_returns_the_stored_bytes()
    {
        var store = NewStore();
        var payload = Encoding.UTF8.GetBytes("the-cipher-text");
        var info = await store.CreateAsync(Writes(payload), Policy);

        await using var stream = store.OpenRead(info.Id);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task Delete_removes_the_artifact()
    {
        var store = NewStore();
        var info = await store.CreateAsync(Writes([1, 2, 3]), Policy);

        Assert.True(store.Delete(info.Id));
        Assert.Empty(store.List());
        Assert.False(store.Exists(info.Id));
    }

    [Fact]
    public async Task Recent_backups_are_all_kept()
    {
        var store = NewStore();
        for (var i = 0; i < 4; i++)
            await store.CreateAsync(Writes([(byte)i]), Policy);

        // All four are created "now" → inside the daily tier → none pruned.
        // (The tiered GFS bucketing itself is unit-tested in BackupRetentionTests.)
        Assert.Equal(4, store.List().Count);
    }

    [Fact]
    public async Task A_failed_write_leaves_no_partial_artifact()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync((_, _) => throw new InvalidOperationException("boom"), Policy));

        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("..\\secret")]
    [InlineData("coffer-bad")]
    [InlineData("")]
    [InlineData("coffer-20260623T031500000Z-ZZZZZZZZ")]  // non-hex suffix
    [InlineData("coffer-20260623T031500Z-0a1b2c3d")]      // old second-grained stamp
    public void OpenRead_rejects_malformed_or_traversal_ids(string id)
    {
        var store = NewStore();
        Assert.Null(store.OpenRead(id));
        Assert.False(store.Delete(id));
        Assert.False(store.Exists(id));
    }

    [Fact]
    public void OpenRead_returns_null_for_a_well_formed_but_absent_id()
    {
        var store = NewStore();
        Assert.Null(store.OpenRead("coffer-20260623T031500000Z-0a1b2c3d"));
    }
}
