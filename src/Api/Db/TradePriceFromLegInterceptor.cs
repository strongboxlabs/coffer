using System.Collections.Concurrent;

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
/// importer (Dapper, bypasses EF) is covered by the migration-177 backfill, not
/// this interceptor.</para>
///
/// <list type="bullet">
///   <item><description>Captures Added/Modified trade legs in
///   <c>SavingChanges</c> (their current per-share execution price + the header
///   they belong to). DELETED legs are IGNORED — a past execution was a real
///   observation and the row is harmless; a feed close or later write
///   supersedes it by rank (ADR-0084 D4).</description></item>
///   <item><description>An edit re-upserts the (possibly new) day at
///   <c>trade</c> rank — reading the leg's CURRENT values covers both Added and
///   Modified.</description></item>
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
    // consumed (with post-save posted_at resolution) in SavedChanges.
    private static readonly ConcurrentDictionary<DbContext, Snapshot> _pending = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is AppDbContext db)
        {
            _pending[db] = CaptureSnapshot(db);
        }
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
        {
            _pending[db] = CaptureSnapshot(db);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is AppDbContext db && _pending.TryRemove(db, out var snapshot))
        {
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
        if (eventData.Context is AppDbContext db && _pending.TryRemove(db, out var snapshot))
        {
            await UpsertAsync(db, snapshot, cancellationToken).ConfigureAwait(false);
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

    private static Snapshot CaptureSnapshot(AppDbContext db)
    {
        var snap = new Snapshot();

        // A recurring TEMPLATE header (and its legs) is never a live cash event
        // (ADR-0047); its execution values must not seed a price. Every template
        // write path keeps the header tracked alongside its legs, so the
        // ChangeTracker is authoritative — no DB round-trip needed.
        var templateHeaderIds = db.ChangeTracker.Entries<TxnHeaderRow>()
            .Where(e => e.Entity.IsRecurringTemplate)
            .Select(e => e.Entity.Id)
            .ToHashSet();

        foreach (var entry in db.ChangeTracker.Entries<TxnLegRow>())
        {
            CaptureLegEntry(entry, snap, templateHeaderIds);
        }

        return snap;
    }

    private static void CaptureLegEntry(
        EntityEntry<TxnLegRow> entry,
        Snapshot snap,
        IReadOnlySet<Guid> templateHeaderIds)
    {
        // DELETED legs are ignored (ADR-0084 D4): a delete does not retract the
        // price row. Only Added/Modified legs seed a price.
        if (entry.State is not (EntityState.Added or EntityState.Modified)) return;

        var leg = entry.Entity;

        // A template's legs never seed a price. HeaderId is init-only, so the
        // current value is authoritative for both Added and Modified.
        if (templateHeaderIds.Contains(leg.HeaderId)) return;

        // Trade shape: a priced security leg. A priceless leg (unit_price 0/NULL)
        // or a zero-quantity leg is not a trade.
        if (leg.SecurityId is not Guid securityId) return;
        if (leg.Quantity is not decimal qty || qty == 0m) return;
        if (leg.UnitPrice is not decimal price || price <= 0m) return;

        snap.Add(leg.HeaderId, securityId, leg.LedgerId, price);
    }

    // ----- post-save upsert -----

    private static async Task UpsertAsync(
        AppDbContext db, Snapshot snap, CancellationToken cancellationToken)
    {
        if (snap.Legs.Count == 0) return;

        // Resolve posted_at for the captured headers from the committed rows.
        // Exclude templates defensively (a template header's legs were already
        // filtered at capture). One query for the whole batch.
        var headerIds = snap.Legs.Select(l => l.HeaderId).Distinct().ToList();
        var postedAtByHeader = await db.TxnHeaders.AsNoTracking()
            .Where(h => headerIds.Contains(h.Id) && !h.IsRecurringTemplate)
            .Select(h => new { h.Id, h.PostedAt })
            .ToDictionaryAsync(h => h.Id, h => h.PostedAt, cancellationToken)
            .ConfigureAwait(false);

        var trades = new List<(Guid LedgerId, Guid SecurityId, DateOnly Day, decimal Price)>();
        foreach (var leg in snap.Legs)
        {
            if (!postedAtByHeader.TryGetValue(leg.HeaderId, out var postedAt)) continue;

            // price_date is the UTC calendar day of posted_at (ADR-0084 D3).
            // Normalize Kind first to dodge the ADR-0070 D7 asymmetry: an
            // Unspecified timestamp from Npgsql is already UTC wall-clock, so
            // stamp it Utc rather than shifting it by the local offset.
            var utc = postedAt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(postedAt, DateTimeKind.Utc)
                : postedAt.ToUniversalTime();
            var day = DateOnly.FromDateTime(utc);

            trades.Add((leg.LedgerId, leg.SecurityId, day, leg.Price));
        }

        if (trades.Count == 0) return;

        var service = new TradePriceRecomputeService(db);
        await service.UpsertAsync(trades, cancellationToken).ConfigureAwait(false);
    }

    // ----- per-context snapshot type -----

    private sealed class Snapshot
    {
        public List<(Guid HeaderId, Guid SecurityId, Guid LedgerId, decimal Price)> Legs { get; }
            = new();

        public void Add(Guid headerId, Guid securityId, Guid ledgerId, decimal price) =>
            Legs.Add((headerId, securityId, ledgerId, price));
    }
}
