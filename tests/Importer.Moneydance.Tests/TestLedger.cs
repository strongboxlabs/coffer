namespace Coffer.Importer.Moneydance.Tests;

/// <summary>
/// The ledger every DB-backed importer test writes into.
/// </summary>
/// <remarks>
/// Production ships no seeded ledger: ADR-0088 (migration 186) removes the …0001
/// placeholder, because a seeded placeholder ledger makes first-run setup
/// misleading. The tests still want a stable id, so it is a *test fixture*
/// concern: <see cref="Db.PostgresFixture"/> seeds this row after applying
/// migrations.
///
/// Deliberately NOT …0001, so nothing can quietly depend on that well-known id
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
