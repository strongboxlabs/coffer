using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Per-ledger tag-dictionary management (Tags v1) — the admin surface over the
/// <c>tags</c> table + <c>txn_header_tags</c> junction that assignment
/// (<see cref="HeaderTags"/>, which every write path calls) and the register
/// filter only ever touched implicitly. List-with-usage, rename / recolor, merge,
/// delete (delete-in-use is allowed — the junction FK is <c>ON DELETE CASCADE</c>),
/// and cleanup-unused. Ledger-scoped; RLS enforces the same isolation at the data
/// layer. Tag names are matched case-insensitively within a ledger (mirroring
/// HeaderTags' resolve), so both writers keep the dictionary free of
/// case-only duplicates. The register's <c>tags</c> view column is computed live
/// from the junction, so every mutation here is reflected with no recompute.
/// </summary>
public sealed class TagsRepository
{
    private readonly AppDbContext _db;

    public TagsRepository(AppDbContext db) => _db = db;

    /// <summary>
    /// Every tag in the ledger with its assignment count, name-sorted
    /// case-insensitively for the management table + the autocomplete list.
    /// </summary>
    /// <remarks>
    /// The count spans BOTH levels since ADR-0103 — header pairings plus leg
    /// pairings. Each junction PK makes its own side distinct, and a transaction
    /// tagged <i>Holiday</i> that also has a posting tagged <i>Holiday</i> counts
    /// twice on purpose: they are two assignments, two things to find, and two
    /// things that go when the tag goes.
    /// <para>Counting headers alone was not merely incomplete — it reported 0 for
    /// a tag used only on postings, which is the number the management table reads
    /// as "safe to delete".</para>
    /// </remarks>
    public async Task<IReadOnlyList<TagDto>> ListWithUsageAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var headerUsage = await _db.TxnHeaderTags.AsNoTracking()
            .Where(x => x.LedgerId == ledgerId)
            .GroupBy(x => x.TagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TagId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        var legUsage = await _db.TxnLegTags.AsNoTracking()
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
            .Select(t => new TagDto(
                t.Id, t.Name, t.Color,
                headerUsage.GetValueOrDefault(t.Id) + legUsage.GetValueOrDefault(t.Id)))
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

        // 1b + 2b) The same two steps on postings (ADR-0103). Without them step 3
        // would DROP every leg pairing instead of merging it: tags.id cascades to
        // txn_leg_tags, so the source tag's postings would lose their attribution
        // with nothing reported — a merge that silently deletes is worse than one
        // that refuses.
        await _db.TxnLegTags
            .Where(x => x.LedgerId == ledgerId && x.TagId == sourceTagId
                        && _db.TxnLegTags.Any(
                            y => y.LegId == x.LegId && y.TagId == intoTagId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        repointed += await _db.TxnLegTags
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
    /// <c>txn_header_tags.tag_id</c> and <c>txn_leg_tags.tag_id</c> FKs are both
    /// <c>ON DELETE CASCADE</c>, so every assignment at either level is removed
    /// with it in one statement.
    /// </summary>
    /// <remarks>
    /// Unchanged by ADR-0103 and correct as it stands: the caller asked for the
    /// tag to go, and taking its postings' attribution with it is what that means.
    /// The count shown next to the button is what has to be right, which is why
    /// <see cref="ListWithUsageAsync"/> spans both levels.
    /// </remarks>
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
    /// <remarks>
    /// "Unused" means unused at BOTH levels (ADR-0103). Checking headers alone made
    /// this the most destructive path in the file: a tag used only on postings has
    /// no header pairing, so it read as an orphan, and deleting it cascaded away
    /// every leg pairing — data loss from a maintenance action whose whole promise
    /// is that it only removes what nothing points at.
    /// </remarks>
    public async Task<int> CleanupUnusedAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        return await _db.Tags
            .Where(t => t.LedgerId == ledgerId
                        && !_db.TxnHeaderTags.Any(x => x.TagId == t.Id)
                        && !_db.TxnLegTags.Any(x => x.TagId == t.Id))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
