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
/// <para><c>BackupService.RestoreAsync(clean: true)</c> is a wholesale
/// <c>pg_restore</c> with no crypto reconciliation, so a cross-install restore
/// leaves the SOURCE install's KEK-wrapped material sitting under THIS install's
/// key. Nothing detected that: <c>ledgers.lek_kek_id</c> was written by
/// <see cref="KekRotationService"/> and the ledger repository but never read, so
/// the mismatch surfaced only when a background job tripped over it — a scheduled
/// backup or feed sync failing long after the operator was told the restore
/// succeeded.</para>
///
/// <para>Two ways to satisfy the invariant. Adopt the source install's key (D4,
/// the clean migration), or — when the operator doesn't have it — discard what
/// can't be opened and record the debt, which is this class. The cost is exactly
/// the three re-establishable secrets the mismatch acknowledgement already
/// promises: feed tokens, the stored backup passphrase, the Drive token. Ledger
/// data and passkeys are unaffected, because neither is KEK-wrapped.</para>
///
/// <para><b>Detection is trial-decrypt, not the KEK fingerprint.</b>
/// <c>BackupCrypto.ReadKekFingerprintAsync</c> returns empty for v1 artifacts and
/// the restore pre-flight lets those through, so gating reconciliation on an
/// acknowledged mismatch would skip exactly the backups that can't be checked.
/// The fingerprint stays a pre-flight courtesy; opening the blob is the
/// authority.</para>
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

    public KekReconciliationService(
        ServiceDbContextFactory factory,
        LedgerKeyService keys,
        ILogger<KekReconciliationService> logger)
    {
        _factory = factory;
        _keys = keys;
        _logger = logger;
    }

    /// <summary>What reconciliation had to abandon. All-zero means the install's
    /// key already opened everything — the common case, including every
    /// same-install restore.</summary>
    public sealed record ReconciliationResult(
        int LedgersRekeyed,
        int FeedConnectionsNeedingReauth,
        bool BackupPassphraseCleared,
        bool DriveDisconnected)
    {
        /// <summary>True when anything was abandoned, i.e. the restore crossed a
        /// KEK boundary.</summary>
        public bool AnythingChanged =>
            LedgersRekeyed > 0 || FeedConnectionsNeedingReauth > 0
            || BackupPassphraseCleared || DriveDisconnected;
    }

    /// <summary>
    /// Trial-open every KEK-wrapped value; replace or clear whatever this
    /// install's key cannot open. Idempotent — a second run over a reconciled
    /// database finds nothing and changes nothing.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var (ledgersRekeyed, feedsCleared) = await ReconcileLedgersAsync(db, ct).ConfigureAwait(false);
        var passphraseCleared = await ReconcileBackupPassphraseAsync(db, ct).ConfigureAwait(false);
        var driveDisconnected = await ReconcileDriveAsync(db, ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        var result = new ReconciliationResult(
            ledgersRekeyed, feedsCleared, passphraseCleared, driveDisconnected);

        if (result.AnythingChanged)
            // Warning, not Information: the operator has re-establishment work to
            // do and the UI panels reflect it as state, but the log is where a
            // post-mortem starts.
            _logger.LogWarning(
                "KEK reconciliation after restore: {Ledgers} ledger key(s) replaced, "
                + "{Feeds} feed connection(s) flagged needs_reauth, backup passphrase cleared={Pass}, "
                + "Drive disconnected={Drive}. These secrets were sealed under a different master KEK "
                + "and could not be carried over; ledger data and passkeys are unaffected.",
                ledgersRekeyed, feedsCleared, passphraseCleared, driveDisconnected);
        else
            _logger.LogInformation(
                "KEK reconciliation after restore: everything opens under this install's master KEK.");

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
        var jobs = await db.GlobalScheduledJobs
            .Where(j => j.PassphraseCiphertext != null)
            .ToListAsync(ct).ConfigureAwait(false);

        var cleared = false;
        foreach (var job in jobs)
        {
            if (CanOpen(job.PassphraseCiphertext!)) continue;

            job.PassphraseCiphertext = null;
            job.Enabled = false;
            job.UpdatedAt = DateTime.UtcNow;
            cleared = true;
        }

        return cleared;
    }

    /// <summary>Clear an unopenable Drive OAuth blob and mark the sync
    /// disconnected, leaving the folder metadata so a reconnect is recognizable.</summary>
    private async Task<bool> ReconcileDriveAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.DriveSync
            .Where(d => d.OauthCiphertext != null)
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
                "Disconnected during restore: the OAuth token was sealed under a different "
                + "master KEK. Reconnect Google Drive to resume syncing.";
            row.UpdatedAt = DateTime.UtcNow;
            disconnected = true;
        }

        return disconnected;
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
