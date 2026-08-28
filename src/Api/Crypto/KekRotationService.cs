using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Crypto;

/// <summary>
/// Master-KEK rotation (ADR-0026 §rotation). Thanks to envelope encryption,
/// rotating the master KEK only re-WRAPS the data keys — no bulk data is
/// re-encrypted. Two things are sealed directly under the master KEK:
/// <list type="bullet">
///   <item><description>every <c>ledgers.wrapped_lek</c> (the per-ledger LEK,
///   wrapped under the KEK), and</description></item>
///   <item><description>the backup passphrase in
///   <c>global_scheduled_jobs.passphrase_ciphertext</c> (ADR-0060), and</description></item>
///   <item><description>the Google Drive OAuth blob in
///   <c>drive_sync.oauth_ciphertext</c> (ADR-0062 D3), when connected.</description></item>
/// </list>
/// Everything else is sealed under the per-ledger LEK, whose plaintext is
/// unchanged, so it needs no work. The whole rotation is one transaction —
/// a half-rotated DB (some blobs under the old KEK, some under the new) would
/// be unopenable, so it's all-or-nothing.
/// </summary>
public sealed class KekRotationService : IKekRotationService
{
    private readonly ServiceDbContextFactory _factory;
    private readonly ILogger<KekRotationService> _logger;

    public KekRotationService(ServiceDbContextFactory factory, ILogger<KekRotationService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Re-wrap every LEK + the backup passphrase from <paramref name="oldKey"/>
    /// to <paramref name="newKey"/>. With <paramref name="dryRun"/>, verifies
    /// every blob opens under the old key and writes nothing. Throws
    /// <see cref="KekRotationException"/> (rolling back) if any blob fails to
    /// open under the old key — meaning the supplied "current" KEK isn't the one
    /// the data was wrapped with.
    /// </summary>
    public async Task<RotationResult> RotateAsync(
        MasterKey oldKey, MasterKey newKey, bool dryRun, CancellationToken ct = default)
    {
        var oldKeys = new LedgerKeyService(oldKey);
        var newKeys = new LedgerKeyService(newKey);

        await using var db = _factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var ledgers = await db.Ledgers
            .Where(l => l.WrappedLek != null)
            .ToListAsync(ct).ConfigureAwait(false);

        var rotated = 0;
        foreach (var l in ledgers)
        {
            var rawLek = OpenOrThrow(
                oldKeys, l.WrappedLek!, oldKey.Id,
                $"ledger {l.Id} (wrapped_lek)");
            try
            {
                if (!dryRun)
                {
                    l.WrappedLek = newKeys.SealWithMasterKey(rawLek);
                    l.LekKekId = newKey.Id;
                }
                rotated++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawLek);
            }
        }

        var backup = await db.GlobalScheduledJobs
            .FirstOrDefaultAsync(j => j.JobType == GlobalJobTypes.Backup, ct)
            .ConfigureAwait(false);
        var passphraseRotated = false;
        if (backup?.PassphraseCiphertext is { Length: > 0 } sealedPass)
        {
            var pt = OpenOrThrow(oldKeys, sealedPass, oldKey.Id, "backup passphrase");
            try
            {
                if (!dryRun) backup.PassphraseCiphertext = newKeys.SealWithMasterKey(pt);
                passphraseRotated = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pt);
            }
        }

        var drive = await db.DriveSync
            .FirstOrDefaultAsync(d => d.Id == (short)1, ct)
            .ConfigureAwait(false);
        var driveTokenRotated = false;
        if (drive?.OauthCiphertext is { Length: > 0 } sealedDrive)
        {
            var pt = OpenOrThrow(oldKeys, sealedDrive, oldKey.Id, "Google Drive OAuth token");
            try
            {
                if (!dryRun) drive.OauthCiphertext = newKeys.SealWithMasterKey(pt);
                driveTokenRotated = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pt);
            }
        }

        // Notification delivery configs (mig 207 / 208, ADR-0096 D8). Sealed under the
        // master KEK exactly like the two above, and omitted from this loop when they
        // were added — so rotating the key left every delivery target holding
        // ciphertext nothing could open. The subsystem then fails in the one way it
        // exists to prevent: alerts stop, and the only trace is a per-target last_error
        // on a settings page nobody visits until they wonder why it went quiet.
        //
        // Both scopes, in one pass. A rotation that fixed the deployment's targets and
        // left every ledger's own targets broken would be worse than one that missed
        // both, because it would look like it worked.
        var subscribersRotated = 0;
        foreach (var row in await db.NotificationSubscribers
                     .Where(t => t.ConfigCiphertext.Length > 0)
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            var pt = OpenOrThrow(oldKeys, row.ConfigCiphertext, oldKey.Id,
                $"notification target '{row.DisplayName}'");
            try
            {
                if (!dryRun) row.ConfigCiphertext = newKeys.SealWithMasterKey(pt);
                subscribersRotated++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pt);
            }
        }

        foreach (var row in await db.LedgerNotificationSubscribers
                     .Where(t => t.ConfigCiphertext.Length > 0)
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            var pt = OpenOrThrow(oldKeys, row.ConfigCiphertext, oldKey.Id,
                $"ledger notification target '{row.DisplayName}'");
            try
            {
                if (!dryRun) row.ConfigCiphertext = newKeys.SealWithMasterKey(pt);
                subscribersRotated++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pt);
            }
        }

        if (dryRun)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "KEK rotation dry-run OK: {Ledgers} ledger key(s) + passphrase={Pass} + "
                + "driveToken={Drive} + {Targets} notification target(s) open under KEK '{Old}'.",
                rotated, passphraseRotated, driveTokenRotated, subscribersRotated, oldKey.Id);
        }
        else
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "KEK rotation complete: re-wrapped {Ledgers} ledger key(s){Pass}{Drive}"
                + " + {Targets} notification target(s) from '{Old}' to '{New}'.",
                rotated,
                passphraseRotated ? " + the backup passphrase" : "",
                driveTokenRotated ? " + the Drive OAuth token" : "",
                subscribersRotated, oldKey.Id, newKey.Id);
        }

        return new RotationResult(
            rotated, passphraseRotated, driveTokenRotated, subscribersRotated, dryRun);
    }

    private static byte[] OpenOrThrow(LedgerKeyService keys, byte[] sealedBytes, string oldId, string what)
    {
        try
        {
            return keys.OpenWithMasterKey(sealedBytes);
        }
        catch (CryptographicException ex)
        {
            // Surfaced verbatim in the admin UI (ADR-0092 D4), so it must not name a
            // retired env var an operator would then go looking for. The likely cause
            // is a cross-KEK restore that skipped reconciliation, which D5 handles.
            throw new KekRotationException(
                $"{what} does not open under the current KEK (id '{oldId}'). Aborting — is the "
                + "master key file the key this data was wrapped with? This usually means a "
                + "backup from another install was restored without its key.", ex);
        }
    }
}

/// <summary>Outcome of a rotation run.</summary>
public sealed record RotationResult(
    int LedgersRotated,
    bool PassphraseRotated,
    bool DriveTokenRotated,
    int NotificationTargetsRotated,
    bool DryRun);

/// <summary>Thrown when rotation can't proceed (a blob won't open under the
/// supplied current KEK); the transaction is rolled back.</summary>
public sealed class KekRotationException : Exception
{
    public KekRotationException(string message, Exception inner) : base(message, inner) { }
}
