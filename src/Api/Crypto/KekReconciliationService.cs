using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;

namespace Coffer.Api.Crypto;

/// <summary>
/// Post-restore reconciliation (ADR-0092 D5). Enforces one invariant: <b>a
/// restore leaves no ciphertext this install cannot open.</b>
/// </summary>
/// <remarks>
/// <para><c>BackupService.RestoreAsync</c> is a wholesale <c>pg_restore</c>
/// (into a new database, swapped in) with no crypto reconciliation, so a cross-install restore
/// leaves the SOURCE install's KEK-wrapped material sitting under THIS install's
/// key. Without this pass nothing detects that: <c>ledgers.lek_kek_id</c> is written by
/// <see cref="KekRotationService"/> and the ledger repository and never read,
/// so the mismatch shows only when a background job trips over it — a scheduled
/// backup or feed sync failing long after the operator was told the restore
/// succeeded.</para>
///
/// <para>Two ways to satisfy the invariant. Adopt the source install's key (ADR-0092 D4,
/// the clean migration), or — when the operator doesn't have it — discard what
/// can't be opened and record the debt, which is this class. The cost is exactly
/// the re-establishable secrets the mismatch warning lists: feed tokens, the stored
/// backup passphrase, the Drive token, notification targets' URLs and tokens, and the OAuth
/// server keys (so AI connectors sign in again). Ledger data and passkeys are
/// unaffected, because neither is KEK-wrapped.</para>
///
/// <para><b>Detection is trial-decrypt, not the KEK fingerprint.</b>
/// <c>BackupCrypto.ReadKekFingerprintAsync</c> returns empty for v1 artifacts and
/// the restore pre-flight lets those through, so gating reconciliation on an
/// acknowledged mismatch would skip exactly the backups that can't be checked.
/// The fingerprint stays a pre-flight courtesy; opening the blob is the
/// authority.</para>
///
/// <para>A <c>--adopt-new-kek</c> run uses it too (<see cref="NewKekAdoption"/>), under
/// the key it just minted, which opens nothing the lost key sealed, so everything is
/// replaced or cleared.</para>
///
/// <para>One transaction, like rotation: a half-reconciled database would carry
/// some cleared secrets and some still-unopenable ones, which is the state this
/// exists to eliminate.</para>
/// </remarks>
public sealed class KekReconciliationService
{
    private readonly ServiceDbContextFactory _factory;
    private readonly LedgerKeyService _keys;
    private readonly ILogger<KekReconciliationService> _logger;
    private readonly TimeProvider _clock;

    public KekReconciliationService(
        ServiceDbContextFactory factory,
        LedgerKeyService keys,
        ILogger<KekReconciliationService> logger,
        TimeProvider clock)
    {
        _factory = factory;
        _keys = keys;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>What reconciliation had to abandon. All-zero means the install's
    /// key already opened everything — the common case, including every
    /// same-install restore.</summary>
    public sealed record ReconciliationResult(
        int LedgersRekeyed,
        int FeedConnectionsNeedingReauth,
        bool BackupPassphraseCleared,
        bool DriveDisconnected,
        int NotificationTargetsDisabled,
        bool OauthKeysDiscarded,
        bool UpgradeTokenDiscarded = false)
    {
        /// <summary>True when anything was abandoned, i.e. the restore crossed a
        /// KEK boundary.</summary>
        public bool AnythingChanged =>
            LedgersRekeyed > 0 || FeedConnectionsNeedingReauth > 0
            || BackupPassphraseCleared || DriveDisconnected
            || NotificationTargetsDisabled > 0 || OauthKeysDiscarded || UpgradeTokenDiscarded;
    }

    /// <summary>
    /// Trial-open every KEK-wrapped value; replace or clear whatever this
    /// install's key cannot open. Idempotent — a second run over a reconciled
    /// database finds nothing and changes nothing.
    /// </summary>
    public Task<ReconciliationResult> ReconcileAsync(CancellationToken ct = default) =>
        ReconcileAsync(beforeCommit: null, ct);

    /// <summary>
    /// <see cref="ReconcileAsync(CancellationToken)"/>, with <paramref name="beforeCommit"/>
    /// adding rows of its own to the same transaction (an adopt run's audit event), so they
    /// commit or roll back with the clearing they describe.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync(
        Action<AppDbContext, ReconciliationResult>? beforeCommit, CancellationToken ct = default)
    {
        // Replacing a ledger's key seals under the master key: held until the commit (FU-078).
        using var sealing = _keys.EnterSealing();
        await using var db = _factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var (ledgersRekeyed, feedsCleared) = await ReconcileLedgersAsync(db, ct).ConfigureAwait(false);
        var passphraseCleared = await ReconcileBackupPassphraseAsync(db, ct).ConfigureAwait(false);
        var driveDisconnected = await ReconcileDriveAsync(db, ct).ConfigureAwait(false);
        var targetsDisabled = await ReconcileNotificationTargetsAsync(db, ct).ConfigureAwait(false);
        var oauthKeysDiscarded = await ReconcileOauthKeysAsync(db, ct).ConfigureAwait(false);
        var upgradeTokenDiscarded = await ReconcileUpgradeTokenAsync(db, ct).ConfigureAwait(false);

        var result = new ReconciliationResult(
            ledgersRekeyed, feedsCleared, passphraseCleared, driveDisconnected,
            targetsDisabled, oauthKeysDiscarded, upgradeTokenDiscarded);
        beforeCommit?.Invoke(db, result);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (result.AnythingChanged)
            // Warning, not Information: the operator has re-establishment work to
            // do and the UI panels reflect it as state, but the log is where a
            // post-mortem starts.
            _logger.LogWarning(
                "KEK reconciliation: {Ledgers} ledger key(s) replaced, "
                + "{Feeds} feed connection(s) flagged needs_reauth, backup passphrase cleared={Pass}, "
                + "{Targets} notification target(s) disabled, "
                + "Drive disconnected={Drive}, OAuth server keys discarded={OAuth}, "
                + "upgrade repository token discarded={UpgradeToken}. These secrets were "
                + "sealed under a different master KEK and could not be carried over; ledger data and "
                + "passkeys are unaffected.",
                ledgersRekeyed, feedsCleared, passphraseCleared, targetsDisabled, driveDisconnected,
                oauthKeysDiscarded, upgradeTokenDiscarded);
        else
            _logger.LogInformation(
                "KEK reconciliation: everything opens under this install's master KEK.");

        return result;
    }

    /// <summary>
    /// Replace every <c>wrapped_lek</c> this install's KEK can't open with a fresh
    /// LEK, and flag the feed connections whose tokens the dead LEK sealed.
    /// </summary>
    /// <remarks>
    /// The new LEK is generated rather than the row left alone on purpose: an
    /// unopenable <c>wrapped_lek</c> would fail every future seal/open for that
    /// ledger, so the ledger would look fine until someone connected a feed. A
    /// fresh LEK leaves the ledger fully functional; only the secrets sealed under
    /// the OLD LEK are lost, and those are cleared here so nothing dangles.
    /// </remarks>
    private async Task<(int Rekeyed, int FeedsCleared)> ReconcileLedgersAsync(
        AppDbContext db, CancellationToken ct)
    {
        var ledgers = await db.Ledgers
            .Where(l => l.WrappedLek != null)
            .ToListAsync(ct).ConfigureAwait(false);

        var rekeyed = new List<Guid>();
        foreach (var ledger in ledgers)
        {
            if (CanOpen(ledger.WrappedLek!)) continue;

            ledger.WrappedLek = _keys.CreateWrappedLek();
            ledger.LekKekId = _keys.CurrentKekId;
            rekeyed.Add(ledger.Id);
        }

        if (rekeyed.Count == 0) return (0, 0);

        // ExecuteUpdateAsync rather than tracked writes: AccessUrlCiphertext is
        // init-only (the token was never meant to be mutated in place), and this
        // is a set-based clear. Runs inside the ambient transaction.
        var feedsCleared = await db.FeedConnections
            .Where(f => rekeyed.Contains(f.LedgerId) && f.AccessUrlCiphertext != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.AccessUrlCiphertext, _ => null)
                .SetProperty(f => f.Status, _ => "needs_reauth"), ct)
            .ConfigureAwait(false);

        return (rekeyed.Count, feedsCleared);
    }

    /// <summary>
    /// Clear an unopenable stored backup passphrase <b>and disable the schedule</b>.
    /// </summary>
    /// <remarks>
    /// Disabling matters as much as clearing: <c>BackupManager</c> throws
    /// "No backup passphrase is configured" on a null ciphertext, so an enabled
    /// schedule with no passphrase fails on every tick, forever, with nobody
    /// watching. Off is the honest state until an admin sets a new one.
    /// </remarks>
    private async Task<bool> ReconcileBackupPassphraseAsync(AppDbContext db, CancellationToken ct)
    {
        // Non-empty, the scope rotation uses (FU-095): with `!= null` alone an empty value,
        // which rotation leaves alone as nothing sealed, was "unopenable" here, so it was
        // cleared and reported lost.
        var jobs = await db.GlobalScheduledJobs
            .Where(j => j.PassphraseCiphertext != null && j.PassphraseCiphertext.Length > 0)
            .ToListAsync(ct).ConfigureAwait(false);

        var cleared = false;
        foreach (var job in jobs)
        {
            if (CanOpen(job.PassphraseCiphertext!)) continue;

            job.PassphraseCiphertext = null;
            job.Enabled = false;
            // The third author of `enabled = FALSE`, and the reason it is a text column
            // rather than an auto-disabled flag (mig 216): this is neither the operator
            // nor the five-strike rule, and a panel that said "the scheduler disabled
            // this" would be telling the reader to go looking for a failing job.
            job.DisabledReason = Scheduling.ScheduleDisableReasons.KeyMaterialMissing;
            job.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            cleared = true;
        }

        return cleared;
    }

    /// <summary>Clear an unopenable Drive OAuth blob and mark the sync
    /// disconnected, leaving the folder metadata so a reconnect is recognizable.</summary>
    private async Task<bool> ReconcileDriveAsync(AppDbContext db, CancellationToken ct)
    {
        // Non-empty, as rotation selects; see ReconcileBackupPassphraseAsync.
        var rows = await db.DriveSync
            .Where(d => d.OauthCiphertext != null && d.OauthCiphertext.Length > 0)
            .ToListAsync(ct).ConfigureAwait(false);

        var disconnected = false;
        foreach (var row in rows)
        {
            if (CanOpen(row.OauthCiphertext!)) continue;

            row.OauthCiphertext = null;
            row.Enabled = false;
            row.ConnectedEmail = null;
            row.LastSyncStatus = "error";
            row.LastSyncError =
                "Disconnected: the OAuth token was sealed under a different master KEK from this "
                + "install's. Reconnect Google Drive to resume syncing.";
            row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            disconnected = true;
        }

        return disconnected;
    }

    /// <summary>
    /// Disable any delivery target whose sealed config this install's KEK cannot open,
    /// recording why on the row.
    /// </summary>
    /// <remarks>
    /// Same treatment as the Drive token above, for the same reason: a target left
    /// enabled with unopenable ciphertext fails on every publish, forever, and the only
    /// trace is a per-target last_error on a page nobody opens until they wonder why it
    /// went quiet. Off with a stated reason is the honest state until an admin re-enters
    /// the URL.
    /// <para>
    /// The ciphertext is EMPTIED, and that detail is load-bearing. Keeping it, on the
    /// reasoning that an unopenable blob is not a secret this install can leak and that
    /// the row should still show what kind of target it was, is wrong although both
    /// halves of that are true: <c>KekRotationService</c>
    /// selects on <c>config_ciphertext</c> length with no <c>is_enabled</c> filter and
    /// calls <c>OpenOrThrow</c>, so a retained foreign blob aborts every later master-key
    /// rotation — permanently, and with an error telling the operator to do the very
    /// reconciliation they already did. Worse on the ledger-scope table, where the only
    /// delete route requires a grant on that ledger, so a deployment admin without one
    /// has no way to remove the row at all.
    /// </para>
    /// <para>
    /// The two sibling reconcilers above avoid this by setting their blob to
    /// <c>null</c>, which rotation's null guards then skip. These columns are BYTEA NOT
    /// NULL, so the equivalent is an empty array — which satisfies the constraint,
    /// keeps the row and its display name, and makes rotation's <c>Length &gt; 0</c>
    /// filter skip it. Same invariant as the siblings: after reconciliation the
    /// database holds no ciphertext this install cannot open.
    /// </para>
    /// <para>
    /// Both scopes. Reconciling the deployment's targets and leaving every ledger's own
    /// targets silently broken would be worse than doing neither, because the panel
    /// would look reconciled.
    /// </para>
    /// </remarks>
    private async Task<int> ReconcileNotificationTargetsAsync(
        AppDbContext db, CancellationToken ct)
    {
        const string Reason =
            "Disabled: this target's URL was sealed under a different master KEK from this "
            + "install's and could not be carried over. Re-enter it to resume delivery.";

        var disabled = 0;
        var now = _clock.GetUtcNow().UtcDateTime;

        // Not filtered on IsEnabled. A target an admin had already switched off still
        // carries a blob rotation will choke on, and this method is the only thing that
        // retires it. Filtering on IsEnabled would also skip exactly the rows a previous
        // run produced, so a re-restore onto a third install would leave the invariant
        // unmet. Rows whose ciphertext is already empty are skipped by the length guard,
        // which is what makes a second run a no-op.
        foreach (var row in await db.NotificationSubscribers
                     .Where(t => t.ConfigCiphertext.Length > 0)
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            if (CanOpen(row.ConfigCiphertext)) continue;

            row.ConfigCiphertext = [];
            row.IsEnabled = false;
            row.LastError = Reason;
            row.LastFailureAt = now;
            row.UpdatedAt = now;
            disabled++;
        }

        foreach (var row in await db.LedgerNotificationSubscribers
                     .Where(t => t.ConfigCiphertext.Length > 0)
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            if (CanOpen(row.ConfigCiphertext)) continue;

            row.ConfigCiphertext = [];
            row.IsEnabled = false;
            row.LastError = Reason;
            row.LastFailureAt = now;
            row.UpdatedAt = now;
            disabled++;
        }

        return disabled;
    }

    /// <summary>
    /// Delete the OAuth server's key pair (mig 253, FU-069) when this install's KEK cannot
    /// open it. The boot step after reconciliation finds no pair and mints one, so every
    /// MCP client re-authorizes; the tokens issued under the old pair could not be
    /// validated here anyway.
    /// </summary>
    /// <remarks>
    /// Deleted rather than replaced here: minting belongs to <c>OpenIddictKeyMaterial</c>,
    /// which already handles "no pair yet". Left in place, the pair would abort every later
    /// rotation the way a retained notification blob did (see below), and the boot load
    /// would refuse it as a key mix-up.
    /// </remarks>
    private async Task<bool> ReconcileOauthKeysAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.OidcServerKeys.ToListAsync(ct).ConfigureAwait(false);
        var discarded = false;
        foreach (var row in rows)
        {
            if (CanOpen(row.SigningKeyCiphertext) && CanOpen(row.EncryptionKeyCiphertext)) continue;
            db.OidcServerKeys.Remove(row);
            discarded = true;
        }
        return discarded;
    }

    /// <summary>
    /// Delete the private-fork release token (mig 269, ADR-0110) when this install's KEK cannot
    /// open it. The admin enters it again; left in place it would abort every later rotation.
    /// </summary>
    private async Task<bool> ReconcileUpgradeTokenAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.UpgradeRepoToken.ToListAsync(ct).ConfigureAwait(false);
        var discarded = false;
        foreach (var row in rows)
        {
            if (CanOpen(row.TokenCiphertext)) continue;
            db.UpgradeRepoToken.Remove(row);
            discarded = true;
        }
        return discarded;
    }

    /// <summary>
    /// True when this install's master KEK opens <paramref name="sealedBlob"/>.
    /// A failed AES-GCM tag check is the expected negative answer here, not an
    /// error — it is precisely the signal that the blob came from another install.
    /// </summary>
    private bool CanOpen(byte[] sealedBlob)
    {
        try
        {
            _keys.OpenWithMasterKey(sealedBlob);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
