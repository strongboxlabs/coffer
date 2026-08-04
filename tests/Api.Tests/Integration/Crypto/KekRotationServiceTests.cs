using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// Master-KEK rotation service (ADR-0026 §rotation). Rotation is a GLOBAL
/// re-wrap of every ledger's LEK + the backup passphrase, so these tests use
/// the same key <b>bytes</b> with different <b>ids</b> (v1 → v2): re-wrapping
/// under identical bytes can't corrupt the other tests' ledgers in the shared
/// DB (everything still opens under the fixture KEK), while still exercising the
/// full path — enumerate → open under old → re-seal under new → bump
/// lek_kek_id → commit. The different-key correctness (a blob sealed under A
/// won't open under B) is covered by <see cref="Unit.Crypto.LedgerKeyServiceTests"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KekRotationServiceTests
{
    private readonly PostgresFixture _fixture;

    public KekRotationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    // ApiFactory pins the test KEK to 32 zero bytes; match it so the existing
    // ledgers stay openable through the rotation.
    private static byte[] FixtureKek => new byte[32];

    private KekRotationService NewService() =>
        new(_fixture.NewServiceFactory(), NullLogger<KekRotationService>.Instance);

    private async Task<Guid> InsertLedgerWithLekAsync(MasterKey wrapUnder, string kekId)
    {
        var id = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        db.Ledgers.Add(new LedgerRow
        {
            Id = id,
            Name = $"rotate-test-{id:N}",
            CreatedAt = DateTime.UtcNow,
            WrappedLek = new LedgerKeyService(wrapUnder).CreateWrappedLek(),
            LekKekId = kekId,
            LekCreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Rewraps_ledger_lek_and_bumps_kek_id()
    {
        var oldKey = new MasterKey(FixtureKek, "v1");
        var newKey = new MasterKey(FixtureKek, "v2-test");
        var ledgerId = await InsertLedgerWithLekAsync(oldKey, "v1");

        byte[] before;
        await using (var pre = _fixture.NewDbContext())
            before = (await pre.Ledgers.AsNoTracking().FirstAsync(l => l.Id == ledgerId)).WrappedLek!;

        var result = await NewService().RotateAsync(oldKey, newKey, dryRun: false);
        Assert.True(result.LedgersRotated >= 1);
        Assert.False(result.DryRun);

        await using var read = _fixture.NewDbContext();
        var row = await read.Ledgers.AsNoTracking().FirstAsync(l => l.Id == ledgerId);
        Assert.Equal("v2-test", row.LekKekId);
        // Re-wrapped: fresh nonce → different ciphertext, but still a valid 32-byte LEK.
        Assert.False(row.WrappedLek!.AsSpan().SequenceEqual(before));
        Assert.Equal(32, new LedgerKeyService(newKey).OpenWithMasterKey(row.WrappedLek!).Length);
    }

    [Fact]
    public async Task DryRun_writes_nothing()
    {
        var oldKey = new MasterKey(FixtureKek, "v1");
        var newKey = new MasterKey(FixtureKek, "v2-test");
        var ledgerId = await InsertLedgerWithLekAsync(oldKey, "v1");

        var result = await NewService().RotateAsync(oldKey, newKey, dryRun: true);
        Assert.True(result.DryRun);

        await using var read = _fixture.NewDbContext();
        var row = await read.Ledgers.AsNoTracking().FirstAsync(l => l.Id == ledgerId);
        Assert.Equal("v1", row.LekKekId);   // untouched
    }

    [Fact]
    public async Task Rotates_the_backup_passphrase()
    {
        var oldKey = new MasterKey(FixtureKek, "v1");
        var newKey = new MasterKey(FixtureKek, "v2-test");

        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM global_scheduled_jobs;");
            db.GlobalScheduledJobs.Add(new GlobalScheduledJobRow
            {
                JobType = GlobalJobTypes.Backup,
                PassphraseCiphertext = new LedgerKeyService(oldKey)
                    .SealWithMasterKey(Encoding.UTF8.GetBytes("drill-pass")),
            });
            await db.SaveChangesAsync();
        }

        var result = await NewService().RotateAsync(oldKey, newKey, dryRun: false);
        Assert.True(result.PassphraseRotated);

        await using var read = _fixture.NewDbContext();
        var row = await read.GlobalScheduledJobs.AsNoTracking()
            .FirstAsync(j => j.JobType == GlobalJobTypes.Backup);
        var pt = new LedgerKeyService(newKey).OpenWithMasterKey(row.PassphraseCiphertext!);
        Assert.Equal("drill-pass", Encoding.UTF8.GetString(pt));
    }

    [Fact]
    public async Task Rotates_the_drive_oauth_token()
    {
        var oldKey = new MasterKey(FixtureKek, "v1");
        var newKey = new MasterKey(FixtureKek, "v2-test");
        var actor = (await SyntheticLedger.CreateAsync(_fixture)).UserId;

        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM drive_sync;");
        }
        var repo = new Coffer.Api.Db.Repositories.DriveSyncRepository(_fixture.NewServiceFactory());
        await repo.ConnectAsync(
            new LedgerKeyService(oldKey).SealWithMasterKey(Encoding.UTF8.GetBytes("drive-token")),
            "folder-1", "Coffer Backups", "u@e.com", actor, DateTime.UtcNow);

        var result = await NewService().RotateAsync(oldKey, newKey, dryRun: false);
        Assert.True(result.DriveTokenRotated);

        var rewrapped = await repo.GetOauthCiphertextAsync();
        var pt = new LedgerKeyService(newKey).OpenWithMasterKey(rewrapped!);
        Assert.Equal("drive-token", Encoding.UTF8.GetString(pt));
    }
}
