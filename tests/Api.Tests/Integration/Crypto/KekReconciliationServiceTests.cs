using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// Post-restore reconciliation (ADR-0092 D5). The invariant under test is that a
/// restore leaves no ciphertext this install cannot open — so every assertion here
/// is either "the foreign blob is gone" or "the local blob was left alone".
/// </summary>
/// <remarks>
/// "Foreign" material is sealed under a DIFFERENT key from the fixture's (32 zero
/// bytes), which is what a cross-install restore produces. Unlike
/// <see cref="KekRotationServiceTests"/> — which deliberately reuses the fixture
/// bytes so it can't disturb other tests' ledgers in the shared database — these
/// tests must use genuinely different key material, because trial-decrypt failure
/// IS the behaviour. That's safe because reconciliation only ever touches rows it
/// cannot open, and every other test's rows are wrapped under the fixture key.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class KekReconciliationServiceTests
{
    private readonly PostgresFixture _fixture;

    public KekReconciliationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The key the app runs under — ApiFactory pins 32 zero bytes.</summary>
    private static MasterKey LocalKey => new(new byte[32], "v1");

    /// <summary>Another install's key. Anything sealed under this must not survive.</summary>
    private static MasterKey ForeignKey
    {
        get
        {
            var bytes = new byte[32];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 7);
            return new MasterKey(bytes, "source-v1");
        }
    }

    private KekReconciliationService NewService() => new(
        _fixture.NewServiceFactory(),
        new LedgerKeyService(LocalKey),
        NullLogger<KekReconciliationService>.Instance);

    /// <summary>
    /// Set the backup schedule's sealed passphrase, creating the row if absent.
    /// No migration seeds <c>global_scheduled_jobs</c> — the row appears when an
    /// admin first configures backups — so a test that assumes it exists fails on
    /// a database where nothing has configured one.
    /// </summary>
    private async Task SetBackupPassphraseAsync(MasterKey sealUnder, string passphrase)
    {
        await using var db = _fixture.NewDbContext();
        var job = await db.GlobalScheduledJobs
            .SingleOrDefaultAsync(j => j.JobType == GlobalJobTypes.Backup);
        if (job is null)
        {
            job = new GlobalScheduledJobRow
            {
                JobType = GlobalJobTypes.Backup,
                HourLocal = 3,
                MinuteLocal = 0,
                CreatedAt = DateTime.UtcNow,
            };
            db.GlobalScheduledJobs.Add(job);
        }
        job.PassphraseCiphertext = new LedgerKeyService(sealUnder)
            .SealWithMasterKey(Encoding.UTF8.GetBytes(passphrase));
        job.Enabled = true;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> InsertLedgerAsync(MasterKey wrapUnder, string kekId)
    {
        var id = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        db.Ledgers.Add(new LedgerRow
        {
            Id = id,
            Name = $"reconcile-{id:N}",
            CreatedAt = DateTime.UtcNow,
            WrappedLek = new LedgerKeyService(wrapUnder).CreateWrappedLek(),
            LekKekId = kekId,
            LekCreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // --- ledgers -----------------------------------------------------------

    [Fact]
    public async Task Replaces_a_wrapped_lek_that_does_not_open_and_stamps_the_local_kek_id()
    {
        var ledgerId = await InsertLedgerAsync(ForeignKey, "source-v1");

        var result = await NewService().ReconcileAsync();

        Assert.True(result.LedgersRekeyed >= 1);
        await using var db = _fixture.NewDbContext();
        var row = await db.Ledgers.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
        Assert.Equal("v1", row.LekKekId);
        // The point of minting rather than nulling: the ledger stays fully
        // functional, so future seals/opens work instead of throwing forever.
        Assert.Equal(32, new LedgerKeyService(LocalKey).OpenWithMasterKey(row.WrappedLek!).Length);
    }

    [Fact]
    public async Task Leaves_a_wrapped_lek_that_already_opens_untouched()
    {
        var ledgerId = await InsertLedgerAsync(LocalKey, "v1");
        byte[] before;
        await using (var db = _fixture.NewDbContext())
            before = (await db.Ledgers.AsNoTracking().SingleAsync(l => l.Id == ledgerId)).WrappedLek!;

        await NewService().ReconcileAsync();

        await using var after = _fixture.NewDbContext();
        var row = await after.Ledgers.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
        Assert.Equal(before, row.WrappedLek);
    }

    [Fact]
    public async Task Flags_feed_connections_of_a_rekeyed_ledger_and_clears_their_token()
    {
        // The token was sealed under the DEAD LEK, so it is unreadable the moment
        // that LEK is replaced. Leaving it in place would look like a working
        // connection until the next sync failed.
        var ledgerId = await InsertLedgerAsync(ForeignKey, "source-v1");
        var connectionId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            db.FeedConnections.Add(new FeedConnectionRow
            {
                Id = connectionId,
                LedgerId = ledgerId,
                Provider = "simplefin",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                AccessUrlCiphertext = new LedgerKeyService(ForeignKey)
                    .SealWithMasterKey(Encoding.UTF8.GetBytes("https://example.invalid/access")),
            });
            await db.SaveChangesAsync();
        }

        var result = await NewService().ReconcileAsync();

        Assert.True(result.FeedConnectionsNeedingReauth >= 1);
        await using var check = _fixture.NewDbContext();
        var conn = await check.FeedConnections.AsNoTracking().SingleAsync(f => f.Id == connectionId);
        Assert.Null(conn.AccessUrlCiphertext);
        Assert.Equal("needs_reauth", conn.Status);
    }

    // --- backup passphrase -------------------------------------------------

    [Fact]
    public async Task Clears_an_unopenable_backup_passphrase_and_disables_the_schedule()
    {
        // Disabling is the load-bearing half: BackupManager throws "No backup
        // passphrase is configured" on a null ciphertext, so an enabled schedule
        // with no passphrase would fail on every tick forever, unwatched.
        await SetBackupPassphraseAsync(ForeignKey, "source-install-passphrase");

        var result = await NewService().ReconcileAsync();

        Assert.True(result.BackupPassphraseCleared);
        await using var check = _fixture.NewDbContext();
        var after = await check.GlobalScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.JobType == GlobalJobTypes.Backup);
        Assert.Null(after.PassphraseCiphertext);
        Assert.False(after.Enabled);
    }

    [Fact]
    public async Task Leaves_a_readable_backup_passphrase_and_its_schedule_alone()
    {
        await SetBackupPassphraseAsync(LocalKey, "local-passphrase");

        var result = await NewService().ReconcileAsync();

        Assert.False(result.BackupPassphraseCleared);
        await using var check = _fixture.NewDbContext();
        var after = await check.GlobalScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.JobType == GlobalJobTypes.Backup);
        Assert.NotNull(after.PassphraseCiphertext);
        Assert.True(after.Enabled);
        Assert.Equal("local-passphrase", Encoding.UTF8.GetString(
            new LedgerKeyService(LocalKey).OpenWithMasterKey(after.PassphraseCiphertext!)));
    }

    // --- idempotence -------------------------------------------------------

    [Fact]
    public async Task Second_run_over_a_reconciled_database_changes_nothing()
    {
        await InsertLedgerAsync(ForeignKey, "source-v1");
        var svc = NewService();

        var first = await svc.ReconcileAsync();
        var second = await svc.ReconcileAsync();

        Assert.True(first.AnythingChanged);
        Assert.False(second.AnythingChanged);
    }

    [Fact]
    public void A_foreign_blob_fails_with_something_CanOpen_actually_catches()
    {
        // Load-bearing for the whole class. A foreign blob raises
        // AuthenticationTagMismatchException, NOT the CryptographicException the
        // name suggests — and CanOpen catches the latter. If that inheritance ever
        // stopped holding, reconciliation would abort on the first foreign blob:
        // exactly the rows it exists to clean up. So assert the assignability
        // directly rather than the concrete type.
        var localKeys = new LedgerKeyService(LocalKey);
        var foreignBlob = new LedgerKeyService(ForeignKey)
            .SealWithMasterKey(Encoding.UTF8.GetBytes("x"));

        var ex = Assert.ThrowsAny<Exception>(() => localKeys.OpenWithMasterKey(foreignBlob));

        Assert.IsAssignableFrom<CryptographicException>(ex);
    }
}
