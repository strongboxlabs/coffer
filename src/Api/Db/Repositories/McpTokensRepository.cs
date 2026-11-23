using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// EF Core gateway to <c>mcp_access_tokens</c> (ADR-0063). Connects via the
/// service-role factory (<see cref="ServiceDbContextFactory"/>) throughout — the
/// MCP auth handler validates a presented token BEFORE <c>app.user_id</c> is set
/// (RLS would otherwise deny the read), exactly like
/// <see cref="SessionsRepository"/>. The user-scoped management methods filter by
/// the authenticated user id explicitly; the per-user RLS policy is belt-and-
/// suspenders on top.
/// </summary>
public sealed class McpTokensRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;
    private readonly TimeProvider _clock;

    public McpTokensRepository(ServiceDbContextFactory serviceFactory, TimeProvider clock)
    {
        _serviceFactory = serviceFactory;
        _clock = clock;
    }

    /// <summary>
    /// Resolve a presented token hash to its owning user (joined for
    /// <c>is_admin</c> so the principal carries it in one round-trip). Returns
    /// null when the hash matches no row, the row is revoked, it has expired, or
    /// its user is disabled — the auth handler treats all four the same (no
    /// identity). On success, bumps <c>last_used_at</c>.
    /// </summary>
    public async Task<ValidatedToken?> ValidateAsync(
        byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        var match = await db.McpAccessTokens.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash
                     && t.RevokedAt == null
                     && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Join(
                db.Users.Where(u => !u.IsDisabled),
                t => t.UserId,
                u => u.Id,
                (t, u) => new ValidatedToken(t.Id, t.UserId, t.Scopes, u.IsAdmin))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (match is null) return null;

        await db.McpAccessTokens
            .Where(t => t.Id == match.TokenId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastUsedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);

        return match;
    }

    /// <summary>Issue a new token row for a user. The caller supplies the
    /// pre-computed hash + optional expiry; the schema assigns id + created_at.</summary>
    public async Task<Guid> IssueAsync(
        Guid userId, string name, byte[] tokenHash, string scopes, DateTime? expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32)
            throw new ArgumentException("Token hash must be SHA-256 (32 bytes).", nameof(tokenHash));

        var row = new McpAccessTokenRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            TokenHash = tokenHash,
            Scopes = scopes,
            ExpiresAt = expiresAt,
        };
        await using var db = _serviceFactory.Create();
        db.McpAccessTokens.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>A user's non-revoked tokens, newest first. Never returns the
    /// hash or any secret material.</summary>
    public async Task<IReadOnlyList<McpTokenSummary>> ListActiveAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.McpAccessTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new McpTokenSummary(
                t.Id, t.Name, t.Scopes, t.CreatedAt, t.LastUsedAt, t.ExpiresAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Every live token of every user, newest first, with its owner's display name — the
    /// admin's view across users. Live means not revoked and not past its expiry: an expired
    /// token is not a connection to show. Never returns the hash or any secret material.
    /// </summary>
    public async Task<IReadOnlyList<AdminMcpToken>> ListAllLiveAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.McpAccessTokens.AsNoTracking()
            .Where(t => t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Join(db.Users.AsNoTracking(), t => t.UserId, u => u.Id, (t, u) => new { Token = t, User = u })
            // Ordered before the projection: EF cannot map a property of the constructed
            // record back to the column it came from, so ordering after it does not translate.
            .OrderByDescending(x => x.Token.CreatedAt)
            .ThenByDescending(x => x.Token.Id)
            .Select(x => new AdminMcpToken(
                x.Token.Id, x.Token.UserId, x.User.DisplayName, x.Token.Name, x.Token.Scopes,
                x.Token.CreatedAt, x.Token.LastUsedAt, x.Token.ExpiresAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rename one active token, scoped to its owner so a user can't rename another user's by
    /// id. Returns true iff a matching active row was renamed.
    /// </summary>
    public Task<bool> RenameAsync(
        Guid userId, Guid tokenId, string name, CancellationToken cancellationToken = default) =>
        RenameCoreAsync(userId, tokenId, name, cancellationToken);

    /// <summary>
    /// Rename one active token whoever owns it — the admin path, reached only from a
    /// <c>RequireAdmin</c> endpoint. Returns true iff an active row was renamed.
    /// </summary>
    public Task<bool> RenameAnyAsync(Guid tokenId, string name, CancellationToken cancellationToken = default) =>
        RenameCoreAsync(null, tokenId, name, cancellationToken);

    private async Task<bool> RenameCoreAsync(
        Guid? userId, Guid tokenId, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = _serviceFactory.Create();
        var rows = await db.McpAccessTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null && (userId == null || t.UserId == userId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, _ => name), cancellationToken)
            .ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>
    /// Revoke one token whoever owns it — the admin path, reached only from a
    /// <c>RequireAdmin</c> endpoint. Returns the owner's id, so the caller can record whose
    /// access was ended, or null when no active token has this id.
    /// </summary>
    public async Task<Guid?> RevokeAnyAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        var owner = await db.McpAccessTokens.AsNoTracking()
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .Select(t => (Guid?)t.UserId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (owner is null)
            return null;

        var rows = await db.McpAccessTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
        return rows == 1 ? owner : null;
    }

    /// <summary>
    /// Revoke one token, scoped to its owner so a user can't revoke another
    /// user's token by id. Idempotent. Returns true iff a matching active row
    /// was flipped.
    /// </summary>
    public async Task<bool> RevokeAsync(
        Guid userId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        var rows = await db.McpAccessTokens
            .Where(t => t.Id == tokenId && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>
    /// Revoke every active token a user holds — disabling them, and an admin's
    /// "sign out everywhere". Returns how many were revoked.
    /// </summary>
    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.McpAccessTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A resolved token: its id (to bump last-used), owning user,
    /// scopes, and the owner's admin flag for the principal.</summary>
    public sealed record ValidatedToken(Guid TokenId, Guid UserId, string Scopes, bool IsAdmin);

    /// <summary>A live token as the admin sees it: the row's metadata and who owns it.</summary>
    public sealed record AdminMcpToken(
        Guid Id, Guid UserId, string User, string Name, string Scopes,
        DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt);
}
