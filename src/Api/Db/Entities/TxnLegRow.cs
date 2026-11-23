using Coffer.Domain.Annotations;

namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_legs</c> (ADR-0022). Two legs per posting; N
/// postings per header. <see cref="PostingIndex"/> structurally pairs
/// the two sides of one posting (same value within a header, different
/// <see cref="AccountId"/>).
/// </summary>
internal sealed class TxnLegRow
{
    public Guid Id { get; init; }
    public Guid HeaderId { get; init; }
    // Denormalized from txn_headers.ledger_id (migration 049). The
    // DB composite FK (header_id, ledger_id) -> txn_headers(id,
    // ledger_id) refuses any insert where this disagrees with the
    // header's ledger, so the API just copies it from the header at
    // write time and lets the DB police coherence.
    public Guid LedgerId { get; init; }
    // Everything a reshape can edit is mutable (get; set;). The bank's
    // postings-reshape flow (ADR-0025) needs AccountId / PostingIndex /
    // Amount; the investment PATCH additionally rewrites the investment
    // metadata in place, so that is mutable too. LegMemo is the exception:
    // only the write primitive's adapter assigns it (AssignLegMemo).
    //
    // The investment metadata was init-only on the reasoning that it never
    // changes after row creation. That was true only because the investment
    // PATCH deleted every leg and rebuilt it — which is precisely the
    // behaviour that silently destroyed a row's reconciliation overlay, since
    // txn_leg_recon.leg_id cascades on delete. Keeping leg identity across an
    // edit means shares and price now change in place, so they must be
    // settable. Id / HeaderId / LedgerId stay init-only: those really are
    // fixed for the life of the row, and the interceptors rely on HeaderId in
    // particular being immutable.
    public Guid AccountId { get; set; }
    public int PostingIndex { get; set; }
    /// <summary>
    /// This side's own memo, or a split line's on its split side. Written only by
    /// the write primitive's adapter, through <see cref="AssignLegMemo"/>.
    /// </summary>
    public string? LegMemo { get; private set; }

    /// <summary>Store a planned leg memo (see <see cref="LegMemo"/>).</summary>
    internal void AssignLegMemo(string? memo) => LegMemo = memo;
    public decimal Amount { get; set; }
    public Guid? SecurityId { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    /// <summary>
    /// Investment posting role marker (migration 056): one of
    /// <c>'security'</c>, <c>'income'</c>, <c>'transfer'</c>, <c>'fee'</c>;
    /// <c>NULL</c> on non-investment legs. Stamped by the importer from
    /// MD's <c>invest.splittype</c> and by the editor when adding
    /// postings. Both legs of a posting share the same role.
    /// </summary>
    public string? PostingRole { get; set; }
    /// <summary>
    /// Denormalized posting-count pair (migration 120, ADR-0036).
    /// <see cref="AccountPostingsOnHeader"/> is the number of postings of
    /// this leg's header that touch this leg's <see cref="AccountId"/>;
    /// <see cref="HeaderTotalPostings"/> is the header's total posting
    /// count. When they're equal the account is touched by every posting;
    /// that does not make it the header's owner (a split whose every line
    /// lands on one category is equal there too). Whether this account's
    /// register can edit the row is <see cref="NotesIsReadOnly"/>.
    /// <para>Maintained entirely by
    /// <c>fn_recompute_posting_counts_for_header</c> (the recompute
    /// interceptor); read-only from EF's side. The DB defaults both to 1
    /// on insert and the recompute fn keeps them correct — EF must never
    /// write them (see the <c>ValueGeneratedOnAddOrUpdate</c> mapping in
    /// <see cref="AppDbContext"/>).</para>
    /// </summary>
    public int AccountPostingsOnHeader { get; init; }
    /// <summary>
    /// Header total posting count. See
    /// <see cref="AccountPostingsOnHeader"/>. DB-maintained (migration
    /// 120); read-only from EF.
    /// </summary>
    public int HeaderTotalPostings { get; init; }
    public DateTime CreatedAt { get; init; }

    // The stored memo-and-tags presentation (migration 264), INSERT-ONLY here: a writer
    // that already knows every input of a new leg's header inserts the leg with them
    // (LegNotes.Stamp, the only caller of StampNotes), so the recompute after the save
    // finds nothing to change. Mapped with an after-save behaviour of Throw, so setting
    // one on a leg already saved fails the save; every later change is LegNotes' own
    // update through its private store. Not read from here: readers read the view.
    public LegKind? NotesKind { get; private set; }
    public bool? NotesIsReadOnly { get; private set; }
    public bool? NotesGroupsByHeader { get; private set; }
    public string? NotesMemo { get; private set; }
    public bool? NotesMemoInherited { get; private set; }
    public string? NotesInheritedMemo { get; private set; }
    public Guid? NotesInheritFromAccountId { get; private set; }
    public bool? NotesTagsInherited { get; private set; }
    public string? NotesHeaderMemo { get; private set; }
    public bool? NotesHeaderInPlay { get; private set; }

    /// <summary>Set the leg's stored presentation before its insert. Called by <see cref="LegNotes.Stamp"/> only.</summary>
    internal void StampNotes(LegNotes.LegValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        NotesKind = values.Kind;
        NotesIsReadOnly = values.IsReadOnly;
        NotesGroupsByHeader = values.GroupsByHeader;
        NotesMemo = values.Memo;
        NotesMemoInherited = values.MemoInherited;
        NotesInheritedMemo = values.InheritedMemo;
        NotesInheritFromAccountId = values.InheritFromAccountId;
        NotesTagsInherited = values.TagsInherited;
        NotesHeaderMemo = values.HeaderMemo;
        NotesHeaderInPlay = values.HeaderInPlay;
    }
}
