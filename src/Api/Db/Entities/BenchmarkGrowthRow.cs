namespace Coffer.Api.Db.Entities;

/// <summary>
/// One trading day of a benchmark's total-return growth (<c>benchmark_growth</c>,
/// migration 280, ADR-0114 D2): that day's adjusted close over the previous trading
/// day's, both taken from one Yahoo response.
/// </summary>
/// <remarks>
/// <para>Keyed on (ledger, symbol, growth date): one value per day, and the first stored value
/// wins (ADR-0114 D6). The request role may insert a day and read it, never update or
/// delete one, so nothing on the request path rewrites a stored day.</para>
/// <para>No reference to <see cref="LedgerBenchmarkRow"/>: the rows outlive the
/// benchmark they were fetched for.</para>
/// </remarks>
internal sealed class BenchmarkGrowthRow
{
    public Guid LedgerId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>The UTC trading day (ADR-0070).</summary>
    public DateOnly GrowthDate { get; init; }

    /// <summary><c>NUMERIC(20,12)</c>, positive. Rounded once, at write, to the column's
    /// scale.</summary>
    public decimal Growth { get; init; }

    /// <summary>Always <see cref="BenchmarkGrowthSource.Fetch"/>: a growth factor is never
    /// typed (ADR-0114 D10).</summary>
    public required string Source { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>The values of <c>ck_benchmark_growth_source</c> (migration 280).</summary>
internal static class BenchmarkGrowthSource
{
    /// <summary>Fetched from the market-data provider's chart response.</summary>
    public const string Fetch = "fetch";
}
