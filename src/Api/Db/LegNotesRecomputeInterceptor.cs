using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// Recomputes the stored memo-and-tags presentation (<see cref="LegNotes"/>) of every
/// header a save changed an input of, after the save and in its transaction: the same
/// lifecycle as <see cref="LegDerivedRecomputeInterceptor"/>, for the same reason (no
/// trigger, ADR-0032).
/// </summary>
/// <remarks>
/// <para><b>Atomic with the save.</b> The recompute commits or rolls back with the
/// write, never after it: a crash or timeout between a committed write and its
/// recompute would leave stale values with nothing to repair them. Inside a caller's
/// transaction (every save that recomputes balances or holdings has one, below) it
/// simply runs in it. When the caller holds none, this opens one in
/// <c>SavingChanges</c> for every save, whether or not it changes an input (EF then saves
/// inside it instead of committing on its own), recomputes in <c>SavedChanges</c>, and
/// commits with no cancellation token, so a cancel that arrives once the work is done
/// cannot leave the outcome unknown. A cancel before that rolls the save back with its
/// recompute. <c>SaveChangesFailed</c>, raised for a failed write and for a failure in
/// any interceptor's <c>SavedChanges</c> (and <c>SaveChangesCanceled</c>, raised instead
/// for a cancel), rolls back and disposes the transaction it opened. EF reports a
/// concurrency failure through neither, so <see cref="AppDbContext"/> calls
/// <see cref="EndFailedSave"/> for every save that throws: the context is never left on a
/// transaction a failed save opened.</para>
/// <para><b>Registered last</b> (Program.cs, <see cref="ServiceDbContextFactory"/>), so
/// its commit comes after every other interceptor's <c>SavedChanges</c> work, which
/// therefore commits or rolls back with the save. A save that recomputes balances or
/// holdings runs in its writer's transaction, opened with the ledger write lock as its
/// first statement (<see cref="LedgerWriteLock.BeginLedgerWriteAsync"/>, migration 275):
/// the recompute functions refuse a transaction that has written without that lock, and
/// the one opened here does not take it. The transaction opened here serves saves that
/// recompute neither (engineering-standards §3.5, rule 4; ADR-0108 D6).</para>
/// <para>The headers are read off the change tracker before the save, while a deleted
/// row's original values and a doomed tag's assignments still exist:</para>
/// <list type="bullet">
///   <item>a header added, or with its memo, action or originating account changed;</item>
///   <item>a leg added or deleted, or with its account, posting, memo or header changed,
///   for its header before and after;</item>
///   <item>a header tag or leg tag added or removed;</item>
///   <item>a tag deleted: every header that carried it;</item>
///   <item>an account whose holdings link changed: every header with a leg on it or owned by it.</item>
/// </list>
/// <para><b>New legs.</b> Every tracked create stamps its new legs with their stored
/// values before the insert (<see cref="AnnotationWriter.ApplyAsync"/>), so the
/// recompute here normally finds nothing to change on them and writes only their tag
/// rows. Whatever a save inserts, the recompute leaves what the rule computes from what
/// it wrote: a leg inserted without values, or with values its writer computed from
/// other inputs, is put right in the same transaction and the save goes through (ADR-0108 D7:
/// production never refuses a save over its stored values). That
/// every path stamps, and that what it stamps is right, is held by the tests
/// (<c>StampedLegInsertTests</c>), and stored == computed by the consistency monitor.
/// The Moneydance import inserts through Dapper and is computed by its own whole-ledger
/// recompute.</para>
/// <para>A deleted header needs nothing: its rows cascade with its legs. A write that
/// bypasses the tracker is not seen here; it calls <see cref="LegNotes.RecomputeAsync"/>
/// itself.</para>
/// </remarks>
public sealed class LegNotesRecomputeInterceptor : SaveChangesInterceptor
{
    // Keyed weakly: a save that ends before SavedChanges or SaveChangesFailed runs (an
    // earlier interceptor throwing in SavingChanges, before this one has opened
    // anything) leaves its entry, which must not keep the context alive.
    private static readonly ConditionalWeakTable<DbContext, Pending> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db)
        {
            var pending = new Pending();
            if (db.Database.CurrentTransaction is null)
                pending.Open(db, db.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted));
            _pending.AddOrUpdate(db, pending);
            CaptureAsync(db, pending, CancellationToken.None).GetAwaiter().GetResult();
            if (pending.IsEmpty && pending.Own is null) _pending.Remove(db);
        }
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db)
        {
            var pending = new Pending();
            if (db.Database.CurrentTransaction is null)
                pending.Open(db, await db.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                    .ConfigureAwait(false));
            // Registered before anything is read, so a failure from here on finds the
            // transaction to roll back.
            _pending.AddOrUpdate(db, pending);
            await CaptureAsync(db, pending, cancellationToken).ConfigureAwait(false);
            if (pending.IsEmpty && pending.Own is null) _pending.Remove(db);
        }
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// End the transaction a failed save of <paramref name="db"/> left open: EF reports
    /// a concurrency failure without raising <c>SaveChangesFailed</c>, so
    /// <see cref="AppDbContext"/> calls this whenever a save throws. It rolls back and
    /// disposes the transaction this interceptor opened for that save, if any is left;
    /// a save that ended any other way has nothing left here.
    /// </summary>
    internal static void EndFailedSave(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db is AppDbContext app && _pending.TryGetValue(app, out var pending))
        {
            _pending.Remove(app);
            pending.RollBack(app);
        }
    }

    /// <inheritdoc cref="EndFailedSave"/>
    internal static async Task EndFailedSaveAsync(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db is AppDbContext app && _pending.TryGetValue(app, out var pending))
        {
            _pending.Remove(app);
            await pending.RollBackAsync(app).ConfigureAwait(false);
        }
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var pending))
        {
            // On a throw the entry stays, and SaveChangesFailed rolls back what it opened.
            RecomputeAsync(db, pending, CancellationToken.None).GetAwaiter().GetResult();
            pending.Own?.Commit();
            _pending.Remove(db);
            pending.Close(db);
        }
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var pending))
        {
            // Nothing is committed yet, inside the caller's transaction or the one opened
            // above, so the caller's token stands: a cancel rolls the save back with its
            // recompute. Once the recompute is done the commit takes no token, so a late
            // cancel cannot leave the outcome unknown. On a throw the entry stays, and
            // SaveChangesFailed rolls back what it opened.
            await RecomputeAsync(db, pending, cancellationToken).ConfigureAwait(false);
            if (pending.Own is { } own) await own.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            _pending.Remove(db);
            await pending.CloseAsync(db).ConfigureAwait(false);
        }
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            pending.RollBack(db);
        }
        base.SaveChangesFailed(eventData);
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            await pending.RollBackAsync(db).ConfigureAwait(false);
        }
        await base.SaveChangesFailedAsync(eventData, cancellationToken).ConfigureAwait(false);
    }

    // A cancel is not a failure to EF: it raises SaveChangesCanceled instead, and the
    // transaction opened for the save is rolled back the same way.
    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            pending.RollBack(db);
        }
        base.SaveChangesCanceled(eventData);
    }

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is AppDbContext db && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            await pending.RollBackAsync(db).ConfigureAwait(false);
        }
        await base.SaveChangesCanceledAsync(eventData, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Pending
    {
        public HashSet<Guid> Headers { get; } = [];
        public HashSet<Guid> Legs { get; } = [];
        public HashSet<Guid> Accounts { get; } = [];
        public HashSet<Guid> DeletedHeaders { get; } = [];
        public bool IsEmpty => Headers.Count == 0 && Legs.Count == 0 && Accounts.Count == 0;

        /// <summary>The transaction this interceptor opened for the save; null inside a caller's.</summary>
        public IDbContextTransaction? Own { get; private set; }

        private bool _savepoints;

        /// <summary>Take <paramref name="transaction"/> as the save's. EF's savepoint around
        /// the save is switched off for it: a failure rolls the whole transaction back.</summary>
        public void Open(AppDbContext db, IDbContextTransaction transaction)
        {
            Own = transaction;
            _savepoints = db.Database.AutoSavepointsEnabled;
            db.Database.AutoSavepointsEnabled = false;
        }

        public void Close(AppDbContext db)
        {
            if (Own is null) return;
            Own.Dispose();
            Own = null;
            db.Database.AutoSavepointsEnabled = _savepoints;
        }

        public async Task CloseAsync(AppDbContext db)
        {
            if (Own is null) return;
            await Own.DisposeAsync().ConfigureAwait(false);
            Own = null;
            db.Database.AutoSavepointsEnabled = _savepoints;
        }

        public void RollBack(AppDbContext db)
        {
            if (Own is null) return;
            // A failed commit can leave nothing to roll back; disposing ends it either way.
            try { Own.Rollback(); }
            catch (InvalidOperationException) { }
            catch (System.Data.Common.DbException) { }
            Close(db);
        }

        public async Task RollBackAsync(AppDbContext db)
        {
            if (Own is null) return;
            try { await Own.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            catch (System.Data.Common.DbException) { }
            await CloseAsync(db).ConfigureAwait(false);
        }
    }

    private static async Task CaptureAsync(AppDbContext db, Pending pending, CancellationToken cancellationToken)
    {
        var deletedTags = new List<Guid>();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case TxnHeaderRow header:
                    if (entry.State == EntityState.Added) pending.Headers.Add(header.Id);
                    else if (entry.State == EntityState.Deleted) pending.DeletedHeaders.Add(header.Id);
                    else if (entry.State == EntityState.Modified
                             && (Modified(entry, nameof(TxnHeaderRow.Memo))
                                 || Modified(entry, nameof(TxnHeaderRow.Action))
                                 || Modified(entry, nameof(TxnHeaderRow.OriginatingAccountId))))
                        pending.Headers.Add(header.Id);
                    break;
                // A leg's amount, role, status or quantity is not an input: only the
                // columns the rule reads make a change worth recomputing.
                case TxnLegRow leg when entry.State is EntityState.Added or EntityState.Deleted
                                        || (entry.State == EntityState.Modified
                                            && (Modified(entry, nameof(TxnLegRow.AccountId))
                                                || Modified(entry, nameof(TxnLegRow.PostingIndex))
                                                || Modified(entry, nameof(TxnLegRow.LegMemo))
                                                || Modified(entry, nameof(TxnLegRow.HeaderId)))):
                    pending.Headers.Add(leg.HeaderId);
                    if (entry.State != EntityState.Added)
                        pending.Headers.Add((Guid)entry.OriginalValues[nameof(TxnLegRow.HeaderId)]!);
                    break;
                case TxnHeaderTagRow tag when entry.State is EntityState.Added or EntityState.Deleted or EntityState.Modified:
                    pending.Headers.Add(tag.HeaderId);
                    break;
                case TxnLegTagRow tag when entry.State is EntityState.Added or EntityState.Deleted or EntityState.Modified:
                    pending.Legs.Add(tag.LegId);
                    break;
                case TagRow tag when entry.State == EntityState.Deleted:
                    deletedTags.Add(tag.Id);
                    break;
                case AccountRow account when entry.State == EntityState.Modified
                                             && Modified(entry, nameof(AccountRow.HoldingsAccountId)):
                    pending.Accounts.Add(account.Id);
                    break;
            }
        }

        // A deleted tag's assignments cascade away with it, so who carried it is read now,
        // with the tag locked first: an assignment another request adds after this read
        // would cascade away unrecomputed (fn_lock_tags).
        if (deletedTags.Count > 0)
        {
            _ = await db.LockTags([.. deletedTags]).Select(r => r.Locked)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            pending.Headers.UnionWith(await db.TxnHeaderTags.AsNoTracking()
                .Where(t => deletedTags.Contains(t.TagId)).Select(t => t.HeaderId)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
            pending.Headers.UnionWith(await db.TxnLegTags.AsNoTracking()
                .Where(t => deletedTags.Contains(t.TagId))
                .Join(db.TxnLegs, t => t.LegId, l => l.Id, (t, l) => l.HeaderId)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private static bool Modified(EntityEntry entry, string property) => entry.Property(property).IsModified;

    private static async Task RecomputeAsync(AppDbContext db, Pending pending, CancellationToken cancellationToken)
    {
        var headers = new HashSet<Guid>(pending.Headers);
        if (pending.Legs.Count > 0)
        {
            var legs = pending.Legs.ToList();
            headers.UnionWith(await db.TxnLegs.AsNoTracking()
                .Where(l => legs.Contains(l.Id)).Select(l => l.HeaderId)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }
        if (pending.Accounts.Count > 0)
        {
            var accounts = pending.Accounts.ToList();
            // Locked before the headers on them are read: a leg another request adds to
            // one of them computes with the link this save is replacing, so it is waited
            // for and read, or it waits for this commit (fn_lock_accounts).
            _ = await db.LockAccounts([.. accounts]).Select(r => r.Locked)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            headers.UnionWith(await db.TxnHeaders.AsNoTracking()
                .Where(h => (h.OriginatingAccountId != null && accounts.Contains(h.OriginatingAccountId.Value))
                            || db.TxnLegs.Any(l => l.HeaderId == h.Id && accounts.Contains(l.AccountId)))
                .Select(h => h.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }
        headers.ExceptWith(pending.DeletedHeaders);
        await LegNotes.RecomputeAsync(db, headers, cancellationToken).ConfigureAwait(false);
    }
}
