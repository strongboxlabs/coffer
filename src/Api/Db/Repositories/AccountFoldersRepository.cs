using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Read and write gateway for the sidebar's folders (mig 258, FU-006):
/// <c>account_folders</c> and <c>account_folder_members</c>. Folders are ledger data,
/// gated by role at the endpoint and by the role-aware RLS pair underneath.
/// </summary>
/// <remarks>
/// <para>Every rule the database enforces is also checked here first, so a refusal is
/// a business result rather than a constraint error: the type match (which the
/// composite foreign keys make a database fact) and a sibling name (a unique index).
/// Those database checks remain the backstop for a race, and their errors map to the
/// same results.</para>
/// <para>A cycle is this class's rule alone (ADR-0032; migration 261 dropped the
/// trigger 258 added): no key can state "not its own ancestor". Only
/// <see cref="MoveAsync"/> names a new parent for an existing folder; a new folder has
/// no descendants, and a delete moves sub-folders up to an ancestor they already
/// have. Both re-parenting writes, move and delete, first lock every folder of the
/// type in the ledger (<see cref="LockSectionAsync"/>), so they run one at a time per
/// section and each walks the tree the last one left. Two moves that would only
/// together make a cycle cannot both pass.</para>
/// <para>Every write that puts something in a folder first locks that folder's row
/// (<see cref="LockFolderAsync"/>) and reads it only after, in the same transaction.
/// A delete's section lock covers that row, so the two run one after the other, and
/// each acts on the folder as the other left it: a delete moves up what was just put
/// in it, and a write into a folder just deleted is refused as not found. What is left
/// (a deadlock, or a reference that a concurrent write broke) maps to
/// <c>Changed</c>, a refusal the caller retries after reloading, never a 500.</para>
/// <para>LINQ filters repeat the ledger scope RLS already applies, so "not found in
/// this ledger" stays a legible answer.</para>
/// </remarks>
public sealed class AccountFoldersRepository
{
    /// <summary>The longest name the table's CHECK accepts.</summary>
    public const int NameMaxLength = 80;

    /// <summary>The account types a folder can hold: the real ones, never
    /// <c>category</c> (mig 258's CHECK).</summary>
    public static readonly IReadOnlySet<string> FolderTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "bank", "credit_card", "investment", "asset", "liability", "loan",
    };

    // Names the database gives the rules this class pre-checks, for mapping a lost race.
    private const string SiblingNameIndex = "uq_account_folders_sibling_name";
    private const string ParentReference = "fk_account_folders_parent";
    private const string FolderReference = "fk_account_folder_members_folder";
    private const string AccountReference = "fk_account_folder_members_account";

    private readonly AppDbContext _db;

    public AccountFoldersRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Every folder in the ledger, flat, ordered by name, each with the ids
    /// of the accounts directly in it.</summary>
    public async Task<IReadOnlyList<AccountFolderDto>> ListAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var folders = await _db.AccountFolders.AsNoTracking()
            .Where(f => f.LedgerId == ledgerId)
            .OrderBy(f => f.Name).ThenBy(f => f.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (folders.Count == 0) return Array.Empty<AccountFolderDto>();

        var members = await _db.AccountFolderMembers.AsNoTracking()
            .Where(m => m.LedgerId == ledgerId)
            .OrderBy(m => m.AccountId)
            .Select(m => new { m.FolderId, m.AccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byFolder = members
            .GroupBy(m => m.FolderId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(m => m.AccountId).ToArray());

        return folders.Select(f => ToDto(f, byFolder)).ToList();
    }

    public enum NameCheck { Ok, Required, TooLong }

    /// <summary>The name rule the table's CHECKs state, on the trimmed name.</summary>
    public static NameCheck CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return NameCheck.Required;
        if (trimmed.Length > NameMaxLength) return NameCheck.TooLong;
        return NameCheck.Ok;
    }

    public enum CreateResult { Ok, TypeInvalid, ParentNotFound, TypeMismatch, NameTaken }

    /// <summary>
    /// Create a folder of <paramref name="accountType"/>, at the top level or inside
    /// <paramref name="parentId"/>, which must be a folder of the same type. The name
    /// must already have passed <see cref="CheckName"/>.
    /// </summary>
    public async Task<(CreateResult Result, AccountFolderDto? Folder)> CreateAsync(
        Guid ledgerId, string accountType, string name, Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        if (!FolderTypes.Contains(accountType)) return (CreateResult.TypeInvalid, null);
        var trimmed = name.Trim();

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (parentId is { } pid)
        {
            var parentType = await LockFolderAsync(ledgerId, pid, cancellationToken).ConfigureAwait(false);
            if (parentType is null) return (CreateResult.ParentNotFound, null);
            if (parentType != accountType) return (CreateResult.TypeMismatch, null);
        }

        if (await SiblingNameTakenAsync(ledgerId, accountType, parentId, trimmed, exceptId: null, cancellationToken)
                .ConfigureAwait(false))
            return (CreateResult.NameTaken, null);

        var row = new AccountFolderRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            AccountType = accountType,
            Name = trimmed,
            ParentId = parentId,
        };
        _db.AccountFolders.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (Violates(ex, SiblingNameIndex))
        {
            return (CreateResult.NameTaken, null);
        }
        return (CreateResult.Ok, ToDto(row, members: null));
    }

    public enum RenameResult { Ok, NotFound, NameTaken }

    /// <summary>Rename a folder; the name must already have passed
    /// <see cref="CheckName"/>.</summary>
    public async Task<(RenameResult Result, AccountFolderDto? Folder)> RenameAsync(
        Guid ledgerId, Guid folderId, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        var folder = await _db.AccountFolders
            .FirstOrDefaultAsync(f => f.Id == folderId && f.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (folder is null) return (RenameResult.NotFound, null);

        if (await SiblingNameTakenAsync(ledgerId, folder.AccountType, folder.ParentId, trimmed, folderId, cancellationToken)
                .ConfigureAwait(false))
            return (RenameResult.NameTaken, null);

        folder.Name = trimmed;
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by another editor since the read above.
            return (RenameResult.NotFound, null);
        }
        catch (DbUpdateException ex) when (Violates(ex, SiblingNameIndex))
        {
            return (RenameResult.NameTaken, null);
        }

        var accountIds = await _db.AccountFolderMembers.AsNoTracking()
            .Where(m => m.FolderId == folderId)
            .OrderBy(m => m.AccountId)
            .Select(m => m.AccountId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return (RenameResult.Ok, new AccountFolderDto(
            folder.Id, folder.AccountType, folder.ParentId, folder.Name, accountIds));
    }

    public enum MoveResult { Ok, NotFound, ParentNotFound, TypeMismatch, Cycle, NameTaken, Changed }

    /// <summary>
    /// Move a folder into <paramref name="parentId"/>, or to the top level of its
    /// type's section with null. Refuses a parent of another type, and the folder
    /// itself or any of its descendants as the parent.
    /// </summary>
    public async Task<MoveResult> MoveAsync(
        Guid ledgerId, Guid folderId, Guid? parentId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // The whole section is locked, not just the folder and its new parent, and
            // before anything but the folder's account type is read (the type never
            // changes, so reading it unlocked is safe). Two moves that share no folder (A into B while C, an
            // ancestor of B, goes into D, a descendant of A) would each pass the walk
            // below against the tree as it was and together close a cycle. Under the
            // section lock the second waits, then walks the tree the first left.
            var folderType = await LockSectionAsync(ledgerId, folderId, cancellationToken).ConfigureAwait(false);
            if (folderType is null) return MoveResult.NotFound;

            var folder = await _db.AccountFolders
                .FirstAsync(f => f.Id == folderId, cancellationToken)
                .ConfigureAwait(false);
            if (folder.ParentId == parentId) return MoveResult.Ok;

            if (parentId is { } pid)
            {
                if (pid == folderId) return MoveResult.Cycle;

                // A parent of this type is in the locked section; one of another type is
                // only refused, so it needs no lock.
                var parentType = await _db.AccountFolders.AsNoTracking()
                    .Where(f => f.Id == pid && f.LedgerId == ledgerId)
                    .Select(f => f.AccountType)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (parentType is null) return MoveResult.ParentNotFound;
                if (parentType != folder.AccountType) return MoveResult.TypeMismatch;

                // One type's folders are few, so the ancestor walk runs in memory.
                var parents = await _db.AccountFolders.AsNoTracking()
                    .Where(f => f.LedgerId == ledgerId && f.AccountType == folder.AccountType)
                    .Select(f => new { f.Id, f.ParentId })
                    .ToDictionaryAsync(f => f.Id, cancellationToken)
                    .ConfigureAwait(false);

                // The walk is bounded by the number of folders, so a cycle already in
                // the data (one a direct SQL write made) could not hang it.
                Guid? cursor = pid;
                for (var steps = 0; cursor is { } c && steps <= parents.Count; steps++)
                {
                    if (c == folderId) return MoveResult.Cycle;
                    cursor = parents.TryGetValue(c, out var next) ? next.ParentId : null;
                }
            }

            if (await SiblingNameTakenAsync(ledgerId, folder.AccountType, parentId, folder.Name, folderId, cancellationToken)
                    .ConfigureAwait(false))
                return MoveResult.NameTaken;

            folder.ParentId = parentId;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Violates(ex, SiblingNameIndex))
        {
            return MoveResult.NameTaken;
        }
        catch (Exception ex) when (LostRace(ex))
        {
            return MoveResult.Changed;
        }
        return MoveResult.Ok;
    }

    public enum DeleteResult { Ok, NotFound, NameTaken, Changed }

    /// <summary>
    /// Delete a folder. Its sub-folders and its accounts move up to its parent (to the
    /// top level when it has none) in the same transaction, so nothing but the folder
    /// itself is deleted. Refused with <see cref="DeleteResult.NameTaken"/> when a
    /// sub-folder would land beside a folder of the same name, and with
    /// <see cref="DeleteResult.Changed"/> when a concurrent write to the same folders
    /// got in the way; either way nothing is changed.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(
        Guid ledgerId, Guid folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Locked before anything is read, so the parent the contents move to is
            // the folder's parent as it is now, not a copy read before another editor
            // moved it, and a move of the folder or a write into it waits for this
            // transaction. The whole section, as a move locks it: moving the sub-folders
            // up re-parents them, and a lock on this row alone would let a move that
            // locked them first deadlock with it.
            var accountType = await LockSectionAsync(ledgerId, folderId, cancellationToken).ConfigureAwait(false);
            if (accountType is null) return DeleteResult.NotFound;
            var newParentId = await _db.AccountFolders.AsNoTracking()
                .Where(f => f.Id == folderId)
                .Select(f => f.ParentId)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            var childNames = await _db.AccountFolders.AsNoTracking()
                .Where(f => f.LedgerId == ledgerId && f.ParentId == folderId)
                .Select(f => f.Name.ToLower())
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (childNames.Count > 0)
            {
                // The folder being deleted is not counted: it is gone by the end.
                var clash = await _db.AccountFolders.AsNoTracking()
                    .AnyAsync(f => f.LedgerId == ledgerId
                                   && f.AccountType == accountType
                                   && f.ParentId == newParentId
                                   && f.Id != folderId
                                   && childNames.Contains(f.Name.ToLower()),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (clash) return DeleteResult.NameTaken;

                // A sub-folder may share the deleted folder's own name, and the index
                // would refuse it beside a folder that still exists until the delete
                // below. The id is unique and fits the name's length limit, and the
                // transaction means nobody sees it.
                var placeholder = folderId.ToString();
                await _db.AccountFolders
                    .Where(f => f.Id == folderId)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, placeholder), cancellationToken)
                    .ConfigureAwait(false);
                await _db.AccountFolders
                    .Where(f => f.LedgerId == ledgerId && f.ParentId == folderId)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, newParentId), cancellationToken)
                    .ConfigureAwait(false);
            }

            // The accounts move up as well; the top level is "no row".
            if (newParentId is { } parentId)
            {
                await _db.AccountFolderMembers
                    .Where(m => m.FolderId == folderId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.FolderId, parentId), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _db.AccountFolderMembers
                    .Where(m => m.FolderId == folderId)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Only an empty folder is deleted. Both references to it cascade, so
            // anything a statement above did not see would otherwise go with it.
            var deleted = await _db.AccountFolders
                .Where(f => f.Id == folderId
                            && !_db.AccountFolders.Any(c => c.ParentId == folderId)
                            && !_db.AccountFolderMembers.Any(m => m.FolderId == folderId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            if (deleted == 0) return DeleteResult.Changed;

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Violates(ex, SiblingNameIndex))
        {
            return DeleteResult.NameTaken;
        }
        catch (Exception ex) when (LostRace(ex))
        {
            return DeleteResult.Changed;
        }
        return DeleteResult.Ok;
    }

    public enum SetFolderResult { Ok, AccountNotInLedger, AccountIsCategory, AccountIsSystem, FolderNotFound, TypeMismatch }

    /// <summary>
    /// Put an account in <paramref name="folderId"/>, or take it out of any folder
    /// with null. Only real, non-system accounts go in folders, and only in a folder
    /// of their own type. Inactive accounts may be moved: they keep their place.
    /// </summary>
    public async Task<SetFolderResult> SetAccountFolderAsync(
        Guid ledgerId, Guid accountId, Guid? folderId, CancellationToken cancellationToken = default)
    {
        var account = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.IsSystem })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return SetFolderResult.AccountNotInLedger;
        if (account.AccountType == "category") return SetFolderResult.AccountIsCategory;
        if (account.IsSystem) return SetFolderResult.AccountIsSystem;

        if (folderId is not { } fid)
        {
            await _db.AccountFolderMembers
                .Where(m => m.AccountId == accountId && m.LedgerId == ledgerId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            return SetFolderResult.Ok;
        }

        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Locked, so a delete of this folder either moves this account up with the
            // rest of its contents or has already happened and this is not found.
            var folderType = await LockFolderAsync(ledgerId, fid, cancellationToken).ConfigureAwait(false);
            if (folderType is null) return SetFolderResult.FolderNotFound;
            if (folderType != account.AccountType) return SetFolderResult.TypeMismatch;

            var moved = await _db.AccountFolderMembers
                .Where(m => m.AccountId == accountId && m.LedgerId == ledgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.FolderId, fid), cancellationToken)
                .ConfigureAwait(false);
            if (moved == 0)
            {
                _db.AccountFolderMembers.Add(new AccountFolderMemberRow
                {
                    AccountId = accountId,
                    LedgerId = ledgerId,
                    FolderId = fid,
                    AccountType = account.AccountType,
                });
                try
                {
                    await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex) when (Violates(ex, "pk_account_folder_members"))
                {
                    // Another request filed the account first; this one's choice is the
                    // later write, so it wins, as an update would have. SaveChanges
                    // rolled back to its own savepoint, so the transaction is usable.
                    _db.ChangeTracker.Clear();
                    await _db.AccountFolderMembers
                        .Where(m => m.AccountId == accountId && m.LedgerId == ledgerId)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.FolderId, fid), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Violates(ex, AccountReference))
        {
            // The account was deleted since the read above.
            return SetFolderResult.AccountNotInLedger;
        }
        return SetFolderResult.Ok;
    }

    /// <summary>
    /// Lock a folder's row until the transaction ends and return its type, or null when
    /// it is not in the ledger. A foreign-key check takes only a share lock, which a
    /// delete's first statements do not wait for; this lock they do. An UPDATE is the
    /// row lock LINQ can take, so the name is set to itself. Call it inside a
    /// transaction, or the lock ends with the statement.
    /// </summary>
    private async Task<string?> LockFolderAsync(Guid ledgerId, Guid folderId, CancellationToken cancellationToken)
    {
        var locked = await _db.AccountFolders
            .Where(f => f.Id == folderId && f.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, f => f.Name), cancellationToken)
            .ConfigureAwait(false);
        if (locked == 0) return null;
        return await _db.AccountFolders.AsNoTracking()
            .Where(f => f.Id == folderId)
            .Select(f => f.AccountType)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lock every folder of <paramref name="folderId"/>'s type in the ledger, its
    /// section of the sidebar, until the transaction ends, and return that type, or
    /// null when the folder is not in the ledger (or was deleted while this waited).
    /// The row lock <see cref="LockFolderAsync"/> takes, over the section: a cycle can
    /// only form inside one type (the parent reference carries it), so the section is
    /// the whole of what a re-parenting write must keep still. The type never changes,
    /// so it is read before the lock. Call it inside a transaction.
    /// </summary>
    private async Task<string?> LockSectionAsync(Guid ledgerId, Guid folderId, CancellationToken cancellationToken)
    {
        var accountType = await _db.AccountFolders.AsNoTracking()
            .Where(f => f.Id == folderId && f.LedgerId == ledgerId)
            .Select(f => f.AccountType)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (accountType is null) return null;

        await _db.AccountFolders
            .Where(f => f.LedgerId == ledgerId && f.AccountType == accountType)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, f => f.Name), cancellationToken)
            .ConfigureAwait(false);

        // A row deleted while the lock waited is skipped, not locked.
        var present = await _db.AccountFolders.AsNoTracking()
            .AnyAsync(f => f.Id == folderId, cancellationToken)
            .ConfigureAwait(false);
        return present ? accountType : null;
    }

    private Task<bool> SiblingNameTakenAsync(
        Guid ledgerId, string accountType, Guid? parentId, string trimmedName, Guid? exceptId,
        CancellationToken cancellationToken)
    {
        // Both sides lowered by Postgres, the function the unique index uses.
        return _db.AccountFolders.AsNoTracking()
            .AnyAsync(f => f.LedgerId == ledgerId
                           && f.AccountType == accountType
                           && f.ParentId == parentId
                           && (exceptId == null || f.Id != exceptId)
                           && f.Name.ToLower() == trimmedName.ToLower(),
                cancellationToken);
    }

    private static bool Violates(Exception ex, string constraint)
    {
        var pg = Postgres(ex);
        return pg is not null
            && pg.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation
                or PostgresErrorCodes.ForeignKeyViolation
            && pg.ConstraintName == constraint;
    }

    /// <summary>A refusal that only a concurrent write to the same folders can cause
    /// once the API's checks have passed: a deadlock, or a folder reference broken
    /// under this transaction. Nothing was written; reloading and retrying is the
    /// answer.</summary>
    private static bool LostRace(Exception ex)
    {
        var pg = Postgres(ex);
        return pg is not null
            && (pg.SqlState == PostgresErrorCodes.DeadlockDetected
                || (pg.SqlState == PostgresErrorCodes.ForeignKeyViolation
                    && pg.ConstraintName is ParentReference or FolderReference));
    }

    private static PostgresException? Postgres(Exception ex) =>
        ex as PostgresException ?? ex.InnerException as PostgresException;

    private static AccountFolderDto ToDto(AccountFolderRow f, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? members) =>
        new(f.Id, f.AccountType, f.ParentId, f.Name,
            members is not null && members.TryGetValue(f.Id, out var ids) ? ids : Array.Empty<Guid>());
}
