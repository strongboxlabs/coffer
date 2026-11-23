using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The managed half of the outside market series (ADR-0114): a ledger's benchmark list
/// (ADR-0114 D1) and its hand-entered exchange rates and rates view (ADR-0114 D10). Request-scoped, as the
/// requesting user: the role-aware RLS pair of migration 280 is the backstop beneath the
/// endpoints' role gate. Fetched series are written by <c>MarketSeriesFetcher</c>; reads of
/// converted figures are not here.
/// </summary>
/// <remarks>
/// Every write takes the ledger write lock (migration 275) first. The benchmark cap and the
/// symbol and position keys are then checked against what the last writer committed, so
/// two adds at once take turns and the second is refused with a business code rather than
/// meeting the unique key (23505); a hand-entered rate takes turns the same way with the
/// market-series fetch, which writes under the same lock.
/// </remarks>
public sealed class MarketSeriesRepository
{
    /// <summary>The most benchmarks a ledger keeps (ADR-0114 D1). Checked here; the
    /// position CHECK (1..3) and its unique key are the database's backstop.</summary>
    public const int MaxBenchmarks = 3;

    /// <summary>The most rates one page of a pair returns.</summary>
    public const int MaxRatePage = 500;

    /// <summary>Decimal places a stored rate has (<c>fx_rates.rate NUMERIC(28,12)</c>).</summary>
    public const int RateScale = 12;

    /// <summary>The largest rate the column holds: 16 digits before the point.</summary>
    private const decimal MaxRate = 9_999_999_999_999_999.999999999999m;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public MarketSeriesRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ----- benchmarks -----

    /// <summary>The availability reason for a benchmark with no stored history whose
    /// requesting user has Yahoo off (<see cref="BenchmarkAvailabilityDto"/>).</summary>
    public const string BenchmarkYahooOff = "yahoo-off";

    /// <summary>The availability reason for a benchmark with no stored history yet.</summary>
    public const string BenchmarkNoHistory = "no-history";

    /// <summary>
    /// The ledger's benchmarks in order, each with whether the Returns report can draw it
    /// (<see cref="BenchmarkAvailabilityDto"/>, ADR-0114 D1) for a user whose Yahoo opt-in
    /// on the ledger is <paramref name="yahooOn"/>.
    /// </summary>
    /// <remarks>Past the list, at most three statements, none when the list is empty: which
    /// symbols have stored growth (one grouped pass of <c>benchmark_growth</c>), the
    /// currencies a series can be drawn in, and which pairs from the benchmarks'
    /// currencies into those hold a stored rate (one grouped pass of
    /// <c>fx_rates</c>).</remarks>
    public async Task<IReadOnlyList<BenchmarkDto>> ListBenchmarksAsync(
        Guid ledgerId, bool yahooOn, CancellationToken cancellationToken = default)
    {
        var list = await _db.LedgerBenchmarks.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .OrderBy(b => b.Position)
            .Select(b => new BenchmarkDto(b.Id, b.Symbol, b.Name, b.CurrencyCode, b.Position, null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (list.Count == 0) return list;

        var symbols = list.Select(b => b.Symbol).ToList();
        var withHistory = (await _db.BenchmarkGrowth.AsNoTracking()
                .Where(g => g.LedgerId == ledgerId && symbols.Contains(g.Symbol))
                .GroupBy(g => g.Symbol)
                .Select(g => g.Key)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        // A series is drawn in the reporting currency or a brokerage's (NeededFxPairs'
        // benchmark source); a Holdings sibling shares its brokerage's.
        var seriesCurrencies = await _db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.CurrencyCode)
            .Union(_db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == ledgerId && a.AccountType == "investment")
                .Select(a => a.CurrencyCode))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var froms = list.Select(b => b.CurrencyCode).Distinct(StringComparer.Ordinal).ToList();
        var rated = (await _db.FxRates.AsNoTracking()
                .Where(r => r.LedgerId == ledgerId && froms.Contains(r.FromCurrency)
                            && seriesCurrencies.Contains(r.ToCurrency))
                .GroupBy(r => new { r.FromCurrency, r.ToCurrency })
                .Select(g => g.Key)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(k => (k.FromCurrency, k.ToCurrency))
            .ToHashSet();

        return [.. list.Select(b => b with
        {
            Availability = new BenchmarkAvailabilityDto(
                withHistory.Contains(b.Symbol) ? null : yahooOn ? BenchmarkNoHistory : BenchmarkYahooOff,
                [.. seriesCurrencies
                    .Where(c => !string.Equals(c, b.CurrencyCode, StringComparison.Ordinal)
                                && !rated.Contains((b.CurrencyCode, c)))
                    .Order(StringComparer.Ordinal)]),
        })];
    }

    public enum AddBenchmarkResult { Ok, AlreadyAdded, LimitReached }

    /// <summary>Whether a benchmark can be added now, without writing: the cheap checks
    /// asked before the add-time check spends a request on Yahoo.</summary>
    public async Task<AddBenchmarkResult> CanAddBenchmarkAsync(
        Guid ledgerId, string symbol, CancellationToken cancellationToken = default)
    {
        var symbols = await _db.LedgerBenchmarks.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .Select(b => b.Symbol)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Decide(symbols, symbol);
    }

    /// <summary>
    /// Add a benchmark at the end of the list, under the ledger write lock: refused when
    /// the symbol is already in the list or the list is full, as the last writer left it.
    /// </summary>
    public async Task<(AddBenchmarkResult Result, BenchmarkDto? Benchmark)> AddBenchmarkAsync(
        Guid ledgerId, string symbol, string name, string currencyCode,
        CancellationToken cancellationToken = default)
    {
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var existing = await _db.LedgerBenchmarks.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .Select(b => new { b.Symbol, b.Position })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var decided = Decide(existing.Select(b => b.Symbol).ToList(), symbol);
        if (decided != AddBenchmarkResult.Ok) return (decided, null);

        var row = new LedgerBenchmarkRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Symbol = symbol,
            Name = name,
            CurrencyCode = currencyCode,
            Position = existing.Count == 0 ? 1 : existing.Max(b => b.Position) + 1,
        };
        _db.LedgerBenchmarks.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (AddBenchmarkResult.Ok, new BenchmarkDto(row.Id, row.Symbol, row.Name, row.CurrencyCode, row.Position));
    }

    private static AddBenchmarkResult Decide(IReadOnlyCollection<string> symbols, string symbol) =>
        symbols.Contains(symbol, StringComparer.Ordinal) ? AddBenchmarkResult.AlreadyAdded
        : symbols.Count >= MaxBenchmarks ? AddBenchmarkResult.LimitReached
        : AddBenchmarkResult.Ok;

    /// <summary>
    /// Remove a benchmark and close the gap it leaves, so positions stay 1..n. The symbol's
    /// stored growth is kept (ADR-0114 D1): adding it back needs no fresh fetch. False when
    /// no such benchmark is in the ledger.
    /// </summary>
    /// <remarks>The position key is deferred (migration 280), so the renumbering can pass
    /// through a moment where two rows share a position inside the transaction.</remarks>
    public async Task<bool> RemoveBenchmarkAsync(
        Guid ledgerId, Guid benchmarkId, CancellationToken cancellationToken = default)
    {
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var rows = await _db.LedgerBenchmarks
            .Where(b => b.LedgerId == ledgerId)
            .OrderBy(b => b.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var target = rows.FirstOrDefault(b => b.Id == benchmarkId);
        if (target is null) return false;

        _db.LedgerBenchmarks.Remove(target);
        var position = 1;
        foreach (var row in rows.Where(b => b.Id != benchmarkId))
            row.Position = position++;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ----- exchange rates -----

    /// <summary>The rates view (ADR-0114 D10): every pair the ledger needs, what needs it,
    /// its coverage (ADR-0114 D9), how many days are stored and from when, and the days a
    /// conversion of it reads no rate for (<see cref="FxPairMissingDays"/>).</summary>
    /// <remarks>Past the derivation of the needed pairs, two statements: the coverage rows,
    /// and one grouped pass over the ledger's stored rates for each pair's count, first day
    /// and latest hand-entered day.</remarks>
    public async Task<FxRatesViewDto> RatesViewAsync(Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var reporting = await _db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.CurrencyCode)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var needed = await NeededFxPairs.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);

        var coverage = await _db.FxPairCoverage.AsNoTracking()
            .Where(c => c.LedgerId == ledgerId)
            .ToDictionaryAsync(c => (c.FromCurrency, c.ToCurrency), c => (c.FetchedFromDate, c.FetchedThroughDate), cancellationToken)
            .ConfigureAwait(false);
        var stored = await _db.FxRates.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId)
            .GroupBy(r => new { r.FromCurrency, r.ToCurrency })
            .Select(g => new
            {
                g.Key.FromCurrency,
                g.Key.ToCurrency,
                Count = g.Count(),
                First = g.Min(r => r.RateDate),
                LatestManual = g.Where(r => r.Source == FxRateSource.Manual).Max(r => (DateOnly?)r.RateDate),
            })
            .ToDictionaryAsync(x => (x.FromCurrency, x.ToCurrency), cancellationToken)
            .ConfigureAwait(false);

        var pairs = needed.Select(p =>
        {
            var hasRange = coverage.TryGetValue((p.FromCurrency, p.ToCurrency), out var range);
            DateOnly? fetchedFrom = hasRange ? range.FetchedFromDate : null;
            DateOnly? fetched = hasRange ? range.FetchedThroughDate : null;
            var s = stored.GetValueOrDefault((p.FromCurrency, p.ToCurrency));
            var manual = s?.LatestManual;
            DateOnly? covered = fetched is null ? manual : manual is null ? fetched : (manual > fetched ? manual : fetched);
            DateOnly? first = s?.First;
            return new FxPairDto(
                p.FromCurrency, p.ToCurrency, ReasonNames(p.Reasons), p.NeededFrom,
                fetchedFrom, fetched, manual, covered, s?.Count ?? 0,
                first,
                [.. FxPairMissingDays.Of(p.NeededFrom, first, covered, today)
                    .Select(m => new FxMissingSpanDto(m.From, m.Through))],
                [.. p.Sources.Select(n => new FxPairSourceDto(
                    ReasonNames(n.Reason).Single(), n.Id, n.Name, n.Symbol, n.AccountId, n.AccountName))]);
        }).ToList();
        return new FxRatesViewDto(reporting, pairs);
    }

    /// <summary>One page of a pair's stored rates, newest first, strictly before
    /// <paramref name="before"/> when given.</summary>
    public async Task<FxRatePageDto> RatesPageAsync(
        Guid ledgerId, string from, string to, DateOnly? before, int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, MaxRatePage);
        var rows = await _db.FxRates.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.FromCurrency == from && r.ToCurrency == to
                        && (before == null || r.RateDate < before))
            .OrderByDescending(r => r.RateDate)
            .Take(take + 1)
            .Select(r => new FxRateDto(r.RateDate, r.Rate, r.Source, r.Upstream, r.EnteredByUserId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new FxRatePageDto(from, to, rows, more ? rows[^1].Date : null);
    }

    public enum AddRateResult { Ok, PairNotNeeded, RateInvalid, DateInFuture, DayTaken }

    /// <summary>
    /// Store a hand-entered rate (ADR-0114 D10): source <c>manual</c>, naming who typed it,
    /// for a needed pair and a day with no stored rate. Rounded once to the column's scale.
    /// </summary>
    /// <param name="from">Already in stored currency form, as is <paramref name="to"/>.</param>
    public async Task<(AddRateResult Result, FxRateDto? Rate)> AddManualRateAsync(
        Guid ledgerId, Guid userId, string from, string to, DateOnly day, decimal rate,
        CancellationToken cancellationToken = default)
    {
        var rounded = decimal.Round(rate, RateScale);
        if (rounded <= 0m || rounded > MaxRate) return (AddRateResult.RateInvalid, null);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (day > today) return (AddRateResult.DateInFuture, null);

        var needed = await NeededFxPairs.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        if (!needed.Any(p => p.FromCurrency == from && p.ToCurrency == to))
            return (AddRateResult.PairNotNeeded, null);

        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var taken = await _db.FxRates.AsNoTracking()
            .AnyAsync(r => r.LedgerId == ledgerId && r.FromCurrency == from && r.ToCurrency == to
                           && r.RateDate == day, cancellationToken)
            .ConfigureAwait(false);
        if (taken) return (AddRateResult.DayTaken, null);

        var row = new FxRateRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            FromCurrency = from,
            ToCurrency = to,
            RateDate = day,
            Rate = rounded,
            Source = FxRateSource.Manual,
            EnteredByUserId = userId,
        };
        _db.FxRates.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (AddRateResult.Ok, new FxRateDto(day, rounded, FxRateSource.Manual, null, userId));
    }

    private static List<string> ReasonNames(NeededFxPairReasons reasons)
    {
        var names = new List<string>(3);
        if (reasons.HasFlag(NeededFxPairReasons.Position)) names.Add("position");
        if (reasons.HasFlag(NeededFxPairReasons.NetWorth)) names.Add("net-worth");
        if (reasons.HasFlag(NeededFxPairReasons.Benchmark)) names.Add("benchmark");
        return names;
    }
}
