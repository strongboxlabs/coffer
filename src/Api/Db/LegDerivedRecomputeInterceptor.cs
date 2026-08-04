using System.Collections.Concurrent;

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
/// that touches <c>txn_legs</c>, <c>txn_headers</c>,
/// <c>txn_header_overrides</c>, or <c>txn_leg_overrides</c>: the running
/// balance on <c>txn_header_account_balances</c> (mig 102, ADR-0034) and
/// the posting counts
/// (<c>account_postings_on_header</c> / <c>header_total_postings</c>) on
/// <c>txn_legs</c> (mig 120, ADR-0036). Both derive from the same
/// structural changes, so one snapshot drives both recomputes.
/// </summary>
/// <remarks>
/// <para><b>Why an interceptor (and not a Postgres trigger)</b>. The
/// trigger family (mig 090 / 094 / 099 / 101) bit us four times: each
/// fix added more trigger surface and each new surface introduced new
/// edge cases (cascade-from-header DELETE order, override-on-override
/// interactions, EF batched SaveChanges firing AFTER STATEMENT triggers
/// with transition tables that don't span the batch). All of those
/// failure modes are gone here:</para>
///
/// <list type="bullet">
///   <item><description>This runs ONCE per <c>SaveChanges</c>, after
///   every DML statement in the batch has committed to the DB. The
///   ChangeTracker gives a single consistent snapshot.</description></item>
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
    // legs of a doomed header); consumed in SavedChangesAsync.
    private static readonly ConcurrentDictionary<DbContext, Snapshot> _pending = new();

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
            _pending[db] = CaptureSnapshotAsync(db, CancellationToken.None).GetAwaiter().GetResult();
            LogSnapshot(_pending[db]);
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
            _pending[db] = await CaptureSnapshotAsync(db, cancellationToken)
                .ConfigureAwait(false);
            LogSnapshot(_pending[db]);
        }
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is AppDbContext db && _pending.TryRemove(db, out var snapshot))
        {
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
        if (eventData.Context is AppDbContext db && _pending.TryRemove(db, out var snapshot))
        {
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
            "LegDerivedRecomputeInterceptor: snapshot captured leg_pairs={LegPairs} touched_headers={Headers} touched_leg_overrides={LegOverrides}",
            snap.TouchedLegPairs.Count,
            snap.TouchedHeaders.Count,
            snap.TouchedLegOverrides.Count);
    }

    // SaveChangesFailedAsync clears the snapshot so the next save on
    // the same context starts clean. Without this, a retried save
    // could see stale entries.
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is AppDbContext db) _pending.TryRemove(db, out _);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db) _pending.TryRemove(db, out _);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
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
                case TxnHeaderOverrideRow:
                    CaptureHeaderOverrideEntry(entry, snap);
                    break;
                case TxnLegOverrideRow:
                    CaptureLegOverrideEntry(entry, snap);
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
            // there. Capture each one's EFFECTIVE posted_at NOW (still
            // live at this point) as an anchor so the recompute re-walks
            // the account from where the deleted row sat — otherwise a
            // hard delete leaves every row after it carrying the
            // now-removed contribution.
            var deletedAnchors = await db.TxnHeaders.AsNoTracking()
                .Where(h => headersBeingDeleted.Contains(h.Id))
                .Select(h => new
                {
                    h.Id,
                    Effective = db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => (DateTime?)o.PostedAt)
                        .FirstOrDefault() ?? h.PostedAt,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var d in deletedAnchors)
                snap.AddOldDateAnchor(d.Id, d.Effective);
        }

        return snap;
    }

    private static void CaptureLegEntry(EntityEntry entry, Snapshot snap)
    {
        // Modified: both OLD and NEW account_ids matter — a leg moving
        // accounts means BOTH need recompute (old wipes the stale row,
        // new builds the fresh row).
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
        else if (entry.State == EntityState.Modified)
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
            // posted_at, is_merged_into, is_hidden shift balances; the
            // recompute filters on all three. status / payee / etc. don't.
            // is_hidden flips here come from the bank + investment
            // soft-delete branches (`header.IsHidden = true`) — both go
            // through SaveChanges and land on this code path.
            var postedAtChanged = entry.Property(nameof(TxnHeaderRow.PostedAt)).IsModified;
            var mergedChanged = entry.Property(nameof(TxnHeaderRow.IsMergedInto)).IsModified;
            var hiddenChanged = entry.Property(nameof(TxnHeaderRow.IsHidden)).IsModified;
            if (postedAtChanged || mergedChanged || hiddenChanged)
                snap.AddTouchedHeader(((TxnHeaderRow)entry.Entity).Id);
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

    private static void CaptureHeaderOverrideEntry(EntityEntry entry, Snapshot snap)
    {
        // posted_at + is_hidden overrides shift balance walk; the
        // recompute filters on the COALESCE chain for both. Other
        // override columns (payee/memo/check_number/etc.) don't.
        bool postedAtMatters = entry.State switch
        {
            EntityState.Added => ((TxnHeaderOverrideRow)entry.Entity).PostedAt is not null,
            EntityState.Deleted => entry.OriginalValues[nameof(TxnHeaderOverrideRow.PostedAt)] is not null,
            EntityState.Modified => entry.Property(nameof(TxnHeaderOverrideRow.PostedAt)).IsModified,
            _ => false,
        };
        bool isHiddenMatters = entry.State switch
        {
            EntityState.Added => ((TxnHeaderOverrideRow)entry.Entity).IsHidden is not null,
            EntityState.Deleted => entry.OriginalValues[nameof(TxnHeaderOverrideRow.IsHidden)] is not null,
            EntityState.Modified => entry.Property(nameof(TxnHeaderOverrideRow.IsHidden)).IsModified,
            _ => false,
        };
        if (postedAtMatters || isHiddenMatters)
        {
            var headerId = entry.State == EntityState.Deleted
                ? (Guid)entry.OriginalValues[nameof(TxnHeaderOverrideRow.HeaderId)]!
                : ((TxnHeaderOverrideRow)entry.Entity).HeaderId;
            snap.AddTouchedHeader(headerId);
            // A date MOVE via the override layer: capture the OLD
            // effective date so the recompute re-walks the vacated range.
            if (postedAtMatters)
            {
                switch (entry.State)
                {
                    case EntityState.Modified:
                    case EntityState.Deleted:
                        var oldOverride = entry.OriginalValues
                            .GetValue<DateTime?>(nameof(TxnHeaderOverrideRow.PostedAt));
                        if (oldOverride is not null)
                            snap.AddOldDateAnchor(headerId, oldOverride.Value);
                        break;
                    case EntityState.Added:
                        // No prior override row: the OLD effective date was
                        // the raw header posted_at (resolved in RecomputeAsync).
                        snap.OverrideDateAdded.Add(headerId);
                        break;
                }
            }
        }
    }

    private static void CaptureLegOverrideEntry(EntityEntry entry, Snapshot snap)
    {
        // Amount override shifts the leg's contribution to balance. Other
        // override columns (leg_memo) don't affect balance.
        bool amountMatters = entry.State switch
        {
            EntityState.Added => ((TxnLegOverrideRow)entry.Entity).Amount is not null,
            EntityState.Deleted => entry.OriginalValues[nameof(TxnLegOverrideRow.Amount)] is not null,
            EntityState.Modified => entry.Property(nameof(TxnLegOverrideRow.Amount)).IsModified,
            _ => false,
        };
        if (amountMatters)
        {
            var legId = entry.State == EntityState.Deleted
                ? (Guid)entry.OriginalValues[nameof(TxnLegOverrideRow.LegId)]!
                : ((TxnLegOverrideRow)entry.Entity).LegId;
            snap.AddTouchedLegOverride(legId);
        }
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
        // posted_at or is_merged_into changed (header-row update) or
        // whose override posted_at moved — both need every account on
        // the header to be recomputed.
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

        // Expand TouchedLegOverrides into (account, header) by reading
        // the leg's header_id and account_id from the DB.
        if (snap.TouchedLegOverrides.Count > 0)
        {
            var legs = await db.TxnLegs.AsNoTracking()
                .Where(l => snap.TouchedLegOverrides.Contains(l.Id))
                .Select(l => new { l.HeaderId, l.AccountId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var leg in legs)
                snap.AddTouchedLegPair(leg.AccountId, leg.HeaderId);
        }

        if (snap.TouchedLegPairs.Count == 0) return;

        // Resolve effective posted_at for each touched header. Use
        // COALESCE(override.posted_at, header.posted_at) so override
        // moves are honoured.
        var headerIds = snap.TouchedLegPairs.Select(p => p.HeaderId).Distinct().ToList();
        var headerInfo = await db.TxnHeaders.AsNoTracking()
            .Where(h => headerIds.Contains(h.Id))
            .Select(h => new
            {
                h.Id,
                h.PostedAt,
                OverridePostedAt = db.TxnHeaderOverrides
                    .Where(o => o.HeaderId == h.Id)
                    .Select(o => (DateTime?)o.PostedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byHeader = headerInfo.ToDictionary(
            h => h.Id,
            h =>
            {
                // New effective posted_at (override wins).
                var anchor = h.OverridePostedAt ?? h.PostedAt;
                // If the date MOVED this save, anchor at the EARLIER of
                // old/new so the recompute (mig 102 — wipe + re-walk from
                // the anchor forward) covers the vacated [old, new) range.
                // Anchoring earlier is safe + idempotent; without it,
                // moving a txn LATER drifts the skipped-over rows by its
                // amount until a manual Verify-balances.
                if (snap.OldDateAnchors.TryGetValue(h.Id, out var oldAnchor)
                    && oldAnchor < anchor)
                    anchor = oldAnchor;
                // Override posted_at ADDED this save → old effective was
                // the raw header posted_at.
                if (snap.OverrideDateAdded.Contains(h.Id) && h.PostedAt < anchor)
                    anchor = h.PostedAt;
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
        public HashSet<Guid> TouchedLegOverrides { get; } = new();

        // Headers whose effective posted_at MOVED this save. The balance
        // recompute must anchor at MIN(old, new) effective date so the
        // rows in the vacated [old, new) range get re-walked — otherwise
        // moving a txn LATER leaves the skipped-over rows drifted by the
        // txn's amount. headerId -> earliest OLD effective posted_at.
        public Dictionary<Guid, DateTime> OldDateAnchors { get; } = new();

        // Headers where an override posted_at was ADDED this save (no
        // prior override row): the OLD effective date was the raw header
        // posted_at, resolved against the live header in RecomputeAsync.
        public HashSet<Guid> OverrideDateAdded { get; } = new();

        public bool IsEmpty =>
            TouchedLegPairs.Count == 0
            && TouchedHeaders.Count == 0
            && TouchedLegOverrides.Count == 0;

        public void AddTouchedLegPair(Guid accountId, Guid headerId) =>
            TouchedLegPairs.Add((accountId, headerId));

        public void AddTouchedHeader(Guid headerId) => TouchedHeaders.Add(headerId);

        public void AddTouchedLegOverride(Guid legId) => TouchedLegOverrides.Add(legId);

        public void AddOldDateAnchor(Guid headerId, DateTime candidate)
        {
            if (!OldDateAnchors.TryGetValue(headerId, out var existing) || candidate < existing)
                OldDateAnchors[headerId] = candidate;
        }
    }
}
