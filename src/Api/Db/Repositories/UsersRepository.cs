using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// EF Core-backed gateway to <c>users</c>. Mixed connection pattern
/// after the PR 3.8 role split:
/// <list type="bullet">
///   <item><description>Authenticated reads of the caller's own row
///   (<see cref="GetByIdAsync"/>) and the
///   <c>users.last_opened_ledger_id</c> self-update
///   (<see cref="SetLastOpenedLedgerAsync"/>) go through the runtime
///   <see cref="AppDbContext"/>. RLS's <c>users_self</c> policy
///   enforces <c>id = current_app_user_id()</c>, so a buggy caller
///   passing the wrong user-id sees an empty result instead of
///   another user's row.</description></item>
///   <item><description>Cross-cutting writes that pre-date or
///   bypass the authentication boundary
///   (<see cref="GetByUsernameAsync"/> at /login/begin,
///   <see cref="CreateAsync"/> during bootstrap setup) use the
///   <see cref="ServiceDbContextFactory"/>.</description></item>
/// </list>
/// </summary>
public sealed class UsersRepository
{
    private readonly AppDbContext _db;
    private readonly ServiceDbContextFactory _serviceFactory;

    public UsersRepository(AppDbContext db, ServiceDbContextFactory serviceFactory)
    {
        _db = db;
        _serviceFactory = serviceFactory;
    }

    /// <summary>
    /// Look up the caller's own user row. Runs through the
    /// RLS-bound <see cref="AppDbContext"/>; the <c>users_self</c>
    /// policy filters to <c>id = current_app_user_id()</c>, so passing
    /// any id other than the authenticated user yields null.
    /// </summary>
    public async Task<UserRow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Look up by username. Used at <c>/login/begin</c> BEFORE
    /// authentication completes; goes through the service-role
    /// factory so RLS doesn't deny the lookup. Returns null when the
    /// username is unknown so the endpoint returns the same shape
    /// regardless (no user-enumeration signal beyond what the
    /// architecture already accepts per ADR-0013).
    /// </summary>
    public async Task<UserRow?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        // Normalise the typed input the same way registration did (ADR-0089).
        // Case is folded by the column's username_ci collation, but NFC is not:
        // "José" typed with a combining accent is a different byte sequence from
        // the precomposed form, and would miss the row without this.
        var normalized = Auth.UsernamePolicy.Normalize(username);

        await using var db = _serviceFactory.Create();
        return await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Create a new user row. Used by the bootstrap-setup flow to mint
    /// the first non-system user at credential-registration time
    /// (pre-authentication, service role), and by the future invite
    /// flow to admin-create additional users.
    /// </summary>
    public async Task<UserRow> CreateAsync(
        string displayName,
        string username,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        var row = new UserRow
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            Username = username,
            CreatedBy = createdBy,
        };
        await using var db = _serviceFactory.Create();
        db.Users.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Set <c>last_opened_ledger_id</c> so the next login auto-opens
    /// the user's most-recent book. Caller validates the user still
    /// has a grant on the supplied ledger before invoking. Runs
    /// through the RLS-bound <see cref="AppDbContext"/>; the
    /// <c>users_self</c> policy filters the UPDATE to the caller's own
    /// row so a misrouted call against another user's id is a no-op
    /// rather than a silent privilege escalation.
    /// </summary>
    public async Task SetLastOpenedLedgerAsync(
        Guid userId, Guid? ledgerId, CancellationToken cancellationToken = default)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.LastOpenedLedgerId, _ => ledgerId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Admin user management (ADR-0083) ──────────────────────────────────────
    // is_admin and is_disabled are service-role-only writes (mig 138 restricts
    // coffer_app's UPDATE on users to last_opened_ledger_id). The endpoint gates
    // RequireAdmin first. Invariant (ADR-0083 D4): the instance keeps ≥1 ENABLED
    // admin — a user who both is_admin and is NOT disabled — so no action can lock
    // every administrator out.

    /// <summary>Outcome of an admin user mutation.</summary>
    public enum AdminUserChangeResult { Ok, NotFound, LastAdmin }

    /// <summary>All real users (the synthetic system identity excluded) with their
    /// admin/disabled flags and ledger-grant count. Service role.</summary>
    public async Task<IReadOnlyList<AdminUserSummary>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await (
            from u in db.Users
            where u.Id != UserRow.SystemUserId
            orderby u.DisplayName
            select new AdminUserSummary(
                u.Id, u.DisplayName, u.Username, u.IsAdmin, u.IsDisabled,
                db.UserLedgerGrants.Count(g => g.UserId == u.Id)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Disable/enable a user (blocks login; keeps grants). Refuses disabling
    /// the last enabled admin.</summary>
    public async Task<AdminUserChangeResult> SetDisabledAsync(
        Guid userId, bool disabled, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId).Select(x => new { x.IsAdmin, x.IsDisabled })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (u is null) return AdminUserChangeResult.NotFound;
        if (u.IsDisabled == disabled) return AdminUserChangeResult.Ok;

        if (disabled && u.IsAdmin
            && await EnabledAdminCountAsync(db, cancellationToken).ConfigureAwait(false) <= 1)
            return AdminUserChangeResult.LastAdmin;

        await db.Users.Where(x => x.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDisabled, disabled), cancellationToken)
            .ConfigureAwait(false);
        return AdminUserChangeResult.Ok;
    }

    /// <summary>Grant/revoke the instance admin flag. Refuses revoking the last enabled
    /// admin.</summary>
    public async Task<AdminUserChangeResult> SetAdminAsync(
        Guid userId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId).Select(x => new { x.IsAdmin, x.IsDisabled })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (u is null) return AdminUserChangeResult.NotFound;
        if (u.IsAdmin == isAdmin) return AdminUserChangeResult.Ok;

        if (!isAdmin && !u.IsDisabled
            && await EnabledAdminCountAsync(db, cancellationToken).ConfigureAwait(false) <= 1)
            return AdminUserChangeResult.LastAdmin;

        await db.Users.Where(x => x.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAdmin, isAdmin), cancellationToken)
            .ConfigureAwait(false);
        return AdminUserChangeResult.Ok;
    }

    private static Task<int> EnabledAdminCountAsync(AppDbContext db, CancellationToken ct) =>
        db.Users.CountAsync(u => u.IsAdmin && !u.IsDisabled, ct);
}
