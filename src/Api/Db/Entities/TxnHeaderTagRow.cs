namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_header_tags</c> (ADR-0022 Rule 6): the
/// transaction-level tags of a multi-posting transaction only (a split or
/// a multi-posting investment event). A one-posting transaction's tags sit
/// on its legs (<see cref="TxnLegTagRow"/>). A raw input of the stored
/// presentation; readers read <c>txn_leg_note_tags</c>.
/// </summary>
internal sealed class TxnHeaderTagRow
{
    public Guid HeaderId { get; init; }
    public Guid TagId { get; init; }
    /// <summary>
    /// Denormalized from <c>txn_headers.ledger_id</c> +
    /// <c>tags.ledger_id</c> (migration 072). Composite FKs lock both
    /// parents to the same ledger; RLS gates on this column directly.
    /// </summary>
    public Guid LedgerId { get; init; }
    public DateTime CreatedAt { get; init; }
}
