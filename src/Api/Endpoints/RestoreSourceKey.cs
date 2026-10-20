using Microsoft.AspNetCore.Http;

using Coffer.Api.Backup;
using Coffer.Api.Crypto;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The SOURCE install's master key supplied with a restore (ADR-0092 D4): checked and
/// staged for the boot that adopts it. One set of rules for both restore paths — the admin
/// restore and the setup-screen restore (FU-074) — so the two cannot drift apart on what a
/// valid key is.
/// </summary>
/// <remarks>
/// Without it a restore onto another install still succeeds, but every secret sealed under
/// the source's key (bank feed tokens, the stored backup passphrase, the Drive token,
/// notification targets) is cleared by reconciliation (D5) because it will not open. That is
/// the right outcome when the key is lost, and the wrong one for a disaster-recovery restore
/// by an operator who has it, which the setup screen used to force on everyone.
/// </remarks>
internal static class RestoreSourceKey
{
    /// <summary>The form field both restore endpoints read.</summary>
    public const string FormField = "sourceMasterKeyBase64";

    /// <summary>
    /// Check the supplied key against the staged archive and stage it. Call after the
    /// archive is at <see cref="BootstrapRestoreStaging.ArchivePath"/> and before anything is
    /// committed.
    /// </summary>
    /// <returns><c>Failure</c> is the problem to answer with (staging has been cleared);
    /// <c>Staged</c> is true when a key was supplied and staged, false when the field was empty.</returns>
    public static async Task<(IResult? Failure, bool Staged)> StageIfSuppliedAsync(
        string? raw, CancellationToken cancellationToken)
    {
        var sourceKeyRaw = (raw ?? string.Empty).Trim();
        if (sourceKeyRaw.Length == 0) return (null, false);

        byte[] sourceKeyBytes;
        try
        {
            sourceKeyBytes = Convert.FromBase64String(sourceKeyRaw);
        }
        catch (FormatException)
        {
            BootstrapRestoreStaging.Clear();
            return (BusinessError.Problem(BusinessError.Codes.BackupSourceKeyInvalid,
                "The source install's master key isn't valid base64. It looks like the output "
                + "of `openssl rand -base64 32` — 44 characters ending in '='."), false);
        }
        if (sourceKeyBytes.Length != 32)
        {
            BootstrapRestoreStaging.Clear();
            return (BusinessError.Problem(BusinessError.Codes.BackupSourceKeyInvalid,
                $"The source install's master key must decode to 32 bytes (got {sourceKeyBytes.Length})."), false);
        }

        // Strong check when the archive carries a fingerprint (v2+): the supplied key must
        // be THE key this backup was sealed under. Catches a paste-the-wrong-key mistake now
        // rather than after the restore has replaced everything and the install can't open
        // its own secrets. A v1 archive has no fingerprint to check against, so it proceeds
        // — and if the key turns out wrong, D5's reconciliation on the post-adopt boot clears
        // what won't open, which is the same outcome as not supplying one.
        try
        {
            await using var fpStream = File.OpenRead(BootstrapRestoreStaging.ArchivePath);
            var archiveFingerprint = await BackupCrypto
                .ReadKekFingerprintAsync(fpStream, cancellationToken).ConfigureAwait(false);
            if (archiveFingerprint.Length > 0
                && !KekFingerprint.Matches(archiveFingerprint, KekFingerprint.Compute(sourceKeyBytes)))
            {
                BootstrapRestoreStaging.Clear();
                return (BusinessError.Problem(BusinessError.Codes.BackupSourceKeyInvalid,
                    "That key is not the one this backup was sealed under. Check you pasted the "
                    + "source install's master key — its Encryption tab shows it, or read it from "
                    + "that install's key file — or leave the field empty to restore without it "
                    + "(feeds, backup passphrase, and Drive will need re-establishing)."), false);
            }
        }
        catch (BackupDecryptException)
        {
            BootstrapRestoreStaging.Clear();
            return (BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "That file isn't a readable Coffer backup."), false);
        }

        await BootstrapRestoreStaging.StageSourceKeyAsync(sourceKeyRaw, cancellationToken)
            .ConfigureAwait(false);
        return (null, true);
    }
}
