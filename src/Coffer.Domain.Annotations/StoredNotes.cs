namespace Coffer.Domain.Annotations;

/// <summary>One leg of a <see cref="NotesHeaderInput"/>: what the rule reads of it.</summary>
/// <param name="LegId">The leg.</param>
/// <param name="AccountId">The leg's account, the register that shows it.</param>
/// <param name="PostingIndex">Its posting; the posting's other leg is the one it may inherit from.</param>
/// <param name="Memo">The leg's own stored memo.</param>
/// <param name="TagIds">The leg's own stored tags.</param>
public sealed record NotesLegInput(
    Guid LegId,
    Guid AccountId,
    int PostingIndex,
    string? Memo,
    IReadOnlyCollection<Guid> TagIds);

/// <summary>
/// Everything the memo-and-tags rule reads for one transaction, from its stored rows:
/// the header, every leg, and the stored notes of both.
/// </summary>
/// <param name="HeaderId">The header.</param>
/// <param name="Action">Its investment action; null for a plain transaction.</param>
/// <param name="OriginatingAccountId">The stored originating account.</param>
/// <param name="Memo">The header's own stored memo.</param>
/// <param name="TagIds">The header's own stored tags.</param>
/// <param name="Legs">Every leg of the header.</param>
public sealed record NotesHeaderInput(
    Guid HeaderId,
    string? Action,
    Guid? OriginatingAccountId,
    string? Memo,
    IReadOnlyCollection<Guid> TagIds,
    IReadOnlyList<NotesLegInput> Legs);

/// <summary>One tag a leg's register shows, filters on or counts, and in which roles.</summary>
/// <param name="TagId">The tag.</param>
/// <param name="Shown">The row shows it.</param>
/// <param name="InPlay">Filters and reports count it.</param>
/// <param name="Inherited">The row would show it with its own tags blank.</param>
/// <param name="TransactionLevel">It is the transaction's tag where the header is in play.</param>
public readonly record struct StoredNoteTag(Guid TagId, bool Shown, bool InPlay, bool Inherited, bool TransactionLevel);

/// <summary>What the rule produces for one leg, seen from its own account, with its tag sources resolved to tags.</summary>
/// <param name="LegId">The leg.</param>
/// <param name="HeaderId">Its header.</param>
/// <param name="Shown">The rule's output. Its tag-source ids are the rule's; <see cref="Tags"/> holds what they name.</param>
/// <param name="Tags">Every tag in any role, each once, in id order.</param>
public sealed record StoredLegNotes(
    Guid LegId,
    Guid HeaderId,
    LegPresentation Shown,
    IReadOnlyList<StoredNoteTag> Tags);

/// <summary>
/// The memo-and-tags rule (<see cref="MemoTagRules"/>) applied to every leg of one
/// transaction at once, in memory, from the transaction's stored rows: what a writer
/// stores so a reader reads the rule's result instead of evaluating it.
/// </summary>
/// <remarks>
/// <para>It builds each leg's <see cref="LegFacts"/> the way the database reader does
/// (the posting's other leg, the distinct posting count, the investment side through
/// the brokerages' holdings links) and runs <see cref="MemoTagRules.Compiled"/>. Nothing
/// else decides what a register shows.</para>
/// <para>Every leg of the header is computed together because one leg's presentation
/// reads the others: an edit to any leg, to the header or to the shape changes what
/// other legs show.</para>
/// </remarks>
public static class StoredNotes
{
    /// <param name="header">The transaction.</param>
    /// <param name="holdingsOf">Each brokerage's holdings account, for every brokerage among the
    /// header's leg accounts and its originating account; an account absent here is not a
    /// brokerage.</param>
    public static IReadOnlyList<StoredLegNotes> Compute(
        NotesHeaderInput header, IReadOnlyDictionary<Guid, Guid> holdingsOf)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(holdingsOf);

        var byId = header.Legs.ToDictionary(l => l.LegId);
        var result = new List<StoredLegNotes>(header.Legs.Count);
        foreach (var facts in Facts(header, holdingsOf))
        {
            var shown = MemoTagRules.Compiled(facts);
            result.Add(new StoredLegNotes(facts.LegId, header.HeaderId, shown, TagsOf(shown, header, byId)));
        }
        return result;
    }

    /// <summary>
    /// The facts the rule reads for each leg of <paramref name="header"/>, in leg order,
    /// built from its stored rows: what <see cref="Compute"/> runs the rule over. Its own
    /// method so a check can hold the construction to the database's own reading of the
    /// rows (the posting's other leg, the posting count).
    /// </summary>
    /// <inheritdoc cref="Compute" path="/param"/>
    public static IReadOnlyList<LegFacts> Facts(
        NotesHeaderInput header, IReadOnlyDictionary<Guid, Guid> holdingsOf)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(holdingsOf);

        var legs = header.Legs;
        var postings = legs.Select(l => l.PostingIndex).Distinct().Count();
        var investmentSide = InvestmentSide(header, holdingsOf);
        var result = new List<LegFacts>(legs.Count);

        foreach (var leg in legs)
        {
            // The view's counterparty: the other leg of the same posting. A posting is two
            // legs; were it ever more, the lowest id is taken, so the result is stable.
            var other = legs
                .Where(o => o.PostingIndex == leg.PostingIndex && o.LegId != leg.LegId)
                .OrderBy(o => o.LegId)
                .FirstOrDefault();

            result.Add(new LegFacts
            {
                LegId = leg.LegId,
                HeaderId = header.HeaderId,
                ViewingAccountId = leg.AccountId,
                OtherLegId = other?.LegId,
                OtherAccountId = other?.AccountId,
                Action = header.Action,
                HeaderTotalPostings = postings,
                OriginatingAccountId = header.OriginatingAccountId,
                ViewingOnInvestmentSide = investmentSide.Contains(leg.AccountId),
                OtherOnInvestmentSide = other is not null && investmentSide.Contains(other.AccountId),
                OwnMemo = leg.Memo,
                OtherMemo = other?.Memo,
                HeaderMemo = header.Memo,
                OwnHasTags = leg.TagIds.Count > 0,
                OtherHasTags = other is not null && other.TagIds.Count > 0,
                HeaderHasTags = header.TagIds.Count > 0,
            });
        }

        return result;
    }

    /// <summary>
    /// The accounts on the event's investment side: the originating brokerage and its
    /// holdings account; for an in-kind transfer every brokerage with a leg on it and
    /// their holdings accounts. Empty for a plain transaction.
    /// </summary>
    private static HashSet<Guid> InvestmentSide(NotesHeaderInput header, IReadOnlyDictionary<Guid, Guid> holdingsOf)
    {
        var side = new HashSet<Guid>();
        if (header.Action is null) return side;

        IEnumerable<Guid> brokerages = header.Action == TxnShape.TransferSharesAction
            ? header.Legs.Select(l => l.AccountId).Where(holdingsOf.ContainsKey).Distinct()
            : header.OriginatingAccountId is { } originating ? [originating] : [];
        foreach (var brokerage in brokerages)
        {
            side.Add(brokerage);
            if (holdingsOf.TryGetValue(brokerage, out var holdings)) side.Add(holdings);
        }
        return side;
    }

    /// <summary>The tags the presentation's sources name, each with the roles it plays.</summary>
    private static List<StoredNoteTag> TagsOf(
        LegPresentation shown, NotesHeaderInput header, IReadOnlyDictionary<Guid, NotesLegInput> legs)
    {
        IEnumerable<Guid> Of(Guid? legId, Guid? headerId) =>
            (legId is { } l && legs.TryGetValue(l, out var leg) ? leg.TagIds : [])
            .Concat(headerId == header.HeaderId ? header.TagIds : []);

        var shownTags = Of(shown.DisplayTagLegId, shown.DisplayTagHeaderId).ToHashSet();
        var inPlay = Of(shown.InPlayTagLegId, shown.InPlayTagHeaderId).ToHashSet();
        var inherited = Of(shown.InheritedTagLegId, shown.InheritedTagHeaderId).ToHashSet();
        var transaction = Of(null, shown.InPlayHeaderTagHeaderId).ToHashSet();

        return shownTags.Concat(inPlay).Concat(inherited).Concat(transaction)
            .Distinct()
            .Order()
            .Select(t => new StoredNoteTag(
                t, shownTags.Contains(t), inPlay.Contains(t), inherited.Contains(t), transaction.Contains(t)))
            .ToList();
    }
}
