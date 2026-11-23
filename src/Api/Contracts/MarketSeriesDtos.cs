namespace Coffer.Api.Contracts;

/// <summary>
/// One of a ledger's benchmarks (ADR-0114 D1): <c>GET/POST /api/ledgers/{id}/benchmarks</c>.
/// </summary>
/// <param name="Symbol">The Yahoo symbol, upper-cased as it was checked.</param>
/// <param name="Name">The fund's name as Yahoo gave it when added, else the symbol.</param>
/// <param name="CurrencyCode">The currency its closes are quoted in, major unit.</param>
/// <param name="Position">1-based order in the Benchmark control; compact (1..n).</param>
/// <param name="Availability">Whether the Returns report can draw it, for the requesting
/// user (ADR-0114 D1: an option that cannot be drawn is disabled and gives its reason). Set
/// on the list (<c>GET</c>); null on the add's answer, which the list read after it
/// fills.</param>
public sealed record BenchmarkDto(
    Guid Id, string Symbol, string Name, string CurrencyCode, int Position,
    BenchmarkAvailabilityDto? Availability = null);

/// <summary>
/// What stands between a benchmark and its line (ADR-0114 D1, D3, D9), as the server knows it.
/// </summary>
/// <param name="UnavailableReason">Null when its history is stored. Otherwise no point can
/// be drawn: <c>yahoo-off</c> when none is stored and the requesting user's Yahoo opt-in is
/// off on this ledger, so their refresh cannot fetch it (ADR-0114 D8); <c>no-history</c>
/// when none is stored yet and Yahoo is on, so the next fetch brings it. With history
/// stored, the opt-in does not matter: drawing reads what is stored and sends
/// nothing.</param>
/// <param name="NoRatesInto">The currencies a returns series can be drawn in (the
/// reporting currency and each brokerage's), other than the benchmark's own, that the
/// ledger holds no stored rate into from the benchmark's currency: against a series in
/// one of these, every point is <c>rate_unavailable</c> (ADR-0114 D9). Ordered
/// ordinally; empty when every one has a rate. The returns view picks its own series'
/// currency out of it, so one list serves every series and one read serves the
/// Settings list and the picker alike.</param>
public sealed record BenchmarkAvailabilityDto(string? UnavailableReason, IReadOnlyList<string> NoRatesInto);

/// <summary>Body of <c>POST /api/ledgers/{id}/benchmarks</c>.</summary>
public sealed class AddBenchmarkRequest
{
    public string? Symbol { get; init; }
}

/// <summary>
/// The rates view (ADR-0114 D10), <c>GET /api/ledgers/{id}/fx-rates</c>: the pairs the
/// ledger needs and how far each is covered.
/// </summary>
public sealed record FxRatesViewDto(string ReportingCurrency, IReadOnlyList<FxPairDto> Pairs);

/// <summary>One needed pair: one unit of <paramref name="FromCurrency"/> in
/// <paramref name="ToCurrency"/>.</summary>
/// <param name="NeededFor">Why it is needed, any of <c>position</c> (a price currency into
/// its holding account's), <c>net-worth</c> (an account's currency into the reporting
/// currency) and <c>benchmark</c> (a benchmark's currency into a returns series').</param>
/// <param name="NeededFrom">The earliest day a figure needs it; null when only the latest
/// rate is needed.</param>
/// <param name="FetchedFromDate">The first day fetches have asked for
/// (<c>fx_pair_coverage</c>); null when never fetched.</param>
/// <param name="FetchedThroughDate">The last day fetches have asked for; null when never
/// fetched.</param>
/// <param name="LatestManualDate">The latest hand-entered rate's day; null when none.</param>
/// <param name="CoveredThroughDate">The pair's coverage (ADR-0114 D9): the later of the
/// two above. A day after it has no rate, whatever is stored.</param>
/// <param name="RateCount">How many days are stored for the pair, from any source.</param>
/// <param name="FirstRateDate">The pair's first stored rate's day, from any source; null when
/// none is stored. A day before it has no rate.</param>
/// <param name="MissingDays">The days, from <paramref name="NeededFrom"/> (today when null)
/// through the server's UTC today, on which a conversion of the pair reads "rate
/// unavailable" (ADR-0114 D9), as spans, earliest first; empty when none is missing. The
/// same rule the conversion reads at (<c>FxPairMissingDays</c>): the stretch before the
/// first rate and the stretch after coverage ends, never a gap between stored days.</param>
/// <param name="NeededBy">What needs the pair, named: accounts first, then securities, then
/// benchmarks.</param>
public sealed record FxPairDto(
    string FromCurrency,
    string ToCurrency,
    IReadOnlyList<string> NeededFor,
    DateOnly? NeededFrom,
    DateOnly? FetchedFromDate,
    DateOnly? FetchedThroughDate,
    DateOnly? LatestManualDate,
    DateOnly? CoveredThroughDate,
    int RateCount,
    DateOnly? FirstRateDate,
    IReadOnlyList<FxMissingSpanDto> MissingDays,
    IReadOnlyList<FxPairSourceDto> NeededBy);

/// <summary>Consecutive days with no rate, <paramref name="FromDate"/> through
/// <paramref name="ThroughDate"/>, both included.</summary>
public sealed record FxMissingSpanDto(DateOnly FromDate, DateOnly ThroughDate);

/// <summary>One thing that needs a pair.</summary>
/// <param name="NeededFor">Which of the pair's <c>NeededFor</c> it is: <c>net-worth</c> (an
/// account, <paramref name="Id"/> its id), <c>position</c> (a security, <paramref name="Id"/>
/// its id, held in <paramref name="AccountId"/>, named <paramref name="AccountName"/>) or
/// <c>benchmark</c> (<paramref name="Id"/> the benchmark's).</param>
/// <param name="Symbol">A security's ticker or a benchmark's symbol; null for an account
/// and for a security with no ticker.</param>
public sealed record FxPairSourceDto(
    string NeededFor, Guid Id, string Name, string? Symbol, Guid? AccountId, string? AccountName);

/// <summary>One stored rate. <paramref name="Source"/> is <c>frankfurter</c> or
/// <c>manual</c>; <paramref name="Upstream"/> the source Frankfurter named for a fetched
/// one; <paramref name="EnteredByUserId"/> who typed a manual one.</summary>
public sealed record FxRateDto(
    DateOnly Date, decimal Rate, string Source, string? Upstream, Guid? EnteredByUserId);

/// <summary>A page of one pair's stored rates, newest first
/// (<c>GET /api/ledgers/{id}/fx-rates/{from}/{to}?before=&amp;limit=</c>). Pass
/// <paramref name="NextBefore"/> as <c>before</c> for the next page; null on the last.</summary>
public sealed record FxRatePageDto(
    string FromCurrency, string ToCurrency, IReadOnlyList<FxRateDto> Rates, DateOnly? NextBefore);

/// <summary>Body of <c>POST /api/ledgers/{id}/fx-rates</c>: a hand-entered rate
/// (ADR-0114 D10), one unit of <see cref="FromCurrency"/> in <see cref="ToCurrency"/> on
/// <see cref="Date"/> (<c>yyyy-MM-dd</c>).</summary>
public sealed class AddFxRateRequest
{
    public string? FromCurrency { get; init; }
    public string? ToCurrency { get; init; }
    public string? Date { get; init; }
    public decimal Rate { get; init; }
}
