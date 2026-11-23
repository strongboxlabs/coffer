using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Db;

/// <summary>
/// EF Core <c>SaveChangesInterceptor</c> that re-derives
/// <c>holdings</c>, <c>lots</c> and <c>realized_gains</c> after every API
/// write that touches investment-shape <c>txn_legs</c> (legs with
/// <c>security_id IS NOT NULL</c> and <c>quantity IS NOT NULL</c>), or
/// changes a header field the FIFO walk reads
/// (<see cref="RecomputeHeaderProperties"/>) on a header that has them.
/// </summary>
/// <remarks>
/// <para><b>Why an interceptor (and not a Postgres trigger)</b>. Mig 104
/// retires the holdings trigger family (mig 068 / 073), as mig 102
/// retired the balance triggers: the arguments against balance
/// triggers apply here in latent form (AFTER STATEMENT triggers see
/// per-statement transition tables, not post-SaveChanges end state;
/// the recompute is invisible to the writer; duplicate dispatch
/// logic).</para>
///
/// <para>Parallels <see cref="LegDerivedRecomputeInterceptor"/> in
/// structure:</para>
///
/// <list type="bullet">
///   <item><description>Runs once per <c>SaveChanges</c>, after the
///   save's statements, inside its transaction
///   (<see cref="LegNotesRecomputeInterceptor"/>). The ChangeTracker
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
///   <item><description>A modified header counts as touching every
///   investment leg it has when a property in
///   <see cref="RecomputeHeaderProperties"/> changed: the walk leaves out
///   a hidden or merged header, orders by its date and reads its action,
///   so a soft-delete, a merge stamp or a re-date changes the
///   position with no leg written. Its legs are read from the live DB
///   before the save, as a deleted header's are.</description></item>
///   <item><description>Recompute is a regular SQL function call from
///   C#, not a trigger; it can't re-fire this interceptor.</description></item>
/// </list>
///
/// <para><b>Every writer's responsibility</b>. Any repository method
/// that mutates <c>txn_legs</c> with investment-shape values, or a
/// header field in <see cref="RecomputeHeaderProperties"/>, MUST reach
/// <c>SaveChangesAsync</c> on the shared <see cref="AppDbContext"/> for
/// its writes (the standard pattern). No explicit recompute call is
/// needed. The interceptor reads <see cref="ChangeTracker"/> entries,
/// identifies the affected (account, security) pairs, and invokes
/// <see cref="HoldingsRecomputeService"/> after the save's statements,
/// inside its transaction.</para>
///
/// <para>If you ARE going to write an investment-leg mutation, or one of
/// those header fields, via raw SQL / Dapper / <c>ExecuteUpdateAsync</c>
/// that bypasses the ChangeTracker, call
/// <see cref="HoldingsRecomputeService"/> explicitly, as the bulk delete
/// and bulk unhide do. The Moneydance importer already does this via
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
/// <para>ADR-0032 (triggers as last resort) — mig 104; the header rule is
/// its 2026-11-03 amendment.</para>
/// </remarks>
public sealed class HoldingsRecomputeInterceptor : SaveChangesInterceptor
{
    // Per-DbContext snapshot of which (account, security) pairs need a
    // post-save recompute. Captured in SavingChangesAsync (where we
    // still see the OLD state of modified rows AND the to-be-cascaded
    // legs of a doomed header); consumed in SavedChangesAsync. Keyed
    // weakly, as in LegDerivedRecomputeInterceptor: an entry a save leaves
    // must not keep its context alive, and the next save replaces it.
    private static readonly ConditionalWeakTable<DbContext, Snapshot> _pending = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is AppDbContext db)
        {
            _pending.AddOrUpdate(db, CaptureSnapshotAsync(db, CancellationToken.None).GetAwaiter().GetResult());
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
            _pending.AddOrUpdate(db, await CaptureSnapshotAsync(db, cancellationToken)
                .ConfigureAwait(false));
        }
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var snapshot))
        {
            _pending.Remove(db);
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
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var snapshot))
        {
            _pending.Remove(db);
            await RecomputeAsync(db, snapshot, cancellationToken).ConfigureAwait(false);
        }
        return await base.SavedChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    // A failed save, and a cancelled one, drop the snapshot it captured.
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

        // Headers whose every investment leg needs its position recomputed, read
        // from the still-live legs BEFORE the save:
        //  * a header being deleted: its legs will cascade away, so this is the
        //    last moment they can be read;
        //  * a header whose change the walk reads (RecomputeHeaderProperties):
        //    hiding, unhiding, merging or re-dating it moves its shares into or
        //    out of the position, or reorders it, with no leg written. A leg the
        //    same save adds, moves or deletes is captured from its own entry.
        // Template headers are excluded — their legs never contributed to holdings.
        var headersToRead = db.ChangeTracker.Entries<TxnHeaderRow>()
            .Where(e => !e.Entity.IsRecurringTemplate
                && (e.State == EntityState.Deleted
                    || (e.State == EntityState.Modified
                        && e.Properties.Any(p => p.IsModified && RecomputeHeaderProperties.Contains(p.Metadata.Name)))))
            .Select(e => e.Entity.Id)
            .ToList();

        foreach (var entry in db.ChangeTracker.Entries<TxnLegRow>())
        {
            CaptureLegEntry(entry, snap, templateHeaderIds);
        }

        // Look up those headers' legs (which the ChangeTracker usually doesn't
        // have: a cascade is DB-side, and a header edit loads no legs) so the
        // affected (account, security) set survives. Filter to investment-shape
        // legs at the DB query — bank legs (security_id IS NULL) don't affect
        // holdings.
        if (headersToRead.Count > 0)
        {
            var legs = await db.TxnLegs.AsNoTracking()
                .Where(l => headersToRead.Contains(l.HeaderId)
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

    /// <summary>
    /// The <see cref="TxnHeaderRow"/> properties the FIFO walk reads that can change
    /// after a header is inserted. A modified header with any of them changed touches
    /// the position of every investment leg it has.
    /// </summary>
    /// <remarks>
    /// Held to the recompute SQL by a guard test: every header column
    /// <c>recompute_holdings_cost_basis</c> and the walk it persists name, other than
    /// one fixed for the life of the row, is here, and nothing else is.
    /// </remarks>
    internal static readonly IReadOnlySet<string> RecomputeHeaderProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        // The walk's event order and its as-of bound.
        nameof(TxnHeaderRow.PostedAt),
        // The walk leaves out a hidden and a merged header (ADR-0040, mig 163).
        nameof(TxnHeaderRow.IsHidden),
        nameof(TxnHeaderRow.IsMergedInto),
        // A transfer_shares disposal books no realized gain (ADR-0065).
        nameof(TxnHeaderRow.Action),
    };

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
