using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Tests.Unit.Db;

/// <summary>
/// The rates view's missing days (ADR-0114 D9) are the conversion's own rule read the other
/// way round: over every combination of first needed day, first stored rate and coverage,
/// a day is listed missing exactly when <see cref="ExchangeRateTable.RateFor"/> reads no
/// rate for it. Synthetic days only.
/// </summary>
public sealed class FxPairMissingDaysTests
{
    private static readonly DateOnly Today = new(2026, 6, 10);

    private static DateOnly D(int offset) => Today.AddDays(offset);

    public static TheoryData<int?, int?, int?> Cases()
    {
        var data = new TheoryData<int?, int?, int?>();
        int?[] neededFrom = [null, -60, -30, -5, -1, 0, 3];
        int?[] firstRate = [null, -45, -30, -20, -3, -1, 0];
        int?[] covered = [null, -45, -25, -10, -2, -1, 0];
        foreach (var n in neededFrom)
            foreach (var f in firstRate)
                foreach (var c in covered)
                {
                    // A stored rate is never after coverage: a fetched day is inside the
                    // fetched range and a hand-entered one extends coverage to itself.
                    if (f is not null && c is not null && c < f) continue;
                    data.Add(n, f, c);
                }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_day_is_missing_exactly_when_the_conversion_reads_no_rate_for_it(
        int? neededFrom, int? firstRate, int? covered)
    {
        DateOnly? needed = neededFrom is { } n ? D(n) : null;
        DateOnly? first = firstRate is { } f ? D(f) : null;
        DateOnly? through = covered is { } c ? D(c) : null;

        var spans = FxPairMissingDays.Of(needed, first, through, Today);

        // Stored days: the first, then every third day while covered, so days between
        // stored rates are asked about too.
        var days = new List<DateOnly>();
        if (first is { } d0)
            for (var d = d0; d <= (through is { } t && t > d0 ? t : d0); d = d.AddDays(3)) days.Add(d);
        var start = needed is { } s && s < Today ? s : Today;
        var pair = new FxPair("EUR", "USD");
        var table = new ExchangeRateTable(
            new Dictionary<FxPair, (DateOnly Min, DateOnly Max)> { [pair] = (start, Today) },
            new Dictionary<FxPair, PairRates>
            {
                [pair] = new PairRates([.. days], [.. days.Select(_ => 1.1m)], through),
            },
            Today);

        for (var day = start; day <= Today; day = day.AddDays(1))
        {
            var unavailable = table.RateFor("EUR", "USD", day).Unavailable is not null;
            var listed = spans.Any(m => m.From <= day && day <= m.Through);
            Assert.True(unavailable == listed,
                $"{day:yyyy-MM-dd}: the conversion reads {(unavailable ? "no rate" : "a rate")}, the view lists it {(listed ? "missing" : "covered")}.");
        }
        // Spans are earliest first, apart and in the window.
        for (var i = 0; i < spans.Count; i++)
        {
            Assert.True(spans[i].From <= spans[i].Through);
            Assert.True(spans[i].From >= start && spans[i].Through <= Today);
            if (i > 0) Assert.True(spans[i - 1].Through.AddDays(1) < spans[i].From);
        }
    }

    [Fact]
    public void A_pair_covered_through_yesterday_misses_only_the_days_before_its_first_rate()
    {
        Assert.Equal([(D(-30), D(-21))], FxPairMissingDays.Of(D(-30), D(-20), D(-1), Today));
    }

    [Fact]
    public void Coverage_that_ended_before_yesterday_misses_every_day_after_it_through_today()
    {
        Assert.Equal([(D(-30), D(-21)), (D(-9), D(0))], FxPairMissingDays.Of(D(-30), D(-20), D(-10), Today));
    }

    [Fact]
    public void A_pair_with_no_rate_misses_every_needed_day_and_one_needed_only_now_misses_today()
    {
        Assert.Equal([(D(-30), D(0))], FxPairMissingDays.Of(D(-30), null, null, Today));
        Assert.Equal([(D(0), D(0))], FxPairMissingDays.Of(null, null, null, Today));
        Assert.Empty(FxPairMissingDays.Of(null, D(-3), D(0), Today));
    }
}
