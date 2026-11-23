using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Domain.Investment;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// One of a ledger's benchmarks as the returns series reads it: its name and currency
/// (<c>ledger_benchmarks</c>), the last day its fetches have asked for
/// (<c>benchmark_coverage.fetched_through_date</c>), and its first stored growth day
/// (<c>benchmark_growth</c>), read together in one statement.
/// </summary>
internal sealed record BenchmarkInLedger(
    string Symbol, string Name, string CurrencyCode, DateOnly? FirstGrowthDay, DateOnly? FetchedThrough)
{
    /// <summary>The ledger's benchmark <paramref name="symbol"/>, or null when it is not one of
    /// them. One statement: the coverage and the first growth day are subqueries of the
    /// benchmark row, each a probe of its table's primary key.</summary>
    public static async Task<BenchmarkInLedger?> FindAsync(
        AppDbContext db, Guid ledgerId, string symbol, CancellationToken cancellationToken)
    {
        var row = await db.LedgerBenchmarks.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId && b.Symbol == symbol)
            .Select(b => new
            {
                b.Symbol,
                b.Name,
                b.CurrencyCode,
                FirstGrowthDay = db.BenchmarkGrowth
                    .Where(g => g.LedgerId == ledgerId && g.Symbol == symbol)
                    .Min(g => (DateOnly?)g.GrowthDate),
                FetchedThrough = db.BenchmarkCoverage
                    .Where(c => c.LedgerId == ledgerId && c.Symbol == symbol)
                    .Select(c => (DateOnly?)c.FetchedThroughDate)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new BenchmarkInLedger(row.Symbol, row.Name, row.CurrencyCode, row.FirstGrowthDay, row.FetchedThrough);
    }
}

/// <summary>
/// A benchmark's stored daily growth over a range of days, read in one statement, and the
/// rule that says which days have a value (ADR-0114 D2, D3).
/// </summary>
/// <remarks>
/// <para><b>What a row is.</b> A <c>benchmark_growth</c> row for day <c>d</c> is the adjusted
/// close of <c>d</c> over the adjusted close of the trading day before it, both from one
/// Yahoo response (ADR-0114 D2). The benchmark's value on day <c>b</c> relative to day
/// <c>a</c> (a ≤ b) is therefore the product of the growth of every stored day after
/// <c>a</c> and through <c>b</c>: the days in (a, b].</para>
/// <para><b>Which days have a value.</b> A day relative to which the line is measured needs a
/// stored row on or before it: the first stored row's own base close is the day before it
/// in its response and is not stored, so a day before the first row has nothing to be
/// measured from. A day after <c>benchmark_coverage.fetched_through_date</c> has not been
/// asked for. Between the first stored row and the fetched-through day, the fetcher has
/// asked for every day (its range is contiguous and only widens by a span it read in full,
/// and each fetch starts at the last stored day so the first new day has its previous
/// close), so a day with no row there is a day the market did not trade: its growth is 1.
/// A value outside that range is missing, never 1: nothing is chained across it.</para>
/// </remarks>
internal sealed class BenchmarkGrowthTable
{
    private readonly DateOnly? _first;
    private readonly DateOnly? _through;
    private readonly DateOnly _loadedFrom;
    private readonly DateOnly _loadedTo;
    private readonly DateOnly[] _days;
    private readonly double[] _cumulative;

    private BenchmarkGrowthTable(
        DateOnly? first, DateOnly? through, DateOnly loadedFrom, DateOnly loadedTo,
        DateOnly[] days, double[] cumulative)
    {
        _first = first;
        _through = through;
        _loadedFrom = loadedFrom;
        _loadedTo = loadedTo;
        _days = days;
        _cumulative = cumulative;
    }

    /// <summary>
    /// The growth of <paramref name="benchmark"/> for the days in (<paramref name="from"/>,
    /// <paramref name="to"/>], enough to measure any day in [from, to] against another:
    /// one statement, an index range of <c>benchmark_growth</c>'s primary key. Nothing is
    /// read when the range holds no day after <paramref name="from"/>.
    /// </summary>
    public static async Task<BenchmarkGrowthTable> LoadAsync(
        AppDbContext db, Guid ledgerId, BenchmarkInLedger benchmark, DateOnly from, DateOnly to,
        CancellationToken cancellationToken)
    {
        var symbol = benchmark.Symbol;
        var rows = from < to
            ? await db.BenchmarkGrowth.AsNoTracking()
                .Where(g => g.LedgerId == ledgerId && g.Symbol == symbol
                            && g.GrowthDate > from && g.GrowthDate <= to)
                .OrderBy(g => g.GrowthDate)
                .Select(g => new { g.GrowthDate, g.Growth })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false)
            : [];

        var days = new DateOnly[rows.Count];
        var cumulative = new double[rows.Count];
        var product = 1.0;
        for (var i = 0; i < rows.Count; i++)
        {
            product *= (double)rows[i].Growth;
            days[i] = rows[i].GrowthDate;
            cumulative[i] = product;
        }
        return new BenchmarkGrowthTable(benchmark.FirstGrowthDay, benchmark.FetchedThrough, from, to, days, cumulative);
    }

    /// <summary>
    /// The day whose close a figure dated <paramref name="day"/> reads. A past day reads its
    /// own. A day on or after the server's UTC today is a current value, as a rate is
    /// (<see cref="RateDay.Of"/>, ADR-0114 D9): no close of today is stored until the day has
    /// closed, so it reads the newest stored day while the fetches reach yesterday, and its
    /// own day, which has no value, once they do not.
    /// </summary>
    public static DateOnly CloseDay(BenchmarkInLedger benchmark, DateOnly day, DateOnly serverToday) =>
        day >= serverToday
        && benchmark.FetchedThrough is { } through
        && through >= serverToday.AddDays(-1)
        && through < day
            ? through
            : day;

    /// <summary>
    /// The first day in [<paramref name="from"/>, <paramref name="to"/>] that has no value
    /// (see the remarks): <paramref name="from"/> when no growth is stored on or before it,
    /// or the first day after the fetched-through day when <paramref name="to"/> is past it.
    /// Null when every day has one. Both are days a close is read on
    /// (<see cref="CloseDay"/>).
    /// </summary>
    public static DateOnly? MissingDay(BenchmarkInLedger benchmark, DateOnly from, DateOnly to)
    {
        if (benchmark.FirstGrowthDay is not { } first || from < first) return from;
        if (benchmark.FetchedThrough is not { } through) return from;
        if (to > through) return through.AddDays(1) > from ? through.AddDays(1) : from;
        return null;
    }

    /// <summary>
    /// The benchmark's growth from the close of <paramref name="from"/> to the close of
    /// <paramref name="to"/>: the product of the growth of the stored days in (from, to], a
    /// day with no row inside the known range being 1. Null when either day has no value or
    /// lies outside what was loaded.
    /// </summary>
    public double? Between(DateOnly from, DateOnly to)
    {
        if (from > to || _first is not { } first || _through is not { } through) return null;
        if (from < first || to > through || from < _loadedFrom || to > _loadedTo) return null;
        return CumulativeThrough(to) / CumulativeThrough(from);
    }

    // The product of the loaded growth through the day: 1 before the first loaded row.
    private double CumulativeThrough(DateOnly day)
    {
        var i = Array.BinarySearch(_days, day);
        if (i < 0) i = ~i - 1;
        return i < 0 ? 1.0 : _cumulative[i];
    }
}

/// <summary>
/// The benchmark drawn beside one returns series (ADR-0114 D3): its total return chained over
/// the series' invested time, rebased to 100 at the series' <c>CoveredFrom</c>, and converted
/// into the series' currency, unhedged.
/// </summary>
/// <remarks>
/// <para><b>Chaining over invested time.</b> The series' chain
/// (<see cref="ReturnsCalculator.TwrChain"/>) runs on its boundaries, the instants money
/// crossed the scope's edge. Each invested sub-period (<see cref="ReturnsCalculator.TwrChain.InvestedSpans"/>),
/// from instant <c>s</c> to instant <c>e</c>, takes the benchmark's growth over the days in
/// (day(s), day(e)], each instant's UTC day (ADR-0070), which is the day a portfolio value
/// at that instant is priced on (<see cref="BenchmarkGrowthTable"/>). Adjacent sub-periods
/// share a boundary day, so their products telescope and no day is counted twice. A point
/// inside a sub-period (<see cref="ReturnsCalculator.TwrChain.OpenSpanFrom"/>) takes the
/// growth from its start through the point's own day, as the series' index grows by the
/// value at the point. A stretch with no invested base contributes nothing: the benchmark
/// pauses with the portfolio, is null with the index's <c>no_invested_base</c> there, and
/// resumes from the value it held. The first invested sub-period starts at
/// <c>CoveredFrom</c>, so the benchmark is 100 there.</para>
/// <para><b>Conversion.</b> In another currency than the series, each sub-period's growth is
/// multiplied by rate(e) / rate(s), the benchmark's currency into the series', each read at
/// <see cref="RateDay.Of"/> its instant. With no pause the rates telescope to
/// rate_t / rate_CoveredFrom; across a pause the currency's move is skipped as the market's
/// is, so the converted line also resumes from the value it held, unhedged.</para>
/// <para><b>Fail closed.</b> A point whose days [day(CoveredFrom), day(point)] are not all
/// known (<see cref="BenchmarkGrowthTable.MissingDay"/>) is null with
/// <c>benchmark_unavailable</c> and the first missing day. A point that needs a missing rate
/// (any sub-period at or before it) is null with <c>rate_unavailable</c> and every missing
/// pair and day (ADR-0114 D9). Once a sub-period's rate is missing every later point needs
/// it too.</para>
/// </remarks>
internal static class BenchmarkLine
{
    /// <summary>The reasons the series' own index gives when it has no invested time to chain
    /// a benchmark over; the benchmark mirrors them.</summary>
    private static readonly HashSet<string> NoInvestedTime = new(StringComparer.Ordinal)
    {
        ReturnsSeriesReasons.BeforeFirstFunding,
        ReturnsSeriesReasons.NoInvestedBase,
        ReturnsSeriesReasons.RateUnavailable,
    };

    /// <summary>Every point with no benchmark value, for the one <paramref name="reason"/>.</summary>
    public static IReadOnlyList<ReturnsSeriesPoint> Without(IReadOnlyList<ReturnsSeriesPoint> points, string reason) =>
        [.. points.Select(p => p with { Benchmark = null, BenchmarkUnavailableReason = reason })];

    /// <summary>
    /// The series' points, each with the benchmark's value or the reason it has none.
    /// </summary>
    /// <param name="points">The series' points, in period-end order.</param>
    /// <param name="chain">The chain the points were read from.</param>
    /// <param name="serverToday">The rate load's server UTC today
    /// (<see cref="ExchangeRateNeeds.ServerToday"/>).</param>
    public static IReadOnlyList<ReturnsSeriesPoint> Draw(
        IReadOnlyList<ReturnsSeriesPoint> points,
        ReturnsCalculator.TwrChain chain,
        BenchmarkInLedger benchmark,
        BenchmarkGrowthTable growth,
        ExchangeRateTable rates,
        DateOnly serverToday,
        string seriesCurrency)
    {
        var spans = chain.InvestedSpans;
        var converts = !string.Equals(benchmark.CurrencyCode, seriesCurrency, StringComparison.Ordinal);

        // The growth of a sub-period from one instant to a later one, converted; null when a
        // day or a rate it needs is missing, each missing rate added to `missing`.
        DateOnly Close(DateTime instant) =>
            BenchmarkGrowthTable.CloseDay(benchmark, DateOnly.FromDateTime(instant), serverToday);

        double? Step(DateTime from, DateTime to, List<RateUnavailable> missing)
        {
            double? factor = growth.Between(Close(from), Close(to));
            if (!converts) return factor;
            var start = RateDay.Of(from, serverToday).Read(rates, benchmark.CurrencyCode, seriesCurrency);
            var end = RateDay.Of(to, serverToday).Read(rates, benchmark.CurrencyCode, seriesCurrency);
            if (start.Unavailable is { } a && !missing.Contains(a)) missing.Add(a);
            if (end.Unavailable is { } b && !missing.Contains(b)) missing.Add(b);
            if (start.Rate is not { } rs || end.Rate is not { } re) return null;
            return factor * ((double)re / (double)rs);
        }

        // The product over the sub-periods that ended at or before the last point read, and
        // the rates they found missing: a sub-period with a gap leaves it null for good.
        double? completed = 1.0;
        var completedMissing = new List<RateUnavailable>();
        var next = 0;

        var drawn = new List<ReturnsSeriesPoint>(points.Count);
        foreach (var p in points)
        {
            var mark = p.PeriodEnd;
            while (next < spans.Count && spans[next].To <= mark)
            {
                completed *= Step(spans[next].From, spans[next].To, completedMissing);
                next++;
            }

            if ((p.IndexUnavailableReason is { } indexReason && NoInvestedTime.Contains(indexReason))
                || chain.CoveredFrom is not { } coveredFrom)
            {
                drawn.Add(p with
                {
                    Benchmark = null,
                    BenchmarkUnavailableReason = p.IndexUnavailableReason ?? ReturnsSeriesReasons.BeforeFirstFunding,
                });
                continue;
            }

            if (BenchmarkGrowthTable.MissingDay(benchmark, Close(coveredFrom), Close(mark)) is { } missingDay)
            {
                drawn.Add(p with
                {
                    Benchmark = null,
                    BenchmarkUnavailableReason = ReturnsSeriesReasons.BenchmarkUnavailable,
                    BenchmarkUnavailableDate = missingDay,
                });
                continue;
            }

            var missing = new List<RateUnavailable>(completedMissing);
            var factor = completed;
            if (chain.OpenSpanFrom(mark) is { } open) factor *= Step(open, mark, missing);

            if (missing.Count > 0)
            {
                drawn.Add(p with
                {
                    Benchmark = null,
                    BenchmarkUnavailableReason = ReturnsSeriesReasons.RateUnavailable,
                    BenchmarkRateUnavailable = missing,
                });
                continue;
            }

            // Every day the point needs has a value, so a null factor cannot arise here; if it
            // did, the point fails closed rather than drawing a partial chain.
            drawn.Add(factor is { } f
                ? p with { Benchmark = f * 100.0 }
                : p with
                {
                    Benchmark = null,
                    BenchmarkUnavailableReason = ReturnsSeriesReasons.BenchmarkUnavailable,
                    BenchmarkUnavailableDate = DateOnly.FromDateTime(coveredFrom),
                });
        }
        return drawn;
    }
}
