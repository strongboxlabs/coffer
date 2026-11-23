using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Synthetic benchmark history, stored as the market-series fetcher stores it
/// (<c>MarketSeriesFetcher</c>): the ledger's benchmark row, one growth row per trading day
/// (that day's close over the previous trading day's), and the range of days the fetches
/// asked for. Rates as the rate fetcher stores them. Every value here is made up.
/// </summary>
internal static class BenchmarkSeed
{
    /// <summary>The ledger's benchmark <paramref name="symbol"/>, quoted in
    /// <paramref name="currency"/>, at the end of its list.</summary>
    public static async Task AddBenchmarkAsync(
        TestDatabase db, SyntheticLedger ledger, string symbol, string currency = "USD", int position = 1)
    {
        await using var ctx = db.NewAppDbContextAsUser(ledger.UserId);
        ctx.LedgerBenchmarks.Add(new LedgerBenchmarkRow
        {
            Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, Symbol = symbol, Name = $"{symbol} Total Market Fund",
            CurrencyCode = currency, Position = position,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A growth value for every weekday in (<paramref name="askedFrom"/>,
    /// <paramref name="through"/>] from <paramref name="growthOf"/>, and the coverage
    /// [<paramref name="askedFrom"/>, <paramref name="through"/>]: the fetcher's first fetch,
    /// whose first trading day in the response is the base and gets no row. Weekends have
    /// no row, as the feed has no bar for them. Returns what was stored, by day.
    /// </summary>
    public static async Task<IReadOnlyDictionary<DateOnly, decimal>> SeedGrowthAsync(
        TestDatabase db, SyntheticLedger ledger, string symbol,
        DateOnly askedFrom, DateOnly through, Func<DateOnly, decimal> growthOf, DateOnly? firstRow = null)
    {
        var stored = new Dictionary<DateOnly, decimal>();
        await using var ctx = db.NewAppDbContextAsUser(ledger.UserId);
        var start = firstRow ?? FirstTradingDayAfter(askedFrom);
        for (var day = start; day <= through; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var growth = growthOf(day);
            stored[day] = growth;
            ctx.BenchmarkGrowth.Add(new BenchmarkGrowthRow
            {
                LedgerId = ledger.LedgerId, Symbol = symbol, GrowthDate = day, Growth = growth,
                Source = BenchmarkGrowthSource.Fetch,
            });
        }
        ctx.BenchmarkCoverage.Add(new BenchmarkCoverageRow
        {
            LedgerId = ledger.LedgerId, Symbol = symbol, FetchedFromDate = askedFrom, FetchedThroughDate = through,
        });
        await ctx.SaveChangesAsync();
        return stored;
    }

    /// <summary>The weekday after the first weekday on or after <paramref name="day"/>: the
    /// first day a fetch from <paramref name="day"/> stores, its first bar being the base.</summary>
    private static DateOnly FirstTradingDayAfter(DateOnly day)
    {
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        day = day.AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        return day;
    }

    /// <summary>A rate for every day in [<paramref name="from"/>, <paramref name="through"/>],
    /// covered through <paramref name="coveredThrough"/>, as the rate fetcher stores them.
    /// Returns what was stored, by day.</summary>
    public static async Task<IReadOnlyDictionary<DateOnly, decimal>> SeedRatesAsync(
        TestDatabase db, SyntheticLedger ledger, string fromCurrency, string toCurrency,
        DateOnly from, DateOnly through, DateOnly coveredThrough, Func<DateOnly, decimal> rateOf)
    {
        var stored = new Dictionary<DateOnly, decimal>();
        await using var ctx = db.NewAppDbContextAsUser(ledger.UserId);
        for (var day = from; day <= through; day = day.AddDays(1))
        {
            var rate = rateOf(day);
            stored[day] = rate;
            ctx.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = fromCurrency, ToCurrency = toCurrency,
                RateDate = day, Rate = rate, Source = FxRateSource.Frankfurter,
            });
        }
        ctx.FxPairCoverage.Add(new FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = fromCurrency, ToCurrency = toCurrency,
            FetchedFromDate = from, FetchedThroughDate = coveredThrough,
        });
        await ctx.SaveChangesAsync();
        return stored;
    }

    /// <summary>The growth from the close of <paramref name="from"/> to the close of
    /// <paramref name="to"/>, worked out here from what was stored: every stored day after
    /// <paramref name="from"/> and through <paramref name="to"/>, multiplied.</summary>
    public static double Growth(IReadOnlyDictionary<DateOnly, decimal> stored, DateOnly from, DateOnly to)
    {
        var product = 1.0;
        foreach (var (day, growth) in stored)
            if (day > from && day <= to) product *= (double)growth;
        return product;
    }
}
