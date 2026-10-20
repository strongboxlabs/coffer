using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Db;

/// <summary>
/// EF Core <c>SaveChangesInterceptor</c> that re-derives
/// <c>holdings</c> + <c>lots</c> after every API write that touches
/// investment-shape <c>txn_legs</c> (legs with
/// <c>security_id IS NOT NULL</c> and <c>quantity IS NOT NULL</c>).
/// </summary>
/// <remarks>
/// <para><b>Why an interceptor (and not a Postgres trigger)</b>. The
/// holdings trigger family (mig 068 / 073) was kept for two slices
/// after mig 102 retired the balance triggers — different surface,
/// narrower trigger set, no observed bugs. Mig 104 retires it anyway:
/// the same arguments that made balance triggers a continuous source
/// of bugs apply here in latent form (AFTER STATEMENT triggers see
/// per-statement transition tables, not post-SaveChanges end state;
/// the recompute is invisible to the writer; duplicate dispatch
/// logic).</para>
///
/// <para>Parallels <see cref="LegDerivedRecomputeInterceptor"/> in
/// structure:</para>
///
/// <list type="bullet">
///   <item><description>Runs once per <c>SaveChanges</c>, after every
///   DML statement in the batch has committed. The ChangeTracker
///   gives a single consistent snapshot.</description></item>
///   <item><description>FK cascade DELETEs are handled explicitly: when
///   a header is being deleted, we read its legs from the live DB
///   inside <see cref="SavingChangesAsync"/> (before the cascade has
///   run) so the affected-(account, security) set still includes the
///   legs that are about to vanish.</description></item>
///   <item><description>A leg moving between (account, security) pairs
///   captures BOTH the OLD and NEW pairs so both holdings reconcile
///   — same dual-end handling the trigger pair did via separate
///   UPDATE-OLD and UPDATE-NEW triggers.</description></item>
///   <item><description>Recompute is a regular SQL function call from
///   C#, not a trigger; it can't re-fire this interceptor.</description></item>
/// </list>
///
/// <para><b>Every writer's responsibility</b>. Any repository method
/// that mutates <c>txn_legs</c> with investment-shape values MUST
/// reach <c>SaveChangesAsync</c> on the shared <see cref="AppDbContext"/>
/// for its writes (the standard pattern). No explicit recompute call
/// is needed. The interceptor reads <see cref="ChangeTracker"/>
/// entries, identifies the affected (account, security) pairs, and
/// invokes <see cref="HoldingsRecomputeService"/> after the save
/// commits.</para>
///
/// <para>If you ARE going to write an investment-leg mutation via
/// raw SQL / Dapper / <c>ExecuteUpdateAsync</c> that bypasses the
/// ChangeTracker, call <see cref="HoldingsRecomputeService"/>
/// explicitly. The Moneydance importer already does this via
/// <c>InvestmentRepository.RecomputeCostBasisAsync(ledgerId)</c> at
/// end-of-import.</para>
///
/// <para>The commission-flip path
/// (<c>AccountsRepository.SetIsTradeCommissionAsync</c>) is unchanged
/// — it already calls <c>recompute_holdings_for_brokerage</c>
/// explicitly per mig 088. That trigger was the first one moved to
/// the explicit-call pattern; mig 104 finishes the job for the
/// txn_legs side.</para>
///
/// <para>ADR-0032 (triggers as last resort) — mig 104.</para>
/// </remarks>
public sealed class HoldingsRecomputeInterceptor : SaveChangesInterceptor
{
    // Per-DbContext snapshot of which (account, security) pairs need a
    // post-save recompute. Captured in SavingChangesAsync (where we
    // still see the OLD state of modified rows AND the to-be-cascaded
    // legs of a doomed header); consumed in SavedChangesAsync.
    private static readonly ConcurrentDictionary<DbContext, Snapshot> _pending = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is AppDbContext db)
        {
            _pending[db] = CaptureSnapshotAsync(db, CancellationToken.None).GetAwaiter().GetResult();
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
        }
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is AppDbContext db && _pending.TryRemove(db, out var snapshot))
        {
            RecomputeAsync(db, snapshot, CancellationToken.None)
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
            await RecomputeAsync(db, snapshot, cancellationToken).ConfigureAwait(false);
        }
        return await base.SavedChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

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

        // ADR-0047 / mig 124-125: a recurring TEMPLATE header (and its legs) is
        // invisible to the holdings walk (recompute_holdings_cost_basis reads
        // live_txn_headers). It must be invisible HERE too: enqueuing a
        // template leg's (account, security) would hit the recompute's
        // unconditional auto-create branch and leave a spurious zero-qty
        // holdings row — a keystone violation (a template must NEVER touch
        // holdings/lots/balances). Every template write path keeps the header
        // tracked alongside its legs (create=Added, edit=Modified,
        // delete=Deleted), so the ChangeTracker is an authoritative source for
        // "which involved headers are templates" — no DB round-trip needed.
        var templateHeaderIds = db.ChangeTracker.Entries<TxnHeaderRow>()
            .Where(e => e.Entity.IsRecurringTemplate)
            .Select(e => e.Entity.Id)
            .ToHashSet();

        // Headers being deleted: their legs will cascade away. Capture
        // (account, security) for the about-to-vanish investment legs
        // BEFORE the DB delete by reading the still-live legs. Template
        // headers are excluded — their legs never contributed to holdings.
        var headersBeingDeleted = db.ChangeTracker.Entries<TxnHeaderRow>()
            .Where(e => e.State == EntityState.Deleted && !e.Entity.IsRecurringTemplate)
            .Select(e => e.Entity.Id)
            .ToList();

        foreach (var entry in db.ChangeTracker.Entries<TxnLegRow>())
        {
            CaptureLegEntry(entry, snap, templateHeaderIds);
        }

        // For headers being deleted, look up their legs (which the
        // ChangeTracker probably doesn't have if the cascade is
        // DB-side) so the affected (account, security) set survives.
        // Filter to investment-shape legs at the DB query — bank legs
        // (security_id IS NULL) don't affect holdings.
        if (headersBeingDeleted.Count > 0)
        {
            var legs = await db.TxnLegs.AsNoTracking()
                .Where(l => headersBeingDeleted.Contains(l.HeaderId)
                    && l.SecurityId != null
                    && l.Quantity != null)
                .Select(l => new { l.AccountId, l.SecurityId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var leg in legs)
                snap.AddPair(leg.AccountId, leg.SecurityId!.Value);
        }

        return snap;
    }

    private static void CaptureLegEntry(
        EntityEntry<TxnLegRow> entry,
        Snapshot snap,
        IReadOnlySet<Guid> templateHeaderIds)
    {
        // A template's legs never enter the holdings walk (ADR-0047). HeaderId
        // is init-only, so Original == Current; one check covers every state.
        var headerId = entry.State == EntityState.Deleted
            ? (Guid)entry.OriginalValues[nameof(TxnLegRow.HeaderId)]!
            : entry.Entity.HeaderId;
        if (templateHeaderIds.Contains(headerId)) return;

        // The trigger family filtered "security_id IS NOT NULL AND
        // quantity IS NOT NULL" inside the transition-table walk; we
        // mirror the predicate here so bank legs are no-ops.
        switch (entry.State)
        {
            case EntityState.Added:
            {
                var leg = entry.Entity;
                if (leg.SecurityId is Guid sid && leg.Quantity is not null)
                    snap.AddPair(leg.AccountId, sid);
                break;
            }
            case EntityState.Deleted:
            {
                var sidObj = entry.OriginalValues[nameof(TxnLegRow.SecurityId)];
                var qtyObj = entry.OriginalValues[nameof(TxnLegRow.Quantity)];
                if (sidObj is Guid sid && qtyObj is not null)
                {
                    var accountId = (Guid)entry.OriginalValues[nameof(TxnLegRow.AccountId)]!;
                    snap.AddPair(accountId, sid);
                }
                break;
            }
            case EntityState.Modified:
            {
                // A leg moving between (account, security) pairs needs
                // BOTH ends reconciled. The trigger pair did this via
                // separate UPDATE-OLD and UPDATE-NEW triggers; we
                // capture both pairs in one pass here.
                var oldSidObj = entry.OriginalValues[nameof(TxnLegRow.SecurityId)];
                var oldQtyObj = entry.OriginalValues[nameof(TxnLegRow.Quantity)];
                if (oldSidObj is Guid oldSid && oldQtyObj is not null)
                {
                    var oldAccountId = (Guid)entry.OriginalValues[nameof(TxnLegRow.AccountId)]!;
                    snap.AddPair(oldAccountId, oldSid);
                }

                var leg = entry.Entity;
                if (leg.SecurityId is Guid newSid && leg.Quantity is not null)
                    snap.AddPair(leg.AccountId, newSid);
                break;
            }
        }
    }

    // ----- recompute -----

    private static async Task RecomputeAsync(
        AppDbContext db, Snapshot snap, CancellationToken cancellationToken)
    {
        if (snap.Pairs.Count == 0) return;

        var service = new HoldingsRecomputeService(db);
        await service.RecomputeAsync(snap.Pairs, cancellationToken).ConfigureAwait(false);
    }

    // ----- per-context snapshot type -----

    private sealed class Snapshot
    {
        public HashSet<(Guid AccountId, Guid SecurityId)> Pairs { get; } = new();

        public void AddPair(Guid accountId, Guid securityId) =>
            Pairs.Add((accountId, securityId));
    }
}
