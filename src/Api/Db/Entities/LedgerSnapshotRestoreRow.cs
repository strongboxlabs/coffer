namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the TVF wrapper
/// <c>ledger_snapshot_restore(uuid, text) RETURNS TABLE(ledger_id uuid)</c>
/// (migration 111 / ADR-0037). Returns the input ledger id so EF has
/// a typed projection; the caller discards the value — the side effect
/// on the in-scope tables + balance materialisation is what matters.
/// </summary>
public sealed class LedgerSnapshotRestoreRow
{
    public Guid LedgerId { get; init; }
}
