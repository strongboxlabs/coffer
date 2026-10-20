using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Outcome of <see cref="CredentialsRepository.DeleteOwnAsync"/>.
/// </summary>
public enum CredentialDeleteResult
{
    /// <summary>The credential was deleted.</summary>
    Deleted,
    /// <summary>No credential with that id belongs to the user.</summary>
    NotFound,
    /// <summary>It was the user's only credential — refused (would lock them out).</summary>
    WasLastCredential,
}

/// <summary>
/// EF Core-backed gateway to <c>webauthn_credentials</c>. Connects via
/// the service-role factory: the assertion-verification flow looks up
/// credentials by their FIDO2 credential id before any
/// <c>app.user_id</c> is set, and the registration flow writes the
/// credential row inside the bootstrap-setup ceremony (no user yet).
/// </summary>
public sealed class CredentialsRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;
    private readonly TimeProvider _clock;

    public CredentialsRepository(ServiceDbContextFactory serviceFactory, TimeProvider clock)
    {
        _serviceFactory = serviceFactory;
        _clock = clock;
    }

    /// <summary>
    /// Insert a freshly registered credential. Id is generated
    /// client-side so the FK chain has a stable value from the moment
    /// the row enters the change tracker. <c>created_at</c> is stamped from the app
    /// clock, not the column default, and truncated to the microseconds timestamptz
    /// keeps, so the row returned is the row stored.
    /// </summary>
    public async Task<WebAuthnCredentialRow> InsertAsync(
        Guid userId,
        byte[] credentialId,
        byte[] publicKey,
        long signatureCounter,
        Guid? aaguid,
        string[]? transports,
        string nickname,
        string? rpId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);

        var now = _clock.GetUtcNow().UtcDateTime;
        var row = new WebAuthnCredentialRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = credentialId,
            PublicKey = publicKey,
            SignatureCounter = signatureCounter,
            Aaguid = aaguid,
            Transports = transports,
            Nickname = nickname,
            RpId = rpId,
            // From the app clock, not the column default: step-up compares it with
            // the session's start, which comes from the same clock.
            CreatedAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc),
        };
        await using var db = _serviceFactory.Create();
        db.WebAuthnCredentials.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Fetch every credential owned by <paramref name="userId"/>. The
    /// register/begin ceremony excludes these from the new-credential
    /// challenge (same authenticator can't enrol twice for one user).
    /// </summary>
    public async Task<IReadOnlyList<WebAuthnCredentialRow>> GetByUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.WebAuthnCredentials.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Look up a credential by its FIDO2 credential id. Used by the
    /// assertion (login) ceremony.
    /// </summary>
    public async Task<WebAuthnCredentialRow?> GetByCredentialIdAsync(
        byte[] credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        await using var db = _serviceFactory.Create();
        return await db.WebAuthnCredentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CredentialId == credentialId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Delete one of <paramref name="userId"/>'s credentials, refusing to
    /// remove their last one (which would lock them out of passkey login).
    /// The "is it the last?" check rides inside the DELETE as a correlated
    /// EXISTS so the guard and the delete are one atomic statement — two
    /// concurrent deletes of different ids can't both succeed down to zero.
    /// Scoped to <paramref name="userId"/> so a caller can only delete their
    /// own rows.
    /// </summary>
    public async Task<CredentialDeleteResult> DeleteOwnAsync(
        Guid id, Guid userId, bool allowLast, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();

        var exists = await db.WebAuthnCredentials
            .AnyAsync(c => c.Id == id && c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
            return CredentialDeleteResult.NotFound;

        var affected = await db.WebAuthnCredentials
            .Where(c => c.Id == id
                     && c.UserId == userId
                     // Refuse the last credential unless the caller has confirmed
                     // another login path exists (recovery codes) — else deleting
                     // it can only lock the user out. The OTHER-credential EXISTS
                     // keeps the guard + delete atomic against a concurrent delete.
                     && (allowLast
                         || db.WebAuthnCredentials.Any(o => o.UserId == userId && o.Id != id)))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return affected > 0
            ? CredentialDeleteResult.Deleted
            : CredentialDeleteResult.WasLastCredential;
    }

    /// <summary>
    /// Bump the signature counter and last-used timestamp after a
    /// successful assertion. The counter is the WebAuthn replay-attack
    /// guard: it must strictly increase per credential, per spec.
    /// Single-statement <c>UPDATE</c> via EF's <c>ExecuteUpdate</c>.
    /// </summary>
    /// <remarks>
    /// A compare-and-set on the counter the assertion was verified against (FU-095). The
    /// update used to be unconditional, so two assertions racing on one credential, each
    /// verified against the same stored counter, both passed, and the later write could
    /// move the counter back. Now only the first commits; the other finds the counter
    /// moved and is refused, as the replay it may be. An authenticator that keeps no
    /// counter reports 0 against a stored 0, and that still succeeds.
    /// </remarks>
    /// <returns>False when another assertion moved the counter first.</returns>
    public async Task<bool> UpdateAfterAssertionAsync(
        Guid id, long verifiedAgainst, long newSignatureCounter, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.WebAuthnCredentials
            .Where(c => c.Id == id && c.SignatureCounter == verifiedAgainst)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.SignatureCounter, _ => newSignatureCounter)
                .SetProperty(c => c.LastUsedAt, _ => now),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }
}
