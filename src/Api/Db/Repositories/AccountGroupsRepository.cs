using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Write + read gateway for user-curated sidebar tabs (migration 033).
/// Each row in <c>user_account_groups</c> is one named tab scoped to
/// (user, ledger); <c>user_account_group_members</c> is the N:M join
/// to <c>accounts</c>. The implicit "All" tab is virtual — not a row
/// in this table; the SPA renders it client-side when no group
/// filter is applied.
/// </summary>
/// <remarks>
/// RLS policies installed in migration 033 enforce that coffer_app
/// can only read/write rows belonging to the current
/// <c>app.user_id</c>. The repository's LINQ filters apply the same
/// predicates explicitly so the API layer's errors stay legible
/// (e.g. "group not found in ledger" vs. an opaque silent-empty
/// result the RLS would otherwise produce).
/// </remarks>
public sealed class AccountGroupsRepository
{
    private readonly AppDbContext _db;

    public AccountGroupsRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// List every group this user has in the supplied ledger, in
    /// sidebar render order (sort_order ASC, created_at ASC), with
    /// the account ids in each. One query for groups + one for
    /// memberships; assembled in memory because the typical
    /// (2-4 groups) × (≤20 accounts) payload is tiny.
    /// </summary>
    public async Task<IReadOnlyList<AccountGroupSummary>> ListAsync(
        Guid userId,
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        var groups = await _db.UserAccountGroups
            .AsNoTracking()
            .Where(g => g.UserId == userId && g.LedgerId == ledgerId)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.CreatedAt)
            .Select(g => new { g.Id, g.Name, g.SortOrder })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (groups.Count == 0) return Array.Empty<AccountGroupSummary>();

        var groupIds = groups.Select(g => g.Id).ToArray();
        var members = await _db.UserAccountGroupMembers
            .AsNoTracking()
            .Where(m => groupIds.Contains(m.GroupId))
            .Select(m => new { m.GroupId, m.AccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var membersByGroup = members
            .GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(m => m.AccountId).ToArray());

        return groups
            .Select(g => new AccountGroupSummary(
                g.Id,
                g.Name,
                g.SortOrder,
                membersByGroup.TryGetValue(g.Id, out var ids) ? ids : Array.Empty<Guid>()))
            .ToList();
    }

    /// <summary>
    /// Result of <see cref="CreateAsync"/>. <c>NameConflict</c>
    /// distinguishes the "another group with the same name already
    /// exists" case from a hard DB error.
    /// </summary>
    public enum CreateResult { Ok, NameConflict }

    /// <summary>
    /// Create a new group with <paramref name="name"/> at the end
    /// of the user's current group order in the ledger. The
    /// case-insensitive uniqueness index on
    /// <c>(user_id, ledger_id, lower(name))</c> backs the
    /// <c>NameConflict</c> branch.
    /// </summary>
    public async Task<(CreateResult Result, Guid? GroupId)> CreateAsync(
        Guid userId,
        Guid ledgerId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        var exists = await _db.UserAccountGroups
            .AnyAsync(g =>
                g.UserId == userId
                && g.LedgerId == ledgerId
                && g.Name.ToLower() == trimmed.ToLower(),
                cancellationToken)
            .ConfigureAwait(false);
        if (exists) return (CreateResult.NameConflict, null);

        var maxSort = await _db.UserAccountGroups
            .Where(g => g.UserId == userId && g.LedgerId == ledgerId)
            .Select(g => (int?)g.SortOrder)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = new UserAccountGroupRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            LedgerId = ledgerId,
            Name = trimmed,
            SortOrder = (maxSort ?? -1) + 1,
        };
        _db.UserAccountGroups.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (CreateResult.Ok, row.Id);
    }

    /// <summary>
    /// Outcome of <see cref="RenameAsync"/>. <c>NotFound</c> covers
    /// both "no such group id" and "RLS-hidden (different user)" —
    /// surfaces as 404 to the API caller in either case.
    /// </summary>
    public enum RenameResult { Ok, NotFound, NameConflict }

    public async Task<RenameResult> RenameAsync(
        Guid userId,
        Guid ledgerId,
        Guid groupId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        var group = await _db.UserAccountGroups
            .FirstOrDefaultAsync(g =>
                g.Id == groupId
                && g.UserId == userId
                && g.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (group is null) return RenameResult.NotFound;

        var conflict = await _db.UserAccountGroups
            .AnyAsync(g =>
                g.UserId == userId
                && g.LedgerId == ledgerId
                && g.Id != groupId
                && g.Name.ToLower() == trimmed.ToLower(),
                cancellationToken)
            .ConfigureAwait(false);
        if (conflict) return RenameResult.NameConflict;

        group.Name = trimmed;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RenameResult.Ok;
    }

    public enum DeleteResult { Ok, NotFound }

    public async Task<DeleteResult> DeleteAsync(
        Guid userId,
        Guid ledgerId,
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _db.UserAccountGroups
            .Where(g =>
                g.Id == groupId
                && g.UserId == userId
                && g.LedgerId == ledgerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0 ? DeleteResult.Ok : DeleteResult.NotFound;
    }

    /// <summary>
    /// Outcome of <see cref="AddMemberAsync"/>. <c>AccountNotInLedger</c>
    /// is the distinct error the SPA can surface; the rest collapse
    /// into NotFound at the endpoint.
    /// </summary>
    public enum AddMemberResult { Ok, GroupNotFound, AccountNotInLedger }

    /// <summary>
    /// Idempotent: re-adding an existing membership is a no-op (the
    /// composite PK absorbs the duplicate).
    /// </summary>
    public async Task<AddMemberResult> AddMemberAsync(
        Guid userId,
        Guid ledgerId,
        Guid groupId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var groupExists = await _db.UserAccountGroups
            .AnyAsync(g =>
                g.Id == groupId
                && g.UserId == userId
                && g.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!groupExists) return AddMemberResult.GroupNotFound;

        var accountInLedger = await _db.Accounts
            .AnyAsync(a => a.Id == accountId && a.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (!accountInLedger) return AddMemberResult.AccountNotInLedger;

        var alreadyMember = await _db.UserAccountGroupMembers
            .AnyAsync(m => m.GroupId == groupId && m.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyMember) return AddMemberResult.Ok;

        _db.UserAccountGroupMembers.Add(new UserAccountGroupMemberRow
        {
            GroupId = groupId,
            AccountId = accountId,
            LedgerId = ledgerId,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return AddMemberResult.Ok;
    }

    public enum RemoveMemberResult { Ok, GroupNotFound }

    /// <summary>
    /// Idempotent: removing a non-existent membership returns Ok as
    /// long as the group itself exists; the endpoint surfaces this
    /// as 204.
    /// </summary>
    public async Task<RemoveMemberResult> RemoveMemberAsync(
        Guid userId,
        Guid ledgerId,
        Guid groupId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var groupExists = await _db.UserAccountGroups
            .AnyAsync(g =>
                g.Id == groupId
                && g.UserId == userId
                && g.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!groupExists) return RemoveMemberResult.GroupNotFound;

        await _db.UserAccountGroupMembers
            .Where(m => m.GroupId == groupId && m.AccountId == accountId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return RemoveMemberResult.Ok;
    }
}
