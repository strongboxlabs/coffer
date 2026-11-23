namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_leg_note_tags</c> (migration 264): one tag in the stored
/// presentation of one leg, with the roles it plays there. Written only by
/// <see cref="LegNotes"/>; the rest of the presentation is the leg's own
/// <c>txn_legs.notes_*</c> columns.
/// </summary>
/// <remarks>
/// <see cref="HeaderId"/> is the leg's header when the row was written; every
/// recompute rewrites the rows of the legs it touches, so it is the leg's current
/// header, and a reader may narrow by it before matching the leg.
/// </remarks>
internal sealed class TxnLegNoteTagRow
{
    public Guid LegId { get; init; }
    public Guid TagId { get; init; }
    public Guid HeaderId { get; init; }
    public Guid LedgerId { get; init; }

    /// <summary>The row shows the tag.</summary>
    public bool Shown { get; init; }

    /// <summary>Filters and reports count the tag.</summary>
    public bool InPlay { get; init; }

    /// <summary>The row would show the tag with its own tags blank.</summary>
    public bool Inherited { get; init; }

    /// <summary>The tag is the transaction's, where the header is in play.</summary>
    public bool TransactionLevel { get; init; }
}
