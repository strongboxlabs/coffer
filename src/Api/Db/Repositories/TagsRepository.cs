using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Per-ledger tag-dictionary management (Tags v1) — the admin surface over the
/// <c>tags</c> table + <c>txn_header_tags</c> junction that assignment
/// (<see cref="TransactionsRepository"/>'s <c>ApplyTagsAsync</c>) and the register
/// filter only ever touched implicitly. List-with-usage, rename / recolor, merge,
/// delete (delete-in-use is allowed — the junction FK is <c>ON DELETE CASCADE</c>),
/// and cleanup-unused. Ledger-scoped; RLS enforces the same isolation at the data
/// layer. Tag names are matched case-insensitively within a ledger (mirroring
/// ApplyTagsAsync's resolve), so both writers keep the dictionary free of
/// case-only duplicates. The register's <c>tags</c> view column is computed live
/// from the junction, so every mutation here is reflected with no recompute.
/// </summary>
public sealed class TagsRepository
{
    private readonly AppDbContext _db;

    public TagsRepository(AppDbContext db) => _db = db;

    /// <summary>
    /// Every tag in the ledger with its assignment count (distinct header
    /// pairings — the junction PK is <c>(header_id, tag_id)</c>, so a plain count
    /// is already distinct), name-sorted case-insensitively for the management
    /// table + the autocomplete list.
    /// </summary>
    public async Task<IReadOnlyList<TagDto>> ListWithUsageAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var usage = await _db.TxnHeaderTags.AsNoTracking()
            .Where(x => x.LedgerId == ledgerId)
            .GroupBy(x => x.TagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TagId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        var tags = await _db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .Select(t => new { t.Id, t.Name, t.Color })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return tags
            .Select(t => new TagDto(t.Id, t.Name, t.Color, usage.GetValueOrDefault(t.Id)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Outcome of <see cref="PatchAsync"/>.</summary>
    public enum PatchTagResult { Ok, NotFound, NameExists }

    /// <summary>
    /// Rename and/or recolor a tag. A rename is rejected with
    /// <see cref="PatchTagResult.NameExists"/> when another tag in the ledger
    /// already carries that name (case-insensitive, matching assignment's
    /// resolve) — the UI offers a merge instead; a case-only self-rename
    /// (<c>work</c> → <c>Work</c>) is allowed. <paramref name="newName"/> /
    /// <paramref name="newColor"/> are already trimmed / validated by the caller;
    /// a <c>null</c> means "leave unchanged" and at least one is non-null.
    /// </summary>
    public async Task<PatchTagResult> PatchAsync(
        Guid ledgerId, Guid tagId, string? newName, string? newColor,
        CancellationToken cancellationToken = default)
    {
        var tag = await _db.Tags
            .Where(t => t.LedgerId == ledgerId && t.Id == tagId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (tag is null)
            return PatchTagResult.NotFound;

        if (newName is not null)
        {
            var lower = newName.ToLowerInvariant();
            var clash = await _db.Tags
                .Where(t => t.LedgerId == ledgerId && t.Id != tagId
                            && t.Name.ToLower() == lower)
                .AnyAsync(cancellationToken).ConfigureAwait(false);
            if (clash)
                return PatchTagResult.NameExists;
            tag.Name = newName;
        }

        if (newColor is not null)
            tag.Color = newColor;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return PatchTagResult.Ok;
    }

    /// <summary>Outcome of <see cref="MergeAsync"/>.</summary>
    public enum MergeTagResult { Ok, MergeSelf, SourceNotFound, TargetNotFound }

    /// <summary>Assignments repointed by a merge (for the API echo).</summary>
    public sealed record MergeTagOutcome(MergeTagResult Result, int TransactionsRepointed);

    /// <summary>
    /// Merge tag <paramref name="sourceTagId"/> into
    /// <paramref name="intoTagId"/>: repoint every source assignment to the
    /// target, then delete the (now-unreferenced) source tag. Headers already
    /// carrying the target keep a single assignment (the source pairing is
    /// dropped rather than repointed, which would collide on the
    /// <c>(header_id, tag_id)</c> PK). Atomic.
    /// </summary>
    public async Task<MergeTagOutcome> MergeAsync(
        Guid ledgerId, Guid sourceTagId, Guid intoTagId,
        CancellationToken cancellationToken = default)
    {
        if (sourceTagId == intoTagId)
            return new MergeTagOutcome(MergeTagResult.MergeSelf, 0);

        var sourceExists = await _db.Tags.AsNoTracking()
            .AnyAsync(t => t.LedgerId == ledgerId && t.Id == sourceTagId, cancellationToken)
            .ConfigureAwait(false);
        if (!sourceExists)
            return new MergeTagOutcome(MergeTagResult.SourceNotFound, 0);

        var targetExists = await _db.Tags.AsNoTracking()
            .AnyAsync(t => t.LedgerId == ledgerId && t.Id == intoTagId, cancellationToken)
            .ConfigureAwait(false);
        if (!targetExists)
            return new MergeTagOutcome(MergeTagResult.TargetNotFound, 0);

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 1) Drop source pairings on headers that ALREADY carry the target —
        //    repointing them would collide on the (header_id, tag_id) PK.
        await _db.TxnHeaderTags
            .Where(x => x.LedgerId == ledgerId && x.TagId == sourceTagId
                        && _db.TxnHeaderTags.Any(
                            y => y.HeaderId == x.HeaderId && y.TagId == intoTagId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // 2) Repoint the remaining source pairings to the target.
        var repointed = await _db.TxnHeaderTags
            .Where(x => x.LedgerId == ledgerId && x.TagId == sourceTagId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TagId, intoTagId), cancellationToken)
            .ConfigureAwait(false);

        // 3) Delete the now-unreferenced source tag.
        await _db.Tags
            .Where(t => t.LedgerId == ledgerId && t.Id == sourceTagId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MergeTagOutcome(MergeTagResult.Ok, repointed);
    }

    /// <summary>Outcome of <see cref="DeleteAsync"/>.</summary>
    public enum DeleteTagResult { Ok, NotFound }

    /// <summary>
    /// Hard-delete a tag (delete-in-use allowed, per the Tags v1 decision): the
    /// <c>txn_header_tags.tag_id</c> FK is <c>ON DELETE CASCADE</c>, so every
    /// assignment is removed with it in one statement.
    /// </summary>
    public async Task<DeleteTagResult> DeleteAsync(
        Guid ledgerId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var deleted = await _db.Tags
            .Where(t => t.LedgerId == ledgerId && t.Id == tagId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0 ? DeleteTagResult.Ok : DeleteTagResult.NotFound;
    }

    /// <summary>Delete every tag in the ledger with zero assignments (orphans left
    /// by prior untag/removals). Returns the count removed.</summary>
    public async Task<int> CleanupUnusedAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        return await _db.Tags
            .Where(t => t.LedgerId == ledgerId
                        && !_db.TxnHeaderTags.Any(x => x.TagId == t.Id))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
