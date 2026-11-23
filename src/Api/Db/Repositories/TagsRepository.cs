using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Per-ledger tag-dictionary management (Tags v1) — the admin surface over the
/// <c>tags</c> table + the <c>txn_header_tags</c> and <c>txn_leg_tags</c> junctions
/// that assignment (<see cref="AnnotationWriter"/>, which every write path calls) and
/// the register filter only ever touched implicitly. List-with-usage, create, rename / recolor,
/// merge, delete (delete-in-use is allowed — the junction FK is <c>ON DELETE CASCADE</c>),
/// and cleanup-unused. Ledger-scoped; RLS enforces the same isolation at the data
/// layer. Two names are the same tag when their <c>tag_name_key</c> is equal
/// (migration 263), the key assignment resolves through as well
/// (<see cref="TagDictionary"/>), so neither writer adds a name that differs from
/// another tag's only by case or surrounding white space. Rows read their tags from
/// the stored presentation (<c>txn_leg_note_tags</c>), which names a tag by id: a
/// rename or recolor changes nothing stored there, and a merge or delete recomputes
/// the headers that carried the tag (<see cref="LegNotes"/>) in its transaction.
/// </summary>
public sealed class TagsRepository
{
    private readonly AppDbContext _db;
    private readonly ILogger<TagsRepository> _logger;

    public TagsRepository(AppDbContext db, ILogger<TagsRepository>? logger = null)
    {
        _db = db;
        _logger = logger ?? NullLogger<TagsRepository>.Instance;
    }

    /// <summary>
    /// Every tag in the ledger with its assignment count and the number of transactions it is
    /// in play on, name-sorted case-insensitively for the management table + the autocomplete
    /// list.
    /// </summary>
    /// <remarks>
    /// The count spans BOTH levels since ADR-0103 — header pairings plus leg
    /// pairings. Each junction PK makes its own side distinct, and a transaction
    /// tagged <i>Holiday</i> that also has a posting tagged <i>Holiday</i> counts
    /// twice on purpose: they are two assignments, two things to find, and two
    /// things that go when the tag goes.
    /// <para>Counting headers alone is not merely incomplete — it reports 0 for
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

        // Distinct transactions, the number a reader means: the ones the tag is in play on a line
        // of, which is what the tag's own page lists (ADR-0111 D5, D6). It is read from the stored
        // presentation (txn_leg_note_tags), the one place that says which tags a line shows once
        // its own tags, the ones it inherits from its category side, and a split's
        // transaction-level ones are resolved, so a tag a register hides behind the line's own tags
        // is not counted and the number cannot say more than the list can show. The category
        // side of a transaction is never a line of the page, and a reminder template, a hidden row
        // and a row merged away are not transactions a register shows (the tests
        // ReportingRepository.ListTransactionsAsync lists lines under).
        var transactionUsage = await (
                from n in _db.TxnLegNoteTags.AsNoTracking()
                join l in _db.TxnLegs.AsNoTracking() on n.LegId equals l.Id
                join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                join h in _db.TxnHeaders.AsNoTracking() on n.HeaderId equals h.Id
                where n.LedgerId == ledgerId && n.InPlay && a.AccountType != "category"
                      && !h.IsRecurringTemplate && !h.IsHidden && h.IsMergedInto == null
                select new { n.TagId, n.HeaderId })
            .Distinct()
            .GroupBy(p => p.TagId)
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
                headerUsage.GetValueOrDefault(t.Id) + legUsage.GetValueOrDefault(t.Id),
                transactionUsage.GetValueOrDefault(t.Id)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether the ledger has this tag.</summary>
    public Task<bool> ExistsAsync(Guid ledgerId, Guid tagId, CancellationToken cancellationToken = default) =>
        _db.Tags.AsNoTracking().AnyAsync(t => t.LedgerId == ledgerId && t.Id == tagId, cancellationToken);

    /// <summary>Outcome of <see cref="CreateAsync"/>.</summary>
    public enum CreateTagResult { Ok, NameExists }

    /// <summary>A tag just made, or why none was.</summary>
    public sealed record CreateTagOutcome(CreateTagResult Result, TagDto? Tag);

    /// <summary>
    /// Make a tag with this name (and colour, when one is given). A name another tag already
    /// carries under <c>tag_name_key</c> (case and surrounding white space aside, as assignment
    /// resolves names) is refused with <see cref="CreateTagResult.NameExists"/>. The tag starts
    /// used nowhere. <paramref name="name"/> and <paramref name="color"/> are already trimmed
    /// and validated by the caller.
    /// </summary>
    public async Task<CreateTagOutcome> CreateAsync(
        Guid ledgerId, string name, string? color, CancellationToken cancellationToken = default)
    {
        var clash = await _db.Tags
            .Where(t => t.LedgerId == ledgerId
                        && AppDbContext.TagNameKey(t.Name) == AppDbContext.TagNameKey(name))
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (clash)
            return new CreateTagOutcome(CreateTagResult.NameExists, null);

        var row = new Entities.TagRow { Id = Guid.NewGuid(), LedgerId = ledgerId, Name = name, Color = color };
        _db.Tags.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TagDictionary.IsNameConflict(ex))
        {
            return new CreateTagOutcome(CreateTagResult.NameExists, null);
        }

        // Another writer that took the name between the check and the save wins quietly:
        // the context re-reads and DETACHES a pending tag whose key now exists, and the save
        // then reports success having inserted nothing. This tag was never made.
        if (_db.Entry(row).State == EntityState.Detached)
            return new CreateTagOutcome(CreateTagResult.NameExists, null);

        return new CreateTagOutcome(CreateTagResult.Ok, new TagDto(row.Id, row.Name, row.Color, 0, 0));
    }

    /// <summary>Outcome of <see cref="PatchAsync"/>.</summary>
    public enum PatchTagResult { Ok, NotFound, NameExists }

    /// <summary>
    /// Rename and/or recolor a tag. A rename is rejected with
    /// <see cref="PatchTagResult.NameExists"/> when another tag in the ledger
    /// already carries that name under <c>tag_name_key</c> (case and surrounding
    /// white space aside, as assignment resolves names) — the UI offers a merge
    /// instead; a case-only self-rename
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
            // Both keys computed in SQL by tag_name_key, the one definition of "the
            // same name". t.Id != tagId keeps a case-only self-rename (work to Work).
            var clash = await _db.Tags
                .Where(t => t.LedgerId == ledgerId && t.Id != tagId
                            && AppDbContext.TagNameKey(t.Name) == AppDbContext.TagNameKey(newName))
                .AnyAsync(cancellationToken).ConfigureAwait(false);
            if (clash)
                return PatchTagResult.NameExists;
            tag.Name = newName;
        }

        if (newColor is not null)
            tag.Color = newColor;

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TagDictionary.IsNameConflict(ex))
        {
            // Another writer took the name between the check and the save. The same
            // answer the check gives, never a 500.
            return PatchTagResult.NameExists;
        }
        return PatchTagResult.Ok;
    }

    /// <summary>Outcome of <see cref="MergeAsync"/>.</summary>
    public enum MergeTagResult { Ok, MergeSelf, SourceNotFound, TargetNotFound }

    /// <summary>What a merge changed, for the API echo.</summary>
    /// <param name="TransactionsRepointed">Assignments repointed.</param>
    /// <param name="SavedReportsRepointed">Saved reports repointed that the merging user
    /// can see (their own and the shared ones). Another member's private report is
    /// repointed too but not counted here, so the echo never reveals it exists.</param>
    public sealed record MergeTagOutcome(MergeTagResult Result, int TransactionsRepointed, int SavedReportsRepointed = 0);

    /// <summary>
    /// Merge tag <paramref name="sourceTagId"/> into
    /// <paramref name="intoTagId"/>: repoint every source assignment to the
    /// target, repoint every saved report of the ledger that filters on the source,
    /// then delete the (now-unreferenced) source tag. Headers already
    /// carrying the target keep a single assignment (the source pairing is
    /// dropped rather than repointed, which would collide on the
    /// <c>(header_id, tag_id)</c> PK). Atomic.
    /// </summary>
    /// <remarks>
    /// <para>A saved report stores the tag it filters on by id inside its spec, the
    /// one tag reference no foreign key covers, so without the repoint a report on the
    /// source would silently match nothing once the source is gone. Saved-report
    /// writes are owner-only under RLS, so the repoint goes through the one privileged
    /// function that reaches every member's reports (<c>saved_reports_repoint_tag</c>),
    /// which checks the caller's owner or editor grant itself. Everything else here
    /// stays under RLS.</para>
    /// </remarks>
    public async Task<MergeTagOutcome> MergeAsync(
        Guid ledgerId, Guid sourceTagId, Guid intoTagId,
        CancellationToken cancellationToken = default)
    {
        if (sourceTagId == intoTagId)
            return new MergeTagOutcome(MergeTagResult.MergeSelf, 0);

        // The ledger write lock (migration 275) first. The merge locks the source, then the
        // headers it recomputes; a write that holds the lock and a header inserts a link to
        // the source, which waits for this lock on it. Taking the ledger's lock before either
        // lets the two take turns instead of each waiting for the other, and the checks below
        // read what a write the merge waited for committed.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

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

        // Every transaction that carries the source at either level shows a tag that is
        // about to change id; read before the repoint, which leaves no trace of it. The
        // source is locked first, so no link to it is added between this read and the
        // repoint, which would move it without its recompute (fn_lock_tags).
        _ = await _db.LockTags([sourceTagId]).Select(r => r.Locked).SingleAsync(cancellationToken).ConfigureAwait(false);
        var touched = await HeadersCarryingAsync(ledgerId, sourceTagId, cancellationToken).ConfigureAwait(false);

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

        // 2c) Saved reports filtering on the source, every member's, before the
        //     source goes. Inside this transaction, so a failed delete rolls it back.
        var reports = await _db.SavedReportsRepointTag(ledgerId, sourceTagId, intoTagId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Tag merge repointed {SavedReports} saved report(s) in ledger {LedgerId}",
            reports.Repointed, ledgerId);

        // 3) Delete the now-unreferenced source tag.
        await _db.Tags
            .Where(t => t.LedgerId == ledgerId && t.Id == sourceTagId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await LegNotes.RecomputeAsync(_db, touched, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MergeTagOutcome(MergeTagResult.Ok, repointed, reports.RepointedVisible);
    }

    /// <summary>The headers that carry <paramref name="tagId"/> on themselves or on a leg.</summary>
    private async Task<List<Guid>> HeadersCarryingAsync(Guid ledgerId, Guid tagId, CancellationToken cancellationToken)
    {
        var onHeaders = await _db.TxnHeaderTags.AsNoTracking()
            .Where(x => x.LedgerId == ledgerId && x.TagId == tagId)
            .Select(x => x.HeaderId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var onLegs = await _db.TxnLegTags.AsNoTracking()
            .Where(x => x.LedgerId == ledgerId && x.TagId == tagId)
            .Join(_db.TxnLegs, x => x.LegId, l => l.Id, (x, l) => l.HeaderId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. onHeaders.Concat(onLegs).Distinct()];
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
        // The ledger write lock first, as the merge takes it: the delete locks the tag, then
        // the headers it recomputes.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        // Its assignments go with it, which can change where a remaining leg's shown tags
        // come from (an own set emptied inherits); read who carried it before they go,
        // with the tag locked so no assignment is added after the read (fn_lock_tags).
        _ = await _db.LockTags([tagId]).Select(r => r.Locked).SingleAsync(cancellationToken).ConfigureAwait(false);
        var touched = await HeadersCarryingAsync(ledgerId, tagId, cancellationToken).ConfigureAwait(false);
        var deleted = await _db.Tags
            .Where(t => t.LedgerId == ledgerId && t.Id == tagId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await LegNotes.RecomputeAsync(_db, touched, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0 ? DeleteTagResult.Ok : DeleteTagResult.NotFound;
    }

    /// <summary>Delete every tag in the ledger with zero assignments (orphans left
    /// by prior untag/removals). Returns the count removed.</summary>
    /// <remarks>
    /// <para>"Unused" means unused at BOTH levels (ADR-0103). Checking headers alone made
    /// this the most destructive path in the file: a tag used only on postings has
    /// no header pairing, so it read as an orphan, and deleting it cascaded away
    /// every leg pairing — data loss from a maintenance action whose whole promise
    /// is that it only removes what nothing points at.</para>
    /// <para>A tag another request is assigning is not unused. A single delete would not
    /// see that request's uncommitted link, wait for it, and delete the tag anyway, taking
    /// the new link with it. So the candidates are read, locked (fn_lock_tags: a link in
    /// flight is waited for, a new one waits), and deleted only if still unused, in a fresh
    /// statement that reads what those requests committed.</para>
    /// </remarks>
    public async Task<int> CleanupUnusedAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        // The ledger write lock first, as the merge and the delete take it: the clean-up
        // locks the tags it deletes, which a write that holds the lock may be linking.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var candidates = await _db.Tags.Where(IsUnused(ledgerId)).Select(t => t.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Length == 0) return 0;
        _ = await _db.LockTags(candidates).Select(r => r.Locked).SingleAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await _db.Tags
            .Where(IsUnused(ledgerId))
            .Where(t => candidates.Contains(t.Id))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>A tag of <paramref name="ledgerId"/> no header or leg link names.</summary>
    private System.Linq.Expressions.Expression<Func<Entities.TagRow, bool>> IsUnused(Guid ledgerId) =>
        t => t.LedgerId == ledgerId
             && !_db.TxnHeaderTags.Any(x => x.TagId == t.Id)
             && !_db.TxnLegTags.Any(x => x.TagId == t.Id);
}
