using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Side-aware memo and tag seeds: a test says which side writes, and the write
/// primitive (<see cref="AnnotationPlanner"/>) decides where the values go.
/// </summary>
/// <remarks>
/// <para>They sit beside <see cref="EditHeaderAsync"/> and <see cref="AddTagAsync"/>,
/// which write the header directly and stay until no test calls them. Those can put
/// a memo on a one-split's header, a state no writer produces once notes live per
/// side; these cannot, because a value the planner would not place is never stored,
/// and a write a real register would refuse throws.</para>
/// <para><c>originatingAccountId</c> is a parameter because the header does not store
/// it yet; it is the account the transaction was entered from, or the split side.</para>
/// <para>Tag names resolve to the ledger's existing tags ignoring case and outer
/// whitespace, so a seed never creates two tags that differ only in case, which no
/// writer can. The comparison is .NET's ordinal-ignore-case, which agrees with the
/// database's tag-name folding for the names seeds use; a test about non-ASCII
/// folding resolves through the database, not through these helpers.</para>
/// </remarks>
public sealed partial class SyntheticLedger
{
    /// <summary>
    /// Plan <paramref name="intent"/> against the transaction's stored notes and store
    /// the result. Throws, storing nothing, when the planner refuses the write.
    /// </summary>
    public async Task<AnnotationPlan<string>> WriteNotesAsync(
        Guid legOrHeaderId,
        AnnotationIntent<string> intent,
        Guid originatingAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var headerId = await ResolveHeaderIdAsync(legOrHeaderId, cancellationToken).ConfigureAwait(false);

        await using var db = NewDbContext();
        var (shape, legs, stored) = await LoadShapeAsync(db, headerId, originatingAccountId, cancellationToken)
            .ConfigureAwait(false);
        var spelling = await TagSpellingsAsync(db, cancellationToken).ConfigureAwait(false);
        var plan = AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, stored), Resolved(intent, spelling));
        await StoreAsync(db, headerId, legs, plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// Change the transaction's structure with <paramref name="restructure"/>, which
    /// keeps the existing legs and their ids as the bank PATCH does, then plan that
    /// change of shape as <paramref name="side"/> makes it, with no edit of its own, and
    /// store what the write primitive places: what a reshape leaves behind.
    /// </summary>
    /// <remarks>Throws when the reshape would hand the transaction to another account,
    /// which this seed does not store.</remarks>
    public async Task<AnnotationPlan<string>> ReshapeAsync(
        Guid headerId, Guid side, Func<Task> restructure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restructure);
        TxnShape before;
        Annotations<string> current;
        await using (var db = NewDbContext())
        {
            var originating = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.Id == headerId)
                .Select(h => h.OriginatingAccountId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            (before, _, current) = await LoadShapeAsync(db, headerId, originating, cancellationToken).ConfigureAwait(false);
        }

        await restructure().ConfigureAwait(false);

        await using var after = NewDbContext();
        var (shape, legs, _) = await LoadShapeAsync(after, headerId, before.OriginatingAccountId, cancellationToken)
            .ConfigureAwait(false);
        var plan = AnnotationPlanner.Plan(
            new ShapeChange<string>(before, current, shape, ShapeChange<string>.ByLegId(before, shape)),
            new AnnotationIntent<string>(side, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<string>>.Leave));
        if (plan.IsValid && plan.OriginatingAccountId != before.OriginatingAccountId)
            throw new InvalidOperationException("This reshape hands the transaction to another account, which the seed does not store.");
        await StoreAsync(after, headerId, legs, plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    /// <summary>Store <paramref name="plan"/>'s notes in place of the transaction's; throws, storing nothing, when the planner refused the write.</summary>
    private async Task StoreAsync(
        AppDbContext db, Guid headerId, IReadOnlyList<StoredLeg> legs, AnnotationPlan<string> plan,
        CancellationToken cancellationToken)
    {
        if (!plan.IsValid)
            throw new InvalidOperationException(
                "The write primitive refused this seed: "
                + string.Join("; ", plan.Violations.Select(v => $"{v.Code} ({v.Detail})")));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ledgerId = LedgerId;
        var headerMemo = plan.Result.Header.Memo;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE txn_headers SET memo = {headerMemo} WHERE id = {headerId};",
            cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM txn_header_tags WHERE header_id = {headerId};",
            cancellationToken).ConfigureAwait(false);
        foreach (var name in plan.Result.Header.Tags)
        {
            var tagId = await EnsureTagAsync(db, name, cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO txn_header_tags (header_id, tag_id, ledger_id) VALUES ({headerId}, {tagId}, {ledgerId});",
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var leg in legs)
        {
            var notes = plan.Result.For(new LegKey(leg.PostingIndex, leg.AccountId));
            var legMemo = notes.Memo;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE txn_legs SET leg_memo = {legMemo} WHERE id = {leg.Id};",
                cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM txn_leg_tags WHERE leg_id = {leg.Id};",
                cancellationToken).ConfigureAwait(false);
            foreach (var name in notes.Tags)
            {
                var tagId = await EnsureTagAsync(db, name, cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO txn_leg_tags (leg_id, tag_id, ledger_id) VALUES ({leg.Id}, {tagId}, {ledgerId});",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await BackfillNotesAsync(headerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The ledger's tag spellings, keyed by trimmed name ignoring case. When a ledger
    /// already holds case variants (only a fixture written by hand can), the first
    /// by name wins, so the choice is stable.
    /// </summary>
    private async Task<Dictionary<string, string>> TagSpellingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var names = await db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == LedgerId)
            .Select(t => t.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Order(StringComparer.Ordinal)) spelling.TryAdd(name.Trim(), name);
        return spelling;
    }

    /// <summary>
    /// <paramref name="intent"/> with every tag name replaced by the spelling the
    /// ledger already has, or, for a new name, by its first trimmed spelling in this
    /// write. Two spellings of one name then plan as one tag.
    /// </summary>
    private static AnnotationIntent<string> Resolved(AnnotationIntent<string> intent, Dictionary<string, string> spelling)
    {
        string Canonical(string name)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return name;
            if (!spelling.TryGetValue(trimmed, out var known)) spelling[trimmed] = known = trimmed;
            return known;
        }

        FieldEdit<IReadOnlyList<string>> Tags(FieldEdit<IReadOnlyList<string>> tags) =>
            tags.Kind == FieldEditKind.Set
                ? FieldEdit<IReadOnlyList<string>>.Set([.. (tags.Value ?? []).Select(Canonical)])
                : tags;

        return intent with
        {
            Tags = Tags(intent.Tags),
            Lines = intent.Lines?.Select(l => l with { Tags = Tags(l.Tags) }).ToList(),
        };
    }

    /// <summary>
    /// The tag of this exact (already resolved) name in the ledger, created on first
    /// use. Names reach here through <see cref="Resolved"/>, so an existing tag is
    /// always found under its own spelling.
    /// </summary>
    private async Task<Guid> EnsureTagAsync(AppDbContext db, string name, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQueryRaw<TagIdRow>(
                "INSERT INTO tags (id, ledger_id, name) VALUES ({0}, {1}, {2}) " +
                "ON CONFLICT (ledger_id, name) DO UPDATE SET name = EXCLUDED.name " +
                "RETURNING id AS \"Value\"",
                Guid.NewGuid(), LedgerId, name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows[0].Value;
    }

    /// <summary>Write <paramref name="memo"/> as <paramref name="side"/> would: null or blank reverts to inheriting.</summary>
    public Task<AnnotationPlan<string>> SetSideMemoAsync(
        Guid legOrHeaderId, Guid side, string? memo, Guid originatingAccountId,
        CancellationToken cancellationToken = default) =>
        WriteNotesAsync(
            legOrHeaderId,
            new AnnotationIntent<string>(side,
                memo is null ? FieldEdit<string>.Inherit : FieldEdit<string>.Set(memo),
                FieldEdit<IReadOnlyList<string>>.Leave),
            originatingAccountId, cancellationToken);

    /// <summary>Write <paramref name="tags"/> as <paramref name="side"/> would: an empty list reverts to inheriting.</summary>
    public Task<AnnotationPlan<string>> SetSideTagsAsync(
        Guid legOrHeaderId, Guid side, IReadOnlyList<string> tags, Guid originatingAccountId,
        CancellationToken cancellationToken = default) =>
        WriteNotesAsync(
            legOrHeaderId,
            new AnnotationIntent<string>(side, FieldEdit<string>.Leave,
                tags.Count == 0 ? FieldEdit<IReadOnlyList<string>>.Inherit : FieldEdit<IReadOnlyList<string>>.Set(tags)),
            originatingAccountId, cancellationToken);

    /// <summary>
    /// The transaction's stored notes by leg (posting, account), tag names in
    /// ordinal order: what a test asserts against, and what the planner reads.
    /// </summary>
    public async Task<Annotations<string>> LoadNotesAsync(
        Guid legOrHeaderId, CancellationToken cancellationToken = default)
    {
        var headerId = await ResolveHeaderIdAsync(legOrHeaderId, cancellationToken).ConfigureAwait(false);
        await using var db = NewDbContext();
        var (_, _, stored) = await LoadShapeAsync(db, headerId, null, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <summary>
    /// What <see cref="MemoTagRules.PresentInMemory{TTag}"/> shows for the leg
    /// <paramref name="seenLegId"/> from its own account, over the transaction's stored
    /// shape, originating account and notes: the in-memory oracle a reader's output is
    /// compared with.
    /// </summary>
    public async Task<ShownNotes<string>> PresentAsync(Guid seenLegId, CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        var leg = await db.TxnLegs.AsNoTracking()
            .Where(l => l.Id == seenLegId)
            .Select(l => new { l.HeaderId, l.PostingIndex, l.AccountId })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var originating = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == leg.HeaderId)
            .Select(h => h.OriginatingAccountId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var (shape, _, stored) = await LoadShapeAsync(db, leg.HeaderId, originating, cancellationToken)
            .ConfigureAwait(false);
        return MemoTagRules.PresentInMemory(shape, stored, new LegKey(leg.PostingIndex, leg.AccountId));
    }

    /// <summary>
    /// What <paramref name="accountId"/>'s register shows for each of its legs of the
    /// transaction, in posting order: the one presentation rule over the stored notes
    /// and the stored owner. The readers compose the same rule, so this is what a user
    /// of that register sees once they read through it.
    /// </summary>
    public async Task<IReadOnlyList<ShownNotes<string>>> ShownForAsync(
        Guid legOrHeaderId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var headerId = await ResolveHeaderIdAsync(legOrHeaderId, cancellationToken).ConfigureAwait(false);
        await using var db = NewDbContext();
        var owner = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId)
            .Select(h => h.OriginatingAccountId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var (shape, _, stored) = await LoadShapeAsync(db, headerId, owner, cancellationToken).ConfigureAwait(false);
        return
        [
            .. shape.Legs
                .Where(l => l.Key.AccountId == accountId)
                .OrderBy(l => l.Key.PostingIndex)
                .Select(l => MemoTagRules.PresentInMemory(shape, stored, l.Key)),
        ];
    }

    private sealed record StoredLeg(Guid Id, int PostingIndex, Guid AccountId, string? PostingRole, string? LegMemo, decimal? Quantity);

    /// <summary>
    /// The source brokerage(s) of an in-kind transfer, by the one shared definition:
    /// the brokerage paired in the same posting with the negative-quantity leg on its
    /// own holdings account; only when no posting pairs that way, any brokerage on the
    /// transfer whose holdings account carries a negative-quantity leg. Strict first,
    /// because two brokerages may share one holdings account.
    /// </summary>
    internal static IReadOnlySet<Guid> TransferSources(
        IReadOnlyList<(int PostingIndex, Guid AccountId, decimal? Quantity)> legs,
        IReadOnlyDictionary<Guid, Guid> holdingsOf)
    {
        var onTransfer = legs.Select(l => l.AccountId).Where(holdingsOf.ContainsKey).ToHashSet();
        var strict = legs
            .Where(b => onTransfer.Contains(b.AccountId))
            .Where(b => legs.Any(h => h.PostingIndex == b.PostingIndex && h.AccountId == holdingsOf[b.AccountId] && h.Quantity < 0))
            .Select(b => b.AccountId)
            .ToHashSet();
        if (strict.Count > 0) return strict;
        return onTransfer
            .Where(b => legs.Any(h => h.AccountId == holdingsOf[b] && h.Quantity < 0))
            .ToHashSet();
    }

    private async Task<(TxnShape Shape, IReadOnlyList<StoredLeg> Legs, Annotations<string> Stored)> LoadShapeAsync(
        AppDbContext db, Guid headerId, Guid? originatingAccountId, CancellationToken cancellationToken)
    {
        var header = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId)
            .Select(h => new { h.Action, h.Memo })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .OrderBy(l => l.PostingIndex)
            .Select(l => new StoredLeg(l.Id, l.PostingIndex, l.AccountId, l.PostingRole, l.LegMemo, l.Quantity))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var legIds = legs.Select(l => l.Id).ToList();

        var headerTags = await db.TxnHeaderTags.AsNoTracking()
            .Where(t => t.HeaderId == headerId)
            .Join(db.Tags.AsNoTracking(), ht => ht.TagId, t => t.Id, (_, t) => t.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var legTags = (await db.TxnLegTags.AsNoTracking()
                .Where(t => legIds.Contains(t.LegId))
                .Join(db.Tags.AsNoTracking(), lt => lt.TagId, t => t.Id, (lt, t) => new { lt.LegId, t.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(t => t.LegId, t => t.Name);

        // The investment side: the originating brokerage and its
        // holdings account; for an in-kind transfer every brokerage on it and theirs.
        var accountIds = legs.Select(l => l.AccountId).Distinct().ToList();
        var holdingsOf = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.HoldingsAccountId != null)
            .ToDictionaryAsync(a => a.Id, a => a.HoldingsAccountId!.Value, cancellationToken).ConfigureAwait(false);
        var investmentSide = new HashSet<Guid>();
        if (header.Action is not null)
        {
            IEnumerable<Guid> brokerages = header.Action == LedgerActions.TransferShares
                ? holdingsOf.Keys
                : originatingAccountId is { } o ? [o] : Enumerable.Empty<Guid>();
            foreach (var brokerage in brokerages)
            {
                investmentSide.Add(brokerage);
                if (holdingsOf.TryGetValue(brokerage, out var holdings)) investmentSide.Add(holdings);
            }
        }

        var sources = header.Action == LedgerActions.TransferShares
            ? TransferSources([.. legs.Select(l => (l.PostingIndex, l.AccountId, l.Quantity))], holdingsOf)
            : new HashSet<Guid>();

        var shape = new TxnShape(originatingAccountId, header.Action,
        [
            .. legs.Select(l => new ShapeLeg(
                new LegKey(l.PostingIndex, l.AccountId), l.Id, l.PostingRole, investmentSide.Contains(l.AccountId),
                IsBrokerage: holdingsOf.ContainsKey(l.AccountId),
                IsTransferSource: sources.Contains(l.AccountId))),
        ]);
        var stored = new Annotations<string>(
            new Notes<string>(header.Memo, Sorted(headerTags)),
            legs.ToDictionary(
                l => new LegKey(l.PostingIndex, l.AccountId),
                l => new Notes<string>(l.LegMemo, Sorted(legTags[l.Id]))));
        return (shape, legs, stored);
    }

    private static List<string> Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];
}
