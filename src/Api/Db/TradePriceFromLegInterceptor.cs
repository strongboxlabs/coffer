using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Db;

/// <summary>
/// EF Core <c>SaveChangesInterceptor</c> that seeds a <c>trade</c>-source row
/// into <c>security_prices</c> after every API write that lands an investment
/// TRADE leg — a <c>txn_legs</c> row with <c>security_id</c> set,
/// <c>quantity &lt;&gt; 0</c>, and <c>unit_price &gt; 0</c> (so
/// <c>buy</c>/<c>sell</c>/<c>buyx</c>/<c>sellx</c>/<c>dividend_reinvest</c>; the
/// priceless <c>dividend_cash</c>/<c>divx</c>/<c>inc</c>/<c>exp</c>/<c>misc</c>/
/// <c>transfer</c>/<c>transfer_shares</c> legs carry <c>pamt = 0 → unit_price 0</c>
/// and are skipped). ADR-0084.
/// </summary>
/// <remarks>
/// <para>A sibling of <see cref="HoldingsRecomputeInterceptor"/> — same
/// lifecycle, same posture (a Postgres function invoked post-save, NOT a DB
/// trigger; ADR-0032). It fires for every EF writer (native API + MCP)
/// automatically; the ChangeTracker gives the changed legs. The Moneydance
/// importer (Dapper, bypasses EF) seeds its ledger's days itself, in its
/// <c>TradePriceSeedStep</c>, not through this interceptor.</para>
///
/// <list type="bullet">
///   <item><description>Captures Added/Modified trade legs in
///   <c>SavingChanges</c> (the header they belong to and their security, not their
///   price: the rule decides the price).</description></item>
///   <item><description>An edit re-upserts the (possibly new) day at
///   <c>trade</c> rank — reading the leg's CURRENT values covers both Added and
///   Modified.</description></item>
///   <item><description>A modified header counts as touching every trade leg
///   it has when a property in <see cref="RecomputeHeaderProperties"/> changed:
///   the rule reads the header's date, action and provider action, and whether
///   it is hidden or merged into another (migration 273), so a date-only edit, a
///   merge moving the winner's date or folding a trade away, and a soft-delete
///   each change what prices a day with no leg written. Its legs are
///   read after the save, and the day they now sit on is re-derived, as an edit
///   re-upserts its day.</description></item>
///   <item><description>The day a trade LEAVES is re-derived too (ADR-0084 D4,
///   2026-11-03 amendment): a deleted header or leg, a modified header as above,
///   and a modified leg whose security, quantity or unit price changed can each
///   take an execution off the (security, day) it priced. Those headers' trade legs
///   are read from the live DB before the save, as they stand before it, and each
///   (security, day) they sat on is re-derived after it. The rule then derives the
///   day from the executions that remain and the rank-gated upsert writes it; when
///   none remains it derives nothing and the stored row is kept, a past execution
///   being a real observation (the unbacked-price advisory lists it). Without this
///   the stored price stayed the departed trade's while the check expected the
///   remaining one's, and the scheduled check reported a repairable mismatch after
///   routine use.</description></item>
///   <item><description>Recurring TEMPLATE headers (and their legs) are excluded
///   — a template is never a live cash event (ADR-0047). The write path keeps
///   the header tracked alongside its legs, so the ChangeTracker is the
///   authoritative "is this a template" source.</description></item>
///   <item><description>The <c>price_date</c> is the UTC calendar day of the
///   header's <c>posted_at</c> (ADR-0070 D5 / ADR-0084 D3), resolved post-save
///   from the committed headers so a trade and a same-day Yahoo close share one
///   day-row (the rank gate then lets the feed overwrite it).</description></item>
///   <item><description>The upsert is a regular SQL function call from C# via
///   <see cref="TradePriceRecomputeService"/>; it can't re-fire this
///   interceptor.</description></item>
/// </list>
/// </remarks>
public sealed class TradePriceFromLegInterceptor : SaveChangesInterceptor
{
    // Per-DbContext snapshot of the trade legs captured in SavingChanges and
    // consumed (with post-save posted_at resolution) in SavedChanges. Keyed
    // weakly, as in LegDerivedRecomputeInterceptor: an entry a save leaves must
    // not keep its context alive, and the next save replaces it.
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
            _pending.AddOrUpdate(db, await CaptureSnapshotAsync(db, cancellationToken).ConfigureAwait(false));
        }
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var snapshot))
        {
            _pending.Remove(db);
            UpsertAsync(db, snapshot, CancellationToken.None)
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
            await UpsertAsync(db, snapshot, cancellationToken).ConfigureAwait(false);
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

        // A recurring TEMPLATE header (and its legs) is never a live cash event
        // (ADR-0047); its execution values must not seed a price. Every template
        // write path keeps the header tracked alongside its legs, so the
        // ChangeTracker is authoritative for that.
        var templateHeaderIds = db.ChangeTracker.Entries<TxnHeaderRow>()
            .Where(e => e.Entity.IsRecurringTemplate)
            .Select(e => e.Entity.Id)
            .ToHashSet();

        // Headers whose trade legs can leave the (security, day) they priced, read
        // below as they stand before the save.
        var leaving = new HashSet<Guid>();

        foreach (var entry in db.ChangeTracker.Entries<TxnLegRow>())
        {
            CaptureLegEntry(entry, snap, templateHeaderIds);
            if (LeavesItsDay(entry))
                leaving.Add((Guid)entry.OriginalValues[nameof(TxnLegRow.HeaderId)]!);
        }

        foreach (var entry in db.ChangeTracker.Entries<TxnHeaderRow>())
        {
            if (entry.Entity.IsRecurringTemplate) continue;
            // A header whose change the rule reads: every trade leg it has needs the day
            // it now sits on re-derived, though no leg is written, and the day it left.
            // Its legs are read after the save for the first.
            if (entry.State == EntityState.Modified
                && entry.Properties.Any(p => p.IsModified && RecomputeHeaderProperties.Contains(p.Metadata.Name)))
            {
                snap.Headers.Add(entry.Entity.Id);
                leaving.Add(entry.Entity.Id);
            }
            // A deleted header's legs cascade away in the database, so this is the last
            // moment they can be read.
            else if (entry.State == EntityState.Deleted)
            {
                leaving.Add(entry.Entity.Id);
            }
        }

        leaving.ExceptWith(templateHeaderIds);
        if (leaving.Count == 0) return snap;

        // The trade legs of those headers as they stand before the save, on the day
        // their header gave them: the days an execution can leave. The ChangeTracker
        // usually holds neither, a cascade being the database's and a header edit loading
        // no legs. Every trade leg of such a header is read, not only the ones that move:
        // re-deriving a day no execution left writes the price the rule already names.
        var before = await (
                from l in db.TxnLegs.AsNoTracking()
                join h in db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                where leaving.Contains(l.HeaderId)
                      && !h.IsRecurringTemplate
                      && l.SecurityId != null
                      && l.Quantity != null && l.Quantity != 0m
                      && l.UnitPrice != null && l.UnitPrice > 0m
                select new { l.LedgerId, SecurityId = l.SecurityId!.Value, h.PostedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var leg in before)
            snap.Left.Add((leg.LedgerId, leg.SecurityId, HeaderDates.UtcDay(leg.PostedAt)));

        return snap;
    }

    /// <summary>
    /// Whether a leg's save can take an execution off the (security, day) it priced:
    /// a deleted leg that was a security leg, or a modified one whose security,
    /// quantity or unit price changed (it may no longer be a trade, or no longer this
    /// security's). A leg staying a trade of its security on its day is re-derived as
    /// an arrival; one whose header moves is captured through the header.
    /// </summary>
    private static bool LeavesItsDay(EntityEntry<TxnLegRow> entry) =>
        (entry.State == EntityState.Deleted
            || (entry.State == EntityState.Modified
                && (entry.Property(l => l.SecurityId).IsModified
                    || entry.Property(l => l.Quantity).IsModified
                    || entry.Property(l => l.UnitPrice).IsModified)))
        && entry.OriginalValues[nameof(TxnLegRow.SecurityId)] is Guid;

    /// <summary>
    /// The <see cref="TxnHeaderRow"/> properties the trade-price rule reads that can
    /// change after a header is inserted. A modified header with any of them changed
    /// touches every trade leg it has.
    /// </summary>
    /// <remarks>
    /// Held to the rule's SQL by a guard test: every header column
    /// <c>fn_trade_price_reseed</c> and the functions it calls name, other than one
    /// fixed for the life of the row, is here, and nothing else is. A hidden or
    /// merged-away row is no trade (migration 273), so a soft-delete, a merge
    /// and the un-merge a deleted winner's losers take each re-derive the day, as the
    /// day a trade leaves and the day it arrives on.
    /// </remarks>
    internal static readonly IReadOnlySet<string> RecomputeHeaderProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        // The day a trade prices.
        nameof(TxnHeaderRow.PostedAt),
        // Whether a leg is an execution: the action, and the provider's own action
        // read from the payload.
        nameof(TxnHeaderRow.Action),
        nameof(TxnHeaderRow.ProviderRawPayload),
        // Whether the row is a trade in the ledger at all.
        nameof(TxnHeaderRow.IsHidden),
        nameof(TxnHeaderRow.IsMergedInto),
    };

    private static void CaptureLegEntry(
        EntityEntry<TxnLegRow> entry,
        Snapshot snap,
        IReadOnlySet<Guid> templateHeaderIds)
    {
        // The day a leg arrives on: only Added/Modified legs seed a price. A deleted
        // leg's day is re-derived as one it leaves (LeavesItsDay), which retracts
        // nothing (ADR-0084 D4).
        if (entry.State is not (EntityState.Added or EntityState.Modified)) return;

        var leg = entry.Entity;

        // A template's legs never seed a price. HeaderId is init-only, so the
        // current value is authoritative for both Added and Modified.
        if (templateHeaderIds.Contains(leg.HeaderId)) return;

        // Trade shape: a priced security leg. A priceless leg (unit_price 0/NULL)
        // or a zero-quantity leg is not a trade.
        //
        // The leg's own price is NOT captured. It says only that this (security,
        // day) needs re-deriving; migration 234's rule decides what the price is,
        // reading every leg on the day rather than the one that happened to be
        // saved.
        if (leg.SecurityId is not Guid securityId) return;
        if (leg.Quantity is not decimal qty || qty == 0m) return;
        if (leg.UnitPrice is not decimal price || price <= 0m) return;

        snap.Add(leg.HeaderId, securityId, leg.LedgerId);
    }

    // ----- post-save upsert -----

    private static async Task UpsertAsync(
        AppDbContext db, Snapshot snap, CancellationToken cancellationToken)
    {
        if (snap.Legs.Count == 0 && snap.Headers.Count == 0 && snap.Left.Count == 0) return;

        // The trade legs of the headers whose change the rule reads, as they stand
        // after the save: the same shape CaptureLegEntry keeps.
        if (snap.Headers.Count > 0)
        {
            var headerLegs = await db.TxnLegs.AsNoTracking()
                .Where(l => snap.Headers.Contains(l.HeaderId)
                    && l.SecurityId != null
                    && l.Quantity != null && l.Quantity != 0m
                    && l.UnitPrice != null && l.UnitPrice > 0m)
                .Select(l => new { l.HeaderId, SecurityId = l.SecurityId!.Value, l.LedgerId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var leg in headerLegs)
                snap.Add(leg.HeaderId, leg.SecurityId, leg.LedgerId);
        }

        // The days trades left, then the days they arrived on.
        var days = new List<(Guid LedgerId, Guid SecurityId, DateOnly Day)>(snap.Left);
        if (snap.Legs.Count > 0)
        {
            // Resolve posted_at for the captured headers from the committed rows.
            // Exclude templates defensively (a template header's legs were already
            // filtered at capture). One query for the whole batch.
            var headerIds = snap.Legs.Select(l => l.HeaderId).Distinct().ToList();
            var postedAtByHeader = await db.TxnHeaders.AsNoTracking()
                .Where(h => headerIds.Contains(h.Id) && !h.IsRecurringTemplate)
                .Select(h => new { h.Id, h.PostedAt })
                .ToDictionaryAsync(h => h.Id, h => h.PostedAt, cancellationToken)
                .ConfigureAwait(false);

            foreach (var leg in snap.Legs)
            {
                if (!postedAtByHeader.TryGetValue(leg.HeaderId, out var postedAt)) continue;

                // price_date is the UTC calendar day of posted_at (ADR-0084 D3), with
                // an Unspecified timestamp from Npgsql stamped UTC rather than shifted
                // by the local offset (the ADR-0070 D7 asymmetry).
                days.Add((leg.LedgerId, leg.SecurityId, HeaderDates.UtcDay(postedAt)));
            }
        }

        if (days.Count == 0) return;

        var service = new TradePriceRecomputeService(db);
        await service.ReseedAsync(days, cancellationToken).ConfigureAwait(false);
    }

    // ----- per-context snapshot type -----

    private sealed class Snapshot
    {
        public List<(Guid HeaderId, Guid SecurityId, Guid LedgerId)> Legs { get; }
            = new();

        // Headers modified in a property the rule reads; their legs are read post-save.
        public HashSet<Guid> Headers { get; } = new();

        // The (security, day) each trade leg that can leave its day sat on before the
        // save, resolved then: re-derived after it, from what remains.
        public List<(Guid LedgerId, Guid SecurityId, DateOnly Day)> Left { get; } = new();

        public void Add(Guid headerId, Guid securityId, Guid ledgerId) =>
            Legs.Add((headerId, securityId, ledgerId));
    }
}
