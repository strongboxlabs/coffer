using Coffer.Domain.Annotations;

namespace Coffer.Api.Db.Entities;

/// <summary>
/// One leg's stored memo-and-tags presentation (<c>txn_legs.notes_*</c>, migration
/// 264) with the leg's account and posting, read straight off the leg: what a text
/// search narrows by before it joins anything, and what a reader with no
/// <c>resolved_transactions</c> row reads (a reminder's template header is not a live
/// header, so the view leaves its legs out). Keyless, so it is never written through
/// it; <see cref="LegNotes"/> writes the columns.
/// </summary>
internal sealed class StoredLegNotesView
{
    public Guid LegId { get; init; }
    public Guid HeaderId { get; init; }
    public Guid LedgerId { get; init; }
    public Guid AccountId { get; init; }
    public int PostingIndex { get; init; }
    public LegKind? Kind { get; init; }
    public bool? GroupsByHeader { get; init; }
    public string? Memo { get; init; }
    public bool? MemoInherited { get; init; }
    public bool? TagsInherited { get; init; }
    public string? HeaderMemo { get; init; }
    public bool? HeaderInPlay { get; init; }
}
