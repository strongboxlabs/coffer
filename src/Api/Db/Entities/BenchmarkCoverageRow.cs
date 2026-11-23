namespace Coffer.Api.Db.Entities;

/// <summary>
/// The range of days a benchmark symbol's growth fetches have asked Yahoo for
/// (<c>benchmark_coverage</c>, migration 280, ADR-0114 D1, D11).
/// </summary>
/// <remarks>
/// Keyed by symbol and kept when the benchmark is removed, as its growth rows are, so
/// adding it back asks nothing already asked. The range is what was asked, whether or not
/// Yahoo had a bar for each day (a span before a fund launched is not asked again), and
/// it is contiguous: a fetch widens it only by an adjoining span it read in full.
/// </remarks>
internal sealed class BenchmarkCoverageRow
{
    public Guid LedgerId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>The first UTC day fetches have asked for.</summary>
    public DateOnly FetchedFromDate { get; set; }

    /// <summary>The last UTC day fetches have asked for.</summary>
    public DateOnly FetchedThroughDate { get; set; }
}
