using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The one place conversion reads exchange rates (ADR-0114 D4, D7, D9). A reader declares
/// every (pair, day) it will convert at in an <see cref="ExchangeRateNeeds"/>, loads them in
/// one call, and then reads each rate from the returned <see cref="ExchangeRateTable"/>
/// without another query.
/// </summary>
/// <remarks>
/// <para><b>The rule.</b> The rate from one currency into another on a day is the pair's
/// last stored rate on or before that day (ADR-0114 D4: the feed skips weekends and
/// holidays). It is missing when the pair has no rate on or before the day, or when the
/// day is after the pair's coverage, which runs to the later of
/// <c>fx_pair_coverage.fetched_through_date</c> and the pair's latest hand-entered
/// <c>rate_date</c> (ADR-0114 D9). A pair with neither has no coverage, so every day is
/// missing. A currency converts into itself at exactly one, and needs no row.</para>
/// <para><b>Direction.</b> Only the stored direction is read: a rate is fetched with the
/// account's currency as the base (ADR-0114 D4), so no stored rate is another's inverse.
/// A pair the fetcher was never asked for is missing, never the inverse of the pair that
/// was. The one place a rate is inverted or crossed is
/// <see cref="ExchangeRateTable.Derived"/>, the rate between two account currencies
/// (ADR-0114 D13), and it does so only through the reporting currency, from the two
/// stored account-currency legs into it.</para>
/// <para><b>Cost.</b> At most three statements per load, however many pairs, days and
/// accounts it covers: the ledger's coverage rows; every pair's rates from its last rate on
/// or before the earliest day asked for through the latest day asked for, in one
/// <c>UNION ALL</c>; and, only for a pair whose fetched coverage stops before the latest
/// day asked for, its hand-entered days after that coverage. Each rate range is a scan of
/// <c>uq_fx_rates_pair_day</c> (migration 280's hot read), and the floor of each range is
/// one backward probe of the same index. A net-worth history over six hundred period
/// ends costs what one period end costs, plus the rows between them.</para>
/// <para>Reads run on the request's own context, so row-level security scopes them to the
/// caller's ledger, and inside a reader's snapshot transaction they see the same state as
/// the figures they convert.</para>
/// </remarks>
public sealed class ExchangeRateLookup
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ExchangeRateLookup(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>A new set of needs for one read, holding the server's UTC today once, so
    /// every latest-rate conversion of the read uses the same day
    /// (<see cref="ExchangeRateTable.LatestDay"/>).</summary>
    public ExchangeRateNeeds NewNeeds() => new(DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));

    /// <summary>
    /// Loads every rate <paramref name="needs"/> declares, for <paramref name="ledgerId"/>.
    /// An empty set of needs (every conversion an identity) sends nothing.
    /// </summary>
    public async Task<ExchangeRateTable> LoadAsync(
        Guid ledgerId, ExchangeRateNeeds needs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(needs);
        var ranges = needs.Ranges;
        if (ranges.Count == 0)
            return new ExchangeRateTable(ranges, new Dictionary<FxPair, PairRates>(), needs.ServerToday);

        // Coverage is a row per pair the ledger has ever fetched: a handful, read whole.
        var fetchedThrough = await _db.FxPairCoverage.AsNoTracking()
            .Where(c => c.LedgerId == ledgerId)
            .Select(c => new { c.FromCurrency, c.ToCurrency, c.FetchedThroughDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var fetchedByPair = fetchedThrough.ToDictionary(
            c => new FxPair(c.FromCurrency, c.ToCurrency), c => c.FetchedThroughDate);

        // Every pair's rates in one statement, two branches a pair: its days from the
        // earliest asked for through the latest, and the one rate before the earliest,
        // which the earliest day falls back to when it has none of its own. Both branches
        // bound the day by a parameter, so both are index conditions on the pair-day key: a
        // bound computed inside the statement (a subquery for the floor) is not leakproof,
        // so under row-level security it is applied as a filter after the policy, over the
        // pair's whole history, instead of bounding the scan.
        IQueryable<FxRateRow>? rates = null;
        foreach (var (pair, range) in ranges)
        {
            var (from, to, min, max) = (pair.From, pair.To, range.Min, range.Max);
            var inRange = _db.FxRates.AsNoTracking()
                .Where(r => r.LedgerId == ledgerId
                            && r.FromCurrency == from
                            && r.ToCurrency == to
                            && r.RateDate >= min
                            && r.RateDate <= max);
            var before = _db.FxRates.AsNoTracking()
                .Where(r => r.LedgerId == ledgerId
                            && r.FromCurrency == from
                            && r.ToCurrency == to
                            && r.RateDate < min)
                .OrderByDescending(r => r.RateDate)
                .Take(1);
            var both = inRange.Concat(before);
            rates = rates is null ? both : rates.Concat(both);
        }
        var rateRows = (await rates!
                .Select(r => new { r.FromCurrency, r.ToCurrency, r.RateDate, r.Rate, r.Source })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(r => new RateRow(r.FromCurrency, r.ToCurrency, r.RateDate, r.Rate, r.Source))
            .ToList();

        // A hand-entered day extends coverage past the fetched one (ADR-0114 D9). It matters
        // only for a pair whose fetched coverage stops before the last day asked for; one
        // inside the loaded range is already among the rows above, and one after the range
        // is read here, so these are only the days after the fetched coverage.
        IQueryable<FxRateRow>? manual = null;
        foreach (var (pair, range) in ranges)
        {
            var hasFetched = fetchedByPair.TryGetValue(pair, out var through);
            if (hasFetched && through >= range.Max) continue;
            var (from, to) = (pair.From, pair.To);
            var after = hasFetched ? through : DateOnly.MinValue;
            var one = _db.FxRates.AsNoTracking()
                .Where(r => r.LedgerId == ledgerId
                            && r.FromCurrency == from
                            && r.ToCurrency == to
                            && r.Source == FxRateSource.Manual
                            && r.RateDate > after);
            manual = manual is null ? one : manual.Concat(one);
        }
        var manualDays = manual is null
            ? []
            : (await manual
                    .Select(r => new { r.FromCurrency, r.ToCurrency, r.RateDate })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(r => new ManualDay(r.FromCurrency, r.ToCurrency, r.RateDate))
                .ToList();

        var latestManual = manualDays
            .Concat(rateRows.Where(r => r.Source == FxRateSource.Manual)
                .Select(r => new ManualDay(r.From, r.To, r.Day)))
            .GroupBy(m => new FxPair(m.From, m.To))
            .ToDictionary(g => g.Key, g => g.Max(m => m.Day));

        var byPair = new Dictionary<FxPair, PairRates>();
        var rowsByPair = rateRows.ToLookup(r => new FxPair(r.From, r.To));
        foreach (var pair in ranges.Keys)
        {
            DateOnly? coveredThrough = null;
            if (fetchedByPair.TryGetValue(pair, out var f)) coveredThrough = f;
            if (latestManual.TryGetValue(pair, out var m) && (coveredThrough is null || m > coveredThrough))
                coveredThrough = m;

            var ordered = rowsByPair[pair].OrderBy(r => r.Day).ToArray();
            byPair[pair] = new PairRates(
                [.. ordered.Select(r => r.Day)], [.. ordered.Select(r => r.Rate)], coveredThrough);
        }
        return new ExchangeRateTable(ranges, byPair, needs.ServerToday);
    }

    private sealed record RateRow(string From, string To, DateOnly Day, decimal Rate, string Source);

    private sealed record ManualDay(string From, string To, DateOnly Day);
}

/// <summary>An ordered currency pair: one unit of <see cref="From"/> converts into
/// <see cref="To"/>.</summary>
public readonly record struct FxPair(string From, string To);

/// <summary>
/// The (pair, day)s a reader will convert at, declared before
/// <see cref="ExchangeRateLookup.LoadAsync"/> so every rate is read in one load. An
/// identity (a currency into itself) is never recorded: it needs no rate.
/// </summary>
public sealed class ExchangeRateNeeds
{
    private readonly Dictionary<FxPair, (DateOnly Min, DateOnly Max)> _ranges = [];

    /// <param name="serverToday">The server clock's UTC date, held for the whole read
    /// (<see cref="ExchangeRateLookup.NewNeeds"/>).</param>
    public ExchangeRateNeeds(DateOnly serverToday)
    {
        ServerToday = serverToday;
    }

    /// <summary>The server clock's UTC date the read's latest-rate conversions use.</summary>
    public DateOnly ServerToday { get; }

    /// <summary>Declares that an amount in <paramref name="fromCurrency"/> will be
    /// converted into <paramref name="toCurrency"/> at the latest rate
    /// (<see cref="ExchangeRateTable.Latest"/>): the pair's newest rate on or before the
    /// server's UTC today, so that day bounds what is read.</summary>
    public ExchangeRateNeeds AddLatest(string fromCurrency, string toCurrency) =>
        AddDay(fromCurrency, toCurrency, ServerToday);

    /// <summary>Declares that an amount in <paramref name="fromCurrency"/>, dated
    /// <paramref name="day"/>, will be converted into <paramref name="toCurrency"/> at the
    /// rate that day reads (<see cref="RateDay.Of(DateOnly, DateOnly)"/>): its own rate
    /// before the server's UTC today, the latest rate on or after it.</summary>
    public ExchangeRateNeeds Add(string fromCurrency, string toCurrency, DateOnly day)
    {
        RateDay.Of(day, ServerToday).Declare(this, fromCurrency, toCurrency);
        return this;
    }

    /// <summary>Declares the stored legs the rate from <paramref name="fromCurrency"/> into
    /// <paramref name="toCurrency"/>, two account currencies, is derived from through
    /// <paramref name="reportingCurrency"/> (<see cref="ExchangeRateTable.Derived"/>): each
    /// account currency into R, read for the day <paramref name="at"/> names. A leg in R
    /// itself needs no rate, so is not declared.</summary>
    public ExchangeRateNeeds AddDerived(
        string fromCurrency, string toCurrency, string reportingCurrency, RateDay at)
    {
        at.Declare(this, fromCurrency, reportingCurrency);
        at.Declare(this, toCurrency, reportingCurrency);
        return this;
    }

    /// <summary><see cref="AddDerived(string, string, string, RateDay)"/> for a figure dated
    /// <paramref name="day"/>, read by the one rule (<see cref="RateDay.Of(DateOnly, DateOnly)"/>).</summary>
    public ExchangeRateNeeds AddDerived(
        string fromCurrency, string toCurrency, string reportingCurrency, DateOnly day) =>
        AddDerived(fromCurrency, toCurrency, reportingCurrency, RateDay.Of(day, ServerToday));

    /// <summary>Declares <paramref name="day"/>'s own rate, as given: only
    /// <see cref="RateDay.Declare"/> calls it, for a day the rule already chose.</summary>
    internal ExchangeRateNeeds AddDay(string fromCurrency, string toCurrency, DateOnly day)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCurrency);
        ArgumentException.ThrowIfNullOrWhiteSpace(toCurrency);
        if (string.Equals(fromCurrency, toCurrency, StringComparison.Ordinal)) return this;

        var pair = new FxPair(fromCurrency, toCurrency);
        _ranges[pair] = _ranges.TryGetValue(pair, out var r)
            ? (day < r.Min ? day : r.Min, day > r.Max ? day : r.Max)
            : (day, day);
        return this;
    }

    /// <summary>True when every declared conversion is an identity.</summary>
    public bool IsEmpty => _ranges.Count == 0;

    internal IReadOnlyDictionary<FxPair, (DateOnly Min, DateOnly Max)> Ranges => _ranges;
}

/// <summary>The rate for one (pair, day): a value, or why it is missing.</summary>
public readonly record struct ExchangeRate(decimal? Rate, RateUnavailable? Unavailable)
{
    /// <summary>The identity rate, for a currency into itself.</summary>
    public static ExchangeRate Identity { get; } = new(1m, null);

    /// <summary><paramref name="amount"/> at this rate, at full precision; null when the
    /// rate is missing.</summary>
    public decimal? Apply(decimal amount) => Rate is { } r ? amount * r : null;
}

/// <summary>
/// The rates one <see cref="ExchangeRateLookup.LoadAsync"/> read, answered without another
/// query.
/// </summary>
/// <remarks>
/// A (pair, day) the load was not told about is a reader's bug, not a missing rate: it
/// throws, so a reader cannot quietly fall back to a query per day or report a rate it
/// never loaded as unavailable.
/// </remarks>
public sealed class ExchangeRateTable
{
    private readonly IReadOnlyDictionary<FxPair, (DateOnly Min, DateOnly Max)> _declared;
    private readonly IReadOnlyDictionary<FxPair, PairRates> _rates;
    private readonly DateOnly _serverToday;

    internal ExchangeRateTable(
        IReadOnlyDictionary<FxPair, (DateOnly Min, DateOnly Max)> declared,
        IReadOnlyDictionary<FxPair, PairRates> rates,
        DateOnly serverToday)
    {
        _declared = declared;
        _rates = rates;
        _serverToday = serverToday;
    }

    /// <summary>The server's UTC today the table was loaded for
    /// (<see cref="ExchangeRateNeeds.ServerToday"/>).</summary>
    public DateOnly ServerToday => _serverToday;

    /// <summary>
    /// The rate a figure dated <paramref name="day"/> converts at, by the one rule
    /// (<see cref="RateDay.Of(DateOnly, DateOnly)"/>): <see cref="RateOn"/> before the
    /// server's UTC today, <see cref="Latest"/> on or after it.
    /// </summary>
    public ExchangeRate RateFor(string fromCurrency, string toCurrency, DateOnly day) =>
        RateDay.Of(day, _serverToday).Read(this, fromCurrency, toCurrency);

    /// <summary>
    /// The latest rate from <paramref name="fromCurrency"/> into <paramref name="toCurrency"/>,
    /// what a current value and every forecast day convert at (ADR-0114 D7, D9): the pair's
    /// newest stored rate on or before the server's UTC today. One for a currency into
    /// itself.
    /// </summary>
    /// <remarks>
    /// <para>It is missing only when the pair's coverage (the later of its fetched-through
    /// day and its latest hand-entered day) ends before the server's UTC yesterday, or
    /// when the pair has no rate on or before today, or no coverage at all. A daily
    /// refresh that has not yet run today leaves coverage at yesterday, which still
    /// covers: a current value does not go blank between midnight and the refresh. The
    /// reason names the pair and the date of the rate it would have used, or the server's
    /// UTC today when there is no rate.</para>
    /// <para>The caller's local today plays no part. This is the ONE place the rule
    /// lives: every current-value and forecast-day conversion reads it, directly or
    /// through <see cref="RateDay.Latest"/>, declared with
    /// <see cref="ExchangeRateNeeds.AddLatest"/>.</para>
    /// </remarks>
    public ExchangeRate Latest(string fromCurrency, string toCurrency) =>
        string.Equals(fromCurrency, toCurrency, StringComparison.Ordinal)
            ? ExchangeRate.Identity
            : StoredLatest(fromCurrency, toCurrency).Rate;

    /// <summary><see cref="Latest"/> for a pair of two currencies, with the day of the
    /// stored rate it read.</summary>
    private StoredRead StoredLatest(string fromCurrency, string toCurrency)
    {
        var today = _serverToday;
        var pair = new FxPair(fromCurrency, toCurrency);
        if (!_declared.TryGetValue(pair, out var range) || today < range.Min || today > range.Max)
            throw new InvalidOperationException(
                $"The latest rate {fromCurrency}->{toCurrency} was not declared before the load.");

        var rates = _rates[pair];
        var i = Array.BinarySearch(rates.Days, today);
        if (i < 0) i = ~i - 1;   // the newest day before it
        if (i < 0) return StoredRead.Missing(new RateUnavailable(fromCurrency, toCurrency, today));
        if (rates.CoveredThrough is not { } covered || covered < today.AddDays(-1))
            return StoredRead.Missing(new RateUnavailable(fromCurrency, toCurrency, rates.Days[i]));
        return new StoredRead(new ExchangeRate(rates.Rates[i], null), rates.Days[i]);
    }

    /// <summary>
    /// The rate from <paramref name="fromCurrency"/> into <paramref name="toCurrency"/> on
    /// <paramref name="day"/>: one for a currency into itself; else the pair's last rate on
    /// or before the day, or <see cref="RateUnavailable"/> naming the pair and the day when
    /// there is none or the day is after the pair's coverage (ADR-0114 D9).
    /// </summary>
    /// <remarks>
    /// <para>Only a day before the server's UTC today has a rate of its own to read. A
    /// figure dated today or later is a current value or a forecast day and converts at
    /// <see cref="Latest"/> (ADR-0114 D7, D9), so today's rate not being published yet
    /// never blanks it. A reader with a dated figure asks <see cref="RateFor"/>, or passes
    /// a <see cref="RateDay"/>; it never picks between the two itself.</para>
    /// <para>The day is refused before the identity is answered, so a reader that reaches
    /// here with today or later fails on a single-currency ledger too, where its tests
    /// run, and not only where the wrong rate would show.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="day"/> is on or after
    /// the server's UTC today.</exception>
    /// <exception cref="InvalidOperationException">The (pair, day) was not declared in the
    /// needs this table was loaded for.</exception>
    public ExchangeRate RateOn(string fromCurrency, string toCurrency, DateOnly day)
    {
        if (day >= _serverToday)
            throw new ArgumentOutOfRangeException(
                nameof(day), day,
                $"{day:yyyy-MM-dd} is on or after the server's UTC today ({_serverToday:yyyy-MM-dd}): "
                + "a figure dated then converts at the latest rate. Read it through RateFor or a RateDay.");
        if (string.Equals(fromCurrency, toCurrency, StringComparison.Ordinal)) return ExchangeRate.Identity;
        return StoredOn(fromCurrency, toCurrency, day).Rate;
    }

    /// <summary><see cref="RateOn"/> for a pair of two currencies and a day before the
    /// server's UTC today, with the day of the stored rate it read.</summary>
    private StoredRead StoredOn(string fromCurrency, string toCurrency, DateOnly day)
    {
        var pair = new FxPair(fromCurrency, toCurrency);
        if (!_declared.TryGetValue(pair, out var range) || day < range.Min || day > range.Max)
            throw new InvalidOperationException(
                $"The rate {fromCurrency}->{toCurrency} on {day:yyyy-MM-dd} was not declared before the load.");

        var missing = StoredRead.Missing(new RateUnavailable(fromCurrency, toCurrency, day));
        var rates = _rates[pair];
        if (rates.CoveredThrough is not { } covered || day > covered) return missing;

        var i = Array.BinarySearch(rates.Days, day);
        if (i < 0) i = ~i - 1;   // the last day before it
        return i < 0 ? missing : new StoredRead(new ExchangeRate(rates.Rates[i], null), rates.Days[i]);
    }

    /// <summary>One stored rate as read, with the day it was stored for; no day when it is
    /// missing.</summary>
    private readonly record struct StoredRead(ExchangeRate Rate, DateOnly? Day)
    {
        public static StoredRead Missing(RateUnavailable why) => new(new ExchangeRate(null, why), null);
    }

    /// <summary>
    /// The rate from <paramref name="fromCurrency"/> into <paramref name="toCurrency"/>, two
    /// account currencies, derived through the reporting currency
    /// <paramref name="reportingCurrency"/> (ADR-0114 D13): the one place a rate is inverted
    /// or crossed, and only through R.
    /// </summary>
    /// <remarks>
    /// <para>For X (<paramref name="fromCurrency"/>), Y (<paramref name="toCurrency"/>) and
    /// R: when R is Y it is the stored X→R rate; when R is X, the inverse of the stored Y→R
    /// rate; otherwise the cross, X→R over Y→R. Only legs into R are read, in the direction
    /// they are stored (ADR-0114 D4): a rate stored the other way (R→X) is never read, so
    /// it supplies nothing and the leg is missing.</para>
    /// <para>Each leg is read by the one rule for <paramref name="at"/>
    /// (<see cref="RateFor"/>): its rate on or before a day before the server's UTC today,
    /// the latest rate otherwise, and none once its coverage has lapsed (ADR-0114 D9). The
    /// derived rate is good for the older of its legs' stored days. When a leg is missing,
    /// so is the derived rate, and each missing leg is named (ADR-0114 D9, D13).</para>
    /// <para>Nothing is rounded here; <see cref="DerivedRate.Convert"/> rounds the one
    /// figure it gives once, at the destination currency's places.</para>
    /// <para>The legs must have been declared with
    /// <see cref="ExchangeRateNeeds.AddDerived(string, string, string, RateDay)"/> for the
    /// same day, or this throws as every undeclared read does.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The two currencies are the same: a pair in one
    /// currency mirrors and takes no rate.</exception>
    public DerivedRate Derived(string fromCurrency, string toCurrency, string reportingCurrency, RateDay at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCurrency);
        ArgumentException.ThrowIfNullOrWhiteSpace(toCurrency);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportingCurrency);
        if (string.Equals(fromCurrency, toCurrency, StringComparison.Ordinal))
            throw new ArgumentException(
                $"A rate between two account currencies needs two currencies; both are {fromCurrency}.",
                nameof(toCurrency));

        var legs = new List<DerivedRateLeg>(2);
        var missing = new List<RateUnavailable>(2);
        // One unit of X is Numerator units of R; one unit of Y is Denominator units of R.
        var numerator = ReadLeg(fromCurrency) ?? 0m;
        var denominator = ReadLeg(toCurrency) ?? 0m;

        return missing.Count > 0
            ? new DerivedRate(fromCurrency, toCurrency, reportingCurrency, null, null, [], missing)
            : new DerivedRate(fromCurrency, toCurrency, reportingCurrency,
                new DerivedRate.Ratio(numerator, denominator), legs.Min(l => l.Day), legs, []);

        // The leg from an account currency into R: one, with no leg, for R itself.
        decimal? ReadLeg(string accountCurrency)
        {
            if (string.Equals(accountCurrency, reportingCurrency, StringComparison.Ordinal)) return 1m;
            var read = at.Day is { } d
                ? StoredOn(accountCurrency, reportingCurrency, d)
                : StoredLatest(accountCurrency, reportingCurrency);
            if (read.Rate.Unavailable is { } why)
            {
                missing.Add(why);
                return null;
            }
            legs.Add(new DerivedRateLeg(accountCurrency, reportingCurrency, read.Day!.Value, read.Rate.Rate!.Value));
            return read.Rate.Rate;
        }
    }

    /// <summary><see cref="Derived(string, string, string, RateDay)"/> for a figure dated
    /// <paramref name="day"/>, read by the one rule
    /// (<see cref="RateDay.Of(DateOnly, DateOnly)"/>).</summary>
    public DerivedRate Derived(string fromCurrency, string toCurrency, string reportingCurrency, DateOnly day) =>
        Derived(fromCurrency, toCurrency, reportingCurrency, RateDay.Of(day, _serverToday));

    /// <summary>A sum of amounts converted into <paramref name="toCurrency"/> through this
    /// table.</summary>
    public ConvertedTotal NewTotal(string toCurrency) => new(this, toCurrency);
}

/// <summary>One pair's loaded rates, ordered by day, and the last day its coverage
/// reaches (null: no coverage at all).</summary>
internal sealed record PairRates(DateOnly[] Days, decimal[] Rates, DateOnly? CoveredThrough);

/// <summary>One stored rate a <see cref="DerivedRate"/> was read from: one unit of
/// <paramref name="FromCurrency"/>, an account currency, in <paramref name="ToCurrency"/>,
/// the reporting currency, as stored for <paramref name="Day"/>, the day the one rate rule
/// read.</summary>
public sealed record DerivedRateLeg(string FromCurrency, string ToCurrency, DateOnly Day, decimal Rate);

/// <summary>
/// The rate between two account currencies on a day, derived through the reporting currency
/// (<see cref="ExchangeRateTable.Derived(string, string, string, RateDay)"/>, ADR-0114 D13):
/// the rate and the day it is good for, or every missing stored leg.
/// </summary>
/// <param name="FromCurrency">X, the currency an amount is in.</param>
/// <param name="ToCurrency">Y, the currency it converts into.</param>
/// <param name="ReportingCurrency">R, the currency the rate is derived through.</param>
/// <param name="Cross">The two legs' rates into R, X's over Y's; null when a leg is
/// missing.</param>
/// <param name="RateDate">The older of the legs' stored days, the day the derived rate is
/// good for (ADR-0114 D14); null exactly when <paramref name="Cross"/> is.</param>
/// <param name="Legs">The stored legs read, X→R then Y→R, each with its own day: one when R
/// is X or Y, two otherwise; empty when a leg is missing.</param>
/// <param name="Unavailable">Each missing stored leg, in the direction it is stored
/// (account currency into R), with its day (ADR-0114 D9): X's then Y's, both when both are
/// missing; empty when the rate is there.</param>
public sealed record DerivedRate(
    string FromCurrency,
    string ToCurrency,
    string ReportingCurrency,
    DerivedRate.Ratio? Cross,
    DateOnly? RateDate,
    IReadOnlyList<DerivedRateLeg> Legs,
    IReadOnlyList<RateUnavailable> Unavailable)
{
    /// <summary>One unit of X is <paramref name="Numerator"/> units of R; one unit of Y is
    /// <paramref name="Denominator"/> units of R. A leg in R itself is one.</summary>
    public readonly record struct Ratio(decimal Numerator, decimal Denominator);

    /// <summary>One unit of <see cref="FromCurrency"/> in <see cref="ToCurrency"/>, at full
    /// precision; null when a stored leg is missing.</summary>
    public decimal? Rate => Cross is { } c ? c.Numerator / c.Denominator : null;

    /// <summary>
    /// <paramref name="amount"/>, in <see cref="FromCurrency"/>, in
    /// <see cref="ToCurrency"/>: computed at full precision from the two legs, with no
    /// rounding in between, and rounded once at <see cref="ToCurrency"/>'s places, half away
    /// from zero (ADR-0114 D13, D15). It keeps the amount's sign. Null when a stored leg is
    /// missing.
    /// </summary>
    /// <remarks>The amount is multiplied before it is divided, so the rate itself, a
    /// quotient, is never rounded into the figure.</remarks>
    public decimal? Convert(decimal amount) =>
        Cross is { } c
            ? Coffer.Domain.Money.CurrencyMinorUnits.RoundAwayFromZero(amount * c.Numerator / c.Denominator, ToCurrency)
            : null;
}

/// <summary>
/// A combined figure in one currency: each amount converted at its own day's rate at full
/// precision, summed, and rounded once at the destination scale (ADR-0114 D7).
/// </summary>
/// <remarks>
/// Fails closed (ADR-0114 D9): one missing rate makes the whole figure null, never a partial
/// sum, and every distinct missing (pair, day) is kept so the figure can name them.
/// </remarks>
public sealed class ConvertedTotal
{
    /// <summary>The decimal places a converted money figure is rounded to, once: the
    /// stored money scale (<c>NUMERIC(19,4)</c>), so a sum of figures already at that
    /// scale, which every identity conversion is, comes back exactly as it was.</summary>
    /// <remarks>It is the internal bound of every read-side converted and combined figure,
    /// on REST and MCP alike, whatever the currency's own minor units: a figure is never
    /// rounded to its currency's places here, and the SPA's formatter presents it at them
    /// (ADR-0114 D15).</remarks>
    public const int DestinationScale = 4;

    /// <summary><paramref name="value"/> at <see cref="DestinationScale"/>, away from zero
    /// at a midpoint as the database rounds.</summary>
    public static decimal Round(decimal value) =>
        Math.Round(value, DestinationScale, MidpointRounding.AwayFromZero);

    private readonly ExchangeRateTable _table;
    private readonly List<RateUnavailable> _unavailable = [];
    private decimal _sum;

    internal ConvertedTotal(ExchangeRateTable table, string toCurrency)
    {
        _table = table;
        CurrencyCode = toCurrency;
    }

    /// <summary>The currency the total is in.</summary>
    public string CurrencyCode { get; }

    /// <summary>Adds <paramref name="amount"/>, in <paramref name="fromCurrency"/> and dated
    /// <paramref name="day"/>, converted at the rate that day reads
    /// (<see cref="ExchangeRateTable.RateFor"/>).</summary>
    /// <remarks>Zero is zero at every rate, so it needs none: an account that held nothing
    /// on a day before its currency's rates begin adds nothing rather than nulling the
    /// total (ADR-0114 D9 fails closed on a figure that NEEDS a missing rate).</remarks>
    public ConvertedTotal Add(decimal amount, string fromCurrency, DateOnly day) =>
        Add(amount, fromCurrency, RateDay.Of(day, _table.ServerToday));

    /// <summary>Adds <paramref name="amount"/>, in <paramref name="fromCurrency"/>, converted
    /// at the latest rate (<see cref="ExchangeRateTable.Latest"/>). Zero needs no rate.</summary>
    public ConvertedTotal AddLatest(decimal amount, string fromCurrency) =>
        amount == 0m ? this : Apply(amount, _table.Latest(fromCurrency, CurrencyCode));

    /// <summary>Adds a figure that is itself null when it needed a missing rate (a native
    /// figure with a position priced in another currency), dated <paramref name="day"/>,
    /// at the rate that day reads: its missing rates become this total's.</summary>
    public ConvertedTotal Add(Converted amount, string fromCurrency, DateOnly day) =>
        Add(amount, fromCurrency, RateDay.Of(day, _table.ServerToday));

    /// <summary>Adds <paramref name="amount"/>, in <paramref name="fromCurrency"/>, at the
    /// rate <paramref name="at"/> reads. Zero needs no rate.</summary>
    public ConvertedTotal Add(decimal amount, string fromCurrency, RateDay at) =>
        amount == 0m ? this : Apply(amount, at.Read(_table, fromCurrency, CurrencyCode));

    /// <summary>Adds a figure that may itself be null, at the rate <paramref name="at"/>
    /// reads; its missing rates become this total's.</summary>
    public ConvertedTotal Add(Converted amount, string fromCurrency, RateDay at)
    {
        if (amount.Amount is { } a) return Add(a, fromCurrency, at);
        foreach (var why in amount.Unavailable) Miss(why);
        return this;
    }

    private ConvertedTotal Apply(decimal amount, ExchangeRate rate)
    {
        if (rate.Unavailable is { } why) Miss(why);
        else _sum += amount * rate.Rate!.Value;
        return this;
    }

    private void Miss(RateUnavailable why)
    {
        if (!_unavailable.Contains(why)) _unavailable.Add(why);
    }

    /// <summary>The total rounded at <see cref="DestinationScale"/>, away from zero at a
    /// midpoint as the database rounds; null when any amount's rate was missing.</summary>
    public decimal? Amount => _unavailable.Count > 0 ? null : Round(_sum);

    /// <summary>The total with its reasons, as one value.</summary>
    public Converted Result => new(Amount, [.. _unavailable]);

    /// <summary>Every distinct (pair, day) whose rate was missing, in the order first met;
    /// empty when <see cref="Amount"/> has a value.</summary>
    public IReadOnlyList<RateUnavailable> Unavailable => _unavailable;
}

/// <summary>
/// A money figure that may have needed a missing rate: the amount, or null with every
/// missing (pair, day) it needed (ADR-0114 D9).
/// </summary>
public readonly record struct Converted(decimal? Amount, IReadOnlyList<RateUnavailable> Unavailable)
{
    /// <summary>A figure that needed no conversion, or whose every rate was there.</summary>
    public static Converted Of(decimal amount) => new(amount, []);

    /// <summary>The missing rates of several figures, distinct, in the order first met.</summary>
    public static IReadOnlyList<RateUnavailable> Reasons(params Converted[] figures) =>
        [.. figures.SelectMany(f => f.Unavailable).Distinct()];
}

/// <summary>
/// The day a figure converts at (ADR-0114 D7): a stated day, for a balance, a transaction
/// or a trade; or the latest rate, for a current value or a forecast day, which only
/// <see cref="ExchangeRateTable.Latest"/> resolves.
/// </summary>
/// <remarks>A reader passes one of these instead of a day, so no reader picks the day a
/// current value converts at itself.</remarks>
public readonly record struct RateDay
{
    private RateDay(DateOnly? day)
    {
        Day = day;
    }

    /// <summary>The stated day; null for the latest rate.</summary>
    public DateOnly? Day { get; }

    /// <summary>The latest rate, for a current value or a forecast day
    /// (<see cref="ExchangeRateTable.Latest"/>).</summary>
    public static RateDay Latest() => new(null);

    /// <summary>
    /// THE rule for which rate a dated figure uses (ADR-0114 D7, D9): a day before
    /// <paramref name="serverToday"/> converts at its own rate (a past balance,
    /// transaction or trade); today or later converts at the latest rate (a current value,
    /// today's postings, a scheduled or forecast day), which is readable while the pair is
    /// covered through UTC yesterday.
    /// </summary>
    /// <remarks>There is no other way to name a day's own rate, and
    /// <see cref="ExchangeRateTable.RateOn"/> refuses one on or after today, so no reader
    /// can convert a today-dated figure at a rate that is not yet published.</remarks>
    /// <param name="serverToday">The read's server UTC today
    /// (<see cref="ExchangeRateNeeds.ServerToday"/>), so the choice and the latest rate
    /// agree on the day.</param>
    public static RateDay Of(DateOnly day, DateOnly serverToday) =>
        day < serverToday ? new(day) : Latest();

    /// <summary>
    /// The rate a figure dated at <paramref name="instant"/> converts at: its UTC day's,
    /// by <see cref="Of(DateOnly, DateOnly)"/>. The end of a caller's today can fall on the
    /// next UTC day, and is still a current value.
    /// </summary>
    public static RateDay Of(DateTime instant, DateOnly serverToday) =>
        Of(DateOnly.FromDateTime(instant), serverToday);

    /// <summary>Declares the rate this day reads into <paramref name="needs"/>.</summary>
    public void Declare(ExchangeRateNeeds needs, string fromCurrency, string toCurrency)
    {
        ArgumentNullException.ThrowIfNull(needs);
        if (Day is { } d) needs.AddDay(fromCurrency, toCurrency, d);
        else needs.AddLatest(fromCurrency, toCurrency);
    }

    /// <summary>Reads the rate this day converts at from <paramref name="table"/>.</summary>
    public ExchangeRate Read(ExchangeRateTable table, string fromCurrency, string toCurrency)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Day is { } d
            ? table.RateOn(fromCurrency, toCurrency, d)
            : table.Latest(fromCurrency, toCurrency);
    }
}
