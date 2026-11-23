using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

using Coffer.Api.Auth;
using Coffer.Api.Configuration;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// A <c>--adopt-new-kek</c> run (ADR-0092 D3) clears what the lost key sealed, audits it in
/// the same transaction, and saves the new key only then (<see cref="NewKekAdoption"/>).
/// </summary>
/// <remarks>
/// The lost key is the fixture key (<see cref="LostKeySeed"/>). The new key is random, so
/// nothing seeded opens under it, which is the state an adopt run meets.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NewKekAdoptionTests : DatabaseTest
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"coffer-adopt-{Guid.NewGuid():N}");

    public NewKekAdoptionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    protected override Task OnDisposingAsync()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    private static (string Base64, MasterKey Key) NewKey(string id)
    {
        var base64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (base64, MasterKeyLoader.LoadFromValueOrThrow(base64, id, "test"));
    }

    private MasterKeyStore StoreAt(string name) => new(Path.Combine(_dir, name, "master.key"));

    private KekReconciliationService ReconciliationUnder(MasterKey key) => new(
        Db.NewServiceFactory(), new LedgerKeyService(key),
        NullLogger<KekReconciliationService>.Instance, TimeProvider.System);

    /// <summary>An adoption as the adopt run makes it: under the mint lock, with the OAuth
    /// keys stored under the new key as its first secret.</summary>
    private Task<KekReconciliationService.ReconciliationResult> AdoptAsync(
        MasterKey key, MasterKeyStore store, string base64, string id) =>
        AdoptWithAsync(ReconciliationUnder(key), key, store, base64, id);

    private async Task<KekReconciliationService.ReconciliationResult> AdoptWithAsync(
        KekReconciliationService reconciliation, MasterKey key, MasterKeyStore store, string base64, string id,
        Func<MasterKeyMintLock, CancellationToken, Task<bool>>? sealFirstSecret = null)
    {
        using var mintLock = MasterKeyMintLock.Acquire(Db.ServiceConnectionString, TimeSpan.FromSeconds(30));
        return await NewKekAdoption.CompleteAsync(
            reconciliation, store, base64, id, TimeProvider.System, mintLock,
            ct => sealFirstSecret is null ? OauthKeysLoadUnder(key) : sealFirstSecret(mintLock, ct));
    }

    /// <summary>End the session holding <paramref name="mintLock"/>, as a dropped connection
    /// would.</summary>
    private async Task KillLockSessionAsync(MasterKeyMintLock mintLock)
    {
        await using var connection = new NpgsqlConnection(Db.ServiceConnectionString);
        await connection.OpenAsync();
        await using var kill = new NpgsqlCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_locks "
            + "WHERE locktype = 'advisory' AND granted AND classid = @hi AND objid = @lo AND objsubid = 1",
            connection);
        kill.Parameters.AddWithValue("hi", NpgsqlTypes.NpgsqlDbType.Oid, (uint)(mintLock.Key >> 32));
        kill.Parameters.AddWithValue("lo", NpgsqlTypes.NpgsqlDbType.Oid, (uint)(mintLock.Key & 0xFFFFFFFF));
        Assert.Equal(1L, await kill.ExecuteScalarAsync());
        Assert.False(mintLock.IsStillHeld());
    }

    private KekRotationService Rotation() =>
        new(Db.NewServiceFactory(), NullLogger<KekRotationService>.Instance);

    private async Task<bool> OauthKeysLoadUnder(MasterKey key)
    {
        var legacy = Path.Combine(_dir, $"legacy-{Guid.NewGuid():N}");
        return await new OpenIddictKeyMaterial(
                Db.NewServiceFactory(), TimeProvider.System, NullLogger<OpenIddictKeyMaterial>.Instance)
            .LoadAsync(new LedgerKeyService(key), legacy);
    }

    [Fact]
    public async Task Without_the_clearing_a_rotation_after_a_new_key_aborts()
    {
        // The defect the adoption fixes, and the anchor for the next test's rotation: a key
        // minted over sealed material leaves blobs nothing opens, and rotation refuses them.
        await LostKeySeed.SeedAsync(Db);
        var (_, current) = NewKey("v2");

        await Assert.ThrowsAsync<KekRotationException>(
            () => Rotation().RotateAsync(current, NewKey("v3").Key, dryRun: false));
        Assert.False(await OauthKeysLoadUnder(current));
    }

    [Fact]
    public async Task Adopting_clears_every_kind_audits_it_then_saves_the_key_so_rotation_and_the_oauth_load_succeed()
    {
        await LostKeySeed.SeedAsync(Db);
        var (base64, current) = NewKey("v2");
        var store = StoreAt("ok");

        var result = await AdoptAsync(current, store, base64, "v2");

        Assert.Equal(new KekReconciliationService.ReconciliationResult(
            LedgersRekeyed: 1, FeedConnectionsNeedingReauth: 1, BackupPassphraseCleared: true,
            DriveDisconnected: true, NotificationTargetsDisabled: 2, OauthKeysDiscarded: true,
            UpgradeTokenDiscarded: true), result);
        Assert.Equal((base64, "v2"), store.Read());
        Assert.True(await LostKeySeed.EverythingOpensUnderAsync(Db, new LedgerKeyService(current)));
        var described = NewKekAdoption.Describe(result);
        Assert.Contains("notification targets disabled:   2", described, StringComparison.Ordinal);
        Assert.Contains("OAuth server keys discarded:     yes", described, StringComparison.Ordinal);
        Assert.Contains("upgrade repository token:        yes", described, StringComparison.Ordinal);

        // The audit row the flag's recovery promises: who cannot be known at boot, what was
        // abandoned can, and never the key itself.
        await using (var db = Db.NewDbContext())
        {
            var audit = await db.AdminAuditEvents.AsNoTracking()
                .SingleAsync(e => e.Action == AdminAuditActions.MasterKeyAdoptedNew);
            Assert.Null(audit.ActorUserId);
            Assert.Equal(NewKekAdoption.AuditDetail("v2", result), audit.Detail);
            Assert.Contains("1 ledger key(s) replaced", audit.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(base64, audit.Detail, StringComparison.Ordinal);
        }

        // The start after the adopt run: the OAuth load finds no pair and makes one, and a
        // rotation then re-wraps everything, the new pair included.
        Assert.True(await OauthKeysLoadUnder(current));
        var next = NewKey("v3").Key;
        var rotated = await Rotation().RotateAsync(current, next, dryRun: false);
        Assert.Equal(1, rotated.LedgersRotated);
        Assert.True(rotated.OauthKeysRotated);
        Assert.True(await OauthKeysLoadUnder(next));
    }

    [Fact]
    public async Task A_clearing_that_cannot_reach_the_database_changes_nothing_and_saves_no_key()
    {
        await LostKeySeed.SeedAsync(Db);
        var before = await LostKeySeed.SealedStateAsync(Db);
        var (base64, current) = NewKey("v2");
        var store = StoreAt("unreachable");
        var unreachable = "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;Password=nobody;"
            + "Timeout=1;Command Timeout=1";
        var broken = new KekReconciliationService(
            PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
            {
                ConnectionString = unreachable,
                ServiceConnectionString = unreachable,
            })),
            new LedgerKeyService(current), NullLogger<KekReconciliationService>.Instance, TimeProvider.System);

        var ex = await Assert.ThrowsAsync<NewKekAdoptionException>(
            () => AdoptWithAsync(broken, current, store, base64, "v2"));

        Assert.Contains("No key was saved", ex.Message, StringComparison.Ordinal);
        Assert.False(store.Exists());
        Assert.Equal(before, await LostKeySeed.SealedStateAsync(Db));
    }

    [Fact]
    public async Task A_failure_after_writes_inside_the_clearing_rolls_all_of_them_back_and_saves_no_key()
    {
        // The clearing writes before it saves: the feed tokens of a replaced ledger key are
        // cleared in the database at once, and the audit row is saved last. Failing that save
        // fails the run after the feed write, so the rollback is what is tested.
        await LostKeySeed.SeedAsync(Db);
        var before = await LostKeySeed.SealedStateAsync(Db);
        var (base64, current) = NewKey("v2");
        var store = StoreAt("rolled-back");
        var refusing = new KekReconciliationService(
            Db.NewServiceFactoryWith(new RefuseAuditSave()), new LedgerKeyService(current),
            NullLogger<KekReconciliationService>.Instance, TimeProvider.System);

        var ex = await Assert.ThrowsAsync<NewKekAdoptionException>(
            () => AdoptWithAsync(refusing, current, store, base64, "v2"));

        Assert.Contains(RefuseAuditSave.Message, ex.Message, StringComparison.Ordinal);
        Assert.Contains("No key was saved", ex.Message, StringComparison.Ordinal);
        Assert.False(store.Exists());
        Assert.Equal(before, await LostKeySeed.SealedStateAsync(Db));

        // The same run again, without the fault: it completes.
        var retry = await AdoptAsync(current, store, base64, "v2");
        Assert.Equal(1, retry.FeedConnectionsNeedingReauth);
        Assert.True(store.Exists());
    }

    /// <summary>Fails any save that would add an admin audit event: the adoption's last
    /// write, made after the clearing has already written.</summary>
    private sealed class RefuseAuditSave : SaveChangesInterceptor
    {
        public const string Message = "injected after the clearing wrote";

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AdminAuditEventRow>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException(Message);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_mint_lock_lost_before_the_clearing_stops_the_run_before_anything_changes()
    {
        // The lock sits on an idle connection through the migrations. Lost there, another
        // start may have minted and sealed its OAuth keys, which the clearing would delete.
        await LostKeySeed.SeedAsync(Db);
        var before = await LostKeySeed.SealedStateAsync(Db);
        var (base64, current) = NewKey("v2");
        var store = StoreAt("lost-before");
        using var mintLock = MasterKeyMintLock.Acquire(Db.ServiceConnectionString, TimeSpan.FromSeconds(30));
        await KillLockSessionAsync(mintLock);

        var ex = await Assert.ThrowsAsync<NewKekAdoptionException>(() => NewKekAdoption.CompleteAsync(
            ReconciliationUnder(current), store, base64, "v2", TimeProvider.System, mintLock,
            _ => OauthKeysLoadUnder(current)));

        Assert.Contains("before anything changed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("another start has saved one", ex.Message, StringComparison.Ordinal);
        Assert.False(store.Exists());
        Assert.Equal(before, await LostKeySeed.SealedStateAsync(Db));
    }

    [Fact]
    public async Task A_mint_lock_lost_after_the_clearing_stops_the_key_write_and_a_repeat_completes()
    {
        await LostKeySeed.SeedAsync(Db);
        var (base64, current) = NewKey("v2");
        var store = StoreAt("lost-after");

        var ex = await Assert.ThrowsAsync<NewKekAdoptionException>(() => AdoptWithAsync(
            ReconciliationUnder(current), current, store, base64, "v2",
            async (mintLock, _) =>
            {
                var sealedIt = await OauthKeysLoadUnder(current);
                await KillLockSessionAsync(mintLock);
                return sealedIt;
            }));

        Assert.Contains("was NOT saved", ex.Message, StringComparison.Ordinal);
        Assert.False(store.Exists());

        await AdoptAsync(current, store, base64, "v2");
        Assert.True(store.Exists());
        Assert.True(await LostKeySeed.EverythingOpensUnderAsync(Db, new LedgerKeyService(current)));
    }

    [Fact]
    public async Task An_oauth_store_that_fails_stops_the_run_before_the_key_is_saved()
    {
        // The new key's first secret. Saved over nothing sealed, the key would leave a start
        // on another key volume to read the install as virgin once the lock is released.
        await LostKeySeed.SeedAsync(Db, withLedger: false);
        var (base64, current) = NewKey("v2");
        var store = StoreAt("oauth-failed");

        var ex = await Assert.ThrowsAsync<NewKekAdoptionException>(() => AdoptWithAsync(
            ReconciliationUnder(current), current, store, base64, "v2", (_, _) => Task.FromResult(false)));

        Assert.Contains("OAuth server keys could not be stored", ex.Message, StringComparison.Ordinal);
        Assert.False(store.Exists());
    }

    [Fact]
    public async Task A_key_that_cannot_be_saved_leaves_ledger_keys_that_refuse_a_start_and_a_repeated_run_clears_without_loss()
    {
        await LostKeySeed.SeedAsync(Db);
        var (base64, unsaved) = NewKey("v2");
        var unwritable = UnwritableStore();

        var ex = await Assert.ThrowsAsync<NewKekAdoptionException>(
            () => AdoptAsync(unsaved, unwritable, base64, "v2"));
        Assert.Contains("after clearing", ex.Message, StringComparison.Ordinal);

        // What is left is the ledger key and the OAuth pair that run sealed under a key nobody
        // has: a start without a key refuses over them rather than minting.
        Assert.True(WrappedMaterialProbeTests.Probe(Db.ServiceConnectionString));
        await using (var db = Db.NewDbContext())
            Assert.Equal(1, await db.Ledgers.CountAsync(l => l.WrappedLek != null));

        // The operator runs it again: a new key, the run's own ledger key replaced, and the
        // secrets already cleared stay cleared, so nothing more is lost.
        var (base64Again, current) = NewKey("v3");
        var store = StoreAt("again");
        var again = await AdoptAsync(current, store, base64Again, "v3");

        Assert.Equal(new KekReconciliationService.ReconciliationResult(
            LedgersRekeyed: 1, FeedConnectionsNeedingReauth: 0, BackupPassphraseCleared: false,
            DriveDisconnected: false, NotificationTargetsDisabled: 0, OauthKeysDiscarded: true), again);
        Assert.Equal((base64Again, "v3"), store.Read());
        await Rotation().RotateAsync(current, NewKey("v4").Key, dryRun: false);
    }

    [Fact]
    public async Task Without_ledgers_a_key_that_cannot_be_saved_still_leaves_the_oauth_pair_so_a_start_refuses()
    {
        // The run stored its OAuth keys before it tried to save the key, so even an install
        // without ledgers is left with something sealed: the next start without a key refuses
        // instead of reading it as virgin, and a repeated adopt run replaces the pair.
        await LostKeySeed.SeedAsync(Db, withLedger: false);
        var (base64, unsaved) = NewKey("v2");

        await Assert.ThrowsAsync<NewKekAdoptionException>(
            () => AdoptAsync(unsaved, UnwritableStore(), base64, "v2"));

        Assert.True(WrappedMaterialProbeTests.Probe(Db.ServiceConnectionString));
        await using var db = Db.NewDbContext();
        Assert.Equal(1, await db.OidcServerKeys.CountAsync());
    }

    /// <summary>A store whose directory is a file, so the key write cannot create it.</summary>
    private MasterKeyStore UnwritableStore()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, $"not-a-directory-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        return new MasterKeyStore(Path.Combine(blocker, "master.key"));
    }
}
