using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Services;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Invite links (ADR-0083 slice B). A generalized, repeatable, scoped bootstrap
/// token — reuses the vetted 32-byte / SHA-256 / base64url primitive from
/// <see cref="BootstrapTokenService"/>. Service-role only (the <c>invites</c> table
/// is not granted to coffer_app): issue runs escalated from an authed owner/admin,
/// redeem runs pre-auth. The single-use consume is inlined into the redeem
/// transaction (InvitesEndpoints), mirroring the setup ceremony.
/// </summary>
public sealed class InvitesRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;

    public InvitesRepository(ServiceDbContextFactory serviceFactory) => _serviceFactory = serviceFactory;

    /// <summary>Default invite lifetime.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    /// <summary>The scope a valid invite confers (for the preview + redeem).</summary>
    public sealed record InviteScope(Guid? LedgerId, string? LedgerName, string? Role, bool GrantsAdmin);

    /// <summary>
    /// Mint an invite and return its ONE-TIME plaintext token (never persisted; only
    /// its SHA-256 is stored). A ledger invite carries a role; an instance-only invite
    /// passes null/null (+ optional admin grant).
    /// </summary>
    public async Task<string> CreateAsync(
        Guid issuedByUserId, Guid? ledgerId, string? role, bool grantsAdmin,
        CancellationToken cancellationToken = default)
    {
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        await using var db = _serviceFactory.Create();
        db.Invites.Add(new InviteRow
        {
            TokenHash = hash,
            Id = Guid.NewGuid(),
            IssuedByUserId = issuedByUserId,
            LedgerId = ledgerId,
            Role = role,
            GrantsAdmin = grantsAdmin,
            ExpiresAt = DateTime.UtcNow.Add(DefaultTtl),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return plaintext;
    }

    /// <summary>
    /// The scope of a valid (unconsumed, unexpired) invite for the presented token, or
    /// null when the token is unknown / spent / expired / malformed. Does NOT consume —
    /// the preview and the redeem-begin step both use it.
    /// </summary>
    public async Task<InviteScope?> GetValidScopeAsync(
        string plaintextToken, CancellationToken cancellationToken = default)
    {
        byte[] hash;
        try { hash = BootstrapTokenService.HashToken(plaintextToken); }
        catch { return null; } // malformed token → not found

        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        return await (
            from i in db.Invites
            where i.TokenHash == hash && i.ConsumedAt == null && i.ExpiresAt > now
            join l in db.Ledgers on i.LedgerId equals l.Id into ledgerJoin
            from l in ledgerJoin.DefaultIfEmpty()
            select new InviteScope(i.LedgerId, l != null ? l.Name : null, i.Role, i.GrantsAdmin))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A pending (unconsumed, unexpired) invite — for the list / revoke surface.</summary>
    public sealed record PendingInvite(
        Guid Id, Guid? LedgerId, string? LedgerName, string? Role, bool GrantsAdmin,
        DateTime CreatedAt, DateTime ExpiresAt);

    /// <summary>Pending invites scoped to one ledger (for the owner's Members panel).</summary>
    public Task<List<PendingInvite>> ListPendingForLedgerAsync(Guid ledgerId, CancellationToken ct = default) =>
        ListPendingAsync(i => i.LedgerId == ledgerId, ct);

    /// <summary>All pending invites (for the admin surface).</summary>
    public Task<List<PendingInvite>> ListPendingAllAsync(CancellationToken ct = default) =>
        ListPendingAsync(_ => true, ct);

    private async Task<List<PendingInvite>> ListPendingAsync(
        System.Linq.Expressions.Expression<Func<InviteRow, bool>> filter, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        return await (
            from i in db.Invites.Where(filter)
            where i.ConsumedAt == null && i.ExpiresAt > now
            join l in db.Ledgers on i.LedgerId equals l.Id into ledgerJoin
            from l in ledgerJoin.DefaultIfEmpty()
            orderby i.CreatedAt descending
            select new PendingInvite(
                i.Id, i.LedgerId, l != null ? l.Name : null, i.Role, i.GrantsAdmin, i.CreatedAt, i.ExpiresAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Revoke a pending invite scoped to a ledger (owner authority). True if removed.</summary>
    public async Task<bool> RevokeForLedgerAsync(Guid ledgerId, Guid inviteId, CancellationToken ct = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.Invites
            .Where(i => i.Id == inviteId && i.LedgerId == ledgerId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Revoke any invite by id (admin authority). True if removed.</summary>
    public async Task<bool> RevokeAsync(Guid inviteId, CancellationToken ct = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.Invites
            .Where(i => i.Id == inviteId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }
}
