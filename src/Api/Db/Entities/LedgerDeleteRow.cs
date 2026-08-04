namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless projection of the <c>ledger_delete(uuid)</c> TVF wrapper
/// (migration 141) — echoes the deleted ledger id so EF has a typed
/// result. The wrapper is the LINQ-bound entry point to the void
/// <c>fn_ledger_delete</c> worker (ADR-0032: no raw SQL in the API).
/// </summary>
public sealed class LedgerDeleteRow
{
    public Guid LedgerId { get; init; }
}
