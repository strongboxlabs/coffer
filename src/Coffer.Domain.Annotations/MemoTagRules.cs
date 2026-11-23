using System.Linq.Expressions;

namespace Coffer.Domain.Annotations;

/// <summary>
/// The one definition of which memo and which tags a register shows for a leg,
/// written once in C# so every reader (register, filters, search, reports, MCP,
/// merge, recall) applies the same rule. Every reader composes <see cref="Present"/>; the planner
/// and the tests run <see cref="Compiled"/>.
/// </summary>
public static class MemoTagRules
{
    /// <summary>The rule as an expression EF composes into a query.</summary>
    /// <remarks>
    /// <para>Read with the kinds table (<see cref="LegKind"/>): the split side and the
    /// event side own the header, so they never inherit; every other kind shows its
    /// own value, else its source. The source is the posting's other leg, except on
    /// an event's opposite row whose other leg is on the investment side: that leg
    /// carries nothing (an event's internal lines are never populated on its
    /// investment side), so the source is the header.</para>
    /// <para>Tags follow the memo's source. "Has tags" decides, not "has a memo": a
    /// side may own its tags and inherit its memo.</para>
    /// </remarks>
    public static Expression<Func<LegFacts, LegPresentation>> Present { get; } = Parts.Rule.Expand();

    /// <summary>The same rule, compiled, for in-memory use.</summary>
    public static Func<LegFacts, LegPresentation> Compiled { get; } = Present.Compile();

    /// <summary>
    /// The viewing account is the one the header belongs to: a plain transaction's
    /// originating account, an event's investment side. The side
    /// <see cref="LegPresentation.GroupsByHeader"/> reports; it reads only
    /// <see cref="LegFacts.ViewingAccountId"/>, <see cref="LegFacts.Action"/>,
    /// <see cref="LegFacts.OriginatingAccountId"/> and
    /// <see cref="LegFacts.ViewingOnInvestmentSide"/>.
    /// </summary>
    public static Expression<Func<LegFacts, bool>> HeadersAccount { get; } = Parts.HeadersAccount.Expand();

    /// <summary>
    /// The rule's named pieces. A nested class so they are initialised before
    /// <see cref="Present"/> reads them, whatever the declaration order.
    /// </summary>
    private static class Parts
    {
        // The account the header belongs to, the one side that keys an entry by the
        // header and edits a multi-posting header: a plain transaction's originating
        // account, an event's investment side. Every part below reads these two.
        public static readonly Expression<Func<LegFacts, bool>> PlainHeadersAccount = f =>
            f.ViewingAccountId == f.OriginatingAccountId;

        public static readonly Expression<Func<LegFacts, bool>> EventHeadersAccount = f =>
            f.ViewingOnInvestmentSide;

        public static readonly Expression<Func<LegFacts, bool>> HeadersAccount = f =>
            f.Action == null ? PlainHeadersAccount.Inline(f) : EventHeadersAccount.Inline(f);

        public static readonly Expression<Func<LegFacts, bool>> IsEventSide = f =>
            f.HeaderTotalPostings > 1 && f.Action != null && EventHeadersAccount.Inline(f);

        public static readonly Expression<Func<LegFacts, bool>> IsSplitSide = f =>
            f.HeaderTotalPostings > 1 && f.Action == null && PlainHeadersAccount.Inline(f);

        /// <summary>The split side and the event side: the header is theirs, so nothing is inherited.</summary>
        public static readonly Expression<Func<LegFacts, bool>> OwnsHeader = f =>
            IsEventSide.Inline(f) || IsSplitSide.Inline(f);

        /// <summary>An event's opposite row whose other leg is on the investment side inherits the header.</summary>
        public static readonly Expression<Func<LegFacts, bool>> SourceIsHeader = f =>
            f.HeaderTotalPostings > 1 && f.Action != null && !EventHeadersAccount.Inline(f) && f.OtherOnInvestmentSide;

        public static readonly Expression<Func<LegFacts, string?>> SourceMemo = f =>
            SourceIsHeader.Inline(f) ? f.HeaderMemo : f.OtherMemo;

        public static readonly Expression<Func<LegFacts, bool>> SourceHasTags = f =>
            SourceIsHeader.Inline(f) ? f.HeaderHasTags : f.OtherHasTags;

        public static readonly Expression<Func<LegFacts, Guid?>> SourceTagLegId = f =>
            SourceIsHeader.Inline(f) ? null : f.OtherLegId;

        public static readonly Expression<Func<LegFacts, Guid?>> SourceTagHeaderId = f =>
            SourceIsHeader.Inline(f) ? f.HeaderId : null;

        public static readonly Expression<Func<LegFacts, LegKind>> Kind = f =>
            f.HeaderTotalPostings <= 1
                ? (f.Action != null && !EventHeadersAccount.Inline(f) ? LegKind.EventCounterpartOne : LegKind.OnePosting)
                : f.Action == null
                    ? (PlainHeadersAccount.Inline(f) ? LegKind.SplitSide : LegKind.SplitOpposite)
                    : (EventHeadersAccount.Inline(f) ? LegKind.EventSide : LegKind.EventOpposite);

        /// <summary>
        /// A multi-posting header is edited only from its account; a one-posting row
        /// from either side, unless it is an event's counterpart.
        /// </summary>
        public static readonly Expression<Func<LegFacts, bool>> IsReadOnly = f =>
            f.HeaderTotalPostings <= 1
                ? f.Action != null && !EventHeadersAccount.Inline(f)
                : !HeadersAccount.Inline(f);

        public static readonly Expression<Func<LegFacts, Guid?>> DisplayTagLegId = f =>
            IsEventSide.Inline(f) ? null
            : IsSplitSide.Inline(f) ? f.LegId
            : f.OwnHasTags ? f.LegId
            : SourceTagLegId.Inline(f);

        public static readonly Expression<Func<LegFacts, Guid?>> DisplayTagHeaderId = f =>
            IsEventSide.Inline(f) ? f.HeaderId
            : IsSplitSide.Inline(f) ? null
            : f.OwnHasTags ? null
            : SourceTagHeaderId.Inline(f);

        public static readonly Expression<Func<LegFacts, LegPresentation>> Rule = f => new LegPresentation
        {
            Kind = Kind.Inline(f),
            IsReadOnly = IsReadOnly.Inline(f),
            // Only the header's account keys an entry by it. An opposite side never
            // groups, so each of its lines is its own row.
            GroupsByHeader = HeadersAccount.Inline(f),
            Memo = IsEventSide.Inline(f) ? f.HeaderMemo
                 : IsSplitSide.Inline(f) ? f.OwnMemo
                 : f.OwnMemo ?? SourceMemo.Inline(f),
            MemoInherited = !OwnsHeader.Inline(f) && f.OwnMemo == null && SourceMemo.Inline(f) != null,
            InheritedMemo = OwnsHeader.Inline(f) ? null : SourceMemo.Inline(f),
            InheritFromAccountId = OwnsHeader.Inline(f) ? null
                                 : SourceIsHeader.Inline(f) ? f.OriginatingAccountId
                                 : f.OtherAccountId,
            DisplayTagLegId = DisplayTagLegId.Inline(f),
            DisplayTagHeaderId = DisplayTagHeaderId.Inline(f),
            TagsInherited = !OwnsHeader.Inline(f) && !f.OwnHasTags && SourceHasTags.Inline(f),
            InheritedTagLegId = OwnsHeader.Inline(f) ? null : SourceTagLegId.Inline(f),
            InheritedTagHeaderId = OwnsHeader.Inline(f) ? null : SourceTagHeaderId.Inline(f),
            // A split-side line counts the header's tags with its own: a filter matches
            // what the split side's register shows, and a report counts the
            // transaction's tags for every line's money.
            InPlayTagLegId = DisplayTagLegId.Inline(f),
            InPlayTagHeaderId = IsSplitSide.Inline(f) ? f.HeaderId : DisplayTagHeaderId.Inline(f),
            InPlayHeaderMemo = OwnsHeader.Inline(f) ? f.HeaderMemo : null,
            InPlayHeaderTagHeaderId = OwnsHeader.Inline(f) ? f.HeaderId : null,
        };
    }

    /// <summary>
    /// The rule applied to an in-memory shape, with the tag sources resolved to the
    /// stored tag sets.
    /// </summary>
    public static ShownNotes<TTag> PresentInMemory<TTag>(TxnShape shape, Annotations<TTag> stored, LegKey viewing)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(stored);

        var leg = shape.Legs.FirstOrDefault(l => l.Key == viewing)
                  ?? throw new ArgumentException("The viewing leg is not on the shape.", nameof(viewing));
        var other = shape.OtherOf(viewing);

        // Ids exist only to name tag sources; they are mapped straight back.
        var headerId = Guid.NewGuid();
        var ids = shape.Legs.ToDictionary(l => l.Key, _ => Guid.NewGuid());
        var keys = ids.ToDictionary(p => p.Value, p => p.Key);

        var own = stored.For(viewing);
        var otherNotes = other is null ? Notes<TTag>.Empty : stored.For(other.Key);
        var presentation = Compiled(new LegFacts
        {
            LegId = ids[viewing],
            HeaderId = headerId,
            ViewingAccountId = viewing.AccountId,
            OtherLegId = other is null ? null : ids[other.Key],
            OtherAccountId = other?.Key.AccountId,
            Action = shape.Action,
            HeaderTotalPostings = shape.PostingCount,
            OriginatingAccountId = shape.OriginatingAccountId,
            ViewingOnInvestmentSide = shape.IsEvent && leg.OnInvestmentSide,
            OtherOnInvestmentSide = shape.IsEvent && other is { OnInvestmentSide: true },
            OwnMemo = own.Memo,
            OtherMemo = otherNotes.Memo,
            HeaderMemo = stored.Header.Memo,
            OwnHasTags = own.Tags.Count > 0,
            OtherHasTags = otherNotes.Tags.Count > 0,
            HeaderHasTags = stored.Header.Tags.Count > 0,
        });

        IReadOnlyList<TTag> TagsOf(Guid? legId, Guid? header)
        {
            var fromHeader = header == headerId ? stored.Header.Tags : [];
            var fromLeg = legId is { } id ? stored.For(keys[id]).Tags : [];
            return AnnotationText.UnionTags(fromHeader, fromLeg);
        }

        return new ShownNotes<TTag>(
            presentation,
            presentation.Memo,
            TagsOf(presentation.DisplayTagLegId, presentation.DisplayTagHeaderId),
            TagsOf(presentation.InPlayTagLegId, presentation.InPlayTagHeaderId),
            TagsOf(presentation.InheritedTagLegId, presentation.InheritedTagHeaderId),
            presentation.InPlayHeaderTagHeaderId is null ? null : TagsOf(null, presentation.InPlayHeaderTagHeaderId));
    }
}

/// <summary><see cref="MemoTagRules.PresentInMemory{TTag}"/>'s result.</summary>
/// <param name="Presentation">The rule's output.</param>
/// <param name="Memo">The memo the register shows.</param>
/// <param name="Tags">The tags the register shows.</param>
/// <param name="InPlayTags">The tags filters and reports count.</param>
/// <param name="InheritedTags">The tags the leg would show with its own blank.</param>
/// <param name="HeaderTags">The transaction's tags where the header is in play; null elsewhere.</param>
public sealed record ShownNotes<TTag>(
    LegPresentation Presentation,
    string? Memo,
    IReadOnlyList<TTag> Tags,
    IReadOnlyList<TTag> InPlayTags,
    IReadOnlyList<TTag> InheritedTags,
    IReadOnlyList<TTag>? HeaderTags)
    where TTag : notnull;
