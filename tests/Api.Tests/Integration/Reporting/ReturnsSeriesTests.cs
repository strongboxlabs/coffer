using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The returns series (<see cref="InvestmentReportingRepository.ReturnsSeriesAsync(Guid, Guid?, bool, DateTime?, DateTime?, ReportTimeBucket, DateTime, CancellationToken)"/>):
/// the time-weighted index, the cumulative gain and the money-weighted return to date at the
/// end of each period, held to the scalar <c>returns</c> report for the same window.
/// </summary>
/// <remarks>
/// <para>The series is read as the scalar is: every point is compared with
/// <c>ReturnsAsync</c> over [from, that period end], which is the same chain run to that
/// instant, so a series that drifted from the report would fail at the point it drifted.
/// The test loops over the scalar; the engine never does.</para>
/// <para>Each case is built from synthetic ledgers through the seeding helpers, whose writes
/// recompute the balances as the API's writes do, with prices from the feed dated after the
/// buy so the market differs from the cost.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsSeriesTests : DatabaseTest
{
    public ReturnsSeriesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The last instant of the day, at the database's resolution.</summary>
    private static DateTime EndOfDay(int y, int m, int d) => Utc(y, m, d).AddDays(1).AddTicks(-TimeSpan.TicksPerMicrosecond);

    /// <summary>A caller's "now" well past every date the cases use, so no case is clamped by it.</summary>
    private static readonly DateTime Later = Utc(2030, 1, 1);

    private async Task<ReturnsSeriesResult> SeriesAsync(
        SyntheticLedger ledger, Guid? accountId, bool includeAccounts,
        DateTime from, DateTime to, ReportTimeBucket interval = ReportTimeBucket.Month, DateTime? now = null)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
            ledger.LedgerId, accountId, includeAccounts, from, to, interval, now ?? Later);
    }

    private async Task<ReturnsResult> ScalarAsync(SyntheticLedger ledger, Guid? accountId, DateTime from, DateTime end)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsAsync(
            ledger.LedgerId, accountId, fromUtc: from, toUtc: null, nowUtc: end);
    }

    /// <summary>The instant the 12 months ending at a period end open: just after the same
    /// period end a year earlier.</summary>
    private static DateTime YearOpening(DateTime periodEnd) =>
        periodEnd.AddTicks(TimeSpan.TicksPerMicrosecond).AddYears(-1);

    /// <summary>
    /// Every point of the series with an index equals what the scalar report gives for the
    /// window from <paramref name="from"/> to that point: the time-weighted growth
    /// (the scalar annualizes it over the days the series reports) and the gain. Every point
    /// with a money-weighted return equals the scalar's for the 12 months ending there, and
    /// every point without one for want of a year opened with nothing invested. Returns how
    /// many points had an index, so a case that compared nothing cannot pass.
    /// </summary>
    private async Task<int> AssertMatchesScalarAsync(
        SyntheticLedger ledger, ReturnsSeries series, DateTime from)
    {
        var compared = 0;
        foreach (var p in series.Points)
        {
            await AssertYearMatchesScalarAsync(ledger, series.AccountId, p);

            var scalar = await ScalarAsync(ledger, series.AccountId, from, p.PeriodEnd);
            if (p.Index is not { } index) continue;

            Assert.NotNull(scalar.TimeWeightedReturn);
            var years = scalar.TimeWeightedCoveredYears!.Value;
            Assert.Equal(scalar.TimeWeightedReturn!.Value, Math.Pow(index / 100.0, 1.0 / years) - 1.0, 9);
            Assert.Equal(scalar.TimeWeightedCoveredDays, p.CoveredDays);
            Assert.Equal(scalar.Gain, p.Gain);
            compared++;
        }
        return compared;
    }

    /// <summary>A point's money-weighted return is the scalar report's for the 12 months
    /// ending at it, and it has none for want of a year exactly when that year opened with
    /// nothing invested.</summary>
    private async Task AssertYearMatchesScalarAsync(SyntheticLedger ledger, Guid? accountId, ReturnsSeriesPoint p)
    {
        var year = await ScalarAsync(ledger, accountId, YearOpening(p.PeriodEnd), p.PeriodEnd);
        if (p.MwrTrailingYear is { } mwr)
        {
            Assert.NotNull(year.MoneyWeightedReturn);
            Assert.Equal(year.MoneyWeightedReturn!.Value, mwr, 9);
            Assert.NotEqual(0m, year.StartValue);
        }
        else if (p.MwrUnavailableReason == ReturnsSeriesReasons.UnderAYearCovered)
        {
            Assert.Equal(0m, year.StartValue);
        }
    }

    private sealed record Book(
        SyntheticLedger Ledger, Guid BankId, Guid AId, Guid BId, Guid SecurityId);

    /// <summary>
    /// One ledger with two brokerages that move money in and out, trade, and roll money
    /// over between them, plus a third that never held anything.
    /// </summary>
    private async Task<Book> TwoBrokeragesAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var a = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var b = await ledger.AddInvestmentAccountAsync("Brokerage B");
        await ledger.AddInvestmentAccountAsync("Brokerage Closed");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(a.Id, a.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 2_000m, Utc(2024, 4, 10));
        await ledger.AddTransactionPairAsync(
            b.Id, a.Id, 3_000m, new DateTime(2024, 6, 10, 12, 0, 0, DateTimeKind.Utc));
        await ledger.AddInvestmentBuyAsync(b.Id, b.HoldingsAccountId!.Value, sec, 20m, 55m, Utc(2024, 6, 11));
        await ledger.AddTransactionPairAsync(bank.Id, a.Id, 1_500m, Utc(2024, 9, 15));
        await ledger.AddTransactionPairAsync(bank.Id, b.Id, 1_000m, Utc(2025, 3, 20));

        await ledger.AddSecurityPriceAsync(sec, 55m, Utc(2024, 3, 15));
        await ledger.AddSecurityPriceAsync(sec, 60m, Utc(2024, 6, 20));
        await ledger.AddSecurityPriceAsync(sec, 58m, Utc(2024, 10, 5));
        await ledger.AddSecurityPriceAsync(sec, 65m, Utc(2025, 2, 10));
        await ledger.AddSecurityPriceAsync(sec, 70m, Utc(2025, 7, 1));
        return new Book(ledger, bank.Id, a.Id, b.Id, sec);
    }

    private static readonly DateTime BookFrom = Utc(2024, 1, 1);
    private static readonly DateTime BookTo = Utc(2025, 9, 30);

    [Fact]
    public async Task Every_point_of_every_series_matches_the_scalar_report_for_the_same_window()
    {
        var book = await TwoBrokeragesAsync();

        var result = await SeriesAsync(book.Ledger, null, true, BookFrom, BookTo);

        // The ledger, then the two brokerages that held something, by name; the third never
        // held anything and is not a line of reasons.
        Assert.Equal(3, result.Series.Count);
        Assert.Equal("ledger", result.Series[0].Scope);
        Assert.Null(result.Series[0].AccountId);
        Assert.Equal([book.AId, book.BId], result.Series.Skip(1).Select(s => s.AccountId!.Value));
        Assert.All(result.Series.Skip(1), s => Assert.Equal("account", s.Scope));
        Assert.Equal(["Brokerage A", "Brokerage B"], result.Series.Skip(1).Select(s => s.AccountName));
        Assert.All(result.Series, s => Assert.Equal(21, s.Points.Count));
        Assert.All(result.Series, s => Assert.Equal("USD", s.CurrencyCode));

        var ledger = await AssertMatchesScalarAsync(book.Ledger, result.Series[0], BookFrom);
        var a = await AssertMatchesScalarAsync(book.Ledger, result.Series[1], BookFrom);
        var b = await AssertMatchesScalarAsync(book.Ledger, result.Series[2], BookFrom);
        Assert.Equal(21, ledger);
        Assert.Equal(21, a);
        // The second brokerage is funded in June: January to May have no index.
        Assert.Equal(16, b);

        // The last point of each series is the window's whole time-weighted growth.
        foreach (var s in result.Series)
        {
            var last = s.Points[^1];
            var scalar = await ScalarAsync(book.Ledger, s.AccountId, BookFrom, last.PeriodEnd);
            Assert.Equal(BookTo, last.PeriodEnd);
            Assert.Equal(
                scalar.TimeWeightedReturn!.Value,
                Math.Pow(last.Index!.Value / 100.0, 1.0 / scalar.TimeWeightedCoveredYears!.Value) - 1.0, 9);
        }
    }

    /// <summary>
    /// Mutation caught: the brokerages listed by id, by creation order, or by a case-sensitive
    /// name. The names are created out of order and mix cases; ids are random, so an id order
    /// agrees with the expected one only by chance (one in 120).
    /// </summary>
    [Fact]
    public async Task The_accounts_are_listed_by_name_after_the_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        foreach (var name in new[] { "charlie", "Delta", "bravo", "Alpha", "echo" })
        {
            var brokerage = await ledger.AddInvestmentAccountAsync(name);
            await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 10));
        }

        var result = await SeriesAsync(ledger, null, true, BookFrom, Utc(2024, 3, 31));

        Assert.Equal("ledger", result.Series[0].Scope);
        Assert.Equal(
            ["Alpha", "bravo", "charlie", "Delta", "echo"],
            result.Series.Skip(1).Select(s => s.AccountName));
    }

    /// <summary>
    /// Mutation caught: the bounds used as given (a reversed range yielding one period end),
    /// or a caller's now before the range not pulling the end back to the start.
    /// </summary>
    [Fact]
    public async Task A_reversed_range_is_swapped_and_a_now_before_it_ends_the_series_early()
    {
        var book = await TwoBrokeragesAsync();

        var forward = await SeriesAsync(book.Ledger, null, true, BookFrom, BookTo);
        var reversed = await SeriesAsync(book.Ledger, null, true, BookTo, BookFrom);

        Assert.Equal(forward.From, reversed.From);
        Assert.Equal(forward.To, reversed.To);
        Assert.Equal(forward.Series.Count, reversed.Series.Count);
        for (var i = 0; i < forward.Series.Count; i++)
        {
            Assert.Equal(21, reversed.Series[i].Points.Count);
            Assert.Equal(forward.Series[i].Points, reversed.Series[i].Points);
        }

        // A now inside the range ends it there.
        var midRange = await SeriesAsync(book.Ledger, null, false, BookFrom, BookTo, now: Utc(2024, 8, 15));
        Assert.Equal(Utc(2024, 8, 15), midRange.To);
        Assert.Equal(Utc(2024, 8, 15), Assert.Single(midRange.Series).Points[^1].PeriodEnd);

        // A now before it leaves the range's start as its one and only end.
        var beforeRange = await SeriesAsync(book.Ledger, null, false, Utc(2024, 6, 1), BookTo, now: Utc(2024, 3, 1));
        Assert.Equal(Utc(2024, 6, 1), beforeRange.From);
        Assert.Equal(Utc(2024, 6, 1), beforeRange.To);
        Assert.Equal([Utc(2024, 6, 1)], Assert.Single(beforeRange.Series).Points.Select(p => p.PeriodEnd));
    }

    [Fact]
    public async Task A_single_account_series_is_that_account_alone_and_matches_its_scalar()
    {
        var book = await TwoBrokeragesAsync();

        var result = await SeriesAsync(book.Ledger, book.BId, false, BookFrom, BookTo);

        var only = Assert.Single(result.Series);
        Assert.Equal("account", only.Scope);
        Assert.Equal(book.BId, only.AccountId);
        Assert.Equal("Brokerage B", only.AccountName);
        // The rollover is money in for the account whose own perimeter it crosses.
        Assert.Equal(16, await AssertMatchesScalarAsync(book.Ledger, only, BookFrom));
        Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, only.Points[0].IndexUnavailableReason);
    }

    [Fact]
    public async Task Sampling_by_year_or_quarter_or_a_shorter_range_changes_no_figure_the_months_read()
    {
        var book = await TwoBrokeragesAsync();

        var months = await SeriesAsync(book.Ledger, null, true, BookFrom, BookTo);
        var quarters = await SeriesAsync(book.Ledger, null, true, BookFrom, BookTo, ReportTimeBucket.Quarter);
        var years = await SeriesAsync(book.Ledger, null, true, BookFrom, BookTo, ReportTimeBucket.Year);
        var shorter = await SeriesAsync(book.Ledger, null, true, BookFrom, Utc(2025, 5, 15));

        void AssertSubset(ReturnsSeriesResult sampled, int expectedPoints, int sharedWithMonths)
        {
            Assert.Equal(months.Series.Count, sampled.Series.Count);
            var shared = 0;
            for (var i = 0; i < months.Series.Count; i++)
            {
                Assert.Equal(months.Series[i].AccountId, sampled.Series[i].AccountId);
                Assert.Equal(expectedPoints, sampled.Series[i].Points.Count);
                var byEnd = months.Series[i].Points.ToDictionary(p => p.PeriodEnd);
                foreach (var point in sampled.Series[i].Points)
                {
                    if (!byEnd.TryGetValue(point.PeriodEnd, out var same)) continue;
                    // The whole point, every figure and every reason, not only the index. The
                    // period return alone differs by sampling, as a month's does from a
                    // quarter's: it is the growth over the period the point ends.
                    Assert.Equal(
                        same with
                        {
                            PeriodReturn = point.PeriodReturn,
                            PeriodReturnUnavailableReason = point.PeriodReturnUnavailableReason,
                        },
                        point);
                    shared++;
                }
            }
            Assert.Equal(sharedWithMonths * months.Series.Count, shared);
        }

        // A period return is derived from the indexes the point and the one before it report,
        // so a quarter's is the growth over the quarter, not a sum of months.
        var ledgerQuarters = quarters.Series[0].Points;
        Assert.Equal(
            (ledgerQuarters[2].Index!.Value / ledgerQuarters[1].Index!.Value) - 1.0,
            ledgerQuarters[2].PeriodReturn!.Value, 12);

        AssertSubset(quarters, 7, 7);   // four quarter ends of 2024, three of 2025, the last the range's end
        AssertSubset(years, 2, 2);
        // The month series up to the shorter range's last month end, and that range's own end.
        AssertSubset(shorter, 17, 16);
    }

    [Fact]
    public async Task A_flow_exactly_on_a_period_end_lands_in_that_period()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 10));
        // Money in at the last instant of April, the period end itself.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 3_000m, EndOfDay(2024, 4, 30));

        var result = await SeriesAsync(ledger, brokerage.Id, false, BookFrom, Utc(2024, 6, 30));
        var points = Assert.Single(result.Series).Points;

        var april = points.Single(p => p.PeriodEnd == EndOfDay(2024, 4, 30));
        // The contribution is April's flow, not April's gain, and not May's: the value at the period
        // end holds it and the gain nets it out.
        Assert.Equal(0m, april.Gain);
        Assert.Equal(100.0, april.Index!.Value, 9);
        Assert.Equal(0m, points.Single(p => p.PeriodEnd == EndOfDay(2024, 5, 31)).Gain);
        Assert.Equal(6, await AssertMatchesScalarAsync(ledger, Assert.Single(result.Series), BookFrom));
    }

    [Fact]
    public async Task A_flow_at_midnight_on_the_first_lands_in_the_new_period_and_the_period_ends_value_the_last_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 10));
        // The price moves, and money arrives, on the first instant of May.
        await ledger.AddSecurityPriceAsync(sec, 60m, Utc(2024, 5, 1));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 3_000m, Utc(2024, 5, 1));

        var result = await SeriesAsync(ledger, brokerage.Id, false, BookFrom, Utc(2024, 6, 30));
        var points = Assert.Single(result.Series).Points;

        // April ends the day before the price moved and the money came: still the opening
        // 10,000, an index of 100 and no gain. A period end taken at midnight would read
        // May's price and May's flow into April.
        var april = points.Single(p => p.PeriodEnd == EndOfDay(2024, 4, 30));
        Assert.Equal(100.0, april.Index!.Value, 9);
        Assert.Equal(0m, april.Gain);

        // May holds the price move (10%) and its own flow, which is not return.
        var may = points.Single(p => p.PeriodEnd == EndOfDay(2024, 5, 31));
        Assert.Equal(110.0, may.Index!.Value, 9);
        Assert.Equal(1_000m, may.Gain);
        Assert.Equal(6, await AssertMatchesScalarAsync(ledger, Assert.Single(result.Series), BookFrom));
    }

    [Fact]
    public async Task Period_ends_are_at_the_databases_resolution_and_never_value_an_empty_portfolio()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 55m, Utc(2024, 3, 15));

        var result = await SeriesAsync(ledger, brokerage.Id, false, BookFrom, Utc(2024, 5, 31));
        var points = Assert.Single(result.Series).Points;

        // A period end keyed on a tick the column cannot hold would miss its row and read the
        // whole portfolio as nothing: an index of zero and a gain of minus the money put in.
        Assert.All(points, p => Assert.Equal(0L, p.PeriodEnd.Ticks % TimeSpan.TicksPerMicrosecond));
        Assert.Equal(
            [EndOfDay(2024, 1, 31), EndOfDay(2024, 2, 29), EndOfDay(2024, 3, 31), EndOfDay(2024, 4, 30)],
            points.Take(4).Select(p => p.PeriodEnd));
        Assert.Equal([100.0, 100.0, 105.0, 105.0], points.Take(4).Select(p => Math.Round(p.Index!.Value, 9)));
        Assert.Equal([0m, 0m, 500m, 500m], points.Take(4).Select(p => p.Gain!.Value));
        // The last period end is the range's own end.
        Assert.Equal(Utc(2024, 5, 31), points[^1].PeriodEnd);
    }

    [Fact]
    public async Task A_leg_dated_after_the_callers_today_never_enters()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 60m, Utc(2024, 6, 1));

        var today = new DateOnly(2025, 2, 15);
        async Task<ReturnsSeriesResult> ReadAsync()
        {
            await using var db = Db.NewDbContext();
            return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
                ledger.LedgerId, brokerage.Id, false, BookFrom, Utc(2025, 12, 31), ReportTimeBucket.Month, today);
        }

        var before = await ReadAsync();

        // A contribution, a price and a withdrawal, all dated after today.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Utc(2025, 3, 1));
        await ledger.AddSecurityPriceAsync(sec, 90m, Utc(2025, 3, 2));
        await ledger.AddTransactionPairAsync(bank.Id, brokerage.Id, 1_000m, Utc(2025, 3, 3));
        var after = await ReadAsync();

        // The series ends at the end of the caller's today, with 14 period ends, and none of the
        // later legs changes any figure of it.
        var points = Assert.Single(after.Series).Points;
        Assert.Equal(14, points.Count);
        Assert.Equal(ScheduledBoundary.LastCountedInstant(today), points[^1].PeriodEnd);
        Assert.Equal(Assert.Single(before.Series).Points, points);
    }

    [Fact]
    public async Task An_emptied_account_has_no_index_until_it_is_funded_again_and_then_resumes_from_its_last_value()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 110m, Utc(2024, 3, 1));
        // Everything sold and withdrawn in April.
        await ledger.AddInvestmentSellAsync(brokerage.Id, holdings, sec, 10m, 110m, Utc(2024, 4, 15));
        await ledger.AddTransactionPairAsync(bank.Id, brokerage.Id, 1_100m, Utc(2024, 4, 15));
        // Funded again in August, and the price moves 10% more by October.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_100m, Utc(2024, 8, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 110m, Utc(2024, 8, 10));
        await ledger.AddSecurityPriceAsync(sec, 121m, Utc(2024, 10, 1));

        var result = await SeriesAsync(ledger, brokerage.Id, false, BookFrom, Utc(2024, 10, 31));
        var points = Assert.Single(result.Series).Points;
        ReturnsSeriesPoint At(int month) => points[month - 1];

        Assert.Equal(100.0, At(1).Index!.Value, 9);
        Assert.Equal(110.0, At(3).Index!.Value, 9);

        // After the April withdrawal, and until the money returns, there is no base: no index,
        // no return, with the reason, and the last value is not carried over it.
        foreach (var month in new[] { 4, 5, 6, 7 })
        {
            var p = At(month);
            Assert.Null(p.Index);
            Assert.Equal(ReturnsSeriesReasons.NoInvestedBase, p.IndexUnavailableReason);
            Assert.Null(p.PeriodReturn);
            Assert.Equal(ReturnsSeriesReasons.NoInvestedBase, p.PeriodReturnUnavailableReason);
        }
        // What the account made stays made: the gain is the 100 it ended up ahead.
        Assert.Equal(100m, At(4).Gain);
        Assert.Equal(100m, At(7).Gain);
        Assert.Null(At(7).GainUnavailableReason);

        // Funded again, the index resumes from where it stopped (110), not from 100 or from zero,
        // and the period return is the growth since the last value it reported.
        Assert.Equal(110.0, At(8).Index!.Value, 9);
        Assert.Equal(0.0, At(8).PeriodReturn!.Value, 9);
        Assert.Equal(121.0, At(10).Index!.Value, 9);
        Assert.Equal(0.1, At(10).PeriodReturn!.Value, 9);

        Assert.Equal(6, await AssertMatchesScalarAsync(ledger, Assert.Single(result.Series), BookFrom));
    }

    [Fact]
    public async Task Before_the_first_funding_every_figure_is_null_with_its_reason()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 3, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 10m, 100m, Utc(2024, 3, 10));
        await ledger.AddSecurityPriceAsync(sec, 120m, Utc(2024, 4, 20));

        var result = await SeriesAsync(ledger, brokerage.Id, false, BookFrom, Utc(2024, 5, 31));
        var points = Assert.Single(result.Series).Points;

        foreach (var p in points.Take(2))
        {
            Assert.Null(p.Index);
            Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, p.IndexUnavailableReason);
            Assert.Null(p.PeriodReturn);
            Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, p.PeriodReturnUnavailableReason);
            Assert.Null(p.Gain);
            Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, p.GainUnavailableReason);
            Assert.Null(p.MwrTrailingYear);
            Assert.Equal(ReturnsSeriesReasons.UnderAYearCovered, p.MwrUnavailableReason);
            Assert.Equal(0, p.CoveredDays);
        }

        // The index starts at 100 on the first invested point, and the first period return is
        // measured from that base.
        var march = points[2];
        Assert.Equal(100.0, march.Index!.Value, 9);
        Assert.Equal(0.0, march.PeriodReturn!.Value, 9);
        Assert.Null(march.IndexUnavailableReason);
        Assert.Equal(120.0, points[3].Index!.Value, 9);
        Assert.Equal(0.2, points[3].PeriodReturn!.Value, 9);
    }

    [Fact]
    public async Task The_money_weighted_return_is_the_year_ending_at_each_point_once_a_year_opens_invested()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 60m, Utc(2024, 6, 1));

        var result = await SeriesAsync(ledger, brokerage.Id, false, BookFrom, Utc(2025, 4, 30));
        var points = Assert.Single(result.Series).Points;

        // The year ending Dec 31 2024 opened on Jan 1 2024, before the funding: nothing was
        // invested as it opened, and so for every earlier point.
        var december = points.Single(p => p.PeriodEnd == EndOfDay(2024, 12, 31));
        Assert.All(points.TakeWhile(p => p.PeriodEnd <= december.PeriodEnd), p =>
        {
            Assert.Null(p.MwrTrailingYear);
            Assert.Equal(ReturnsSeriesReasons.UnderAYearCovered, p.MwrUnavailableReason);
        });

        // The year ending Jan 31 2025 opened on Feb 1 2024 holding the uninvested cash and the
        // units at their cost, and ends with the units at the later price, with no flow between.
        var january = points.Single(p => p.PeriodEnd == EndOfDay(2025, 1, 31));
        Assert.Null(january.MwrUnavailableReason);
        var expected = ReturnsCalculator.Xirr(
        [
            new ReturnsCalculator.CashFlow(Utc(2024, 2, 1), -10_000m),
            new ReturnsCalculator.CashFlow(january.PeriodEnd, 11_000m),
        ]);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Solved, expected.Outcome);
        Assert.Equal(expected.Rate!.Value, january.MwrTrailingYear!.Value, 9);

        Assert.Equal(16, await AssertMatchesScalarAsync(ledger, Assert.Single(result.Series), BookFrom));
        Assert.All(points.Where(p => p.PeriodEnd > december.PeriodEnd), p => Assert.NotNull(p.MwrTrailingYear));
    }

    /// <summary>
    /// A funded opening balance is the brokerage's money from its start date, never before.
    /// A ledger year that closes before a second brokerage opened is the first brokerage's
    /// alone, and one it opens inside takes its balance in as money on that day; the index
    /// before that day is the first brokerage's growth, not diluted by an idle balance.
    /// Mutation caught: the balance valued as held from the year's start (the ledger's
    /// rates diluted), or every year before the opening left blank (the ledger's history lost).
    /// </summary>
    [Fact]
    public async Task A_brokerage_opened_with_a_balance_counts_from_its_start_date_in_the_ledgers_year()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var a = await ledger.AddInvestmentAccountAsync("Brokerage A");
        await ledger.AddInvestmentAccountAsync("Brokerage B", openingBalance: 5_000m, openedOn: new DateOnly(2024, 6, 1));
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 10_000m, Utc(2023, 1, 10));
        await ledger.AddInvestmentBuyAsync(a.Id, a.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2023, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 60m, Utc(2024, 3, 1));
        await ledger.AddSecurityPriceAsync(sec, 66m, Utc(2024, 9, 1));

        var series = Assert.Single((await SeriesAsync(ledger, null, false, BookFrom, Utc(2024, 12, 31))).Series);
        var points = series.Points;

        // The year ending March 2024 closes before B opened: the ledger's rate is A's own.
        var march = points.Single(p => p.PeriodEnd == EndOfDay(2024, 3, 31));
        var aYear = await ScalarAsync(ledger, a.Id, YearOpening(march.PeriodEnd), march.PeriodEnd);
        Assert.Null(march.MwrUnavailableReason);
        Assert.Equal(aYear.MoneyWeightedReturn!.Value, march.MwrTrailingYear!.Value, 9);

        // The year ending November 2024 opens with A alone (its uninvested cash and its units
        // at cost on Nov 30 2023), takes B's balance in on June 1, and ends with A's units at
        // the September price beside B.
        var november = points.Single(p => p.PeriodEnd == EndOfDay(2024, 11, 30));
        var expected = ReturnsCalculator.Xirr(
        [
            new ReturnsCalculator.CashFlow(Utc(2023, 12, 1), -10_000m),
            new ReturnsCalculator.CashFlow(Utc(2024, 6, 1), -5_000m),
            new ReturnsCalculator.CashFlow(november.PeriodEnd, 16_600m),
        ]);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Solved, expected.Outcome);
        Assert.Null(november.MwrUnavailableReason);
        Assert.Equal(expected.Rate!.Value, november.MwrTrailingYear!.Value, 9);

        // May 2024, before B opened: A's growth alone, from its funding to its cash plus the
        // units at the March price.
        var may = points.Single(p => p.PeriodEnd == EndOfDay(2024, 5, 31));
        Assert.Equal(110.0, may.Index!.Value, 9);
        Assert.Equal(1_000m, may.Gain);

        Assert.Equal(12, await AssertMatchesScalarAsync(ledger, series, BookFrom));
    }

    /// <summary>
    /// A range that starts after the money went in reads the year before it: its first point
    /// has a money-weighted return, over a year that opens before the range and holds a
    /// contribution made before the range, while its index still starts at 100 where the
    /// range opens.
    /// </summary>
    [Fact]
    public async Task A_range_after_inception_has_a_money_weighted_return_from_its_first_point()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2023, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2023, 1, 10));
        // A contribution inside the year ending at the range's first point, before the range.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 2_000m, Utc(2024, 8, 15));
        await ledger.AddSecurityPriceAsync(sec, 60m, Utc(2024, 6, 1));
        await ledger.AddSecurityPriceAsync(sec, 66m, Utc(2025, 2, 1));

        var rangeFrom = Utc(2025, 1, 1);
        var result = await SeriesAsync(ledger, brokerage.Id, false, rangeFrom, Utc(2025, 3, 31));
        var points = Assert.Single(result.Series).Points;
        Assert.Equal(3, points.Count);

        // January 2025: the year opened Feb 1 2024 holding the uninvested cash and the units at
        // cost; the second contribution came in on Aug 15; it ends holding both contributions'
        // cash and the units at the June price.
        var january = points[0];
        Assert.Equal(100.0, january.Index!.Value, 9);
        Assert.Null(january.MwrUnavailableReason);
        var expected = ReturnsCalculator.Xirr(
        [
            new ReturnsCalculator.CashFlow(Utc(2024, 2, 1), -10_000m),
            new ReturnsCalculator.CashFlow(Utc(2024, 8, 15), -2_000m),
            new ReturnsCalculator.CashFlow(january.PeriodEnd, 13_000m),
        ]);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Solved, expected.Outcome);
        Assert.Equal(expected.Rate!.Value, january.MwrTrailingYear!.Value, 9);

        Assert.All(points, p => Assert.NotNull(p.MwrTrailingYear));
        Assert.Equal(3, await AssertMatchesScalarAsync(ledger, Assert.Single(result.Series), rangeFrom));
    }

    /// <summary>A USD ledger with a USD brokerage funded in January and a EUR one funded in
    /// February, both buying the same fund.</summary>
    private async Task<(SyntheticLedger Ledger, Coffer.Api.Db.Entities.AccountRow Usd, Coffer.Api.Db.Entities.AccountRow Eur)>
        TwoCurrencyBrokeragesAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usdBank = await ledger.AddBankAccountAsync("Checking USD");
        var eurBank = await ledger.AddBankAccountAsync("Checking EUR", currencyCode: "EUR");
        var usd = await ledger.AddInvestmentAccountAsync("Brokerage USD");
        var eur = await ledger.AddInvestmentAccountAsync("Brokerage EUR", currencyCode: "EUR");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        var secEur = await ledger.AddSecurityAsync("Growth EUR", "GRWE");
        await ledger.AddTransactionPairAsync(usd.Id, usdBank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(usd.Id, usd.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(eur.Id, eurBank.Id, 8_000m, Utc(2024, 2, 10));
        await ledger.AddInvestmentBuyAsync(eur.Id, eur.HoldingsAccountId!.Value, secEur, 40m, 100m, Utc(2024, 2, 10));
        await ledger.AddSecurityPriceAsync(sec, 110m, Utc(2024, 4, 1));
        // Each fund priced in its brokerage's currency, so only the EUR brokerage's own
        // conversion into the reporting currency is in play.
        await ledger.AddSecurityPriceAsync(secEur, 110m, Utc(2024, 4, 1), currencyCode: "EUR");
        return (ledger, usd, eur);
    }

    /// <summary>EUR→USD for each day from the start of 2024 (or <paramref name="from"/>)
    /// through July, covered through the server's today, as the rate fetcher stores them
    /// (ADR-0114 D6).</summary>
    private async Task SeedEurUsdAsync(SyntheticLedger ledger, DateOnly? from = null)
    {
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var i = 0;
        for (var day = from ?? new DateOnly(2024, 1, 1); day <= new DateOnly(2024, 7, 31); day = day.AddDays(1), i++)
        {
            db.FxRates.Add(new Coffer.Api.Db.Entities.FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                RateDate = day, Rate = 1.05m + (i % 11) / 100m, Source = Coffer.Api.Db.Entities.FxRateSource.Frankfurter,
            });
        }
        db.FxPairCoverage.Add(new Coffer.Api.Db.Entities.FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
            FetchedThroughDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The ledger series of brokerages in two currencies is in the reporting currency,
    /// unhedged: each brokerage's value and flows converted at each instant before the chain
    /// runs (ADR-0114 D7), so every point is the scalar report's for the same window. Each
    /// brokerage's own series stays in its own currency.
    /// </summary>
    [Fact]
    public async Task A_ledger_whose_brokerages_span_currencies_converts_its_series_and_each_account_keeps_its_own()
    {
        var (ledger, usd, eur) = await TwoCurrencyBrokeragesAsync();
        await SeedEurUsdAsync(ledger);

        var result = await SeriesAsync(ledger, null, true, BookFrom, Utc(2024, 6, 30));

        var all = result.Series[0];
        Assert.Equal("ledger", all.Scope);
        Assert.Equal("USD", all.CurrencyCode);
        Assert.Equal(6, all.Points.Count);
        Assert.All(all.Points, p => Assert.Empty(p.RateUnavailable));
        Assert.Equal(6, await AssertMatchesScalarAsync(ledger, all, BookFrom));

        // Each brokerage is valid in its own currency, unconverted.
        Assert.Equal(3, result.Series.Count);
        var usdSeries = result.Series.Single(s => s.AccountId == usd.Id);
        var eurSeries = result.Series.Single(s => s.AccountId == eur.Id);
        Assert.Equal("USD", usdSeries.CurrencyCode);
        Assert.Equal("EUR", eurSeries.CurrencyCode);
        Assert.Equal(6, await AssertMatchesScalarAsync(ledger, usdSeries, BookFrom));
        Assert.Equal(5, await AssertMatchesScalarAsync(ledger, eurSeries, BookFrom));

        var alone = Assert.Single((await SeriesAsync(ledger, eur.Id, false, BookFrom, Utc(2024, 6, 30))).Series);
        Assert.Equal("EUR", alone.CurrencyCode);
        Assert.Equal(5, alone.Points.Count(p => p.Index is not null));
    }

    /// <summary>
    /// Fail-closed (ADR-0114 D9) for the year before a range: the EUR brokerage's February
    /// funding is a flow of the ledger's year ending in July, and no rate converts it. The
    /// July point keeps every figure of its own window, which needs only rates from June on,
    /// and its money-weighted return alone is null with the rate_unavailable reason, the
    /// point naming the pair.
    /// </summary>
    [Fact]
    public async Task A_rate_missing_only_before_the_range_leaves_only_the_money_weighted_return_null()
    {
        var (ledger, _, _) = await TwoCurrencyBrokeragesAsync();
        await SeedEurUsdAsync(ledger, from: new DateOnly(2024, 6, 1));

        var result = await SeriesAsync(ledger, null, false, Utc(2024, 7, 1), Utc(2024, 7, 31));
        var july = Assert.Single(result.Series).Points[0];

        Assert.NotNull(july.Index);
        Assert.NotNull(july.Gain);
        Assert.Null(july.IndexUnavailableReason);
        Assert.Null(july.MwrTrailingYear);
        Assert.Equal(ReturnsSeriesReasons.RateUnavailable, july.MwrUnavailableReason);
        Assert.NotEmpty(july.RateUnavailable);
        Assert.All(july.RateUnavailable, w => Assert.Equal(("EUR", "USD"), (w.FromCurrency, w.ToCurrency)));
    }

    /// <summary>
    /// Fail-closed (ADR-0114 D9): with no EUR rate, every ledger point from the EUR
    /// brokerage's first value on is null in every figure with the rate_unavailable reason
    /// and names the pair; the point before it, when the EUR brokerage held nothing, needs
    /// no rate and keeps its figures. Each brokerage's own series needs no rate at all.
    /// </summary>
    [Fact]
    public async Task A_ledger_series_with_a_missing_rate_is_null_from_the_first_point_that_needs_it()
    {
        var (ledger, usd, eur) = await TwoCurrencyBrokeragesAsync();

        var result = await SeriesAsync(ledger, null, true, BookFrom, Utc(2024, 6, 30));

        var all = result.Series[0];
        Assert.NotNull(all.Points[0].Index);   // January: the EUR brokerage held nothing
        Assert.Empty(all.Points[0].RateUnavailable);
        Assert.All(all.Points.Skip(1), p =>
        {
            Assert.Null(p.Index);
            Assert.Null(p.PeriodReturn);
            Assert.Null(p.Gain);
            Assert.Null(p.MwrTrailingYear);
            Assert.Equal(ReturnsSeriesReasons.RateUnavailable, p.IndexUnavailableReason);
            Assert.Equal(ReturnsSeriesReasons.RateUnavailable, p.PeriodReturnUnavailableReason);
            Assert.Equal(ReturnsSeriesReasons.RateUnavailable, p.GainUnavailableReason);
            Assert.Equal(ReturnsSeriesReasons.RateUnavailable, p.MwrUnavailableReason);
            Assert.NotEmpty(p.RateUnavailable);
            Assert.All(p.RateUnavailable, w => Assert.Equal(("EUR", "USD"), (w.FromCurrency, w.ToCurrency)));
        });

        Assert.Equal(6, await AssertMatchesScalarAsync(ledger, result.Series.Single(s => s.AccountId == usd.Id), BookFrom));
        Assert.Equal(5, await AssertMatchesScalarAsync(ledger, result.Series.Single(s => s.AccountId == eur.Id), BookFrom));
    }

    [Fact]
    public async Task A_ledger_whose_brokerages_share_a_currency_is_in_the_reporting_currency_whatever_its_bank_accounts()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("Checking EUR", currencyCode: "EUR");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        var usdBank = await ledger.AddBankAccountAsync("Checking USD");
        await ledger.AddTransactionPairAsync(brokerage.Id, usdBank.Id, 1_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 10m, 100m, Utc(2024, 1, 10));

        var result = await SeriesAsync(ledger, null, false, BookFrom, Utc(2024, 3, 31));

        var only = Assert.Single(result.Series);
        Assert.Equal("USD", only.CurrencyCode);
        Assert.NotNull(only.Points[0].Index);
    }

    [Fact]
    public async Task A_day_interval_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var e = await Assert.ThrowsAsync<ReportRequestException>(() =>
            SeriesAsync(ledger, null, false, BookFrom, BookTo, ReportTimeBucket.Day));

        Assert.Contains("month, quarter or year", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_600_period_ends_are_refused_and_exactly_600_are_served()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        // 2000-01 through 2049-12 is 600 month ends.
        var served = await SeriesAsync(ledger, null, false, Utc(2000, 1, 1), EndOfDay(2049, 12, 31), now: Utc(2100, 1, 1));
        Assert.Equal(600, Assert.Single(served.Series).Points.Count);

        var e = await Assert.ThrowsAsync<ReportRequestException>(() =>
            SeriesAsync(ledger, null, false, Utc(2000, 1, 1), Utc(2050, 1, 1), now: Utc(2100, 1, 1)));
        Assert.Contains("600", e.Message, StringComparison.Ordinal);
        Assert.Contains("widen the interval", e.Message, StringComparison.Ordinal);

        // The same range by year is well inside it.
        var yearly = await SeriesAsync(
            ledger, null, false, Utc(2000, 1, 1), Utc(2050, 1, 1), ReportTimeBucket.Year, Utc(2100, 1, 1));
        Assert.Equal(51, Assert.Single(yearly.Series).Points.Count);
    }

    [Fact]
    public async Task An_explicit_range_past_the_cap_is_refused_before_any_read()
    {
        // A context that cannot reach a database: any read, including opening the
        // snapshot, would throw a connection error and not the refusal.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1")
            .Options;
        await using var db = new AppDbContext(options);

        var e = await Assert.ThrowsAsync<ReportRequestException>(() =>
            new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
                Guid.NewGuid(), null, true, Utc(1900, 1, 1), Utc(2099, 12, 31),
                ReportTimeBucket.Month, Utc(2100, 1, 1)));

        Assert.Contains("600", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_that_is_not_a_brokerage_of_the_ledger_is_refused_by_every_returns_read()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddInvestmentAccountAsync("Brokerage A");
        var bank = await ledger.AddBankAccountAsync("Checking");

        foreach (var id in new[] { Guid.NewGuid(), bank.Id })
        {
            await Assert.ThrowsAsync<ReportRequestException>(() => SeriesAsync(ledger, id, false, BookFrom, BookTo));
            await Assert.ThrowsAsync<ReportRequestException>(() => ScalarAsync(ledger, id, BookFrom, BookTo));
            await using var db = Db.NewDbContext();
            await Assert.ThrowsAsync<ReportRequestException>(() =>
                new InvestmentReportingRepository(db, TimeProvider.System)
                    .ReturnsCostEstimateAsync(ledger.LedgerId, id, BookFrom, null, BookTo));
        }
    }

    [Fact]
    public async Task The_result_carries_the_reports_provenance_stamp()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var result = await SeriesAsync(ledger, null, false, BookFrom, Utc(2024, 3, 31));

        IReportProvenance stamp = result;
        Assert.False(string.IsNullOrWhiteSpace(stamp.EngineVersion));
        Assert.NotEqual(default, stamp.ComputedAt);
        Assert.Equal("month", result.Interval);
        // A ledger with no brokerage has the period ends and nothing to say about each.
        var only = Assert.Single(result.Series);
        Assert.Equal(3, only.Points.Count);
        Assert.All(only.Points, p => Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, p.IndexUnavailableReason));
    }
}
