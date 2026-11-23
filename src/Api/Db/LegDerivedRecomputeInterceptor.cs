using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Db;

/// <summary>
/// EF Core <c>SaveChangesInterceptor</c> that re-derives the two
/// leg-derived denormalizations automatically after every API write
/// that touches <c>txn_legs</c> or <c>txn_headers</c>: the running
/// balance on <c>txn_header_account_balances</c> (mig 102, ADR-0034) and
/// the posting counts
/// (<c>account_postings_on_header</c> / <c>header_total_postings</c>) on
/// <c>txn_legs</c> (mig 120, ADR-0036). Both derive from the same
/// structural changes, so one snapshot drives both recomputes.
/// </summary>
/// <remarks>
/// <para><b>Why an interceptor (and not a Postgres trigger)</b>. A
/// trigger family (mig 090 / 094 / 099 / 101) has failure modes
/// under EF's batched <c>SaveChanges</c> (ADR-0034): cascade-from-header
/// DELETE order, override-on-override interactions, and AFTER STATEMENT
/// triggers whose transition tables don't span the batch. Each fix adds
/// trigger surface and each new surface brings new edge cases. None of
/// those failure modes exists here:</para>
///
/// <list type="bullet">
///   <item><description>This runs ONCE per <c>SaveChanges</c>, after
///   the save's statements, inside its transaction
///   (<see cref="LegNotesRecomputeInterceptor"/>). The ChangeTracker
///   gives a single consistent snapshot.</description></item>
///   <item><description>FK cascade DELETEs are handled explicitly:
///   when a header is being deleted, we read its legs from the live
///   DB inside <see cref="SavingChangesAsync"/> (before the cascade
///   has run) so the affected-accounts set still includes the legs
///   that are about to vanish.</description></item>
///   <item><description>Recompute is a regular SQL function call from
///   C#, not a trigger; it can't re-fire this interceptor.</description></item>
///   <item><description>It's in the codebase — F12-able, debuggable,
///   PR-reviewable. Not buried in <c>pg_proc</c>.</description></item>
/// </list>
///
/// <para><b>Every writer's responsibility</b>. Any repository method
/// that mutates the relevant tables MUST reach <c>SaveChangesAsync</c>
/// on the shared <see cref="AppDbContext"/> for its writes (which is
/// the standard pattern). No explicit recompute call is needed. The
/// interceptor reads <see cref="ChangeTracker"/> entries on those
/// tables, identifies the affected accounts + headers, and invokes
/// <see cref="LegDerivedRecomputeService"/> (both balance and
/// posting-count recompute) after the save commits but before the
/// caller's transaction commits — atomic with the write.</para>
///
/// <para><b>Which changes count</b>. An added or deleted leg always
/// counts. A modified leg counts only when a property the recomputes
/// read changed: <see cref="NonRecomputeLegProperties"/> lists the ones
/// they never read (its memo, the stored memo-and-tags presentation, the
/// investment metadata), and a change to any other property counts.
/// The list names what does NOT count, so a new column recomputes until
/// someone shows the recomputes ignore it; a guard test holds every
/// <see cref="TxnLegRow"/> property to one of the two sets and the
/// ignored set to the recompute functions' SQL. A modified header counts
/// only when a property in <see cref="RecomputeHeaderProperties"/>
/// changed (<c>posted_at</c>, <c>is_merged_into</c>, <c>is_hidden</c>),
/// and then counts for every leg it has. The balance recompute re-walks
/// the account from the posting date, so a change the walk cannot see
/// must not start one. A second guard test holds that set to the header
/// columns the recompute SQL names that can change after insert.</para>
///
/// <para>If you ARE going to write a leg-affecting mutation via
/// raw SQL / Dapper / <c>ExecuteUpdateAsync</c> that bypasses the
/// ChangeTracker, you have to call
/// <see cref="LegDerivedRecomputeService"/> explicitly. The importer
/// (Dapper) is the place this applies today. (The investment editor
/// once used the <c>insert_investment_legs</c> TVF here; that was
/// retired in favour of EF-tracked inserts so this interceptor covers
/// it automatically.)</para>
///
/// <para>ADR-0032 (triggers as last resort) + ADR-0034 (header-walk
/// running balance, mig 102) + ADR-0036 (denormalized posting counts,
/// mig 120).</para>
/// </remarks>
public sealed class LegDerivedRecomputeInterceptor : SaveChangesInterceptor
{
    // Per-DbContext snapshot of which (account, anchor) pairs need a
    // post-save recompute. Captured in SavingChangesAsync (where we
    // still see the OLD state of modified rows AND the to-be-cascaded
    // legs of a doomed header); consumed in SavedChangesAsync. Keyed
    // weakly, as LegNotesRecomputeInterceptor keys its own: a save that
    // ends before SavedChanges, SaveChangesFailed or SaveChangesCanceled
    // removes it (a concurrency failure, which EF reports through neither,
    // or an interceptor throwing after this one captured) leaves its entry,
    // which must not keep the context alive. The next save replaces it.
    private static readonly ConditionalWeakTable<DbContext, Snapshot> _pending = new();

    private readonly ILogger<LegDerivedRecomputeInterceptor> _logger;

    public LegDerivedRecomputeInterceptor(ILogger<LegDerivedRecomputeInterceptor> logger)
    {
        _logger = logger;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is AppDbContext db)
        {
            // Sync path mirrors the async path; SaveChanges() is rare
            // in this codebase but valid.
            var snapshot = CaptureSnapshotAsync(db, CancellationToken.None).GetAwaiter().GetResult();
            _pending.AddOrUpdate(db, snapshot);
            LogSnapshot(snapshot);
        }
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
        {
            var snapshot = await CaptureSnapshotAsync(db, cancellationToken)
                .ConfigureAwait(false);
            _pending.AddOrUpdate(db, snapshot);
            LogSnapshot(snapshot);
        }
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var snapshot))
        {
            _pending.Remove(db);
            RecomputeAsync(db, snapshot, _logger, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var snapshot))
        {
            _pending.Remove(db);
            await RecomputeAsync(db, snapshot, _logger, cancellationToken).ConfigureAwait(false);
        }
        return await base.SavedChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    // Trace-level: every save logs what it saw. Filter via
    // Logging:LogLevel:Coffer.Api.Db.LegDerivedRecomputeInterceptor=Debug
    // when chasing a stale-balance bug.
    private void LogSnapshot(Snapshot snap)
    {
        if (snap.IsEmpty)
        {
            _logger.LogDebug(
                "LegDerivedRecomputeInterceptor: snapshot empty (no balance-affecting changes)");
            return;
        }
        _logger.LogDebug(
            "LegDerivedRecomputeInterceptor: snapshot captured leg_pairs={LegPairs} touched_headers={Headers}",
            snap.TouchedLegPairs.Count,
            snap.TouchedHeaders.Count);
    }

    // A failed save, and a cancelled one (EF raises SaveChangesCanceled for
    // a cancel, not SaveChangesFailed), drop the snapshot it captured.
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is AppDbContext db) _pending.Remove(db);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db) _pending.Remove(db);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        if (eventData.Context is AppDbContext db) _pending.Remove(db);
        base.SaveChangesCanceled(eventData);
    }

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db) _pending.Remove(db);
        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    // ----- snapshot capture -----

    private static async Task<Snapshot> CaptureSnapshotAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        var snap = new Snapshot();

        // Headers being deleted: their legs will cascade away. Capture
        // (header_id, account_id, posted_at) BEFORE the DB delete by
        // reading the still-live legs.
        var headersBeingDeleted = db.ChangeTracker.Entries<TxnHeaderRow>()
            .Where(e => e.State == EntityState.Deleted)
            .Select(e => e.Entity.Id)
            .ToList();

        foreach (var entry in db.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case TxnLegRow:
                    CaptureLegEntry(entry, snap);
                    break;
                case TxnHeaderRow:
                    CaptureHeaderEntry(entry, snap);
                    break;
            }
        }

        // For headers being deleted, look up their legs (which the
        // ChangeTracker probably doesn't have if the cascade is
        // DB-side) so the affected accounts get recomputed even
        // though the legs disappear with the cascade.
        if (headersBeingDeleted.Count > 0)
        {
            var legs = await db.TxnLegs.AsNoTracking()
                .Where(l => headersBeingDeleted.Contains(l.HeaderId))
                .Select(l => new { l.HeaderId, l.AccountId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var leg in legs)
                snap.AddTouchedLegPair(leg.AccountId, leg.HeaderId);

            // The deleted headers vanish from the DB (FK cascade) before
            // RecomputeAsync runs, so their posted_at can't be looked up
            // there. Capture it NOW (still live at this point) as an anchor so
            // the recompute re-walks the account from where the deleted row
            // sat — otherwise a hard delete leaves every row after it carrying
            // the now-removed contribution.
            var deletedAnchors = await db.TxnHeaders.AsNoTracking()
                .Where(h => headersBeingDeleted.Contains(h.Id))
                .Select(h => new { h.Id, h.PostedAt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var d in deletedAnchors)
                snap.AddOldDateAnchor(d.Id, d.PostedAt);
        }

        return snap;
    }

    /// <summary>
    /// The <see cref="TxnLegRow"/> properties neither recompute reads: a
    /// modified leg whose changes all fall in this set touches no balance
    /// and no posting count. Fail closed: a property not listed here
    /// counts, so a new column recomputes until it is shown to be ignored.
    /// </summary>
    internal static readonly IReadOnlySet<string> NonRecomputeLegProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        // The leg's own memo (ADR-0108).
        nameof(TxnLegRow.LegMemo),
        // Investment metadata. The walk sums amount, which is authoritative
        // (ADR-0073); holdings recompute from these in their own interceptor.
        nameof(TxnLegRow.SecurityId),
        nameof(TxnLegRow.Quantity),
        nameof(TxnLegRow.UnitPrice),
        nameof(TxnLegRow.PostingRole),
        // Row bookkeeping.
        nameof(TxnLegRow.CreatedAt),
        // The stored memo-and-tags presentation (mig 264).
        nameof(TxnLegRow.NotesKind),
        nameof(TxnLegRow.NotesIsReadOnly),
        nameof(TxnLegRow.NotesGroupsByHeader),
        nameof(TxnLegRow.NotesMemo),
        nameof(TxnLegRow.NotesMemoInherited),
        nameof(TxnLegRow.NotesInheritedMemo),
        nameof(TxnLegRow.NotesInheritFromAccountId),
        nameof(TxnLegRow.NotesTagsInherited),
        nameof(TxnLegRow.NotesHeaderMemo),
        nameof(TxnLegRow.NotesHeaderInPlay),
    };

    /// <summary>
    /// The <see cref="TxnLegRow"/> properties a recompute reads, or whose
    /// column the guard test cannot show unread. Not consulted at runtime
    /// (anything outside <see cref="NonRecomputeLegProperties"/> counts);
    /// it makes the classification complete, so the guard can fail on a
    /// property in neither set.
    /// </summary>
    internal static readonly IReadOnlySet<string> RecomputeLegProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        // Read by the balance walk (the header it nets into, the account it
        // walks, the amount it sums) and by the posting-count recompute.
        nameof(TxnLegRow.HeaderId),
        nameof(TxnLegRow.AccountId),
        nameof(TxnLegRow.Amount),
        // Read by the posting-count recompute (distinct postings).
        nameof(TxnLegRow.PostingIndex),
        // Written by the posting-count recompute.
        nameof(TxnLegRow.AccountPostingsOnHeader),
        nameof(TxnLegRow.HeaderTotalPostings),
        // Named by the recompute SQL as another table's column. Fixed for
        // the life of the row, so classing them here costs nothing.
        nameof(TxnLegRow.Id),
        nameof(TxnLegRow.LedgerId),
    };

    /// <summary>
    /// The <see cref="TxnHeaderRow"/> properties the balance walk reads that can change
    /// after a header is inserted. A modified header with any of them changed touches
    /// every leg it has; a change to any other header property starts no recompute.
    /// </summary>
    /// <remarks>
    /// Held to the recompute SQL by a guard test: every header column the balance and
    /// posting-count functions name, other than one fixed for the life of the row, is
    /// here, and nothing else is. The posting counts read no header column.
    /// </remarks>
    internal static readonly IReadOnlySet<string> RecomputeHeaderProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        // The walk's order and its from-date.
        nameof(TxnHeaderRow.PostedAt),
        // The walk leaves out a merged and a hidden header.
        nameof(TxnHeaderRow.IsMergedInto),
        nameof(TxnHeaderRow.IsHidden),
    };

    private static void CaptureLegEntry(EntityEntry entry, Snapshot snap)
    {
        // Modified: both OLD and NEW account_ids matter — a leg moving
        // accounts means BOTH need recompute (old wipes the stale row,
        // new builds the fresh row). A modified leg counts only when a
        // property outside NonRecomputeLegProperties changed: a memo edit
        // leaves every balance and posting count as it was.
        if (entry.State == EntityState.Added)
        {
            var leg = (TxnLegRow)entry.Entity;
            snap.AddTouchedLegPair(leg.AccountId, leg.HeaderId);
        }
        else if (entry.State == EntityState.Deleted)
        {
            // OriginalValues — Entity still holds them on delete, but use the
            // tracked originals for safety.
            var headerId = (Guid)entry.OriginalValues[nameof(TxnLegRow.HeaderId)]!;
            var accountId = (Guid)entry.OriginalValues[nameof(TxnLegRow.AccountId)]!;
            snap.AddTouchedLegPair(accountId, headerId);
        }
        else if (entry.State == EntityState.Modified
                 && entry.Properties.Any(p => p.IsModified && !NonRecomputeLegProperties.Contains(p.Metadata.Name)))
        {
            var oldHeaderId = (Guid)entry.OriginalValues[nameof(TxnLegRow.HeaderId)]!;
            var oldAccountId = (Guid)entry.OriginalValues[nameof(TxnLegRow.AccountId)]!;
            var leg = (TxnLegRow)entry.Entity;
            snap.AddTouchedLegPair(oldAccountId, oldHeaderId);
            snap.AddTouchedLegPair(leg.AccountId, leg.HeaderId);
        }
    }

    private static void CaptureHeaderEntry(EntityEntry entry, Snapshot snap)
    {
        if (entry.State == EntityState.Modified)
        {
            // Only what the walk reads (RecomputeHeaderProperties): payee, status and
            // the rest shift no balance. The soft-delete path flips
            // is_hidden, a merge stamps is_merged_into and moves the winner's date,
            // and an edit moves a date; each reaches this through SaveChanges.
            if (entry.Properties.Any(p => p.IsModified && RecomputeHeaderProperties.Contains(p.Metadata.Name)))
                snap.AddTouchedHeader(((TxnHeaderRow)entry.Entity).Id);
            var postedAtChanged = entry.Property(nameof(TxnHeaderRow.PostedAt)).IsModified;
            // A date MOVE: capture the OLD posted_at so the recompute
            // anchors at MIN(old, new) and re-walks the vacated range.
            if (postedAtChanged)
                snap.AddOldDateAnchor(
                    ((TxnHeaderRow)entry.Entity).Id,
                    entry.OriginalValues.GetValue<DateTime>(nameof(TxnHeaderRow.PostedAt)));
        }
        // Header Added: legs will be Added too, those triggers capture.
        // Header Deleted: handled separately by the cascade-aware leg
        // lookup in CaptureSnapshotAsync.
    }

    // ----- recompute -----

    private static async Task RecomputeAsync(
        AppDbContext db, Snapshot snap,
        ILogger<LegDerivedRecomputeInterceptor> logger,
        CancellationToken cancellationToken)
    {
        if (snap.IsEmpty) return;

        // Expand TouchedHeaders into (account, header) pairs by reading
        // current legs from the DB. Headers in this set are ones whose
        // posted_at, is_merged_into or is_hidden changed — every account on
        // the header needs recomputing.
        if (snap.TouchedHeaders.Count > 0)
        {
            var headerLegs = await db.TxnLegs.AsNoTracking()
                .Where(l => snap.TouchedHeaders.Contains(l.HeaderId))
                .Select(l => new { l.HeaderId, l.AccountId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var leg in headerLegs)
                snap.AddTouchedLegPair(leg.AccountId, leg.HeaderId);
        }

        if (snap.TouchedLegPairs.Count == 0) return;

        // posted_at for each touched header. Canonical since mig 230 — there is
        // no override layer to COALESCE through.
        var headerIds = snap.TouchedLegPairs.Select(p => p.HeaderId).Distinct().ToList();
        var headerInfo = await db.TxnHeaders.AsNoTracking()
            .Where(h => headerIds.Contains(h.Id))
            .Select(h => new { h.Id, h.PostedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byHeader = headerInfo.ToDictionary(
            h => h.Id,
            h =>
            {
                var anchor = h.PostedAt;
                // If the date MOVED this save, anchor at the EARLIER of
                // old/new so the recompute (mig 102 — wipe + re-walk from
                // the anchor forward) covers the vacated [old, new) range.
                // Anchoring earlier is safe + idempotent; without it,
                // moving a txn LATER drifts the skipped-over rows by its
                // amount until a manual Verify-balances.
                if (snap.OldDateAnchors.TryGetValue(h.Id, out var oldAnchor)
                    && oldAnchor < anchor)
                    anchor = oldAnchor;
                return anchor;
            });

        // Build the per-(account, anchor) list. Per account, the anchor
        // is the EARLIEST effective posted_at across all touched headers
        // that included that account.
        var affected = new List<(Guid AccountId, DateTime FromPostedAt)>(snap.TouchedLegPairs.Count);
        foreach (var (accountId, headerId) in snap.TouchedLegPairs)
        {
            if (byHeader.TryGetValue(headerId, out var ts))
                affected.Add((accountId, ts));
            else if (snap.OldDateAnchors.TryGetValue(headerId, out var deletedAnchor))
                // Header was DELETED this save (gone from byHeader). Anchor
                // on its captured effective posted_at so the account still
                // recomputes from where the deleted row sat.
                affected.Add((accountId, deletedAnchor));
        }

        // Information-level: one log entry per recompute invocation.
        // Carries (account_id, anchor) — enough to grep for "did the
        // recompute fire for this account on or before <date>?" when
        // staleness is reported. Cheap on volume (one line per write
        // that touched balances, deduped per account).
        if (logger.IsEnabled(LogLevel.Information))
        {
            foreach (var (accountId, fromPostedAt) in affected)
            {
                logger.LogInformation(
                    "LegDerivedRecomputeInterceptor: recompute account_id={AccountId} from_posted_at={FromPostedAt:O}",
                    accountId, fromPostedAt);
            }
        }

        var service = new LegDerivedRecomputeService(db);
        await service.RecomputeAsync(affected, cancellationToken).ConfigureAwait(false);

        // Posting counts are the second leg-derived denormalization (mig
        // 120). They depend on the same touched headers as balances, so
        // we recompute them from the SAME snapshot — one capture, both
        // recomputes. Using the balance-affected header set is slightly
        // over-broad on pure amount edits (which don't change posting
        // structure), but the recompute is idempotent and cheap.
        await service.RecomputePostingCountsAsync(headerIds, cancellationToken).ConfigureAwait(false);
    }

    // ----- per-context snapshot type -----

    private sealed class Snapshot
    {
        public HashSet<(Guid AccountId, Guid HeaderId)> TouchedLegPairs { get; } = new();
        public HashSet<Guid> TouchedHeaders { get; } = new();

        // Headers whose posted_at MOVED this save. The balance
        // recompute must anchor at MIN(old, new) effective date so the
        // rows in the vacated [old, new) range get re-walked — otherwise
        // moving a txn LATER leaves the skipped-over rows drifted by the
        // txn's amount. headerId -> earliest OLD posted_at.
        public Dictionary<Guid, DateTime> OldDateAnchors { get; } = new();

        public bool IsEmpty =>
            TouchedLegPairs.Count == 0
            && TouchedHeaders.Count == 0;

        public void AddTouchedLegPair(Guid accountId, Guid headerId) =>
            TouchedLegPairs.Add((accountId, headerId));

        public void AddTouchedHeader(Guid headerId) => TouchedHeaders.Add(headerId);

        public void AddOldDateAnchor(Guid headerId, DateTime candidate)
        {
            if (!OldDateAnchors.TryGetValue(headerId, out var existing) || candidate < existing)
                OldDateAnchors[headerId] = candidate;
        }
    }
}
