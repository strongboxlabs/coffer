using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Coffer.Api.Db.Entities;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Db;

/// <summary>
/// The one writer of the stored memo-and-tags presentation (migration 264): the
/// <c>txn_legs.notes_*</c> columns and <c>txn_leg_note_tags</c>. It reads a header's
/// stored rows, runs the rule in memory (<see cref="StoredNotes.Compute"/>) and stores
/// what it produces for every leg of the header.
/// </summary>
/// <remarks>
/// <para><b>Who calls it.</b> Every save through the change tracker reaches it through
/// <see cref="LegNotesRecomputeInterceptor"/>, in the save's transaction. A writer that
/// changes an input of the rule without the tracker (<c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c>, a database function) calls <see cref="RecomputeAsync"/> itself
/// for the headers it touched, inside its own transaction; <c>StoredNotesWriteCatalogTests</c>
/// holds every such writer to it. The upgrade's backfill computes through
/// <see cref="ComputeFrom"/> and writes through a store of its own, frozen at migration
/// 264's columns, and a writer that already knows a new leg's values inserts it with them
/// through <see cref="Stamp"/>.</para>
/// <para><b>What is an input.</b> The header's action, originating account, memo and
/// tags; every leg's account, posting, memo and tags; which legs exist; and the
/// holdings link of the brokerages involved. Nothing else the rule reads.</para>
/// <para><b>Nothing else writes the output.</b> The application's model maps the
/// <c>notes_*</c> columns on <see cref="TxnLegRow"/> only as insert-only values that
/// <see cref="Stamp"/> sets; every update of them and every tag row is written through
/// <see cref="Store"/>, a context private to this class, on the caller's connection and
/// transaction, or by the upgrade's backfill through its own.</para>
/// <para><b>Deterministic.</b> The result depends on the stored rows only, so the
/// upgrade's backfill and a save compute the same thing.</para>
/// </remarks>
internal static class LegNotes
{
    /// <summary>One header's inputs of the rule, with its ledger (for the tag rows).</summary>
    internal sealed record HeaderInput(NotesHeaderInput Input, Guid LedgerId);

    /// <summary>What a recompute wrote: the legs whose columns changed, and the tag rows it deleted and inserted.</summary>
    internal sealed record Writes(int LegsUpdated, int TagRowsDeleted, int TagRowsInserted)
    {
        public static readonly Writes None = new(0, 0, 0);

        public Writes Plus(Writes other) => new(
            LegsUpdated + other.LegsUpdated, TagRowsDeleted + other.TagRowsDeleted, TagRowsInserted + other.TagRowsInserted);
    }

    /// <summary>
    /// Store, for every leg of <paramref name="headerIds"/>, what the rule computes from
    /// their current rows. A header that no longer exists leaves nothing behind (its
    /// columns went with its legs, its tag rows cascade with them).
    /// </summary>
    /// <remarks>
    /// <para><b>Atomic.</b> Runs on <paramref name="db"/>'s connection and in its
    /// transaction when the caller holds one, so it commits or rolls back with the
    /// caller's write; without one it opens its own. Either way a failure leaves the
    /// previous values.</para>
    /// <para><b>One recompute of a header at a time.</b> The header rows are locked first,
    /// in id order (<see cref="AppDbContext.LockTxnHeaders"/>), and the rule's inputs are
    /// read after the lock. The order is what keeps two recomputes over overlapping
    /// headers from deadlocking: each batch is in id order, batches go in id order, and
    /// the lock takes a batch's rows in id order. Two recomputes of one header therefore run
    /// one after the other, and the second reads what the first's writer committed.</para>
    /// <para><b>READ COMMITTED only.</b> Reading after the lock sees the latest committed
    /// rows only because each READ COMMITTED statement takes a fresh snapshot; a REPEATABLE
    /// READ or SERIALIZABLE transaction would read rows as they were before the lock was
    /// granted. The lock refuses to run in any other isolation level, so such a caller
    /// fails rather than storing values computed from rows another writer has replaced.</para>
    /// <para><b>Only what changed is written.</b> A leg's columns are updated only where
    /// the computed value differs from the stored one, and a tag row is deleted and
    /// inserted again only if it disappeared, appeared, or its header or a role changed.</para>
    /// <para><b>Batched.</b> <see cref="BatchSize"/> headers per round trip, in id order,
    /// all inside the one transaction.</para>
    /// <para>The context must have no pending change of its own: the recompute reads the
    /// rule's inputs from the database, which would not see them, so that is refused
    /// rather than computed from the wrong rows.</para>
    /// </remarks>
    internal static Task<Writes> RecomputeAsync(
        AppDbContext db, IReadOnlyCollection<Guid> headerIds, CancellationToken cancellationToken) =>
        RecomputeAsync(db, headerIds, lockHeaders: true, cancellationToken);

    /// <param name="lockHeaders">False only for a whole-ledger rebuild inside the
    /// transaction that wrote (and so holds) every row of the ledger: the import.</param>
    private static async Task<Writes> RecomputeAsync(
        AppDbContext db, IReadOnlyCollection<Guid> headerIds, bool lockHeaders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(headerIds);
        if (headerIds.Count == 0) return Writes.None;
        var ids = headerIds.Distinct().Order().ToList();

        if (db.ChangeTracker.HasChanges())
            throw new InvalidOperationException(
                "The stored notes are recomputed after a write is saved; this context still has unsaved changes.");

        await using var own = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var writes = Writes.None;
        await using (var store = StoreOn(db))
        {
            foreach (var batch in ids.Chunk(BatchSize))
            {
                if (lockHeaders)
                {
                    // In id order (fn_lock_txn_headers): batches go in id order too, so two
                    // recomputes over overlapping headers always lock them in the same order.
                    // It also refuses any isolation level but READ COMMITTED.
                    _ = await db.LockTxnHeaders(batch).Select(r => r.Locked)
                        .SingleAsync(cancellationToken).ConfigureAwait(false);
                }
                var computed = Compute(await ReadInputsAsync(db, batch, cancellationToken).ConfigureAwait(false));
                writes = writes.Plus(await WriteAsync(store, batch, computed, cancellationToken).ConfigureAwait(false));
            }
        }
        if (own is not null) await own.CommitAsync(cancellationToken).ConfigureAwait(false);
        return writes;
    }

    /// <summary>Headers per round trip of a recompute.</summary>
    internal const int BatchSize = 2000;

    /// <summary>
    /// Recompute every header of <paramref name="ledgerId"/> on <paramref name="db"/>, in
    /// its current transaction: for a writer that created the ledger's rows wholesale
    /// inside one (the Moneydance import), so the values commit or roll back with it. No
    /// header lock: that transaction already holds every row it wrote.
    /// </summary>
    internal static async Task RecomputeLedgerInPlaceAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A ledger is rebuilt in place only inside the transaction that wrote it.");
        var all = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId)
            .OrderBy(h => h.Id).Select(h => h.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await RecomputeAsync(db, all, lockHeaders: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="RecomputeLedgerInPlaceAsync"/> on a transaction opened outside EF (the
    /// Moneydance import's, through Dapper): the stored values commit or roll back with
    /// the import, so no reader ever sees the imported ledger without them.
    /// </summary>
    internal static async Task RecomputeLedgerOnAsync(
        DbTransaction transaction, Guid ledgerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The import's transaction has no connection.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        db.Database.SetCommandTimeout(600);
        await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await RecomputeLedgerInPlaceAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the rule computes, and how it is stored, for <paramref name="inputs"/> read by
    /// the caller: the upgrade's backfill, which reads the inputs through a projection of
    /// its own and writes the result through a store of its own.
    /// </summary>
    internal static IReadOnlyList<Computed> ComputeFrom(
        IReadOnlyList<HeaderInput> inputs, IReadOnlyDictionary<Guid, Guid> holdingsOf)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(holdingsOf);
        return Compute((inputs, holdingsOf));
    }

    /// <summary>
    /// Set the stored presentation on new legs before they are inserted, for a writer that
    /// already knows every input of their header: the insert then carries the values and
    /// the recompute that follows the save finds nothing to change on the legs (it still
    /// writes their tag rows).
    /// </summary>
    /// <param name="header">The header's inputs as the save will leave them.</param>
    /// <param name="holdingsOf">Each brokerage's holdings account, for the header's accounts.</param>
    /// <param name="legs">The header's new legs, each one of <paramref name="header"/>'s legs.</param>
    /// <remarks>The values are the rule's output for exactly these inputs; the
    /// recompute after the save corrects any that are not, in the same transaction, and
    /// no save is refused over them (ADR-0108 D7: stored == computed is held by the tests and the
    /// consistency monitor, <c>StampedLegInsertTests</c> for new legs). The
    /// properties are insert-only, so a leg already saved cannot be stamped (the save
    /// would throw). <see cref="AnnotationWriter.ApplyManyAsync"/> stamps every new leg of
    /// the writes it applies, which is every tracked create.</remarks>
    internal static void Stamp(
        NotesHeaderInput header, IReadOnlyDictionary<Guid, Guid> holdingsOf, IReadOnlyCollection<TxnLegRow> legs)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(holdingsOf);
        ArgumentNullException.ThrowIfNull(legs);
        var byId = StoredNotes.Compute(header, holdingsOf).ToDictionary(n => n.LegId);
        foreach (var leg in legs)
        {
            if (leg.HeaderId != header.HeaderId || !byId.TryGetValue(leg.Id, out var notes))
                throw new ArgumentException($"Leg {leg.Id} is not a leg of header {header.HeaderId}.", nameof(legs));
            leg.StampNotes(Values(notes));
        }
    }

    /// <summary>What the rule produces for one leg, as the leg's <c>notes_*</c> columns store it.</summary>
    internal sealed record LegValues(
        Guid LegId,
        Guid HeaderId,
        LegKind Kind,
        bool IsReadOnly,
        bool GroupsByHeader,
        string? Memo,
        bool MemoInherited,
        string? InheritedMemo,
        Guid? InheritFromAccountId,
        bool TagsInherited,
        string? HeaderMemo,
        bool HeaderInPlay);

    /// <summary>The values and tag rows the rule computes for one leg.</summary>
    internal sealed record Computed(LegValues Notes, IReadOnlyList<TxnLegNoteTagRow> Tags);

    /// <summary>
    /// What the rule computes for every leg of <paramref name="headerIds"/> from their
    /// stored rows, without writing anything: the writer's input, and what the
    /// stored-equals-computed check compares the stored values with.
    /// </summary>
    internal static async Task<IReadOnlyList<Computed>> ComputeAsync(
        AppDbContext db, IReadOnlyCollection<Guid> headerIds, CancellationToken cancellationToken) =>
        Compute(await ReadInputsAsync(db, headerIds, cancellationToken).ConfigureAwait(false));

    /// <summary>The rule's inputs for <paramref name="headerIds"/>, read through <paramref name="db"/>.</summary>
    private static async Task<(IReadOnlyList<HeaderInput> Inputs, IReadOnlyDictionary<Guid, Guid> HoldingsOf)> ReadInputsAsync(
        AppDbContext db, IReadOnlyCollection<Guid> headerIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(headerIds);
        var ids = headerIds as List<Guid> ?? headerIds.ToList();

        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.LedgerId, h.Action, h.OriginatingAccountId, h.Memo })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (headers.Count == 0) return ([], new Dictionary<Guid, Guid>());

        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => ids.Contains(l.HeaderId))
            .Select(l => new { l.Id, l.HeaderId, l.AccountId, l.PostingIndex, l.LegMemo })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var headerTags = (await db.TxnHeaderTags.AsNoTracking()
                .Where(t => ids.Contains(t.HeaderId))
                .Select(t => new { t.HeaderId, t.TagId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(t => t.HeaderId, t => t.TagId);
        var legIds = legs.Select(l => l.Id).ToList();
        var legTags = (await db.TxnLegTags.AsNoTracking()
                .Where(t => legIds.Contains(t.LegId))
                .Select(t => new { t.LegId, t.TagId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(t => t.LegId, t => t.TagId);

        // The brokerages among the accounts the rule can ask about: every leg's, and
        // every originating account.
        var accountIds = legs.Select(l => l.AccountId)
            .Concat(headers.Where(h => h.OriginatingAccountId != null).Select(h => h.OriginatingAccountId!.Value))
            .Distinct().ToList();
        var holdingsOf = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.HoldingsAccountId != null)
            .Select(a => new { a.Id, Holdings = a.HoldingsAccountId!.Value })
            .ToDictionaryAsync(a => a.Id, a => a.Holdings, cancellationToken)
            .ConfigureAwait(false);

        var legsOf = legs.ToLookup(l => l.HeaderId);
        var inputs = headers.Select(h => new HeaderInput(
                new NotesHeaderInput(
                    h.Id, h.Action, h.OriginatingAccountId, h.Memo,
                    [.. headerTags[h.Id]],
                    [.. legsOf[h.Id].Select(l => new NotesLegInput(l.Id, l.AccountId, l.PostingIndex, l.LegMemo, [.. legTags[l.Id]]))]),
                h.LedgerId))
            .ToList();
        return (inputs, holdingsOf);
    }

    /// <summary>The rule over every header of <paramref name="read"/>.</summary>
    private static IReadOnlyList<Computed> Compute(
        (IReadOnlyList<HeaderInput> Inputs, IReadOnlyDictionary<Guid, Guid> HoldingsOf) read)
    {
        var result = new List<Computed>();
        foreach (var h in read.Inputs)
            foreach (var leg in StoredNotes.Compute(h.Input, read.HoldingsOf))
                result.Add(ToRows(leg, h.LedgerId));
        return result;
    }

    /// <summary>The stored form of one leg's result: its column values and its tag rows.</summary>
    internal static Computed ToRows(StoredLegNotes leg, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(leg);
        var tags = leg.Tags.Select(t => new TxnLegNoteTagRow
        {
            LegId = leg.LegId,
            TagId = t.TagId,
            HeaderId = leg.HeaderId,
            LedgerId = ledgerId,
            Shown = t.Shown,
            InPlay = t.InPlay,
            Inherited = t.Inherited,
            TransactionLevel = t.TransactionLevel,
        }).ToList();
        return new Computed(Values(leg), tags);
    }

    private static LegValues Values(StoredLegNotes leg)
    {
        var p = leg.Shown;
        return new LegValues(
            leg.LegId, leg.HeaderId, p.Kind, p.IsReadOnly, p.GroupsByHeader, p.Memo, p.MemoInherited,
            p.InheritedMemo, p.InheritFromAccountId, p.TagsInherited, p.InPlayHeaderMemo,
            HeaderInPlay: p.InPlayHeaderTagHeaderId != null);
    }

    /// <summary>
    /// Store <paramref name="computed"/> for <paramref name="headerIds"/>: each leg's
    /// columns where they differ, and the tag rows by difference.
    /// </summary>
    /// <remarks>The stored tag rows compared are those of the headers and of the current
    /// legs: a leg a set-based writer moved still carries its old header id on its rows.
    /// A row stays only if its key and its value (header and roles) are what the rule
    /// computes; every other stored row is deleted, and every computed row not kept is
    /// inserted. The deletes are saved before the inserts, so a row whose value changed
    /// is deleted and inserted again rather than updated, as the grants require.</remarks>
    private static async Task<Writes> WriteAsync(
        Store store, IReadOnlyCollection<Guid> headerIds, IReadOnlyList<Computed> computed,
        CancellationToken cancellationToken)
    {
        var ids = headerIds.ToArray();
        var byLeg = computed.ToDictionary(c => c.Notes.LegId);
        var legIds = byLeg.Keys.ToList();
        try
        {
            // The legs as stored, tracked: the store writes only the columns that differ.
            var stored = await store.Legs.Where(l => legIds.Contains(l.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in stored)
                row.Set(byLeg[row.Id].Notes);
            var legsUpdated = store.ChangeTracker.Entries<StoredLeg>().Count(e => e.State == EntityState.Modified);

            var existing = await store.Tags
                .Where(t => ids.Contains(t.HeaderId) || legIds.Contains(t.LegId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var wanted = computed.SelectMany(c => c.Tags).ToDictionary(t => (t.LegId, t.TagId));
            var kept = new HashSet<(Guid, Guid)>();
            var deleted = 0;
            foreach (var row in existing)
            {
                if (wanted.TryGetValue((row.LegId, row.TagId), out var want) && Same(row, want))
                {
                    kept.Add((row.LegId, row.TagId));
                    continue;
                }
                store.Tags.Remove(row);
                deleted++;
            }
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            store.ChangeTracker.Clear();

            var inserts = wanted.Where(w => !kept.Contains(w.Key)).Select(w => w.Value).ToList();
            if (inserts.Count > 0)
            {
                store.Tags.AddRange(inserts);
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return new Writes(legsUpdated, deleted, inserts.Count);
        }
        finally
        {
            // Nothing is read back through the store; a later batch starts clean.
            store.ChangeTracker.Clear();
        }
    }

    private static bool Same(TxnLegNoteTagRow a, TxnLegNoteTagRow b) =>
        a.HeaderId == b.HeaderId && a.Shown == b.Shown && a.InPlay == b.InPlay
        && a.Inherited == b.Inherited && a.TransactionLevel == b.TransactionLevel;

    /// <summary>
    /// Every leg of <paramref name="ledgerId"/> (every ledger when null) whose stored
    /// presentation is not what the rule computes from its current rows, or that has
    /// none, and every stored tag row the rule does not produce: one line each, empty
    /// when they agree.
    /// </summary>
    /// <remarks>The stored-equals-computed check, read through the context it is given;
    /// the tests run it at the end of every seed and writer test that asks, and over the
    /// whole test database when the fixture is torn down.</remarks>
    internal static async Task<IReadOnlyList<string>> DifferencesAsync(
        AppDbContext db, Guid? ledgerId, CancellationToken cancellationToken)
    {
        var found = await CompareHeadersOfAsync(db, ledgerId, BatchSize, describe: true, cancellationToken)
            .ConfigureAwait(false);
        return [.. found.Lines().SelectMany(d => d.Value)];
    }

    /// <summary>
    /// The headers among <paramref name="headerIds"/> whose stored presentation differs
    /// from what the rule computes, each with its lines (see <see cref="DifferencesAsync"/>).
    /// </summary>
    /// <remarks>Reads on <paramref name="db"/>'s connection, in its transaction when it
    /// holds one, so a caller decides which moment the stored values and the inputs are
    /// read at.</remarks>
    internal static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> DiffAsync(
        AppDbContext db, IReadOnlyCollection<Guid> headerIds, CancellationToken cancellationToken) =>
        (await CompareAmongAsync(db, headerIds, BatchSize, describe: true, cancellationToken).ConfigureAwait(false))
            .Lines();

    /// <summary>How many headers a comparison read, and the ids of those whose stored presentation differs.</summary>
    internal sealed record Drift(int Checked, IReadOnlyList<Guid> Headers);

    /// <summary>
    /// The stored-equals-computed comparison over every header of <paramref name="ledgerId"/>,
    /// <paramref name="chunkSize"/> headers at a time, keeping only the ids of the headers
    /// that differ: what the consistency monitor asks.
    /// </summary>
    /// <remarks>The headers are paged on their id (keyset), so no list of the ledger's
    /// headers is held, and nothing of a chunk outlives it. Reads on <paramref name="db"/>'s
    /// connection and in its transaction, as <see cref="DiffAsync"/> does.</remarks>
    internal static async Task<Drift> DriftOfLedgerAsync(
        AppDbContext db, Guid ledgerId, int chunkSize, CancellationToken cancellationToken)
    {
        var found = await CompareHeadersOfAsync(db, ledgerId, chunkSize, describe: false, cancellationToken)
            .ConfigureAwait(false);
        return new Drift(found.Compared, [.. found.Headers]);
    }

    /// <summary>
    /// The headers among <paramref name="headerIds"/> whose stored presentation differs,
    /// <paramref name="chunkSize"/> at a time, by id only (see <see cref="DriftOfLedgerAsync"/>).
    /// </summary>
    internal static async Task<IReadOnlyList<Guid>> DriftAmongAsync(
        AppDbContext db, IReadOnlyCollection<Guid> headerIds, int chunkSize, CancellationToken cancellationToken) =>
        [.. (await CompareAmongAsync(db, headerIds, chunkSize, describe: false, cancellationToken).ConfigureAwait(false))
            .Headers];

    /// <summary>The comparison over every header of <paramref name="ledgerId"/> (every ledger when null), paged on the header id.</summary>
    private static async Task<Found> CompareHeadersOfAsync(
        AppDbContext db, Guid? ledgerId, int chunkSize, bool describe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        var headers = db.TxnHeaders.AsNoTracking();
        if (ledgerId is { } ledger) headers = headers.Where(h => h.LedgerId == ledger);
        var ids = headers.Select(h => h.Id);
        var found = new Found(describe);
        await OnStoreAsync(db, async store =>
        {
            Guid? after = null;
            while (true)
            {
                var page = await (after is { } last ? ids.Where(id => id > last) : ids)
                    .OrderBy(id => id).Take(chunkSize)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                await CompareChunkAsync(db, store, page, found, cancellationToken).ConfigureAwait(false);
                if (page.Length < chunkSize) break;
                after = page[^1];
            }
        }, cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>The comparison over <paramref name="headerIds"/>, in chunks.</summary>
    private static async Task<Found> CompareAmongAsync(
        AppDbContext db, IReadOnlyCollection<Guid> headerIds, int chunkSize, bool describe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(headerIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        var found = new Found(describe);
        await OnStoreAsync(db, async store =>
        {
            foreach (var chunk in headerIds.Chunk(chunkSize))
                await CompareChunkAsync(db, store, chunk, found, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>Run <paramref name="work"/> with the store on <paramref name="db"/>'s connection.</summary>
    private static async Task OnStoreAsync(AppDbContext db, Func<Store, Task> work, CancellationToken cancellationToken)
    {
        // The store shares the context's connection, which must be open for it; opened
        // here only if the caller has not, and closed again only then.
        var opened = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var store = StoreOn(db);
            await work(store).ConfigureAwait(false);
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Compare one chunk of headers: every leg's stored values and tag rows with what the
    /// rule computes, and every stored tag row of the chunk's headers or legs that the rule
    /// does not produce. What differs goes to <paramref name="found"/>.
    /// </summary>
    /// <remarks>The comparison is on the values themselves; a line describing a difference
    /// is built only when <paramref name="found"/> asks for lines, so a comparison that
    /// agrees allocates nothing per leg beyond the rows it reads.</remarks>
    private static async Task CompareChunkAsync(
        AppDbContext db, Store store, Guid[] chunk, Found found, CancellationToken cancellationToken)
    {
        if (chunk.Length == 0) return;
        found.Compared += chunk.Length;
        var computed = await ComputeAsync(db, chunk, cancellationToken).ConfigureAwait(false);
        var legIds = computed.Select(c => c.Notes.LegId).ToList();
        var stored = await store.Legs.AsNoTracking()
            .Where(l => legIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, cancellationToken).ConfigureAwait(false);
        var storedTags = await store.Tags.AsNoTracking()
            .Where(t => chunk.Contains(t.HeaderId) || legIds.Contains(t.LegId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagsOf = storedTags.ToLookup(t => t.LegId);

        foreach (var c in computed)
        {
            var s = stored[c.Notes.LegId];
            if (s.Kind is null)
            {
                found.Add(c.Notes.HeaderId, found.Describes ? $"leg {c.Notes.LegId}: no stored notes" : null);
                continue;
            }
            var values = s.Values();
            var tags = tagsOf[s.Id];
            if (values == c.Notes && SameTags(c.Tags, tags)) continue;
            found.Add(c.Notes.HeaderId, found.Describes
                ? $"leg {c.Notes.LegId}: stored {Describe(values, tags)} but the rule computes {Describe(c.Notes, c.Tags)}"
                : null);
            // A row still naming the header a leg left is that header's drift too, wherever
            // the two headers fall: the same as when they fall in different chunks, below.
            foreach (var t in tags)
                if (t.HeaderId != c.Notes.HeaderId) found.Add(t.HeaderId, found.Describes ? NotProduced(t) : null);
        }
        var produced = legIds.ToHashSet();
        foreach (var t in storedTags.Where(t => !produced.Contains(t.LegId)))
            found.Add(t.HeaderId, found.Describes ? NotProduced(t) : null);
    }

    private static string NotProduced(TxnLegNoteTagRow t) =>
        $"leg {t.LegId}: a stored tag row for header {t.HeaderId} the rule does not produce";

    /// <summary>
    /// Whether a leg's stored tag rows are exactly the computed ones: the same tags, each
    /// with the same header and roles.
    /// </summary>
    /// <remarks>A leg holds one stored row per tag (the key), so every stored row matching
    /// a computed one and the counts agreeing is equality of the two sets.</remarks>
    private static bool SameTags(IReadOnlyList<TxnLegNoteTagRow> computed, IEnumerable<TxnLegNoteTagRow> stored)
    {
        var count = 0;
        foreach (var s in stored)
        {
            count++;
            var matched = false;
            foreach (var c in computed)
            {
                if (c.TagId != s.TagId) continue;
                matched = Same(s, c);
                break;
            }
            if (!matched) return false;
        }
        return count == computed.Count;
    }

    /// <summary>
    /// What a comparison found, by header: only the ids, or the ids with a line for each
    /// difference (the tests' report).
    /// </summary>
    private sealed class Found(bool describe)
    {
        private readonly Dictionary<Guid, List<string>?> _byHeader = [];

        /// <summary>Whether a difference is described in a line, or only its header kept.</summary>
        public bool Describes => describe;

        /// <summary>Headers compared.</summary>
        public int Compared { get; set; }

        /// <summary>The headers that differ, in the order they were found.</summary>
        public IEnumerable<Guid> Headers => _byHeader.Keys;

        public void Add(Guid header, string? line)
        {
            if (!_byHeader.TryGetValue(header, out var lines))
                _byHeader[header] = lines = describe ? [] : null;
            if (line is not null) lines?.Add(line);
        }

        public IReadOnlyDictionary<Guid, IReadOnlyList<string>> Lines() =>
            _byHeader.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)(p.Value ?? []));
    }

    // A line names the tag rows' header too: it is compared, and a reader narrows by it.
    private static string Describe(LegValues n, IEnumerable<TxnLegNoteTagRow> tags) =>
        string.Join('|',
            n.HeaderId, n.Kind, n.IsReadOnly, n.GroupsByHeader, n.Memo ?? "∅", n.MemoInherited,
            n.InheritedMemo ?? "∅", n.InheritFromAccountId?.ToString() ?? "∅", n.TagsInherited,
            n.HeaderMemo ?? "∅", n.HeaderInPlay,
            string.Join(',', tags.OrderBy(t => t.TagId).Select(t =>
                $"{t.TagId}@{t.HeaderId}:{(t.Shown ? "s" : "")}{(t.InPlay ? "p" : "")}{(t.Inherited ? "i" : "")}{(t.TransactionLevel ? "t" : "")}")));

    /// <summary>The store on <paramref name="db"/>'s open connection, in its transaction if it holds one.</summary>
    private static Store StoreOn(AppDbContext db) =>
        StoreOn(db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction(),
            db.Database.GetCommandTimeout());

    private static Store StoreOn(DbConnection connection, DbTransaction? transaction, int? commandTimeout)
    {
        var store = new Store(new DbContextOptionsBuilder<Store>()
            .UseNpgsql(connection, o => o.MaxBatchSize(StoreBatch))
            .Options);
        store.Database.SetCommandTimeout(commandTimeout);
        if (transaction is not null)
            store.Database.UseTransaction(transaction);
        return store;
    }

    /// <summary>Statements per round trip when the store saves.</summary>
    private const int StoreBatch = 1000;

    /// <summary>
    /// The only model that maps the stored presentation for updating: the
    /// <c>notes_*</c> columns of <c>txn_legs</c> (with the leg's key and header, read
    /// only) and <c>txn_leg_note_tags</c>. Private, so nothing outside this class can
    /// write them through EF.
    /// </summary>
    private sealed class Store(DbContextOptions<Store> options) : DbContext(options)
    {
        public DbSet<StoredLeg> Legs => Set<StoredLeg>();
        public DbSet<TxnLegNoteTagRow> Tags => Set<TxnLegNoteTagRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StoredLeg>(b =>
            {
                b.ToTable("txn_legs");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).HasColumnName("id");
                b.Property(x => x.HeaderId).HasColumnName("header_id");
                b.Property(x => x.Kind).HasColumnName("notes_kind").HasConversion<string>();
                b.Property(x => x.IsReadOnly).HasColumnName("notes_is_read_only");
                b.Property(x => x.GroupsByHeader).HasColumnName("notes_groups_by_header");
                b.Property(x => x.Memo).HasColumnName("notes_memo");
                b.Property(x => x.MemoInherited).HasColumnName("notes_memo_inherited");
                b.Property(x => x.InheritedMemo).HasColumnName("notes_inherited_memo");
                b.Property(x => x.InheritFromAccountId).HasColumnName("notes_inherit_from_account_id");
                b.Property(x => x.TagsInherited).HasColumnName("notes_tags_inherited");
                b.Property(x => x.HeaderMemo).HasColumnName("notes_header_memo");
                b.Property(x => x.HeaderInPlay).HasColumnName("notes_header_in_play");
            });
            modelBuilder.Entity<TxnLegNoteTagRow>(b =>
            {
                b.ToTable("txn_leg_note_tags");
                b.HasKey(x => new { x.LegId, x.TagId });
                b.Property(x => x.LegId).HasColumnName("leg_id");
                b.Property(x => x.TagId).HasColumnName("tag_id");
                b.Property(x => x.HeaderId).HasColumnName("header_id");
                b.Property(x => x.LedgerId).HasColumnName("ledger_id");
                b.Property(x => x.Shown).HasColumnName("shown");
                b.Property(x => x.InPlay).HasColumnName("in_play");
                b.Property(x => x.Inherited).HasColumnName("inherited");
                b.Property(x => x.TransactionLevel).HasColumnName("transaction_level");
            });
        }
    }

    /// <summary>A leg's stored presentation columns; all null until the rule has run for it.</summary>
    private sealed class StoredLeg
    {
        public Guid Id { get; init; }
        public Guid HeaderId { get; init; }
        public LegKind? Kind { get; set; }
        public bool? IsReadOnly { get; set; }
        public bool? GroupsByHeader { get; set; }
        public string? Memo { get; set; }
        public bool? MemoInherited { get; set; }
        public string? InheritedMemo { get; set; }
        public Guid? InheritFromAccountId { get; set; }
        public bool? TagsInherited { get; set; }
        public string? HeaderMemo { get; set; }
        public bool? HeaderInPlay { get; set; }

        /// <summary>Take <paramref name="v"/>; the tracker sees which columns changed.</summary>
        public void Set(LegValues v)
        {
            Kind = v.Kind;
            IsReadOnly = v.IsReadOnly;
            GroupsByHeader = v.GroupsByHeader;
            Memo = v.Memo;
            MemoInherited = v.MemoInherited;
            InheritedMemo = v.InheritedMemo;
            InheritFromAccountId = v.InheritFromAccountId;
            TagsInherited = v.TagsInherited;
            HeaderMemo = v.HeaderMemo;
            HeaderInPlay = v.HeaderInPlay;
        }

        /// <summary>The stored values of a presented leg.</summary>
        public LegValues Values() => new(
            Id, HeaderId, Kind!.Value, IsReadOnly!.Value, GroupsByHeader!.Value, Memo, MemoInherited!.Value,
            InheritedMemo, InheritFromAccountId, TagsInherited!.Value, HeaderMemo, HeaderInPlay!.Value);
    }
}
