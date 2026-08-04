using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// EF Core-backed gateway to <c>recovery_codes</c>. Connects via the
/// service-role factory: redeem happens pre-auth (the user is signing in
/// precisely because they can't use a passkey), and regenerate replaces
/// the whole set in one transaction. The plaintext codes are shown once
/// at generation; only the Argon2id PHC string lives here (see
/// <see cref="Auth.Webauthn.RecoveryCodes"/>).
/// </summary>
public sealed class RecoveryCodesRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;

    public RecoveryCodesRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>
    /// Every unused (<c>used_at IS NULL</c>) code hash for the user, with
    /// its row id so the matching one can be marked used. The recovery
    /// login flow verifies the presented code against each hash.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string CodeHash)>> GetUnusedByUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var rows = await db.RecoveryCodes.AsNoTracking()
            .Where(c => c.UserId == userId && c.UsedAt == null)
            .Select(c => new { c.Id, c.CodeHash })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => (r.Id, r.CodeHash)).ToList();
    }

    /// <summary>
    /// How many unused codes the user has left. Surfaced on the security
    /// screen so a user knows when to regenerate.
    /// </summary>
    public async Task<int> CountUnusedByUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.RecoveryCodes
            .CountAsync(c => c.UserId == userId && c.UsedAt == null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Mark a single code consumed. Idempotent-ish: the WHERE guards
    /// <c>used_at IS NULL</c> so a replayed redeem of the same code
    /// affects zero rows. Returns true iff this call is the one that
    /// consumed it.
    /// </summary>
    public async Task<bool> MarkUsedAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        var affected = await db.RecoveryCodes
            .Where(c => c.Id == id && c.UsedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.UsedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// Replace the user's entire code set: delete all existing rows
    /// (used or not) and insert the supplied fresh hashes, in one
    /// transaction. Used by the regenerate endpoint.
    /// </summary>
    public async Task ReplaceAllAsync(
        Guid userId, IReadOnlyList<string> newHashes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newHashes);

        await using var db = _serviceFactory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                        .ConfigureAwait(false);

        await db.RecoveryCodes
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var hash in newHashes)
        {
            db.RecoveryCodes.Add(new RecoveryCodeRow
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CodeHash = hash,
            });
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
