using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// API-side gateway to <c>ledgers</c> + <c>user_ledger_grants</c>.
/// Reads go through the <c>user_visible_ledgers</c> view introduced in
/// migration 014 (with <c>security_invoker = true</c> after migration
/// 017 so RLS on its underlying tables applies); per-user scoping is
/// one SQL predicate, not a join that endpoint code has to remember to
/// write.
/// </summary>
/// <remarks>
/// <see cref="CreateWithOwnerAsync"/> uses the service-role factory:
/// the ledger INSERT and the grant INSERT happen across an RLS
/// boundary (the new ledger.id isn't in the user's grant set yet at
/// the moment the WITH CHECK on <c>ledgers_per_user</c> would fire),
/// so the authenticated endpoint escalates this single write to
/// coffer_service. The caller's user-id is supplied explicitly so the
/// grant is minted for the right user.
/// </remarks>
public sealed class LedgersRepository
{
    private readonly AppDbContext _db;
    private readonly ServiceDbContextFactory _serviceFactory;
    private readonly Crypto.LedgerKeyService _ledgerKeys;

    public LedgersRepository(
        AppDbContext db,
        ServiceDbContextFactory serviceFactory,
        Crypto.LedgerKeyService ledgerKeys)
    {
        _db = db;
        _serviceFactory = serviceFactory;
        _ledgerKeys = ledgerKeys;
    }

    /// <summary>
    /// Every ledger the user has any grant on, keyed by id. Backed by
    /// the <c>user_visible_ledgers</c> view; security_invoker means the
    /// view runs with the caller's permissions and the underlying RLS
    /// policies on <c>user_ledger_grants</c> + <c>ledgers</c> apply.
    /// </summary>
    public async Task<IReadOnlyList<LedgerSummary>> GetVisibleAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        await _db.UserVisibleLedgers
            .Where(v => v.UserId == userId)
            .OrderBy(v => v.LedgerName)
            .Select(v => new LedgerSummary(v.LedgerId, v.LedgerName, v.Role))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Look up one ledger only if the user has a grant. Returns null
    /// when the user can't see it (or it doesn't exist) — same
    /// contract RLS itself enforces.
    /// </summary>
    public async Task<LedgerSummary?> GetVisibleByIdAsync(
        Guid userId, Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _db.UserVisibleLedgers
            .Where(v => v.UserId == userId && v.LedgerId == ledgerId)
            .Select(v => new LedgerSummary(v.LedgerId, v.LedgerName, v.Role))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Create a ledger and grant the supplied user <c>owner</c> in one
    /// transaction. Runs as coffer_service: <c>ledgers</c> and
    /// <c>user_ledger_grants</c> are SELECT-only for coffer_app under
    /// migration 017, and the deferred ≥1-owner trigger checks the
    /// pair at COMMIT — neither the ledger insert nor the grant
    /// insert would otherwise satisfy a WITH CHECK predicate while
    /// the other hasn't landed yet.
    /// </summary>
    public async Task<LedgerSummary> CreateWithOwnerAsync(
        Guid userId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = _serviceFactory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                          .ConfigureAwait(false);

        // Freshly-generated LEK wrapped under the current master KEK
        // (ADR-0026). Persisted in the same transaction as the ledger
        // + grant so a partial commit can't leave a ledger without
        // its crypto material.
        var ledger = new LedgerRow
        {
            Id = Guid.NewGuid(),
            Name = name,
            WrappedLek = _ledgerKeys.CreateWrappedLek(),
            LekKekId = _ledgerKeys.CurrentKekId,
            LekCreatedAt = DateTime.UtcNow,
        };
        db.Ledgers.Add(ledger);

        var grant = new UserLedgerGrantRow
        {
            UserId = userId,
            LedgerId = ledger.Id,
            Role = "owner",
        };
        db.UserLedgerGrants.Add(grant);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new LedgerSummary(ledger.Id, ledger.Name, "owner");
    }

    /// <summary>
    /// Rename a ledger. Service role — same rationale as
    /// <see cref="CreateWithOwnerAsync"/> (coffer_app can't UPDATE
    /// <c>ledgers</c> under migration 017). The caller (endpoint) must
    /// have already verified the user is an <c>owner</c> of the ledger.
    /// </summary>
    public async Task RenameAsync(
        Guid ledgerId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = _serviceFactory.Create();
        await db.Ledgers
            .Where(l => l.Id == ledgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Name, name), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a ledger and its entire footprint via the
    /// <c>ledger_delete</c> TVF (migration 141) — a complete, FK-ordered
    /// wipe in one transaction. Service role (the wipe spans every
    /// ledger-scoped table + the <c>ledgers</c> row, across RLS). The
    /// caller (endpoint) must have already verified the user is an
    /// <c>owner</c>. Destructive + irreversible.
    /// </summary>
    public async Task DeleteAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                        .ConfigureAwait(false);
        try
        {
            await db.LedgerDelete(ledgerId)
                .Select(r => r.LedgerId)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Lazy LEK backfill (ADR-0026 follow-through). Returns the
    /// ledger's wrapped LEK, generating one if the row pre-dates
    /// migration 035 and still carries NULL. Atomic against
    /// concurrent callers via a conditional UPDATE — whichever
    /// caller wins the race owns the LEK and subsequent callers
    /// read the surviving value.
    /// </summary>
    /// <returns>The wrapped LEK bytes, or null if the ledger row
    /// itself doesn't exist (caller should have already passed a
    /// ledger-grant check).</returns>
    public async Task<byte[]?> EnsureWrappedLekAsync(
        Guid ledgerId,
        Crypto.LedgerKeyService ledgerKeys,
        CancellationToken cancellationToken = default)
    {
        // Service role: the row UPDATE crosses coffer_app's RLS WITH
        // CHECK on `ledgers` (the user has a grant but can't
        // generally mutate the row), and master-KEK material lives
        // in this process anyway — no privilege gain.
        await using var db = _serviceFactory.Create();

        var existing = await db.Ledgers
            .AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.WrappedLek)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null) return existing;

        // Generate + try-conditional-UPDATE. ExecuteUpdate with a
        // WHERE wrapped_lek IS NULL clause is atomic at the SQL
        // level — Postgres serialises concurrent updates on the
        // same row and the second one matches zero rows.
        var freshWrapped = ledgerKeys.CreateWrappedLek();
        var kekId = ledgerKeys.CurrentKekId;
        var now = DateTime.UtcNow;

        var rowsTouched = await db.Ledgers
            .Where(l => l.Id == ledgerId && l.WrappedLek == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(l => l.WrappedLek, freshWrapped)
                .SetProperty(l => l.LekKekId, kekId)
                .SetProperty(l => l.LekCreatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (rowsTouched == 1) return freshWrapped;

        // Either the ledger row vanished (race with delete — rare,
        // returns null) or another concurrent caller backfilled
        // first. Re-read to get the surviving value.
        return await db.Ledgers
            .AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.WrappedLek)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Membership management (ADR-0083) ──────────────────────────────────────
    // user_ledger_grants is SELECT-only + self-scoped for coffer_app (mig 017), so
    // listing ALL members and mutating grants both run as the service role. The
    // endpoint gates authority first: listing = any member (read), role/remove =
    // owner (AsLedgerOwner). The ≥1-owner invariant (ADR-0020 / ADR-0083 D4) is
    // enforced here in API code (its DB trigger was dropped in mig 087 / ADR-0032).

    private static readonly string[] GrantRoles = ["owner", "editor", "viewer"];

    /// <summary>Outcome of a member role/remove mutation.</summary>
    public enum MemberChangeResult { Ok, InvalidRole, NotAMember, LastOwner, SystemUser }

    /// <summary>Every HUMAN member of the ledger with their role (service role — the
    /// caller's membership is verified by the endpoint's read gate). The synthetic system
    /// service identity holds an owner grant for service-role flows but is NOT a
    /// manageable member, so it's hidden here.</summary>
    public async Task<IReadOnlyList<LedgerMember>> ListMembersAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await (
            from g in db.UserLedgerGrants
            join u in db.Users on g.UserId equals u.Id
            where g.LedgerId == ledgerId && g.UserId != UserRow.SystemUserId
            orderby u.DisplayName
            select new LedgerMember(g.UserId, u.DisplayName, u.Username, g.Role))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Change an existing member's role. Rejects an unknown role, a non-member
    /// (invite them first), and demoting the ledger's last owner.</summary>
    public async Task<MemberChangeResult> SetMemberRoleAsync(
        Guid ledgerId, Guid userId, string role, CancellationToken cancellationToken = default)
    {
        if (userId == UserRow.SystemUserId) return MemberChangeResult.SystemUser;
        if (!GrantRoles.Contains(role)) return MemberChangeResult.InvalidRole;

        await using var db = _serviceFactory.Create();
        var current = await db.UserLedgerGrants.AsNoTracking()
            .Where(g => g.LedgerId == ledgerId && g.UserId == userId)
            .Select(g => g.Role)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null) return MemberChangeResult.NotAMember;
        if (current == role) return MemberChangeResult.Ok;

        if (current == "owner" && role != "owner"
            && await HumanOwnerCountAsync(db, ledgerId, cancellationToken).ConfigureAwait(false) <= 1)
            return MemberChangeResult.LastOwner;

        await db.UserLedgerGrants
            .Where(g => g.LedgerId == ledgerId && g.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Role, role), cancellationToken)
            .ConfigureAwait(false);
        return MemberChangeResult.Ok;
    }

    /// <summary>Remove a member's grant. Rejects removing the ledger's last owner.</summary>
    public async Task<MemberChangeResult> RemoveMemberAsync(
        Guid ledgerId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == UserRow.SystemUserId) return MemberChangeResult.SystemUser;

        await using var db = _serviceFactory.Create();
        var grant = await db.UserLedgerGrants
            .FirstOrDefaultAsync(g => g.LedgerId == ledgerId && g.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (grant is null) return MemberChangeResult.NotAMember;

        if (grant.Role == "owner"
            && await HumanOwnerCountAsync(db, ledgerId, cancellationToken).ConfigureAwait(false) <= 1)
            return MemberChangeResult.LastOwner;

        db.UserLedgerGrants.Remove(grant);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return MemberChangeResult.Ok;
    }

    // HUMAN owners only: the system identity's owner grant (for service-role flows)
    // must NOT satisfy the ≥1-owner invariant — a ledger has to keep a real human owner.
    private static Task<int> HumanOwnerCountAsync(AppDbContext db, Guid ledgerId, CancellationToken ct) =>
        db.UserLedgerGrants.CountAsync(
            g => g.LedgerId == ledgerId && g.Role == "owner" && g.UserId != UserRow.SystemUserId, ct);
}
