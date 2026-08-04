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

    public McpTokensRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>
    /// Resolve a presented token hash to its owning user (joined for
    /// <c>is_admin</c> so the principal carries it in one round-trip). Returns
    /// null when the hash matches no row, the row is revoked, or it has expired
    /// — the auth handler treats all three the same (no identity). On success,
    /// bumps <c>last_used_at</c>.
    /// </summary>
    public async Task<ValidatedToken?> ValidateAsync(
        byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32)
            return null;

        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        var match = await db.McpAccessTokens.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash
                     && t.RevokedAt == null
                     && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Join(
                db.Users,
                t => t.UserId,
                u => u.Id,
                (t, u) => new ValidatedToken(t.Id, t.UserId, t.Scopes, u.IsAdmin))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (match is null) return null;

        await db.McpAccessTokens
            .Where(t => t.Id == match.TokenId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastUsedAt, _ => (DateTime?)DateTime.UtcNow),
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
            .Select(t => new McpTokenSummary(
                t.Id, t.Name, t.Scopes, t.CreatedAt, t.LastUsedAt, t.ExpiresAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revoke one token, scoped to its owner so a user can't revoke another
    /// user's token by id. Idempotent. Returns true iff a matching active row
    /// was flipped.
    /// </summary>
    public async Task<bool> RevokeAsync(
        Guid userId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        var rows = await db.McpAccessTokens
            .Where(t => t.Id == tokenId && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>A resolved token: its id (to bump last-used), owning user,
    /// scopes, and the owner's admin flag for the principal.</summary>
    public sealed record ValidatedToken(Guid TokenId, Guid UserId, string Scopes, bool IsAdmin);
}
