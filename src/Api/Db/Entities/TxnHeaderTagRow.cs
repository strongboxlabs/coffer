namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_header_tags</c> (ADR-0022 Rule 6). Tags
/// describe the event, not individual legs; a "vacation" tag on a
/// multi-split paycheck applies to all legs by virtue of being on the
/// header.
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
