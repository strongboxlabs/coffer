using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// A notification target's sealed URL is carried across a key rotation, and honestly
/// abandoned when a restore crosses a KEK boundary.
/// </summary>
/// <remarks>
/// <para>
/// <c>notification_subscribers.config_ciphertext</c> is sealed under the master KEK
/// exactly like the backup passphrase and the Drive token, and it was omitted from both
/// KEK paths when it was added (ADR-0096 D8 introduced the column; migs 207 and 208
/// created the tables). The consequences differ and both are bad:
/// </para>
/// <para>
/// ROTATION left every target holding ciphertext nothing could open. The documented
/// rotation procedure therefore silently broke delivery — the subsystem failing in the
/// one way it exists to prevent, with the only trace a per-target <c>last_error</c> on
/// a settings page nobody visits until they wonder why it went quiet.
/// </para>
/// <para>
/// RESTORE from another install left the same rows enabled and unopenable, so every
/// publish failed forever. The Drive token's treatment is the right precedent: turn it
/// off and say why.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NotificationTargetKekTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public NotificationTargetKekTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Rotation reads both delivery-target tables; this class has to control both.
    /// Full rationale on <see cref="NotificationTargetReset"/>.
    /// </summary>
    /// <remarks>
    /// Clearing only notification_subscribers (which SeedTargetAsync used to do) left
    /// ledger_notification_subscribers holding whatever the endpoint tests deposited —
    /// including a deliberately unopenable 3-byte row — which aborts a rotation before
    /// its first assert.
    /// </remarks>
    public Task InitializeAsync() => NotificationTargetReset.ClearAsync(_fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The key the app runs under — ApiFactory pins 32 zero bytes.</summary>
    private static MasterKey LocalKey => new(new byte[32], "v1");

    /// <summary>Another install's key. Nothing sealed under this may survive a restore.</summary>
    private static MasterKey ForeignKey
    {
        get
        {
            var bytes = new byte[32];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 7);
            return new MasterKey(bytes, "source-v1");
        }
    }

    private const string Url = "https://hc.invalid/the-token-is-in-the-path";

    private async Task<Guid> SeedTargetAsync(MasterKey sealUnder, string key = "healthchecks")
    {
        await using var db = _fixture.NewServiceDbContext();

        var row = new NotificationSubscriberRow
        {
            SubscriberKey = key,
            DisplayName = key,
            Monitors = key == "healthchecks" ? NotificationMonitors.Backup : null,
            ConfigCiphertext = new LedgerKeyService(sealUnder).SealWithMasterKey(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SubscriberConfig(Url, null)))),
        };
        db.NotificationSubscribers.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<NotificationSubscriberRow> ReadAsync(Guid id)
    {
        await using var db = _fixture.NewServiceDbContext();
        return await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    /// <summary>
    /// Rotation carries a target's sealed URL across, re-wrapping it in place.
    /// </summary>
    /// <remarks>
    /// Rotates to the same key BYTES under a different id, which is the convention the
    /// two sibling rotation classes document and for the same reason: RotateAsync is a
    /// global, committing re-wrap of every ledger LEK, the backup passphrase and the
    /// Drive token, and PostgresFixture is one database shared by the whole collection
    /// with no per-class reset. An earlier version of this test rotated to 32 bytes of
    /// 0x09 and committed, which would have sealed every other test's material under a
    /// key none of them holds. It passed only because an unrelated poison row made the
    /// rotation throw before the commit — so fixing that row would have turned a green
    /// suite red in four other classes, for reasons pointing nowhere near here.
    ///
    /// What that costs: "the blob no longer opens under the old key" cannot be asserted
    /// under identical bytes. It is not lost — Unit.Crypto.LedgerKeyServiceTests covers
    /// different-bytes correctness directly, with no shared database to corrupt. What
    /// this test uniquely proves is that rotation REACHES this row at all, which is what
    /// the branch changed and what the ciphertext-changed assertion below pins.
    /// </remarks>
    [Fact]
    public async Task Rotating_the_master_key_keeps_a_targets_url_openable()
    {
        var id = await SeedTargetAsync(LocalKey);
        var before = (await ReadAsync(id)).ConfigCiphertext;
        var newKey = new MasterKey(new byte[32], "v2");

        var service = new KekRotationService(
            _fixture.NewServiceFactory(), NullLogger<KekRotationService>.Instance);
        var result = await service.RotateAsync(LocalKey, newKey, dryRun: false);

        Assert.Equal(1, result.NotificationTargetsRotated);

        // The point of the whole exercise: the URL still opens, under the NEW key.
        var row = await ReadAsync(id);
        var opened = JsonSerializer.Deserialize<SubscriberConfig>(
            Encoding.UTF8.GetString(new LedgerKeyService(newKey).OpenWithMasterKey(row.ConfigCiphertext)));
        Assert.Equal(Url, opened!.Url);

        // ...and it was genuinely re-sealed rather than left alone. Identical plaintext
        // under identical bytes still yields different ciphertext, because SealWithMasterKey
        // draws a fresh nonce — so this distinguishes "rotation touched this row" from
        // "rotation skipped it and the assert above passed on the original blob", which
        // is the only failure mode the same-bytes trade-off leaves open.
        Assert.NotEqual(before, row.ConfigCiphertext);
    }

    [Fact]
    public async Task A_restore_across_a_kek_boundary_disables_the_target_and_says_why()
    {
        // Sealed by another install: this is what a cross-KEK restore leaves behind.
        var id = await SeedTargetAsync(ForeignKey);

        var service = new KekReconciliationService(
            _fixture.NewServiceFactory(),
            new LedgerKeyService(LocalKey),
            NullLogger<KekReconciliationService>.Instance);
        var result = await service.ReconcileAsync();

        Assert.Equal(1, result.NotificationTargetsDisabled);
        Assert.True(result.AnythingChanged);

        var row = await ReadAsync(id);

        // Off, with a stated reason. Left enabled it would fail on every publish
        // forever, which is indistinguishable from working until someone looks.
        Assert.False(row.IsEnabled);
        Assert.NotNull(row.LastError);
        Assert.Contains("different", row.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(row.LastFailureAt);

        // And the foreign blob is GONE. Retaining it would leave a row that rotation
        // still selects (it filters on length, not on is_enabled) and still cannot open,
        // so every later master-key rotation would abort — telling the operator to
        // perform the reconciliation that produced the row. The row survives; the
        // ciphertext does not.
        Assert.Empty(row.ConfigCiphertext);
    }

    [Fact]
    public async Task A_reconciled_target_no_longer_blocks_a_later_rotation()
    {
        // The regression this pairs with: reconciliation used to leave the unopenable
        // blob in place, which permanently wedged rotation. Reconcile, then rotate.
        await SeedTargetAsync(ForeignKey);

        await new KekReconciliationService(
            _fixture.NewServiceFactory(),
            new LedgerKeyService(LocalKey),
            NullLogger<KekReconciliationService>.Instance).ReconcileAsync();

        var rotation = new KekRotationService(
            _fixture.NewServiceFactory(), NullLogger<KekRotationService>.Instance);

        // Same bytes, different id — see the rotation test above for why that matters.
        var result = await rotation.RotateAsync(
            LocalKey, new MasterKey(new byte[32], "v2"), dryRun: false);

        // It completed at all, which is the assertion. The retired row is not counted:
        // there is nothing left to re-wrap.
        Assert.Equal(0, result.NotificationTargetsRotated);
    }

    [Fact]
    public async Task Reconciliation_leaves_a_target_this_install_can_open_alone()
    {
        // The negative half: reconciliation runs after EVERY restore, including one
        // that never crossed a key boundary. Disabling a working target then would
        // turn a routine restore into an outage.
        var id = await SeedTargetAsync(LocalKey);

        var service = new KekReconciliationService(
            _fixture.NewServiceFactory(),
            new LedgerKeyService(LocalKey),
            NullLogger<KekReconciliationService>.Instance);
        var result = await service.ReconcileAsync();

        Assert.Equal(0, result.NotificationTargetsDisabled);

        var row = await ReadAsync(id);
        Assert.True(row.IsEnabled);
        Assert.Null(row.LastError);
    }
}
