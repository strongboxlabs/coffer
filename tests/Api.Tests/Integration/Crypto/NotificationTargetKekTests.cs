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
public sealed class NotificationTargetKekTests : DatabaseTest
{
    public NotificationTargetKekTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

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
        await using var db = Db.NewServiceDbContext();

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
        await using var db = Db.NewServiceDbContext();
        return await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    /// <summary>
    /// Rotation carries a target's sealed URL across, re-wrapping it in place.
    /// </summary>
    /// <remarks>
    /// Rotates to the same key BYTES under a different id, the convention of the two
    /// sibling rotation classes: everything stays openable under the fixture key every
    /// test host runs with.
    ///
    /// What that costs: "the blob no longer opens under the old key" cannot be asserted
    /// under identical bytes. It is not lost — Unit.Crypto.LedgerKeyServiceTests covers
    /// different-bytes correctness directly. What
    /// this test uniquely proves is that rotation REACHES this row at all, which is what
    /// the ciphertext-changed assertion below pins.
    /// </remarks>
    [Fact]
    public async Task Rotating_the_master_key_keeps_a_targets_url_openable()
    {
        var id = await SeedTargetAsync(LocalKey);
        var before = (await ReadAsync(id)).ConfigCiphertext;
        var newKey = new MasterKey(new byte[32], "v2");

        var service = new KekRotationService(
            Db.NewServiceFactory(), NullLogger<KekRotationService>.Instance);
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
            Db.NewServiceFactory(),
            new LedgerKeyService(LocalKey),
            NullLogger<KekReconciliationService>.Instance, TimeProvider.System);
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
        // The pair to the test above: an unopenable blob left in place would
        // permanently wedge rotation. Reconcile, then rotate.
        await SeedTargetAsync(ForeignKey);

        await new KekReconciliationService(
            Db.NewServiceFactory(),
            new LedgerKeyService(LocalKey),
            NullLogger<KekReconciliationService>.Instance, TimeProvider.System).ReconcileAsync();

        var rotation = new KekRotationService(
            Db.NewServiceFactory(), NullLogger<KekRotationService>.Instance);

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
            Db.NewServiceFactory(),
            new LedgerKeyService(LocalKey),
            NullLogger<KekReconciliationService>.Instance, TimeProvider.System);
        var result = await service.ReconcileAsync();

        Assert.Equal(0, result.NotificationTargetsDisabled);

        var row = await ReadAsync(id);
        Assert.True(row.IsEnabled);
        Assert.Null(row.LastError);
    }
}
