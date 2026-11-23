using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes.Frankfurter;
using Coffer.Api.Quotes.Yahoo;

namespace Coffer.Api.Quotes.MarketSeries;

/// <summary>
/// Fetches and stores a ledger's outside market series (ADR-0114 D6, D11): each
/// benchmark's daily growth from Yahoo, and the exchange rates the ledger needs from
/// Frankfurter. Rides the quote refresh (<see cref="QuoteOrchestrator.RunAllPullsAsync"/>)
/// and the standalone backfill (<see cref="MarketSeriesBackfill"/>); never a feed sync
/// (ADR-0054 D1).
/// </summary>
/// <remarks>
/// <para><b>Consent per destination</b> (ADR-0114 D8). The acting user's <c>quotes</c> preference
/// for the ledger decides each destination on its own: <see cref="YahooFinanceQuoteProvider.Key"/>
/// for benchmarks, <see cref="FrankfurterRatesClient.Key"/> for rates. A run with no user,
/// or a destination the user has not turned on, sends nothing there.</para>
///
/// <para><b>What is asked.</b> Each series keeps the range of days its fetches have ASKED
/// for (<c>benchmark_coverage</c> per symbol, <c>fx_pair_coverage</c> per pair), whatever
/// the source returned, so a span before a currency existed or a fund launched is never
/// asked twice. The day a series is needed from (<see cref="NeededFxPairs"/>: a pair's
/// earliest needed day; a benchmark's, the first day any returns series of the ledger can
/// be invested from, <see cref="InvestmentReportingRepository.FirstInvestedDayAsync"/>, where
/// its line is rebased) is reached back
/// <see cref="PublishedDayReachBack"/> days, because neither feed publishes weekends or
/// holidays: a day converts at the last rate on or before it (ADR-0114 D4), and a
/// benchmark's first day is drawn from the trading day before it. A closure longer than
/// that leaves those first days without a value, which reads as ADR-0114 D9's
/// <c>rate_unavailable</c>, fail-closed. With no needed day at all (nothing invested, no history)
/// the start is that many days before today. A run then asks:</para>
/// <list type="bullet">
/// <item><b>A first fetch</b>, with no range recorded: from the start to today.</item>
/// <item><b>Forward</b>: a pair from its through day, inclusive, so a rate published for
/// that day after the last fetch is read; a benchmark only once a day past its through
/// day has closed, from its last stored growth day when that is within the reach-back of
/// the through day (its close is the first new day's base, ADR-0114 D2), else from the
/// through day.</item>
/// <item><b>Back</b>, when the start has moved before the recorded from day (an older
/// transaction in a covered currency, an older first investment, ADR-0114 D11): a pair over
/// [start, from day); a benchmark over [start, its first stored growth day], or the from
/// day when it has none, so the day that was the first fetch's base gets its growth.</item>
/// </list>
/// <para>A range widens only by a span the fetch read in full and that adjoins it: a
/// forward fetch to where it read through, a back fetch only when every year of it
/// answered. A back fetch that stopped part-way records nothing and is asked again.</para>
///
/// <para><b>First stored wins</b> (ADR-0114 D6, D10). <c>benchmark_growth</c> and <c>fx_rates</c>
/// are INSERT-only for the app: a day already stored, whatever its source (a hand-entered
/// rate included), is skipped and never updated. No day after today (UTC) is stored: both
/// clients ask for none and drop any a response carries.</para>
///
/// <para><b>Concurrent runs.</b> The HTTP fetches run outside any transaction; the writes
/// then run in one transaction under the ledger write lock (ADR-0112), which reads the days
/// already stored and inserts only the rest, and merges each recorded range with the stored
/// one (the earlier from day, the later through day). Two runs over one ledger (a manual
/// Refresh beside a backfill, or the scheduled refresh) each fetch, but their writes take
/// turns, and the second finds the first's days stored and skips them, so no write meets
/// the unique key (23505). A hand-entered rate takes the same lock. The lock is not held
/// across egress, so a slow feed never makes a ledger's other writes wait. A write refused
/// because another held the lock past its bound stores nothing and is reported as
/// <see cref="LedgerWriteBusyCode"/>; the next run asks the same days again.</para>
///
/// <para><b>Failures</b> come back as <see cref="QuoteError"/>s, one per failing symbol or
/// pair, with the client's own code; the caller writes each as a
/// <c>ledger_operation_errors</c> row. What a failing fetch read before it stopped is
/// kept.</para>
///
/// <para>One <see cref="YahooBenchmarkClient"/> per fetcher, so every benchmark request of
/// a run shares its throttle.</para>
/// </remarks>
public sealed class MarketSeriesFetcher
{
    /// <summary>How many days before the first day a figure needs a fetch starts: a week,
    /// so the last published day before it is in the response across a weekend and the
    /// holidays either feed skips.</summary>
    public const int PublishedDayReachBack = 7;

    /// <summary>The run's market-series write was refused because another write to the
    /// ledger held its write lock past the wait. Nothing of this run's series was stored.</summary>
    public const string LedgerWriteBusyCode = "ledger-write-busy";

    /// <summary>Yahoo named a different currency for a benchmark than the one stored when
    /// it was added. Its growth is not stored: it would convert at the wrong rate.</summary>
    public const string BenchmarkCurrencyChangedCode = "benchmark-currency-changed";

    private readonly AppDbContext _db;
    private readonly YahooBenchmarkClient _yahoo;
    private readonly FrankfurterRatesClient _rates;
    private readonly UserPreferencesRepository _prefs;
    private readonly ILogger<MarketSeriesFetcher> _logger;
    private readonly TimeProvider _clock;

    public MarketSeriesFetcher(
        AppDbContext db,
        YahooBenchmarkClient yahoo,
        FrankfurterRatesClient rates,
        UserPreferencesRepository prefs,
        ILogger<MarketSeriesFetcher> logger,
        TimeProvider clock)
    {
        _db = db;
        _yahoo = yahoo;
        _rates = rates;
        _prefs = prefs;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Which destinations <paramref name="userId"/> has turned on for the ledger.</summary>
    public async Task<MarketSeriesConsent> ConsentAsync(
        Guid ledgerId, Guid? userId, CancellationToken cancellationToken = default)
    {
        if (userId is not { } uid) return new MarketSeriesConsent(false, false);
        var enabled = (await _prefs.GetQuotesAsync(uid, ledgerId, cancellationToken).ConfigureAwait(false))
            .EnabledProviders;
        return new MarketSeriesConsent(
            Benchmarks: enabled.Contains(YahooFinanceQuoteProvider.Key, StringComparer.Ordinal),
            Rates: enabled.Contains(FrankfurterRatesClient.Key, StringComparer.Ordinal));
    }

    /// <summary>
    /// Fetch and store what <paramref name="ledgerId"/>'s series lack, under
    /// <paramref name="userId"/>'s consent. Writes no audit row: the caller's run owns it.
    /// </summary>
    public async Task<MarketSeriesOutcome> FetchAsync(
        Guid ledgerId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var consent = await ConsentAsync(ledgerId, userId, cancellationToken).ConfigureAwait(false);
        if (!consent.Any) return MarketSeriesOutcome.Nothing;

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var errors = new List<QuoteError>();
        var fetched = new Fetched();
        if (consent.Benchmarks)
            await FetchGrowthAsync(ledgerId, today, fetched, errors, cancellationToken).ConfigureAwait(false);
        if (consent.Rates)
            await FetchRatesAsync(ledgerId, today, fetched, errors, cancellationToken).ConfigureAwait(false);

        if (fetched.IsEmpty) return new MarketSeriesOutcome(0, 0, errors);

        try
        {
            var (ratesInserted, daysInserted) = await StoreAsync(ledgerId, fetched, cancellationToken)
                .ConfigureAwait(false);
            return new MarketSeriesOutcome(ratesInserted, daysInserted, errors);
        }
        catch (LedgerWriteBusyException ex)
        {
            _logger.LogWarning(ex, "Market-series write for ledger {LedgerId} was refused as busy.", ledgerId);
            errors.Add(new QuoteError(null, string.Empty, LedgerWriteBusyCode,
                "Another change to this ledger was being saved, so no benchmark or exchange-rate day "
                + "was stored this time. The next refresh fetches them again."));
            return new MarketSeriesOutcome(0, 0, errors);
        }
    }

    /// <summary>What a run read, and the ranges it asked in full, for the write.</summary>
    private sealed class Fetched
    {
        public List<BenchmarkGrowthRow> Growth { get; } = [];
        public List<FxRateRow> Rates { get; } = [];
        public List<(string Symbol, DateOnly From, DateOnly Through)> BenchmarkAsked { get; } = [];
        public List<(string From, string To, DateOnly AskedFrom, DateOnly Through)> PairAsked { get; } = [];
        public bool IsEmpty => Growth.Count == 0 && Rates.Count == 0 && BenchmarkAsked.Count == 0 && PairAsked.Count == 0;
    }

    // ----- benchmarks -----

    private async Task FetchGrowthAsync(
        Guid ledgerId, DateOnly today, Fetched fetched, List<QuoteError> errors, CancellationToken ct)
    {
        var benchmarks = await _db.LedgerBenchmarks.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .OrderBy(b => b.Position)
            .Select(b => new { b.Symbol, b.CurrencyCode })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (benchmarks.Count == 0) return;

        var symbols = benchmarks.Select(b => b.Symbol).ToList();
        var coverage = await _db.BenchmarkCoverage.AsNoTracking()
            .Where(c => c.LedgerId == ledgerId && symbols.Contains(c.Symbol))
            .ToDictionaryAsync(c => c.Symbol, c => (c.FetchedFromDate, c.FetchedThroughDate), ct)
            .ConfigureAwait(false);
        var storedSpan = await _db.BenchmarkGrowth.AsNoTracking()
            .Where(g => g.LedgerId == ledgerId && symbols.Contains(g.Symbol))
            .GroupBy(g => g.Symbol)
            .Select(g => new { Symbol = g.Key, First = g.Min(x => x.GrowthDate), Last = g.Max(x => x.GrowthDate) })
            .ToDictionaryAsync(x => x.Symbol, x => (x.First, x.Last), ct)
            .ConfigureAwait(false);
        // From the first day any returns series can be CoveredFrom, where a benchmark line is
        // rebased, not the first trade: a brokerage funded with cash before it buys anything
        // is invested from the funding.
        var firstInvested = await InvestmentReportingRepository.FirstInvestedDayAsync(_db, ledgerId, ct)
            .ConfigureAwait(false);
        var start = (firstInvested ?? today).AddDays(-PublishedDayReachBack);
        var yesterday = today.AddDays(-1);

        foreach (var benchmark in benchmarks)
        {
            var asks = new List<(DateOnly From, DateOnly To)>();
            if (!coverage.TryGetValue(benchmark.Symbol, out var covered))
            {
                asks.Add((start, today));
            }
            else
            {
                if (covered.FetchedThroughDate < yesterday)
                {
                    var forward = storedSpan.TryGetValue(benchmark.Symbol, out var span)
                                  && span.Last >= covered.FetchedThroughDate.AddDays(-PublishedDayReachBack)
                        ? span.Last
                        : covered.FetchedThroughDate;
                    asks.Add((forward, today));
                }
                if (start < covered.FetchedFromDate)
                {
                    var end = storedSpan.TryGetValue(benchmark.Symbol, out var span) ? span.First : covered.FetchedFromDate;
                    asks.Add((start, end));
                }
            }

            foreach (var (from, to) in asks)
            {
                // The client asks [from, min(to, yesterday)] and nothing when that is empty.
                var lastAsked = to < yesterday ? to : yesterday;
                if (from >= lastAsked) continue;
                var growth = await _yahoo.FetchGrowthAsync(benchmark.Symbol, from, to, ct).ConfigureAwait(false);
                if (growth.ErrorCode is { } code)
                {
                    errors.Add(new QuoteError(null, benchmark.Symbol, code, growth.Message ?? benchmark.Symbol));
                    continue;
                }
                if (growth.Days.Count > 0
                    && !string.Equals(growth.CurrencyCode, benchmark.CurrencyCode, StringComparison.Ordinal))
                {
                    errors.Add(new QuoteError(null, benchmark.Symbol, BenchmarkCurrencyChangedCode,
                        $"Yahoo now quotes {benchmark.Symbol} in {growth.CurrencyCode}, not the "
                        + $"{benchmark.CurrencyCode} it was added in, so its growth was not stored. "
                        + "Remove it and add it again to take the new currency."));
                    continue;
                }
                fetched.Growth.AddRange(growth.Days.Select(d => new BenchmarkGrowthRow
                {
                    LedgerId = ledgerId,
                    Symbol = benchmark.Symbol,
                    GrowthDate = d.Day,
                    Growth = d.Growth,
                    Source = BenchmarkGrowthSource.Fetch,
                }));
                fetched.BenchmarkAsked.Add((benchmark.Symbol, from, lastAsked));
            }
        }
    }

    // ----- exchange rates -----

    private async Task FetchRatesAsync(
        Guid ledgerId, DateOnly today, Fetched fetched, List<QuoteError> errors, CancellationToken ct)
    {
        var needed = await NeededFxPairs.ForLedgerAsync(_db, ledgerId, ct).ConfigureAwait(false);
        if (needed.Count == 0) return;

        var coverage = await _db.FxPairCoverage.AsNoTracking()
            .Where(c => c.LedgerId == ledgerId)
            .ToDictionaryAsync(c => (c.FromCurrency, c.ToCurrency), c => (c.FetchedFromDate, c.FetchedThroughDate), ct)
            .ConfigureAwait(false);

        // Every range each pair needs asked, then one request per base currency and range:
        // pairs that share both share a fetch. A back range must be read in full to count.
        var asks = new List<(string From, string To, DateOnly Start, DateOnly End, bool Back)>();
        foreach (var pair in needed)
        {
            var start = (pair.NeededFrom ?? today).AddDays(-PublishedDayReachBack);
            if (!coverage.TryGetValue((pair.FromCurrency, pair.ToCurrency), out var covered))
            {
                asks.Add((pair.FromCurrency, pair.ToCurrency, start, today, false));
                continue;
            }
            asks.Add((pair.FromCurrency, pair.ToCurrency, covered.FetchedThroughDate, today, false));
            if (start < covered.FetchedFromDate)
                asks.Add((pair.FromCurrency, pair.ToCurrency, start, covered.FetchedFromDate.AddDays(-1), true));
        }

        foreach (var group in asks
                     .GroupBy(a => (a.From, a.Start, a.End, a.Back))
                     .OrderBy(g => g.Key.From, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Start))
        {
            var (from, start, end, back) = group.Key;
            var tos = group.Select(a => a.To).ToList();
            var result = await _rates.FetchRatesAsync(from, tos, start, end, ct).ConfigureAwait(false);

            fetched.Rates.AddRange(result.Rates.Select(r => new FxRateRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledgerId,
                FromCurrency = r.FromCurrency,
                ToCurrency = r.ToCurrency,
                RateDate = r.Day,
                Rate = r.Rate,
                Source = FxRateSource.Frankfurter,
                Upstream = r.UpstreamSource,
            }));
            // FetchedThrough is the last day read in full, never after today. A forward or
            // first fetch records [start, it]; a back fetch only when it read to its end, so
            // the recorded range never claims a span it did not read.
            var lastDay = end < today ? end : today;
            if (result.FetchedThrough is { } through && (!back || (result.ErrorCode is null && through == lastDay)))
            {
                foreach (var to in tos) fetched.PairAsked.Add((from, to, start, through));
            }
            if (result.ErrorCode is { } code)
            {
                foreach (var to in tos)
                    errors.Add(new QuoteError(null, $"{from}/{to}", code, $"{from}/{to}: {result.Message}"));
            }
        }
    }

    // ----- the write -----

    private async Task<(int Rates, int Days)> StoreAsync(Guid ledgerId, Fetched fetched, CancellationToken ct)
    {
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, ct).ConfigureAwait(false);
        try
        {
            var stored = await StageAsync(ledgerId, fetched, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await write.CommitAsync(ct).ConfigureAwait(false);
            return stored;
        }
        catch
        {
            // The context may be the caller's (a refresh writes its audit row through it
            // next, and the scheduler keeps its job row on it), so only what this write
            // staged is let go: a rolled-back row must not ride the caller's next save.
            foreach (var entry in _db.ChangeTracker.Entries()
                         .Where(e => e.Entity is BenchmarkGrowthRow or FxRateRow or FxPairCoverageRow or BenchmarkCoverageRow)
                         .ToList())
                entry.State = EntityState.Detached;
            throw;
        }
    }

    private async Task<(int Rates, int Days)> StageAsync(Guid ledgerId, Fetched fetched, CancellationToken ct)
    {
        var daysInserted = 0;
        foreach (var symbol in fetched.Growth.GroupBy(g => g.Symbol))
        {
            var first = symbol.Min(g => g.GrowthDate);
            var last = symbol.Max(g => g.GrowthDate);
            var stored = (await _db.BenchmarkGrowth.AsNoTracking()
                    .Where(g => g.LedgerId == ledgerId && g.Symbol == symbol.Key
                                && g.GrowthDate >= first && g.GrowthDate <= last)
                    .Select(g => g.GrowthDate)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToHashSet();
            foreach (var row in symbol)
            {
                if (!stored.Add(row.GrowthDate)) continue;
                _db.BenchmarkGrowth.Add(row);
                daysInserted++;
            }
        }

        var ratesInserted = 0;
        foreach (var pair in fetched.Rates.GroupBy(r => (r.FromCurrency, r.ToCurrency)))
        {
            var first = pair.Min(r => r.RateDate);
            var last = pair.Max(r => r.RateDate);
            var stored = (await _db.FxRates.AsNoTracking()
                    .Where(r => r.LedgerId == ledgerId
                                && r.FromCurrency == pair.Key.FromCurrency && r.ToCurrency == pair.Key.ToCurrency
                                && r.RateDate >= first && r.RateDate <= last)
                    .Select(r => r.RateDate)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToHashSet();
            foreach (var row in pair)
            {
                if (!stored.Add(row.RateDate)) continue;
                _db.FxRates.Add(row);
                ratesInserted++;
            }
        }

        // Each recorded range merges with the stored one: the earlier from day, the later
        // through day. Every range a run records adjoins the one it read, so the merge
        // stays contiguous.
        foreach (var asked in fetched.BenchmarkAsked.GroupBy(a => a.Symbol))
        {
            var from = asked.Min(a => a.From);
            var through = asked.Max(a => a.Through);
            var existing = await _db.BenchmarkCoverage
                .FirstOrDefaultAsync(c => c.LedgerId == ledgerId && c.Symbol == asked.Key, ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.BenchmarkCoverage.Add(new BenchmarkCoverageRow
                {
                    LedgerId = ledgerId, Symbol = asked.Key, FetchedFromDate = from, FetchedThroughDate = through,
                });
                continue;
            }
            if (from < existing.FetchedFromDate) existing.FetchedFromDate = from;
            if (through > existing.FetchedThroughDate) existing.FetchedThroughDate = through;
        }

        foreach (var asked in fetched.PairAsked.GroupBy(a => (a.From, a.To)))
        {
            var from = asked.Min(a => a.AskedFrom);
            var through = asked.Max(a => a.Through);
            var existing = await _db.FxPairCoverage
                .FirstOrDefaultAsync(c => c.LedgerId == ledgerId
                                          && c.FromCurrency == asked.Key.From && c.ToCurrency == asked.Key.To, ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.FxPairCoverage.Add(new FxPairCoverageRow
                {
                    LedgerId = ledgerId, FromCurrency = asked.Key.From, ToCurrency = asked.Key.To,
                    FetchedFromDate = from, FetchedThroughDate = through,
                });
                continue;
            }
            if (from < existing.FetchedFromDate) existing.FetchedFromDate = from;
            if (through > existing.FetchedThroughDate) existing.FetchedThroughDate = through;
        }

        return (ratesInserted, daysInserted);
    }
}

/// <summary>Which destinations a user has turned on for a ledger (ADR-0114 D8).</summary>
public sealed record MarketSeriesConsent(bool Benchmarks, bool Rates)
{
    public bool Any => Benchmarks || Rates;
}

/// <summary>What a market-series fetch stored, and each symbol or pair that failed.</summary>
public sealed record MarketSeriesOutcome(int RatesInserted, int BenchmarkDaysInserted, IReadOnlyList<QuoteError> Errors)
{
    public static MarketSeriesOutcome Nothing { get; } = new(0, 0, []);
}
