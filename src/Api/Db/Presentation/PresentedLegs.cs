using Coffer.Api.Db.Entities;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Db.Presentation;

/// <summary>
/// One register row: the view's leg, the memo-and-tags presentation stored for it
/// from that leg's own account, and the register entry it belongs to.
/// </summary>
/// <remarks>
/// A class with <c>init</c> properties: EF composes further <c>Where</c>,
/// <c>GroupBy</c> and <c>OrderBy</c> over member-init projections.
/// </remarks>
internal sealed class RegisterLeg
{
    public ResolvedTransactionView Row { get; init; } = null!;

    /// <summary>The leg whose presentation <see cref="Shown"/> is: the row's own.</summary>
    public Guid NotesLegId { get; init; }

    /// <summary>What <see cref="MemoTagRules"/> presented for the leg when it was stored.</summary>
    public LegPresentation Shown { get; init; } = null!;

    /// <summary>
    /// The register entry: the header on a side that groups its legs
    /// (<see cref="LegPresentation.GroupsByHeader"/>), else the leg itself.
    /// </summary>
    public Guid EntryKey { get; init; }
}

/// <summary>
/// One leg a report or the transaction listing reads, presented from that reader's
/// perspective (<see cref="StoredNotesQuery.ReportLegs"/>, <see cref="StoredNotesQuery.ListingLegs"/>).
/// </summary>
internal sealed class ReportLeg
{
    public ResolvedTransactionView Row { get; init; } = null!;

    /// <summary>The leg the posting is presented from: the row's own, or the posting's other leg.</summary>
    public Guid NotesLegId { get; init; }

    /// <summary>What the perspective's register shows for this posting, and what it counts.</summary>
    public LegPresentation Shown { get; init; } = null!;
}

/// <summary>What a transaction listing is scoped by (<see cref="StoredNotesQuery.ListingLegs"/>).</summary>
internal enum ListingScope
{
    /// <summary>One account: its own legs.</summary>
    Account,

    /// <summary>The category (or account) a money-side leg pairs with.</summary>
    Counterparty,

    /// <summary>Nothing: every money-side leg of the ledger.</summary>
    Ledger,
}
