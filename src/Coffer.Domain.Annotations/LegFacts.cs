namespace Coffer.Domain.Annotations;

/// <summary>
/// Everything the memo-and-tags rule may look at for one leg seen from one
/// register: plain values, so EF can project them from the tables and the planner
/// can build them in memory.
/// </summary>
/// <remarks>
/// A class with <c>init</c> properties rather than a positional record: EF composes
/// further <c>Where</c> and <c>OrderBy</c> only over member-init projections.
/// </remarks>
public sealed class LegFacts
{
    /// <summary>The leg being presented.</summary>
    public Guid LegId { get; init; }

    public Guid HeaderId { get; init; }

    /// <summary>The account whose register shows this leg: the leg's own account.</summary>
    public Guid ViewingAccountId { get; init; }

    /// <summary>The other leg of the same posting; null only for a malformed posting.</summary>
    public Guid? OtherLegId { get; init; }

    public Guid? OtherAccountId { get; init; }

    /// <summary>The header's investment action; null for a plain transaction.</summary>
    public string? Action { get; init; }

    /// <summary>Distinct posting indexes on the header, across every account.</summary>
    public int HeaderTotalPostings { get; init; }

    /// <summary>
    /// The stored originating account: the side that entered the
    /// transaction or last reshaped it. It, not a posting count, decides which side
    /// of a multi-split is the split side.
    /// </summary>
    public Guid? OriginatingAccountId { get; init; }

    /// <summary>
    /// The viewing account is on the event's investment side: the originating
    /// brokerage and its holdings account, or for <c>transfer_shares</c> both
    /// brokerages and both holdings accounts. Never true on a plain transaction.
    /// </summary>
    public bool ViewingOnInvestmentSide { get; init; }

    /// <inheritdoc cref="ViewingOnInvestmentSide"/>
    public bool OtherOnInvestmentSide { get; init; }

    public string? OwnMemo { get; init; }
    public string? OtherMemo { get; init; }
    public string? HeaderMemo { get; init; }

    public bool OwnHasTags { get; init; }
    public bool OtherHasTags { get; init; }
    public bool HeaderHasTags { get; init; }
}

/// <summary>
/// The six ways a leg can be seen. Which one applies is decided by the action, the
/// posting count, the originating account and the investment side: never by
/// counting the viewing account's postings.
/// </summary>
public enum LegKind
{
    /// <summary>A one-posting transaction from either side, or a one-posting event from its investment side. Own, else the other leg.</summary>
    OnePosting,

    /// <summary>A one-posting event seen from outside its investment side (an Xfr from the bank). Own, else the other leg; read-only.</summary>
    EventCounterpartOne,

    /// <summary>A multi-split line on the originating account. The row shows the header; the line its own value only.</summary>
    SplitSide,

    /// <summary>A multi-split line on any other account. Own, else that line's other leg; read-only.</summary>
    SplitOpposite,

    /// <summary>A multi-posting event on its investment side. The header; stored leg values are ignored.</summary>
    EventSide,

    /// <summary>A multi-posting event anywhere else, a funding brokerage included. Own, else the other leg, which is the header when that leg is on the investment side; read-only.</summary>
    EventOpposite,
}

/// <summary>What one register shows, filters on and counts for one leg.</summary>
public sealed class LegPresentation
{
    public LegKind Kind { get; init; }

    /// <summary>Edit and Duplicate are not offered: the row only displays its memo and tags.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// The register keys the leg's entry by the header, with the header's other legs on
    /// the same account: on the originating account, and on an event's investment side.
    /// Everywhere else the leg is an entry of its own, keyed by the leg; that is how an
    /// opposite side shows each line as its own row.
    /// </summary>
    public bool GroupsByHeader { get; init; }

    /// <summary>The memo this register shows for the leg.</summary>
    public string? Memo { get; init; }

    /// <summary>The shown memo is the other side's, not this side's own.</summary>
    public bool MemoInherited { get; init; }

    /// <summary>What the leg would show with its own memo blank; null where nothing is inherited by construction.</summary>
    public string? InheritedMemo { get; init; }

    /// <summary>The account an inherited value comes from; null on the split side and the event side.</summary>
    public Guid? InheritFromAccountId { get; init; }

    /// <summary>The leg whose tags the row shows, if they come from a leg.</summary>
    public Guid? DisplayTagLegId { get; init; }

    /// <summary>The header whose tags the row shows, if they come from the header.</summary>
    public Guid? DisplayTagHeaderId { get; init; }

    /// <summary>The shown tags are the other side's.</summary>
    public bool TagsInherited { get; init; }

    public Guid? InheritedTagLegId { get; init; }
    public Guid? InheritedTagHeaderId { get; init; }

    /// <summary>
    /// What filters and reports count. A split-side line counts the header's tags and
    /// its own together, because the transaction's tags apply to every line; everywhere else this equals the display
    /// source.
    /// </summary>
    public Guid? InPlayTagLegId { get; init; }

    /// <inheritdoc cref="InPlayTagLegId"/>
    public Guid? InPlayTagHeaderId { get; init; }

    /// <summary>The header memo where the header is in play (split side, event side); a split-side line's text search matches it too.</summary>
    public string? InPlayHeaderMemo { get; init; }

    /// <summary>
    /// The header whose tags are the transaction's, where the header is in play (split
    /// side, event side), like <see cref="InPlayHeaderMemo"/>; null everywhere else, an
    /// event's opposite row that inherits the header included.
    /// </summary>
    public Guid? InPlayHeaderTagHeaderId { get; init; }
}
