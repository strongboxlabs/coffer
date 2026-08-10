using Microsoft.Extensions.Logging;

namespace Coffer.Api.Crypto;

/// <summary>
/// Owns the ORDER of a master-KEK rotation (ADR-0092 D4): the key-file swap and
/// the database re-wrap, sequenced so no crash leaves an unrecoverable state.
/// </summary>
/// <remarks>
/// <para>Separate from the endpoint so the sequence can be tested without an HTTP
/// host, and separate from <see cref="KekRotationService"/> — which owns the
/// re-wrap transaction and knows nothing about files — so each has one job.</para>
///
/// <para>The new key is a PARAMETER rather than generated here. That keeps the
/// coordinator deterministic, and lets a test rotate to a key with the same bytes
/// under a different id: the database ends up re-wrapped under identical bytes, so
/// a shared test database still opens for every other test, while the file swap,
/// the archive, and the rollback all run for real. Same trick, and same reason, as
/// <c>KekRotationServiceTests</c>.</para>
/// </remarks>
public sealed class MasterKeyRotationCoordinator
{
    private readonly IKekRotationService _rotation;
    private readonly ILogger<MasterKeyRotationCoordinator> _logger;

    public MasterKeyRotationCoordinator(
        IKekRotationService rotation,
        ILogger<MasterKeyRotationCoordinator> logger)
    {
        _rotation = rotation;
        _logger = logger;
    }

    /// <summary>Why a rotation was refused, when it was.</summary>
    public enum Refusal
    {
        /// <summary>Not refused.</summary>
        None,
        /// <summary>Something wrapped in the database doesn't open under the current
        /// key, so rotation can't safely re-wrap it. Nothing was touched.</summary>
        Blocked,
        /// <summary>The key file isn't writable — the documented read-only injection
        /// case (<c>/run/secrets/…</c>, a projected Kubernetes Secret). Nothing was
        /// touched.</summary>
        KeyFileNotWritable,
        /// <summary>The re-wrap failed and the previous key file was restored.
        /// <see cref="KekRotationService.RotateAsync"/> is transactional, so the
        /// database is unchanged too.</summary>
        RolledBack,
    }

    /// <summary>Outcome. <see cref="Refusal"/> is <see cref="Refusal.None"/> exactly
    /// when the rotation committed.</summary>
    public sealed record Outcome(
        Refusal Refusal,
        string? Message,
        RotationResult? Result,
        string? PreviousKeyArchivedAt);

    /// <summary>
    /// Rotate from <paramref name="currentKey"/> to <paramref name="newKey"/>,
    /// swapping <paramref name="store"/>'s contents.
    /// </summary>
    /// <remarks>
    /// Sequence, and why:
    /// <list type="number">
    ///   <item><description>Dry run. If anything in the database doesn't open under
    ///   the current key, refuse before touching a file — otherwise the operator
    ///   discovers a pre-existing mismatch only after the key has moved.</description></item>
    ///   <item><description>Archive the current key, then write the new one. A crash
    ///   here leaves the file ahead of the database, which is recoverable because the
    ///   old key is in the archive.</description></item>
    ///   <item><description>Re-wrap. The reverse order — database first — would leave
    ///   the database ahead of the file with the new key existing nowhere, which is
    ///   not recoverable. On failure the file is rolled back explicitly.</description></item>
    /// </list>
    /// </remarks>
    public async Task<Outcome> RotateAsync(
        MasterKey currentKey,
        MasterKey newKey,
        string newKeyBase64,
        MasterKeyStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentKey);
        ArgumentNullException.ThrowIfNull(newKey);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(newKeyBase64);

        // 1 — everything must open under the CURRENT key first.
        try
        {
            await _rotation.RotateAsync(currentKey, currentKey, dryRun: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KekRotationException ex)
        {
            return new(Refusal.Blocked, ex.Message, null, null);
        }

        // 2 — file first, archived so step 3's failure is reversible.
        string? archivedAt = null;
        try
        {
            archivedAt = store.Archive($"{DateTime.UtcNow:yyyyMMddTHHmmssZ}");
            store.Write(newKeyBase64, newKey.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Archive may have SUCCEEDED before Write failed — a read-only directory
            // still permits the move out on some platforms, and a disk-full hits the
            // write, not the rename. Put it back, or "nothing was changed" is a lie
            // and the install is left with no key file at all, which D3 then refuses
            // to boot over its own wrapped material.
            if (archivedAt is not null)
            {
                try
                {
                    store.RestoreFromArchive(archivedAt);
                }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    // Nothing left to try. Say exactly where the key is, since the
                    // operator now has to move it back by hand.
                    _logger.LogError(restoreEx,
                        "Master-KEK rotation failed AND the previous key file could not be put "
                        + "back. The key is at {Archive} — restore it to {Path} manually before "
                        + "restarting.", archivedAt, store.Path);
                    return new(Refusal.KeyFileNotWritable,
                        $"Rotation failed and the previous key file could not be restored. Your key "
                        + $"is at '{archivedAt}' — move it back to '{store.Path}' before restarting. "
                        + $"The database was not touched.",
                        null, null);
                }
            }

            _logger.LogWarning(ex,
                "Master-KEK rotation refused: the key file at {Path} is not writable.", store.Path);
            return new(Refusal.KeyFileNotWritable,
                $"The master key file at '{store.Path}' is not writable, so it can't be rotated "
                + "here — nothing was changed. This is expected when the key is injected read-only "
                + "(a Docker secret, a projected Kubernetes Secret): rotate it where that secret is "
                + "managed, then restart.",
                null, null);
        }

        // 3 — re-wrap, rolling the file back if it fails.
        try
        {
            var result = await _rotation
                .RotateAsync(currentKey, newKey, dryRun: false, cancellationToken)
                .ConfigureAwait(false);
            return new(Refusal.None, null, result, archivedAt);
        }
        catch (Exception ex)
        {
            if (archivedAt is not null) store.RestoreFromArchive(archivedAt);
            _logger.LogError(ex,
                "Master-KEK rotation failed; the previous key file was restored and the database "
                + "was left untouched.");
            return new(Refusal.RolledBack,
                $"Rotation failed and was rolled back — nothing changed. {ex.Message}",
                null, null);
        }
    }
}
