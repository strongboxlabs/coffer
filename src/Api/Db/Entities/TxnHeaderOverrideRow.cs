namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_header_overrides</c> (ADR-0022 Rule 5). User
/// edits to header-level fields land here; the resolved view's
/// COALESCE chain prefers the override when present. Status was
/// dropped from this layer in migration 030 — reconciliation status
/// is user-action data and lives directly on <c>txn_headers</c> now
/// (not an override of an imported value).
/// </summary>
internal sealed class TxnHeaderOverrideRow
{
    public Guid HeaderId { get; init; }
    /// <summary>
    /// Denormalized from <c>txn_headers.ledger_id</c> (migration 072).
    /// Composite FK <c>(header_id, ledger_id) → txn_headers(id, ledger_id)</c>
    /// enforces coherence; RLS gates on this column directly.
    /// </summary>
    public Guid LedgerId { get; init; }
    public string? Payee { get; init; }
    public string? Memo { get; init; }
    public DateTime? PostedAt { get; init; }
    public DateTime? TransactedAt { get; init; }
    public string? CheckNumber { get; init; }
    public bool? IsHidden { get; init; }
    public DateTime UpdatedAt { get; init; }
}
