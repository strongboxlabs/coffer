namespace Coffer.Importer.Moneydance.Tests;

/// <summary>
/// The ledger every DB-backed importer test writes into.
/// </summary>
/// <remarks>
/// Production used to expose <c>LedgerRow.DefaultId</c> — the …0001 row seeded by
/// migration 014 — and these tests leaned on it as a convenient known ledger.
/// ADR-0088 removed that row (migration 186) because a seeded placeholder ledger
/// is exactly what made first-run setup misleading. The tests still want a stable
/// id, but it is a *test fixture* concern now, not something production ships:
/// <see cref="Db.PostgresFixture"/> seeds this row after applying migrations.
///
/// Deliberately NOT …0001, so nothing can quietly depend on the old well-known id
/// and pass for the wrong reason.
/// </remarks>
public static class TestLedger
{
    /// <summary>Id of the ledger seeded by <see cref="Db.PostgresFixture"/>.</summary>
    public static readonly Guid Id =
        Guid.Parse("0000000f-0000-0000-0000-000000000001");

    /// <summary>Name of that ledger. Nothing asserts on it; it aids debugging.</summary>
    public const string Name = "Test Ledger";
}
