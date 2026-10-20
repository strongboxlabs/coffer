namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>ledger_operation_errors</c> (ADR-0055; formerly
/// <c>sync_run_errors</c>, migration 038). One row per provider-reported error
/// captured during a run — a SimpleFIN v2 <c>errlist[]</c> entry, a quote
/// provider's per-symbol failure, etc. Lets the SPA expand a partial run to
/// show what the provider reported.
/// </summary>
internal sealed class LedgerOperationErrorRow
{
    public Guid Id { get; init; }
    public Guid LedgerOperationId { get; init; }
    /// <summary>
    /// Denormalized from <c>ledger_operations.ledger_id</c> (migration 072).
    /// Composite FK locks the parent's ledger; RLS gates on this column.
    /// </summary>
    public Guid LedgerId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? SimpleFinConnectionId { get; init; }
    public string? SimpleFinAccountId { get; init; }
    public DateTime CreatedAt { get; init; }
}
