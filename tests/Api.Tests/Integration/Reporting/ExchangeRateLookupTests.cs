using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The one rate read conversion makes (<see cref="ExchangeRateLookup"/>, ADR-0114 D4, D9):
/// the last rate on or before a day, missing when there is none or the day is after the
/// pair's coverage, an identity for a currency into itself, and a load whose statement count
/// does not grow with the days or pairs it covers.
/// </summary>
/// <remarks>
/// Rates and coverage are seeded as the ledger's owner through EF, the role and path the
/// rate writers use (the request role may insert a rate and advance coverage, never rewrite
/// either), so no state here is one a writer could not produce.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ExchangeRateLookupTests : DatabaseTest
{
    public ExchangeRateLookupTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateOnly Jan(int day) => new(2026, 1, day);

    /// <summary>Counts the statements that read the rate tables.</summary>
    private sealed class RateStatements : DbCommandInterceptor
    {
        public List<string> Sent { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("fx_rates", StringComparison.Ordinal)
                || command.CommandText.Contains("fx_pair_coverage", StringComparison.Ordinal))
                Sent.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task SeedAsync(SyntheticLedger ledger, Action<SeedBuilder> build)
    {
        var seed = new SeedBuilder(ledger.LedgerId, ledger.UserId);
        build(seed);
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        db.FxRates.AddRange(seed.Rates);
        db.FxPairCoverage.AddRange(seed.Coverage);
        await db.SaveChangesAsync();
    }

    private sealed class SeedBuilder(Guid ledgerId, Guid ownerId)
    {
        public List<FxRateRow> Rates { get; } = [];
        public List<FxPairCoverageRow> Coverage { get; } = [];

        public SeedBuilder Fetched(string from, string to, DateOnly day, decimal rate)
        {
            Rates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledgerId, FromCurrency = from, ToCurrency = to,
                RateDate = day, Rate = rate, Source = FxRateSource.Frankfurter, Upstream = "Source A",
            });
            return this;
        }

        public SeedBuilder Manual(string from, string to, DateOnly day, decimal rate)
        {
            Rates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledgerId, FromCurrency = from, ToCurrency = to,
                RateDate = day, Rate = rate, Source = FxRateSource.Manual, EnteredByUserId = ownerId,
            });
            return this;
        }

        public SeedBuilder CoveredThrough(string from, string to, DateOnly day)
        {
            Coverage.Add(new FxPairCoverageRow
            {
                LedgerId = ledgerId, FromCurrency = from, ToCurrency = to, FetchedThroughDate = day,
            });
            return this;
        }
    }

    private async Task<(ExchangeRateTable Table, RateStatements Statements)> LoadAsync(
        SyntheticLedger ledger, ExchangeRateNeeds needs)
    {
        var statements = new RateStatements();
        await using var db = Db.NewRequestDbContextAsUser(ledger.UserId, statements);
        var table = await new ExchangeRateLookup(db, Clock).LoadAsync(ledger.LedgerId, needs);
        return (table, statements);
    }

    /// <summary>The server's clock: midday UTC on the 31st, after every day the tests
    /// convert at.</summary>
    private static readonly TestClock Clock = new(new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));

    private static ExchangeRateNeeds NewNeeds() => new(DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime));

    private static ExchangeRateNeeds Needs(string from, string to, params DateOnly[] days)
    {
        var needs = NewNeeds();
        foreach (var d in days) needs.Add(from, to, d);
        return needs;
    }

    private static RateUnavailable Missing(string from, string to, DateOnly day) => new(from, to, day);

    [Fact]
    public async Task A_day_converts_at_the_last_rate_on_or_before_it_until_the_pair_coverage_ends()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        // Friday the 2nd and Monday the 5th: no weekend rows, as the feed sends none.
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(2), 1.100000000001m)
            .Fetched("EUR", "USD", Jan(5), 1.200000000002m)
            .CoveredThrough("EUR", "USD", Jan(6)));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(1), Jan(7)));

        Assert.Equal(new ExchangeRate(null, Missing("EUR", "USD", Jan(1))), table.RateOn("EUR", "USD", Jan(1)));
        Assert.Equal(1.100000000001m, table.RateOn("EUR", "USD", Jan(2)).Rate);
        Assert.Equal(1.100000000001m, table.RateOn("EUR", "USD", Jan(3)).Rate);
        Assert.Equal(1.100000000001m, table.RateOn("EUR", "USD", Jan(4)).Rate);
        Assert.Equal(1.200000000002m, table.RateOn("EUR", "USD", Jan(5)).Rate);
        // Covered, and no rate published for it: the 5th's.
        Assert.Equal(1.200000000002m, table.RateOn("EUR", "USD", Jan(6)).Rate);
        // After the coverage: missing, though a rate on or before it exists.
        Assert.Equal(new ExchangeRate(null, Missing("EUR", "USD", Jan(7))), table.RateOn("EUR", "USD", Jan(7)));
    }

    [Fact]
    public async Task A_pair_with_rates_but_no_coverage_converts_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s.Fetched("EUR", "USD", Jan(2), 1.1m));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(2)));

        Assert.Equal(Missing("EUR", "USD", Jan(2)), table.RateOn("EUR", "USD", Jan(2)).Unavailable);
    }

    [Fact]
    public async Task A_hand_entered_day_after_the_fetched_coverage_extends_it_to_that_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(5), 1.2m)
            .CoveredThrough("EUR", "USD", Jan(6))
            .Manual("EUR", "USD", Jan(9), 1.3m));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(7), Jan(10)));

        // Inside the extended coverage, before the hand-entered day: the last fetched rate.
        Assert.Equal(1.2m, table.RateOn("EUR", "USD", Jan(7)).Rate);
        Assert.Equal(1.2m, table.RateOn("EUR", "USD", Jan(8)).Rate);
        Assert.Equal(1.3m, table.RateOn("EUR", "USD", Jan(9)).Rate);
        Assert.Equal(Missing("EUR", "USD", Jan(10)), table.RateOn("EUR", "USD", Jan(10)).Unavailable);
    }

    [Fact]
    public async Task A_hand_entered_day_after_the_days_asked_for_still_covers_them()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(5), 1.2m)
            .CoveredThrough("EUR", "USD", Jan(6))
            .Manual("EUR", "USD", Jan(20), 1.3m));

        // The load reads only through the 8th; the hand entry on the 20th lies beyond it.
        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(8)));

        Assert.Equal(1.2m, table.RateOn("EUR", "USD", Jan(8)).Rate);
    }

    [Fact]
    public async Task Hand_entered_days_alone_are_a_pair_coverage()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Manual("EUR", "USD", Jan(5), 1.2m)
            .Manual("EUR", "USD", Jan(9), 1.3m));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(6), Jan(10)));

        Assert.Equal(1.2m, table.RateOn("EUR", "USD", Jan(6)).Rate);
        Assert.Equal(1.3m, table.RateOn("EUR", "USD", Jan(9)).Rate);
        Assert.Equal(Missing("EUR", "USD", Jan(10)), table.RateOn("EUR", "USD", Jan(10)).Unavailable);
    }

    [Fact]
    public async Task A_hand_entered_day_before_the_fetched_coverage_does_not_shorten_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Manual("EUR", "USD", Jan(2), 1.1m)
            .Fetched("EUR", "USD", Jan(5), 1.2m)
            .CoveredThrough("EUR", "USD", Jan(8)));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(3), Jan(8)));

        Assert.Equal(1.1m, table.RateOn("EUR", "USD", Jan(3)).Rate);
        Assert.Equal(1.2m, table.RateOn("EUR", "USD", Jan(8)).Rate);
    }

    [Fact]
    public async Task The_first_day_asked_for_falls_back_to_a_rate_long_before_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", new DateOnly(2025, 6, 2), 1.05m)
            .CoveredThrough("EUR", "USD", Jan(31)));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(15), Jan(30)));

        Assert.Equal(1.05m, table.RateOn("EUR", "USD", Jan(15)).Rate);
        Assert.Equal(1.05m, table.RateOn("EUR", "USD", Jan(30)).Rate);
    }

    [Fact]
    public async Task Only_the_stored_direction_converts_and_only_the_ledger_own_rates()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var other = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(5), 1.2m)
            .CoveredThrough("EUR", "USD", Jan(6)));
        await SeedAsync(other, s => s
            .Fetched("GBP", "USD", Jan(5), 1.3m)
            .CoveredThrough("GBP", "USD", Jan(6)));

        var needs = NewNeeds()
            .Add("USD", "EUR", Jan(5))
            .Add("GBP", "USD", Jan(5));
        var (table, _) = await LoadAsync(ledger, needs);

        // Never the inverse of a stored rate.
        Assert.Equal(Missing("USD", "EUR", Jan(5)), table.RateOn("USD", "EUR", Jan(5)).Unavailable);
        // Never another ledger's.
        Assert.Equal(Missing("GBP", "USD", Jan(5)), table.RateOn("GBP", "USD", Jan(5)).Unavailable);
    }

    [Fact]
    public async Task A_currency_into_itself_is_one_and_reads_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var needs = Needs("USD", "USD", Jan(5));
        Assert.True(needs.IsEmpty);
        var (table, statements) = await LoadAsync(ledger, needs);

        Assert.Equal(new ExchangeRate(1m, null), table.RateOn("USD", "USD", Jan(5)));
        // Undeclared and never loaded, and still one: an identity is not a lookup.
        Assert.Equal(1m, table.RateOn("JPY", "JPY", Jan(9)).Rate);
        Assert.Empty(statements.Sent);
    }

    [Fact]
    public async Task A_day_that_was_not_declared_before_the_load_throws()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(5), 1.2m)
            .CoveredThrough("EUR", "USD", Jan(9)));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(5), Jan(7)));

        Assert.Throws<InvalidOperationException>(() => table.RateOn("EUR", "USD", Jan(8)));
        Assert.Throws<InvalidOperationException>(() => table.RateOn("GBP", "USD", Jan(6)));
    }

    [Fact]
    public async Task The_load_sends_the_same_statements_for_one_day_as_for_years_of_days_and_many_pairs()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s =>
        {
            foreach (var from in new[] { "EUR", "GBP", "CHF" })
            {
                for (var d = new DateOnly(2020, 1, 1); d <= Jan(31); d = d.AddDays(7))
                    s.Fetched(from, "USD", d, 1.1m);
                // Fetched coverage short of the last day asked for, so the hand-entered
                // statement is sent too: the most a load sends.
                s.CoveredThrough(from, "USD", Jan(20));
                s.Manual(from, "USD", Jan(31), 1.2m);
            }
        });

        var one = await LoadAsync(ledger, Needs("EUR", "USD", Jan(31)));

        var many = NewNeeds();
        foreach (var from in new[] { "EUR", "GBP", "CHF" })
            for (var first = new DateOnly(2020, 1, 1); first <= Jan(1); first = first.AddMonths(1))
                many.Add(from, "USD", first.AddMonths(1).AddDays(-1));   // each month's last day
        var all = await LoadAsync(ledger, many);

        Assert.Equal(3, one.Statements.Sent.Count);
        Assert.Equal(3, all.Statements.Sent.Count);
        // The 31st is the server's today, so it reads the latest rate.
        Assert.Equal(1.2m, one.Table.Latest("EUR", "USD").Rate);
        Assert.Equal(1.1m, all.Table.RateOn("CHF", "USD", new DateOnly(2020, 1, 31)).Rate);
        Assert.Equal(1.2m, all.Table.Latest("GBP", "USD").Rate);
    }

    [Fact]
    public async Task Each_rate_range_is_read_through_the_pair_day_key_not_a_scan_of_the_table()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        // Years of daily rates for several pairs, so the table is large enough that a scan
        // of it would cost what the key avoids, and planned on fresh statistics.
        await SeedAsync(ledger, s =>
        {
            foreach (var from in new[] { "EUR", "GBP", "CHF", "JPY" })
            {
                for (var d = new DateOnly(2021, 1, 1); d <= Jan(20); d = d.AddDays(1))
                    s.Fetched(from, "USD", d, 1.1m);
                s.CoveredThrough(from, "USD", Jan(20));
            }
        });
        await using (var conn = Db.OpenServiceConnection())
        await using (var analyze = new Npgsql.NpgsqlCommand("ANALYZE fx_rates", conn))
            await analyze.ExecuteNonQueryAsync();

        var capture = new ExplainCapture
        {
            Selects = sql => sql.Contains("fx_rates", StringComparison.Ordinal), Enabled = true,
        };
        await using (var db = Db.NewRequestDbContextAsUser(ledger.UserId, capture))
        {
            var needs = NewNeeds().Add("EUR", "USD", Jan(5)).Add("EUR", "USD", Jan(9))
                .Add("GBP", "USD", Jan(9));
            var table = await new ExchangeRateLookup(db, Clock).LoadAsync(ledger.LedgerId, needs);
            Assert.Equal(1.1m, table.RateOn("GBP", "USD", Jan(9)).Rate);
        }

        var plan = Assert.Single(capture.Drain()).Plan;
        Output.WriteLine(plan);
        var lines = plan.Split('\n').Select(l => l.Trim()).ToList();

        // Every day bound is an index condition on the pair-day key. A bound the scan
        // cannot use (a floor computed by a subquery in the statement, which row-level
        // security applies only after its policy) shows as a Filter over the pair's whole
        // history.
        var conditions = lines.Where(l => l.StartsWith("Index Cond:", StringComparison.Ordinal)
                                          && l.Contains("from_currency", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, conditions.Count);   // two pairs, two branches each
        Assert.All(conditions, c => Assert.Contains("rate_date", c, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("Filter:", StringComparison.Ordinal)
                                          && l.Contains("rate_date", StringComparison.Ordinal));
        Assert.Equal(4, lines.Count(l => l.Contains("using uq_fx_rates_pair_day on fx_rates", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.Contains("Seq Scan on fx_rates", StringComparison.Ordinal)
                                          || l.Contains("Bitmap Heap Scan on fx_rates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_total_converts_at_full_precision_and_rounds_once()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(5), 1.5m)
            .CoveredThrough("EUR", "USD", Jan(5)));

        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(5)));

        // Three of the smallest stored euro amounts each convert to a figure finer than the
        // stored money scale, half-way at the next place: rounded one by one they would
        // overstate the total, so they are summed first and the sum is rounded once.
        var total = table.NewTotal("USD")
            .Add(0.0001m, "EUR", Jan(5))
            .Add(0.0001m, "EUR", Jan(5))
            .Add(0.0001m, "EUR", Jan(5));
        Assert.Equal(0.0005m, total.Amount);
        Assert.Empty(total.Unavailable);

        // An amount already in the total's currency adds as it stands, at the stored
        // money scale.
        Assert.Equal(10.1234m, table.NewTotal("USD").Add(10.1234m, "USD", Jan(5)).Amount);
        Assert.Equal(10.0003m, table.NewTotal("USD").Add(10m, "USD", Jan(5)).Add(0.0002m, "EUR", Jan(5)).Amount);
    }

    [Fact]
    public async Task The_latest_rate_is_the_newest_on_or_before_server_today_while_coverage_reaches_yesterday()
    {
        // The server's UTC today is the 31st (Clock), so yesterday is the 30th.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            // Covered through yesterday: today's refresh has not run. Covered.
            .Fetched("EUR", "USD", Jan(29), 1.1m)
            .Fetched("EUR", "USD", Jan(30), 1.2m)
            .CoveredThrough("EUR", "USD", Jan(30))
            // Covered through today, with a rate today: today's.
            .Fetched("CHF", "USD", Jan(30), 1.4m)
            .Fetched("CHF", "USD", Jan(31), 1.5m)
            .CoveredThrough("CHF", "USD", Jan(31))
            // Covered through today, the newest rate days earlier (the feed skipped):
            // that rate, however old, since no staleness threshold applies.
            .Fetched("JPY", "USD", Jan(26), 0.0066m)
            .CoveredThrough("JPY", "USD", Jan(31))
            // Covered through the day before yesterday: missing, naming the rate's date.
            .Fetched("GBP", "USD", Jan(28), 1.3m)
            .Fetched("GBP", "USD", Jan(29), 1.31m)
            .CoveredThrough("GBP", "USD", Jan(29))
            // Covered, but no rate on or before today: missing, naming today.
            .Fetched("SEK", "USD", new DateOnly(2026, 2, 3), 0.1m)
            .CoveredThrough("SEK", "USD", new DateOnly(2026, 2, 3)));

        var needs = NewNeeds();
        foreach (var from in new[] { "EUR", "CHF", "JPY", "GBP", "SEK" }) needs.AddLatest(from, "USD");
        var (table, _) = await LoadAsync(ledger, needs);

        Assert.Equal(new ExchangeRate(1.2m, null), table.Latest("EUR", "USD"));
        Assert.Equal(new ExchangeRate(1.5m, null), table.Latest("CHF", "USD"));
        Assert.Equal(new ExchangeRate(0.0066m, null), table.Latest("JPY", "USD"));
        Assert.Equal(new ExchangeRate(null, Missing("GBP", "USD", Jan(29))), table.Latest("GBP", "USD"));
        Assert.Equal(new ExchangeRate(null, Missing("SEK", "USD", Jan(31))), table.Latest("SEK", "USD"));
        Assert.Equal(ExchangeRate.Identity, table.Latest("USD", "USD"));
        Assert.Equal(1.2m, table.NewTotal("USD").AddLatest(1m, "EUR").Amount);
        Assert.Equal(1.2m, table.NewTotal("USD").Add(1m, "EUR", RateDay.Latest()).Amount);
    }

    [Fact]
    public async Task The_latest_rate_with_no_coverage_at_all_is_missing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s.Fetched("EUR", "USD", Jan(31), 1.2m));

        var (table, _) = await LoadAsync(ledger, NewNeeds().AddLatest("EUR", "USD"));

        Assert.Equal(new ExchangeRate(null, Missing("EUR", "USD", Jan(31))), table.Latest("EUR", "USD"));
    }

    [Fact]
    public async Task A_total_with_any_missing_rate_is_null_and_names_each_missing_pair_and_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(5), 1.5m)
            .CoveredThrough("EUR", "USD", Jan(5)));

        var needs = NewNeeds()
            .Add("EUR", "USD", Jan(5))
            .Add("EUR", "USD", Jan(6))
            .Add("GBP", "USD", Jan(5));
        var (table, _) = await LoadAsync(ledger, needs);

        var total = table.NewTotal("USD")
            .Add(100m, "USD", Jan(5))
            .Add(100m, "EUR", Jan(5))
            .Add(100m, "EUR", Jan(6))
            .Add(5m, "GBP", Jan(5))
            .Add(7m, "GBP", Jan(5));

        // Never a partial sum.
        Assert.Null(total.Amount);
        Assert.Equal([Missing("EUR", "USD", Jan(6)), Missing("GBP", "USD", Jan(5))], total.Unavailable);
    }

    // ---- the one rule for which rate a dated figure uses ------------------------------

    /// <summary>
    /// The guard on every conversion site: a day's own rate can be read only for a day
    /// before the server's UTC today. A reader that asks for today's or a later day's own
    /// rate throws, on a one-currency ledger as well, so a new site that skips the rule
    /// fails the first test that reaches it with a figure dated today, whatever its
    /// currencies. Through the rule, the same day reads the latest rate.
    /// </summary>
    [Fact]
    public async Task A_day_on_or_after_server_today_has_no_rate_of_its_own_and_reads_the_latest()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var (table, _) = await LoadAsync(ledger, NewNeeds());
        var today = Jan(31);

        foreach (var day in new[] { today, today.AddDays(1), today.AddDays(400) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => table.RateOn("EUR", "USD", day));
            // An identity too, before the identity is answered.
            Assert.Throws<ArgumentOutOfRangeException>(() => table.RateOn("USD", "USD", day));
            Assert.Null(RateDay.Of(day, today).Day);
            Assert.Null(RateDay.Of(day.ToDateTime(new TimeOnly(23, 59), DateTimeKind.Utc), today).Day);
        }
        Assert.Equal(Jan(30), RateDay.Of(Jan(30), today).Day);
        Assert.Equal(ExchangeRate.Identity, table.RateOn("USD", "USD", Jan(30)));
        Assert.Equal(today, table.ServerToday);
    }

    /// <summary>
    /// A figure dated today, or scheduled later, converts at the latest rate (ADR-0114 D7,
    /// D9), so it is not null while the pair is covered through UTC yesterday, though no rate
    /// is published for its own day. Declared and read through every dated entry point:
    /// the needs, the table and a total.
    /// </summary>
    [Fact]
    public async Task A_figure_dated_today_or_later_converts_at_the_latest_rate_while_yesterday_is_covered()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedAsync(ledger, s => s
            .Fetched("EUR", "USD", Jan(20), 1.1m)
            .Fetched("EUR", "USD", Jan(30), 1.5m)
            .CoveredThrough("EUR", "USD", Jan(30)));
        var scheduled = new DateOnly(2026, 2, 20);

        // Only today and a scheduled day are declared: the latest rate is what they load.
        var (table, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(31), scheduled));

        Assert.Equal(new ExchangeRate(1.5m, null), table.RateFor("EUR", "USD", Jan(31)));
        Assert.Equal(new ExchangeRate(1.5m, null), table.RateFor("EUR", "USD", scheduled));
        var total = table.NewTotal("USD")
            .Add(10m, "EUR", Jan(31))
            .Add(20m, "EUR", scheduled)
            .Add(Converted.Of(1m), "EUR", Jan(31));
        Assert.Equal(46.5m, total.Amount);
        Assert.Empty(total.Unavailable);

        // A past day still reads its own rate, through the same entry point.
        var (past, _) = await LoadAsync(ledger, Needs("EUR", "USD", Jan(25)));
        Assert.Equal(1.1m, past.RateFor("EUR", "USD", Jan(25)).Rate);
    }
}
