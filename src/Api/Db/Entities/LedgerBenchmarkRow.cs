namespace Coffer.Api.Db.Entities;

/// <summary>
/// One of a ledger's benchmark symbols (<c>ledger_benchmarks</c>, migration 280,
/// ADR-0114 D1): a fund or ETF the Returns report can be compared with.
/// </summary>
/// <remarks>
/// <para>At most three per ledger. The cap is the API's to check, with a business error
/// (ADR-0032: no trigger); the database backs it with <see cref="Position"/>'s CHECK
/// (1 to 3) and its per-ledger unique key, which is DEFERRABLE INITIALLY DEFERRED so a
/// reorder can swap two positions inside one save.</para>
/// <para>Removing a row leaves the symbol's <see cref="BenchmarkGrowthRow"/> history in
/// place, so adding it back needs no fresh fetch.</para>
/// </remarks>
internal sealed class LedgerBenchmarkRow
{
    public Guid Id { get; init; }

    public Guid LedgerId { get; init; }

    /// <summary>The Yahoo symbol, as the add-time check resolved it (ADR-0114 D2).
    /// Unique per ledger.</summary>
    public required string Symbol { get; init; }

    /// <summary>What the Returns footnote names (ADR-0114 D3). Non-blank.</summary>
    public required string Name { get; set; }

    /// <summary>The currency its closes are quoted in, in the major unit
    /// (ADR-0114 D5).</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>The order the Benchmark control lists it in: 1, 2 or 3, once per
    /// ledger.</summary>
    public int Position { get; set; }

    public DateTime CreatedAt { get; init; }
}
