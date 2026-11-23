namespace Coffer.Api.Db.Entities;

/// <summary>
/// The range of days a currency pair's fetches have asked for (<c>fx_pair_coverage</c>,
/// migration 280, ADR-0114 D9, D11).
/// </summary>
/// <remarks>
/// <para>A rate is missing for a day after the pair's coverage, whatever <c>fx_rates</c>
/// holds: coverage runs to <see cref="FetchedThroughDate"/>, or to the pair's latest
/// hand-entered day if that is later. A pair with no row has no fetched coverage.</para>
/// <para>The range is what was ASKED, whether or not the feed had a rate for each day, so
/// a span before a currency existed is not asked again. It is contiguous, and a fetch
/// widens it only by an adjoining span it read in full.</para>
/// </remarks>
internal sealed class FxPairCoverageRow
{
    public Guid LedgerId { get; init; }

    public required string FromCurrency { get; init; }

    /// <summary>Never <see cref="FromCurrency"/>.</summary>
    public required string ToCurrency { get; init; }

    /// <summary>The first UTC day the pair's fetches have asked for.</summary>
    public DateOnly FetchedFromDate { get; set; }

    /// <summary>The last UTC day the pair's fetches have asked for.</summary>
    public DateOnly FetchedThroughDate { get; set; }
}
