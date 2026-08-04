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
    // AccountId / PostingIndex / Amount / LegMemo are mutable (get;
    // set;) because the postings-reshape flow (ADR-0025) edits them
    // in place — same pattern as TxnHeaderRow.Status. Other fields
    // (id, header, investment metadata) stay init-only since they
    // don't change after row creation.
    public Guid AccountId { get; set; }
    public int PostingIndex { get; set; }
    public string? LegMemo { get; set; }
    public decimal Amount { get; set; }
    public Guid? SecurityId { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    /// <summary>
    /// Investment posting role marker (migration 056): one of
    /// <c>'security'</c>, <c>'income'</c>, <c>'transfer'</c>, <c>'fee'</c>;
    /// <c>NULL</c> on non-investment legs. Stamped by the importer from
    /// MD's <c>invest.splittype</c> and by the editor when adding
    /// postings. Both legs of a posting share the same role.
    /// </summary>
    public string? PostingRole { get; init; }
    /// <summary>
    /// Denormalized posting-count pair (migration 120, ADR-0036).
    /// <see cref="AccountPostingsOnHeader"/> is the number of postings of
    /// this leg's header that touch this leg's <see cref="AccountId"/>;
    /// <see cref="HeaderTotalPostings"/> is the header's total posting
    /// count. When they're equal the account ORIGINATES the header (it's
    /// touched by every posting); when
    /// <c>AccountPostingsOnHeader &lt; HeaderTotalPostings</c> this is a
    /// target-split leg whose header is owned elsewhere (read-only from
    /// this account's register).
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
}
