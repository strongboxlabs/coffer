using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Db;

/// <summary>
/// The EF adapter of the write primitive (<see cref="AnnotationPlanner"/>): it reads a
/// transaction into the planner's terms and stores a plan back. Every API writer of a
/// memo, a tag pairing or the originating account goes through here, so none of them
/// decides placement on its own.
/// </summary>
/// <remarks>
/// <para><b>Shape.</b> A transaction's shape is its legs by (posting, account), its
/// action and its originating account, plus the account facts the rule needs: which
/// accounts are brokerages (an account that names a holdings account), and which legs
/// are on an investment event's investment side: the originating brokerage and its
/// holdings account; on an in-kind transfer, every brokerage on it and theirs. An
/// in-kind transfer's source brokerage is its originating account: the writers that
/// build one name the source as its owner, and every stored one is asserted to be
/// owned by its source, so the adapter reads the stored owner rather than quantities.</para>
///
/// <para><b>Tags.</b> The planner plans tag ids: a request's names are resolved once,
/// through <see cref="TagDictionary"/>, before planning, so case variants of one name
/// are one tag when the tag limit counts a set.</para>
///
/// <para><b>Order.</b> A writer plans in its validate phase, so a refusal is a 422
/// before anything is written, and applies the plan once the legs it names exist.</para>
/// </remarks>
internal static class AnnotationWriter
{
    /// <summary>A leg of a shape being planned: stored (<see cref="LegId"/>) or about to be created.</summary>
    internal readonly record struct PlannedLeg(int PostingIndex, Guid AccountId, Guid? LegId = null, string? Role = null);

    /// <summary>Which accounts are brokerages, and their holdings accounts.</summary>
    internal sealed class AccountFacts
    {
        private readonly IReadOnlyDictionary<Guid, Guid> _holdingsOf;

        public AccountFacts(IReadOnlyDictionary<Guid, Guid> holdingsOf) => _holdingsOf = holdingsOf;

        public static AccountFacts None { get; } = new(new Dictionary<Guid, Guid>());

        public bool IsBrokerage(Guid accountId) => _holdingsOf.ContainsKey(accountId);

        public Guid? HoldingsOf(Guid accountId) => _holdingsOf.TryGetValue(accountId, out var h) ? h : null;

        /// <summary>Each brokerage's holdings account, as the stored-notes rule takes it.</summary>
        public IReadOnlyDictionary<Guid, Guid> Holdings => _holdingsOf;
    }

    /// <summary>The brokerage facts of <paramref name="accountIds"/>, in one query.</summary>
    internal static async Task<AccountFacts> AccountFactsAsync(
        AppDbContext db, IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ids = accountIds.Distinct().ToList();
        if (ids.Count == 0) return AccountFacts.None;
        var holdingsOf = await db.Accounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.HoldingsAccountId != null)
            .ToDictionaryAsync(a => a.Id, a => a.HoldingsAccountId!.Value, cancellationToken)
            .ConfigureAwait(false);
        return new AccountFacts(holdingsOf);
    }

    /// <summary>The planner's view of a transaction with these legs, action and owner.</summary>
    internal static TxnShape Shape(Guid? owner, string? action, IEnumerable<PlannedLeg> legs, AccountFacts facts)
    {
        var list = legs.ToList();
        var isTransferShares = action == TxnShape.TransferSharesAction;
        var investmentSide = new HashSet<Guid>();
        if (action is not null)
        {
            IEnumerable<Guid> brokerages = isTransferShares
                ? list.Select(l => l.AccountId).Where(facts.IsBrokerage)
                : owner is { } o ? [o] : [];
            foreach (var brokerage in brokerages)
            {
                investmentSide.Add(brokerage);
                if (facts.HoldingsOf(brokerage) is { } holdings) investmentSide.Add(holdings);
            }
        }

        return new TxnShape(owner, action,
        [
            .. list.Select(l => new ShapeLeg(
                new LegKey(l.PostingIndex, l.AccountId), l.LegId, l.Role,
                OnInvestmentSide: investmentSide.Contains(l.AccountId),
                IsBrokerage: facts.IsBrokerage(l.AccountId),
                IsTransferSource: isTransferShares && l.AccountId == owner)),
        ]);
    }

    /// <summary>A stored transaction in the planner's terms, with its tracked rows.</summary>
    internal sealed record Stored(
        TxnHeaderRow Header,
        IReadOnlyList<TxnLegRow> Legs,
        TxnShape Shape,
        Annotations<Guid> Notes,
        AccountFacts Facts);

    /// <summary>
    /// Read a tracked header's legs (tracked) and notes. The legs' accounts' facts are
    /// loaded with them, with <paramref name="moreAccounts"/> (accounts a reshape is
    /// about to put on it), so one query serves the before and the after shape.
    /// </summary>
    internal static async Task<Stored> LoadAsync(
        AppDbContext db, TxnHeaderRow header, CancellationToken cancellationToken,
        IEnumerable<Guid>? moreAccounts = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        return (await LoadManyAsync(db, [header], cancellationToken, moreAccounts).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// <see cref="LoadAsync"/> for several tracked headers at once, in the same four
    /// queries whatever their number; one <see cref="Stored"/> per header, in order.
    /// </summary>
    internal static async Task<IReadOnlyList<Stored>> LoadManyAsync(
        AppDbContext db, IReadOnlyList<TxnHeaderRow> headers, CancellationToken cancellationToken,
        IEnumerable<Guid>? moreAccounts = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Count == 0) return [];
        var headerIds = headers.Select(h => h.Id).ToList();
        var legsByHeader = (await db.TxnLegs
                .Where(l => headerIds.Contains(l.HeaderId))
                .OrderBy(l => l.PostingIndex)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(l => l.HeaderId);
        var legIds = legsByHeader.SelectMany(g => g).Select(l => l.Id).ToList();

        var headerTags = (await db.TxnHeaderTags.AsNoTracking()
                .Where(t => headerIds.Contains(t.HeaderId))
                .Select(t => new { t.HeaderId, t.TagId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(t => t.HeaderId, t => t.TagId);
        var legTags = (legIds.Count == 0
                ? []
                : await db.TxnLegTags.AsNoTracking()
                    .Where(t => legIds.Contains(t.LegId))
                    .Select(t => new { t.LegId, t.TagId })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
            .ToLookup(t => t.LegId, t => t.TagId);

        var facts = await AccountFactsAsync(
                db, legsByHeader.SelectMany(g => g).Select(l => l.AccountId).Concat(moreAccounts ?? []), cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. headers.Select(header =>
            {
                var legs = legsByHeader[header.Id].ToList();
                var shape = Shape(header.OriginatingAccountId, header.Action,
                    legs.Select(l => new PlannedLeg(l.PostingIndex, l.AccountId, l.Id, l.PostingRole)), facts);
                var notes = new Annotations<Guid>(
                    new Notes<Guid>(header.Memo, Sorted(headerTags[header.Id])),
                    legs.ToDictionary(
                        l => new LegKey(l.PostingIndex, l.AccountId),
                        l => new Notes<Guid>(l.LegMemo, Sorted(legTags[l.Id]))));
                return new Stored(header, legs, shape, notes, facts);
            }),
        ];
    }

    private static List<Guid> Sorted(IEnumerable<Guid> ids) => [.. ids.Order()];

    /// <summary>
    /// <paramref name="intent"/> with every tag name resolved to its tag, in one
    /// dictionary call; new names are created on first use (tracked).
    /// </summary>
    internal static async Task<AnnotationIntent<Guid>> ResolveAsync(
        AppDbContext db, Guid ledgerId, AnnotationIntent<string> intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var names = Names(intent.Tags).Concat((intent.Lines ?? []).SelectMany(l => Names(l.Tags)));
        var ids = await TagDictionary.ResolveAsync(db, ledgerId, names, cancellationToken).ConfigureAwait(false);

        FieldEdit<IReadOnlyList<Guid>> Map(FieldEdit<IReadOnlyList<string>> tags) => tags.Kind switch
        {
            FieldEditKind.Set => FieldEdit<IReadOnlyList<Guid>>.Set(
                [.. (tags.Value ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Select(n => ids[n])]),
            FieldEditKind.Inherit => FieldEdit<IReadOnlyList<Guid>>.Inherit,
            _ => FieldEdit<IReadOnlyList<Guid>>.Leave,
        };

        return new AnnotationIntent<Guid>(
            intent.Side,
            intent.Memo,
            Map(intent.Tags),
            intent.Lines?.Select(l => new LineIntent<Guid>(l.PostingIndex, l.Memo, Map(l.Tags))).ToList());

        static IEnumerable<string> Names(FieldEdit<IReadOnlyList<string>> tags) =>
            tags.Kind == FieldEditKind.Set ? tags.Value ?? [] : [];
    }

    /// <summary>
    /// Store <paramref name="plan"/>: the header's memo and tag pairings, each of
    /// <paramref name="legs"/>' memo and tag pairings, and the originating account.
    /// Only pushes changes into the change tracker; the caller owns SaveChanges and
    /// the transaction.
    /// </summary>
    /// <param name="legs">Every leg the transaction has after the write, keyed by its
    /// final posting and account. A leg the plan names that is not among them is a bug.</param>
    internal static Task ApplyAsync(
        AppDbContext db, TxnHeaderRow header, IReadOnlyCollection<TxnLegRow> legs,
        AnnotationPlan<Guid> plan, CancellationToken cancellationToken) =>
        ApplyManyAsync(db, [new PlannedWrite(header, legs, plan)], cancellationToken);

    /// <summary>One transaction's plan and the rows it is stored on (see <see cref="ApplyAsync"/>).</summary>
    internal sealed record PlannedWrite(TxnHeaderRow Header, IReadOnlyCollection<TxnLegRow> Legs, AnnotationPlan<Guid> Plan);

    /// <summary>
    /// <see cref="ApplyAsync"/> for several transactions at once: the stored pairings
    /// are read in two queries whatever their number.
    /// </summary>
    internal static async Task ApplyManyAsync(
        AppDbContext db, IReadOnlyList<PlannedWrite> writes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var headerTargets = new List<(TxnHeaderRow Header, IReadOnlyList<Guid> Tags)>(writes.Count);
        var legTargets = new List<(TxnLegRow Leg, IReadOnlyList<Guid> Tags)>();
        foreach (var (header, legs, plan) in writes)
        {
            ArgumentNullException.ThrowIfNull(header);
            ArgumentNullException.ThrowIfNull(legs);
            ArgumentNullException.ThrowIfNull(plan);
            if (!plan.IsValid)
                throw new InvalidOperationException("A plan with violations is never written.");

            var byKey = legs.ToDictionary(l => new LegKey(l.PostingIndex, l.AccountId));
            var unknown = plan.Result.Legs.Keys.Where(k => !byKey.ContainsKey(k)).ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"The plan names {unknown.Count} leg(s) the transaction does not have after the write.");

            header.AssignMemo(plan.Result.Header.Memo);
            foreach (var (key, leg) in byKey)
            {
                leg.AssignLegMemo(plan.Result.For(key).Memo);
                legTargets.Add((leg, plan.Result.For(key).Tags));
            }
            headerTargets.Add((header, plan.Result.Header.Tags));
        }

        await DiffHeaderTagsAsync(db, headerTargets, cancellationToken).ConfigureAwait(false);
        await DiffLegTagsAsync(db, legTargets, cancellationToken).ConfigureAwait(false);

        foreach (var (header, _, plan) in writes)
            if (plan.OriginatingAccountId is { } owner) header.AssignOriginatingAccount(owner);

        await StampNewLegsAsync(db, writes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Give every leg a write is creating its stored presentation before the insert
    /// (<see cref="LegNotes.Stamp"/>), so the save inserts it with its values instead of
    /// inserting it unfilled and updating it in the recompute that follows.
    /// </summary>
    /// <remarks>
    /// <para>The plan holds every input of the rule as the save will leave it: the
    /// header's memo and tags, and the memo and tags of every leg the transaction has
    /// after the write (<c>legs</c> is all of them, by contract). The action and the
    /// originating account are the header's, the owner assigned just above. The
    /// holdings links matter only to an investment event, so they are read only for
    /// one.</para>
    /// <para>A leg that already exists is not stamped: its columns are insert-only here,
    /// and the recompute updates it where its value changed. The stamp spares the
    /// recompute a second write of every new leg; it is not what makes the stored values
    /// right, which the recompute in the same save does whatever was stamped, so no save
    /// is refused over a missing or stale stamp (ADR-0108 D7). The tests hold every create path
    /// to stamping, and to stamping what the rule computes (<c>StampedLegInsertTests</c>);
    /// the consistency monitor checks stored == computed over every leg.</para>
    /// </remarks>
    private static async Task StampNewLegsAsync(
        AppDbContext db, IReadOnlyList<PlannedWrite> writes, CancellationToken cancellationToken)
    {
        var stamping = writes.Where(w => w.Legs.Any(l => IsNew(db, l))).ToList();
        if (stamping.Count == 0) return;

        var events = stamping.Where(w => w.Header.Action is not null).ToList();
        var facts = events.Count == 0
            ? AccountFacts.None
            : await AccountFactsAsync(db,
                    events.SelectMany(w => w.Legs.Select(l => l.AccountId)
                        .Concat(w.Header.OriginatingAccountId is { } o ? [o] : [])),
                    cancellationToken)
                .ConfigureAwait(false);

        foreach (var (header, legs, plan) in stamping)
        {
            var input = new NotesHeaderInput(
                header.Id, header.Action, header.OriginatingAccountId, header.Memo,
                [.. plan.Result.Header.Tags.Distinct()],
                [
                    .. legs.Select(l => new NotesLegInput(
                        l.Id, l.AccountId, l.PostingIndex, l.LegMemo,
                        [.. plan.Result.For(new LegKey(l.PostingIndex, l.AccountId)).Tags.Distinct()])),
                ]);
            LegNotes.Stamp(input, facts.Holdings, [.. legs.Where(l => IsNew(db, l))]);
        }
    }

    // A header or leg this write is creating has no stored pairings yet, so the diff
    // reads only the rows that are already in the database.
    private static bool IsNew(AppDbContext db, object entity) => db.Entry(entity).State == EntityState.Added;

    private static async Task DiffHeaderTagsAsync(
        AppDbContext db, IReadOnlyList<(TxnHeaderRow Header, IReadOnlyList<Guid> Tags)> targets,
        CancellationToken cancellationToken)
    {
        var storedIds = targets.Where(t => !IsNew(db, t.Header)).Select(t => t.Header.Id).ToList();
        var current = (storedIds.Count == 0
                ? []
                : await db.TxnHeaderTags
                    .Where(t => storedIds.Contains(t.HeaderId))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
            .ToLookup(t => t.HeaderId);
        foreach (var (header, target) in targets)
        {
            var wanted = target.ToHashSet();
            var have = current[header.Id].ToList();
            foreach (var pair in have.Where(p => !wanted.Contains(p.TagId)))
                db.TxnHeaderTags.Remove(pair);
            foreach (var tagId in wanted.Except(have.Select(p => p.TagId)))
                db.TxnHeaderTags.Add(new TxnHeaderTagRow { HeaderId = header.Id, TagId = tagId, LedgerId = header.LedgerId });
        }
    }

    private static async Task DiffLegTagsAsync(
        AppDbContext db, IReadOnlyList<(TxnLegRow Leg, IReadOnlyList<Guid> Tags)> targets,
        CancellationToken cancellationToken)
    {
        var storedIds = targets.Where(t => !IsNew(db, t.Leg)).Select(t => t.Leg.Id).ToList();
        var current = (storedIds.Count == 0
                ? []
                : await db.TxnLegTags
                    .Where(t => storedIds.Contains(t.LegId))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
            .ToLookup(t => t.LegId);
        foreach (var (leg, target) in targets)
        {
            var wanted = target.ToHashSet();
            var have = current[leg.Id].ToList();
            foreach (var pair in have.Where(p => !wanted.Contains(p.TagId)))
                db.TxnLegTags.Remove(pair);
            foreach (var tagId in wanted.Except(have.Select(p => p.TagId)))
                db.TxnLegTags.Add(new TxnLegTagRow { LegId = leg.Id, TagId = tagId, LedgerId = leg.LedgerId });
        }
    }

    /// <summary>
    /// Move every pairing this unit of work is about to insert for the tag
    /// <paramref name="from"/> onto <paramref name="to"/>: for a save that lost a race
    /// for a new tag's name, where another writer's tag of that name is the one to link
    /// (<see cref="TagDictionary.ReResolvePendingAsync"/>). Only pending pairings move;
    /// one already pending for <paramref name="to"/> is kept as it is.
    /// </summary>
    internal static void RepointPendingTag(AppDbContext db, Guid from, Guid to)
    {
        ArgumentNullException.ThrowIfNull(db);
        foreach (var entry in db.ChangeTracker.Entries<TxnHeaderTagRow>()
                     .Where(e => e.State == EntityState.Added && e.Entity.TagId == from).ToList())
        {
            var pairing = entry.Entity;
            entry.State = EntityState.Detached;
            if (!db.ChangeTracker.Entries<TxnHeaderTagRow>().Any(e =>
                    e.State == EntityState.Added && e.Entity.HeaderId == pairing.HeaderId && e.Entity.TagId == to))
                db.TxnHeaderTags.Add(new TxnHeaderTagRow { HeaderId = pairing.HeaderId, TagId = to, LedgerId = pairing.LedgerId });
        }
        foreach (var entry in db.ChangeTracker.Entries<TxnLegTagRow>()
                     .Where(e => e.State == EntityState.Added && e.Entity.TagId == from).ToList())
        {
            var pairing = entry.Entity;
            entry.State = EntityState.Detached;
            if (!db.ChangeTracker.Entries<TxnLegTagRow>().Any(e =>
                    e.State == EntityState.Added && e.Entity.LegId == pairing.LegId && e.Entity.TagId == to))
                db.TxnLegTags.Add(new TxnLegTagRow { LegId = pairing.LegId, TagId = to, LedgerId = pairing.LedgerId });
        }
    }

    /// <summary>
    /// Store the owner <see cref="AnnotationPlanner.Repoint"/> gave a header whose legs
    /// moved: no notes are written, since values sit on legs and the legs keep their ids.
    /// </summary>
    internal static void ApplyRepoint(TxnHeaderRow header, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(header);
        header.AssignOriginatingAccount(owner);
    }

    /// <summary>
    /// After every leg of <paramref name="fromAccountId"/> on the given headers (or on
    /// the whole ledger, when <paramref name="headerIds"/> is null) was repointed to
    /// <paramref name="toAccountId"/>: hand each header that <paramref name="fromAccountId"/>
    /// owned, and on which it no longer has a leg, to <paramref name="toAccountId"/>.
    /// Run inside the repoint's transaction, after the legs moved.
    /// </summary>
    /// <remarks>
    /// The set-based form of <see cref="AnnotationPlanner.Repoint"/>, for the repoints
    /// that move every leg of an account at once (category merge, move, bulk move,
    /// including the move that clears an investment action off a cash transfer). A
    /// repoint never changes who owns a transaction while the owner keeps a leg on it;
    /// only when none is left does ownership follow the legs. No annotation is
    /// rewritten: values sit on legs, and the legs keep their ids.
    /// </remarks>
    internal static Task<int> FollowRepointAsync(
        AppDbContext db,
        Guid ledgerId,
        Guid fromAccountId,
        Guid toAccountId,
        IReadOnlyCollection<Guid>? headerIds,
        CancellationToken cancellationToken)
    {
        var owned = db.TxnHeaders.Where(h =>
            h.LedgerId == ledgerId
            && h.OriginatingAccountId == fromAccountId
            && !db.TxnLegs.Any(l => l.HeaderId == h.Id && l.AccountId == fromAccountId));
        if (headerIds is not null)
            owned = owned.Where(h => headerIds.Contains(h.Id));
        return owned.ExecuteUpdateAsync(
            s => s.SetProperty(h => h.OriginatingAccountId, (Guid?)toAccountId),
            cancellationToken);
    }

    /// <summary>
    /// Capture the header's original (feed) values before the first edit overwrites
    /// them (migration 230), the memo included: the transaction's memo wherever it
    /// sits, which on a one-posting transaction is its originating side's own memo and
    /// on a multi-posting one the header's. Call BEFORE mutating anything.
    /// </summary>
    /// <remarks>
    /// The amount is captured too (migration 301, ADR-0114 D15): the transaction's
    /// amount on its originating account, the sum of its legs there, which is the Amount
    /// its own register shows, as it stood before the edit. Read from the database
    /// (<c>AsNoTracking</c>), so a caller that captures first records the stored legs
    /// whatever it has staged since. An edit rounds nothing, so the mark is false.
    /// </remarks>
    internal static async Task CaptureOriginalsAsync(
        AppDbContext db, TxnHeaderRow header, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(header);
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == header.Id)
            .Select(l => new { l.PostingIndex, l.AccountId, l.LegMemo, l.Amount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var originating = legs.Where(l => l.AccountId == header.OriginatingAccountId).ToList();
        decimal? amount = originating.Count > 0 ? originating.Sum(l => l.Amount) : null;
        var onePosting = legs.Select(l => l.PostingIndex).Distinct().Count() <= 1;
        // A one-posting header still holding a memo is data stored before the memo
        // moved onto the legs; that memo is the transaction's.
        var memo = onePosting && legs.Count > 0
            ? legs.Where(l => l.AccountId == header.OriginatingAccountId)
                  .OrderBy(l => l.PostingIndex)
                  .Select(l => l.LegMemo)
                  .FirstOrDefault() ?? header.Memo
            : header.Memo;
        await HeaderOriginals.CaptureAsync(db, header, memo, amount, roundedAtImport: false, cancellationToken)
            .ConfigureAwait(false);
    }
}
