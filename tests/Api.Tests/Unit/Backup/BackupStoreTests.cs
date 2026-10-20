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

    private const long TestPartSize = 1024 * 1024;

    private BackupStore NewStore() =>
        new(_dir, NullLogger<BackupStore>.Instance, TestPartSize, TimeProvider.System);

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
        Assert.Empty(Directory.EnumerateFiles(_dir));   // not even a .partial
    }

    [Fact]
    public async Task An_artifact_is_not_listed_while_it_is_being_written()
    {
        var store = NewStore();
        IReadOnlyList<BackupFileInfo>? listedMidWrite = null;
        string[]? filesMidWrite = null;

        await store.CreateAsync(async (stream, ct) =>
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            await stream.FlushAsync(ct);
            listedMidWrite = store.List();
            filesMidWrite = Directory.GetFiles(_dir);
        }, Policy);

        Assert.Empty(listedMidWrite!);
        Assert.Single(filesMidWrite!);
        Assert.EndsWith(".cofferbak.partial", filesMidWrite![0]);
        Assert.Single(store.List());                       // renamed once complete
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.partial"));
    }

    [Fact]
    public async Task Verification_reads_back_exactly_what_was_written()
    {
        var store = NewStore();
        var payload = Encoding.UTF8.GetBytes("COFFERBAK-bytes-to-verify");
        byte[]? seen = null;

        await store.CreateAsync(Writes(payload), Policy, verify: async (stream, ct) =>
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            seen = ms.ToArray();
        });

        Assert.Equal(payload, seen);
    }

    [Fact]
    public async Task A_backup_that_fails_verification_is_deleted_and_never_listed()
    {
        var store = NewStore();
        var ex = await Assert.ThrowsAsync<BackupException>(() => store.CreateAsync(
            Writes([1, 2, 3]), Policy,
            verify: (_, _) => throw new BackupException("no data entry for public.accounts")));

        Assert.Contains("public.accounts", ex.Message);
        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task A_failed_verification_does_not_prune_the_good_backups_before_it()
    {
        var store = NewStore();
        var good = await store.CreateAsync(Writes([1]), Policy);
        // Retention that would delete everything old: a failed create must not run it.
        await Assert.ThrowsAsync<BackupException>(() => store.CreateAsync(
            Writes([2]), new RetentionPolicy(1, 0, 0),
            verify: (_, _) => throw new BackupException("bad")));

        Assert.Equal(good.Id, Assert.Single(store.List()).Id);
    }

    [Fact]
    public async Task Unfinished_writes_are_removed_at_startup_and_finished_ones_are_left()
    {
        var store = NewStore();
        var kept = await store.CreateAsync(Writes([1, 2, 3]), Policy);
        var stray = Path.Combine(_dir, "coffer-20260101T000000000Z-deadbeef.cofferbak.partial");
        await File.WriteAllBytesAsync(stray, [9, 9]);

        store.DeletePartials();

        Assert.False(File.Exists(stray));
        Assert.Equal(kept.Id, Assert.Single(store.List()).Id);
    }

    [Fact]
    public void A_file_that_vanished_between_being_listed_and_being_read_is_skipped_not_an_error()
    {
        // FU-067: List() read each file's size after enumerating the folder, so a delete or a
        // retention pass in another request between the two threw out of the listing, and out
        // of the retention pass that follows every create.
        var gone = Path.Combine(_dir, "coffer-20260101T000000000Z-deadbeef.cofferbak");

        Assert.Null(BackupStore.TryDescribe(gone));
    }

    [Fact]
    public async Task A_file_that_is_there_is_described()
    {
        var store = NewStore();
        var info = await store.CreateAsync(Writes([1, 2, 3]), Policy);

        var described = BackupStore.TryDescribe(Path.Combine(_dir, info.Id + ".cofferbak"));

        Assert.Equal(info, described);
    }

    private sealed class PinsThatThrowWhenRead : IReadOnlySet<string>
    {
        // Count > 0 so retention reads the set; reading it fails the way a vanished or
        // unreadable file would partway through pruning.
        public int Count => 1;
        public IEnumerator<string> GetEnumerator() => throw new IOException("the disk went away");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Contains(string item) => throw new IOException("the disk went away");
        public bool IsProperSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<string> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<string> other) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_pruning_failure_after_a_good_write_does_not_fail_the_backup()
    {
        // FU-067: a retention pass that threw after the artifact was written, verified and
        // listed reported the create as FAILED, announced a good backup as failed, and counted
        // toward the scheduler's five-failure auto-disable. The next create prunes again.
        var store = NewStore();

        var info = await store.CreateAsync(Writes([1, 2, 3]), Policy, new PinsThatThrowWhenRead());

        Assert.True(store.Exists(info.Id));
        Assert.Equal(info.Id, Assert.Single(store.List()).Id);
    }

    [Fact]
    public async Task Retention_of_zero_keeps_the_backup_just_written()
    {
        var store = NewStore();
        var info = await store.CreateAsync(Writes([1, 2, 3]), new RetentionPolicy(0, 0, 0));

        Assert.Equal(info.Id, Assert.Single(store.List()).Id);
        Assert.True(store.Exists(info.Id));
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
