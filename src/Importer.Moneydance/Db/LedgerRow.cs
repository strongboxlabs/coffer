namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>ledgers</c> (per ADR-0020 Phase A).
/// A ledger is the unit of book-isolation: every anchor table carries
/// <c>ledger_id</c>, derived tables inherit transitively.
/// </summary>
/// <remarks>
/// There is deliberately no well-known "default" ledger id (ADR-0088): migration
/// 186 drops the …0001 row migration 014 seeds, so every caller has to say which
/// ledger it means. <see cref="SystemUserId"/> is a different thing and still
/// real — the seeded system *user*, not a ledger.
/// </remarks>
public sealed record LedgerRow(
    Guid Id,
    string Name,
    DateTime CreatedAt)
{
    /// <summary>
    /// The bootstrap "system" user (migration 014) — a service identity, not a
    /// human. The reconcile diagnostic's scratch ledger, which never commits, is
    /// created under it. Imports pass the importing human's id so the ledger is
    /// owned by the person who created it (ADR-0071 D2).
    /// </summary>
    public static readonly Guid SystemUserId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");
}
