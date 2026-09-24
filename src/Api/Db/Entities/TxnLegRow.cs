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
    // Amount / LegMemo; the investment PATCH additionally rewrites the
    // investment metadata in place, so that is mutable too.
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
    public string? LegMemo { get; set; }
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
