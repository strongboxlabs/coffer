using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// A rotation that actually commits (ADR-0092 D4) — the file swap, the archive, the
/// database re-wrap, and the rollback, end to end.
/// </summary>
/// <remarks>
/// <para>Rotation targets a key with the SAME bytes as the fixture's under a
/// DIFFERENT id. The database therefore ends up re-wrapped under identical bytes,
/// so every other test in the shared collection still opens its ledgers, while the
/// file swap, archive, and rollback all run for real. Same trick and same reason as
/// <see cref="KekRotationServiceTests"/>; different-bytes correctness (a blob sealed
/// under A won't open under B) is covered by
/// <c>Unit.Crypto.LedgerKeyServiceTests</c>.</para>
///
/// <para>Each test uses its OWN <see cref="MasterKeyStore"/> on a temp path, never
/// the process-wide fixture key file — rotating that would change the id every
/// later host build resolves.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MasterKeyRotationCoordinatorTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _dir = Directory.CreateTempSubdirectory("coffer-rotate").FullName;

    public MasterKeyRotationCoordinatorTests(PostgresFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>32 zero bytes — what ApiFactory pins, and what every other test's
    /// wrapped material is sealed under.</summary>
    private static byte[] FixtureBytes => new byte[32];
    private static string FixtureBase64 => Convert.ToBase64String(FixtureBytes);

    private MasterKeyStore NewStore()
    {
        var store = new MasterKeyStore(Path.Combine(_dir, $"{Guid.NewGuid():N}.key"));
        store.Write(FixtureBase64, "v1");
        return store;
    }

    private MasterKeyRotationCoordinator NewCoordinator() => new(
        new KekRotationService(
            _fixture.NewServiceFactory(), NullLogger<KekRotationService>.Instance),
        NullLogger<MasterKeyRotationCoordinator>.Instance);

    private async Task<Guid> InsertLedgerAsync(MasterKey wrapUnder, string kekId)
    {
        var id = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        db.Ledgers.Add(new LedgerRow
        {
            Id = id,
            Name = $"rotate-coord-{id:N}",
            CreatedAt = DateTime.UtcNow,
            WrappedLek = new LedgerKeyService(wrapUnder).CreateWrappedLek(),
            LekKekId = kekId,
            LekCreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Commits_the_rewrap_and_leaves_the_new_key_and_id_in_the_file()
    {
        var newId = $"v2-{Guid.NewGuid():N}"[..12];
        var ledgerId = await InsertLedgerAsync(new MasterKey(FixtureBytes, "v1"), "v1");
        var store = NewStore();

        var outcome = await NewCoordinator().RotateAsync(
            currentKey: new MasterKey(FixtureBytes, "v1"),
            newKey: new MasterKey(FixtureBytes, newId),
            newKeyBase64: FixtureBase64,
            store: store);

        Assert.Equal(MasterKeyRotationCoordinator.Refusal.None, outcome.Refusal);
        Assert.NotNull(outcome.Result);
        Assert.True(outcome.Result!.LedgersRotated >= 1);

        // The file carries the new key AND its id — the pairing is the whole reason
        // the id lives on disk rather than in the environment.
        var (fileKey, fileId) = store.Read();
        Assert.Equal(FixtureBase64, fileKey);
        Assert.Equal(newId, fileId);

        // The database agrees with the file.
        await using var db = _fixture.NewDbContext();
        var row = await db.Ledgers.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
        Assert.Equal(newId, row.LekKekId);
        Assert.Equal(32, new LedgerKeyService(new MasterKey(FixtureBytes, newId))
            .OpenWithMasterKey(row.WrappedLek!).Length);
    }

    [Fact]
    public async Task Archives_the_previous_key_rather_than_clobbering_it()
    {
        var store = NewStore();

        var outcome = await NewCoordinator().RotateAsync(
            new MasterKey(FixtureBytes, "v1"),
            new MasterKey(FixtureBytes, "v2-archive"),
            FixtureBase64,
            store);

        Assert.Equal(MasterKeyRotationCoordinator.Refusal.None, outcome.Refusal);
        Assert.NotNull(outcome.PreviousKeyArchivedAt);
        Assert.True(File.Exists(outcome.PreviousKeyArchivedAt!));
        // The archive is the pre-rotation pairing, which is what makes a mistaken
        // rotation reversible.
        Assert.Contains("id=v1", await File.ReadAllTextAsync(outcome.PreviousKeyArchivedAt!));
    }

    [Fact]
    public async Task Refuses_and_writes_nothing_when_something_does_not_open_under_the_current_key()
    {
        // A pre-existing mismatch — a cross-KEK restore that skipped reconciliation,
        // say. Rotation must refuse BEFORE the key file moves, or the operator
        // discovers the problem only after the key has changed.
        var foreign = new byte[32];
        for (var i = 0; i < foreign.Length; i++) foreign[i] = (byte)(i + 31);
        var strandedId = await InsertLedgerAsync(new MasterKey(foreign, "other"), "other");
        var store = NewStore();

        try
        {
            var outcome = await NewCoordinator().RotateAsync(
                new MasterKey(FixtureBytes, "v1"),
                new MasterKey(FixtureBytes, "v2-blocked"),
                FixtureBase64,
                store);

            Assert.Equal(MasterKeyRotationCoordinator.Refusal.Blocked, outcome.Refusal);
            Assert.Null(outcome.Result);
            Assert.Null(outcome.PreviousKeyArchivedAt);

            // Untouched: still the original key AND the original id.
            var (key, id) = store.Read();
            Assert.Equal(FixtureBase64, key);
            Assert.Equal("v1", id);
        }
        finally
        {
            // MUST clean up: a ledger that doesn't open under the fixture key aborts
            // any later rotation or preview in this shared database. Learned the hard
            // way — an unopenable drive_sync row from a neighbouring test did exactly
            // that to the preview endpoint test.
            await using var db = _fixture.NewDbContext();
            await db.Ledgers.Where(l => l.Id == strandedId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Refuses_when_the_key_file_is_not_writable()
    {
        // The documented read-only injection case: /run/secrets/…, a projected
        // Kubernetes Secret. Modelled as a path whose parent is a FILE, so any
        // create/move under it fails the same way a read-only mount would.
        var blocker = Path.Combine(_dir, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        var store = new MasterKeyStore(Path.Combine(blocker, "master.key"));

        var outcome = await NewCoordinator().RotateAsync(
            new MasterKey(FixtureBytes, "v1"),
            new MasterKey(FixtureBytes, "v2-readonly"),
            FixtureBase64,
            store);

        Assert.Equal(MasterKeyRotationCoordinator.Refusal.KeyFileNotWritable, outcome.Refusal);
        Assert.Contains("not writable", outcome.Message);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task Leaves_the_key_file_in_place_when_the_write_fails_after_the_archive()
    {
        // Archive can succeed and Write still fail — a read-only directory permits the
        // move out on some platforms, and a disk-full hits the write, not the rename.
        // Without putting the archive back, "nothing was changed" is a lie and the
        // install is left with NO key file, which D3 then refuses to boot over its own
        // wrapped material.
        var store = NewStore();
        var original = store.ReadRaw();

        // Make Write fail while leaving Archive able to move the file out: the archive
        // path is a sibling (fine), but the destination becomes a directory, so
        // creating master.key.tmp under it is impossible.
        var blocked = new MasterKeyStore(Path.Combine(_dir, "blocked", "master.key"));
        Directory.CreateDirectory(blocked.Path);      // the KEY PATH itself is a directory
        await File.WriteAllTextAsync(Path.Combine(_dir, "blocked", "seed"), "x");

        var outcome = await NewCoordinator().RotateAsync(
            new MasterKey(FixtureBytes, "v1"),
            new MasterKey(FixtureBytes, "v2-writefail"),
            FixtureBase64,
            blocked);

        Assert.Equal(MasterKeyRotationCoordinator.Refusal.KeyFileNotWritable, outcome.Refusal);
        Assert.Null(outcome.Result);
        // The untouched store is still intact — this rotation never reached it.
        Assert.Equal(original, store.ReadRaw());
    }

    [Fact]
    public async Task Rolls_the_key_file_back_when_the_rewrap_fails()
    {
        // The failure that would otherwise be unrecoverable: file already swapped,
        // database not. A coordinator whose re-wrap throws must put the old key back,
        // or the install is left holding a key that opens nothing.
        var store = NewStore();
        var coordinator = new MasterKeyRotationCoordinator(
            new ThrowingRotationService(),
            NullLogger<MasterKeyRotationCoordinator>.Instance);

        var outcome = await coordinator.RotateAsync(
            new MasterKey(FixtureBytes, "v1"),
            new MasterKey(FixtureBytes, "v2-rollback"),
            Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()),
            store);

        Assert.Equal(MasterKeyRotationCoordinator.Refusal.RolledBack, outcome.Refusal);

        // Rolled back to the ORIGINAL key and id, not left on the new one.
        var (key, id) = store.Read();
        Assert.Equal(FixtureBase64, key);
        Assert.Equal("v1", id);
    }

    /// <summary>
    /// Passes the dry run, throws on the real re-wrap. The only way to reach the
    /// coordinator's rollback branch: <c>KekRotationService</c>'s transaction either
    /// commits or throws on its own terms, so no real input drives it there — and
    /// that branch is what keeps a failed rotation from leaving an install holding a
    /// key which opens nothing.
    /// </summary>
    private sealed class ThrowingRotationService : IKekRotationService
    {
        public Task<RotationResult> RotateAsync(
            MasterKey oldKey, MasterKey newKey, bool dryRun, CancellationToken ct = default)
            => dryRun
                ? Task.FromResult(new RotationResult(0, false, false, true))
                : throw new InvalidOperationException("simulated re-wrap failure");
    }
}
