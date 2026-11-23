using System.Text.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// A returns series with no start is since inception: it begins at the scope's own anchor,
/// the one the scalar <c>returns</c> report resolves for a window with no start.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReturnsSeriesInceptionTests : DatabaseTest
{
    public ReturnsSeriesInceptionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime EndOfDay(int y, int m, int d) =>
        Utc(y, m, d).AddDays(1).AddTicks(-TimeSpan.TicksPerMicrosecond);

    private static readonly DateTime Later = Utc(2030, 1, 1);

    private async Task<(SyntheticLedger Ledger, Guid BrokerageId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        // The first money crosses the boundary in the middle of the first quarter.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 2, 14));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 100m, 50m, Utc(2024, 2, 14));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 8, 20));
        await ledger.AddSecurityPriceAsync(security, 55m, Utc(2024, 3, 15));
        await ledger.AddSecurityPriceAsync(security, 60m, Utc(2024, 9, 15));
        return (ledger, brokerage.Id);
    }

    private async Task<ReturnsSeriesResult> SeriesAsync(
        SyntheticLedger ledger, Guid? accountId, bool includeAccounts, DateTime? from, DateTime? to,
        ReportTimeBucket interval = ReportTimeBucket.Quarter)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
            ledger.LedgerId, accountId, includeAccounts, from, to, interval, Later);
    }

    private async Task<ReturnsResult> ScalarSinceInceptionAsync(SyntheticLedger ledger, Guid? accountId, DateTime end)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsAsync(
            ledger.LedgerId, accountId, fromUtc: null, toUtc: null, nowUtc: end);
    }

    [Fact]
    public async Task The_first_period_end_is_the_end_of_the_period_holding_the_anchor()
    {
        var (ledger, _) = await SeedAsync();
        var end = EndOfDay(2024, 12, 31);

        var scalar = await ScalarSinceInceptionAsync(ledger, null, end);
        var result = await SeriesAsync(ledger, null, false, null, end);

        // The scalar's own anchor: the first external flow.
        Assert.Equal(Utc(2024, 2, 14), scalar.StartDate);
        Assert.Equal(scalar.StartDate, result.From);
        Assert.Equal(end, result.To);

        var series = Assert.Single(result.Series);
        Assert.Equal(4, series.Points.Count);
        Assert.Equal(EndOfDay(2024, 3, 31), series.Points[0].PeriodEnd);
        Assert.Equal(end, series.Points[^1].PeriodEnd);
        // Funded from the anchor, so the very first point already has an index.
        Assert.NotNull(series.Points[0].Index);
    }

    [Fact]
    public async Task Since_inception_equals_a_window_opening_at_the_anchor()
    {
        var (ledger, _) = await SeedAsync();
        var end = EndOfDay(2024, 12, 31);

        var inception = await SeriesAsync(ledger, null, false, null, end);
        var explicitFrom = await SeriesAsync(ledger, null, false, Utc(2024, 2, 14), end);

        var a = Assert.Single(inception.Series).Points;
        var b = Assert.Single(explicitFrom.Series).Points;
        Assert.Equal(b.Count, a.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(b[i].PeriodEnd, a[i].PeriodEnd);
            Assert.Equal(b[i].Index, a[i].Index);
            Assert.Equal(b[i].Gain, a[i].Gain);
        }
    }

    [Fact]
    public async Task With_no_end_the_series_stops_at_the_callers_today()
    {
        var (ledger, _) = await SeedAsync();
        await using var db = Db.NewDbContext();
        var repository = new InvestmentReportingRepository(db, TimeProvider.System);

        var result = await repository.ReturnsSeriesAsync(
            ledger.LedgerId, null, false, null, null, ReportTimeBucket.Year, new DateOnly(2025, 6, 15));

        var points = Assert.Single(result.Series).Points;
        Assert.Equal([EndOfDay(2024, 12, 31), EndOfDay(2025, 6, 15)], points.Select(p => p.PeriodEnd));
    }

    [Fact]
    public async Task One_account_since_inception_anchors_on_its_own_scope()
    {
        var (ledger, brokerageId) = await SeedAsync();
        var end = EndOfDay(2024, 12, 31);

        var scalar = await ScalarSinceInceptionAsync(ledger, brokerageId, end);
        var result = await SeriesAsync(ledger, brokerageId, false, null, end);

        Assert.Equal(scalar.StartDate, result.From);
        var series = Assert.Single(result.Series);
        Assert.Equal("account", series.Scope);
        Assert.Equal(brokerageId, series.AccountId);
    }

    [Fact]
    public async Task Include_accounts_since_inception_lists_the_brokerage_beside_the_ledger()
    {
        var (ledger, brokerageId) = await SeedAsync();

        var result = await SeriesAsync(ledger, null, true, null, EndOfDay(2024, 12, 31));

        Assert.Equal(2, result.Series.Count);
        Assert.Equal("ledger", result.Series[0].Scope);
        Assert.Equal(brokerageId, result.Series[1].AccountId);
        Assert.All(result.Series, s => Assert.Equal(4, s.Points.Count));
    }

    /// <summary>The last point of a since-inception series is what the scalar report gives
    /// for the same window: gain, covered days and the time-weighted growth (annualized over
    /// the days the series reports); and, when the point has one, its money-weighted return
    /// is the scalar's for the 12 months ending there.</summary>
    private async Task AssertLastPointEqualsScalarAsync(
        SyntheticLedger ledger, ReturnsSeries series, bool expectMoneyWeighted)
    {
        var last = series.Points[^1];
        var scalar = await ScalarSinceInceptionAsync(ledger, series.AccountId, last.PeriodEnd);
        ReturnsResult year;
        await using (var db = Db.NewDbContext())
        {
            year = await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsAsync(
                ledger.LedgerId, series.AccountId,
                fromUtc: last.PeriodEnd.AddTicks(TimeSpan.TicksPerMicrosecond).AddYears(-1), toUtc: null,
                nowUtc: last.PeriodEnd);
        }

        Assert.NotNull(last.Index);
        Assert.Equal(scalar.Gain, last.Gain);
        Assert.Equal(scalar.TimeWeightedCoveredDays, last.CoveredDays);
        Assert.Equal(
            scalar.TimeWeightedReturn!.Value,
            Math.Pow(last.Index!.Value / 100.0, 1.0 / scalar.TimeWeightedCoveredYears!.Value) - 1.0, 9);
        if (expectMoneyWeighted)
        {
            Assert.NotNull(last.MwrTrailingYear);
            Assert.Equal(year.MoneyWeightedReturn!.Value, last.MwrTrailingYear!.Value, 9);
        }
        else
        {
            Assert.Null(last.MwrTrailingYear);
        }
    }

    /// <summary>
    /// Mutation caught: the opening value left out of the gain (gain = value - net
    /// contributions), counted twice in the money-weighted flows, or the series anchored
    /// after the funded opening date instead of on it.
    /// </summary>
    [Fact]
    public async Task A_funded_opening_balance_is_the_start_value_of_the_gain_and_of_the_money_weighted_return()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync(
            "Brokerage A", openingBalance: 10_000m, openedOn: new DateOnly(2023, 1, 1));
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 100m, 100m, Utc(2023, 1, 2));
        await ledger.AddSecurityPriceAsync(security, 110m, Utc(2023, 6, 1));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 2_000m, Utc(2023, 9, 15));
        await ledger.AddSecurityPriceAsync(security, 121m, Utc(2024, 3, 1));

        var result = await SeriesAsync(ledger, null, false, null, Utc(2024, 6, 30));

        // The anchor is the opening date, not the first external flow (September).
        Assert.Equal(Utc(2023, 1, 1), result.From);
        var series = Assert.Single(result.Series);
        Assert.Equal(6, series.Points.Count);

        // Value less the opening value less the contributions put in since.
        Assert.Equal([0m, 1_000m, 1_000m, 1_000m, 2_100m, 2_100m], series.Points.Select(p => p.Gain!.Value));

        // The year ending with 2023 opens on the opening date itself, valued just before it,
        // when nothing was held yet; the rate waits for a year that opens invested.
        var december = series.Points[3];
        Assert.Null(december.MwrTrailingYear);
        Assert.Equal(ReturnsSeriesReasons.UnderAYearCovered, december.MwrUnavailableReason);

        // The year ending in March 2024 opens on Apr 1 2023 holding the units at their cost;
        // the second contribution comes in during it, and it ends with the units at the
        // later price beside that contribution.
        var march = series.Points[4];
        var expected = ReturnsCalculator.Xirr(
        [
            new ReturnsCalculator.CashFlow(Utc(2023, 4, 1), -10_000m),
            new ReturnsCalculator.CashFlow(Utc(2023, 9, 15), -2_000m),
            new ReturnsCalculator.CashFlow(march.PeriodEnd, 14_100m),
        ]);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Solved, expected.Outcome);
        Assert.Equal(expected.Rate!.Value, march.MwrTrailingYear!.Value, 9);

        await AssertLastPointEqualsScalarAsync(ledger, series, expectMoneyWeighted: true);
    }

    /// <summary>
    /// Mutation caught: a zero index treated as no base (dropping the point), a period return
    /// divided by a zero index (Infinity or NaN), or the reason for it named otherwise.
    /// </summary>
    [Fact]
    public async Task A_total_loss_is_an_index_of_zero_and_the_next_period_return_has_no_base()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(security, 0m, Utc(2024, 3, 15));

        var result = await SeriesAsync(ledger, brokerage.Id, false, null, Utc(2024, 6, 30), ReportTimeBucket.Month);

        var points = Assert.Single(result.Series).Points;
        // February still holds the position at its trade price.
        Assert.Equal(100.0, points[1].Index!.Value, 9);

        // March: priced to nothing, a real zero and a return of -100% over the period.
        var march = points[2];
        Assert.Equal(0.0, march.Index!.Value);
        Assert.Equal(-1.0, march.PeriodReturn!.Value, 9);
        Assert.Equal(-1_000m, march.Gain);

        // April on: still zero, and a return from a zero index has no base.
        foreach (var p in points.Skip(3))
        {
            Assert.Equal(0.0, p.Index!.Value);
            Assert.Null(p.PeriodReturn);
            Assert.Equal(ReturnsSeriesReasons.PriorIndexZero, p.PeriodReturnUnavailableReason);
        }

        // No NaN or Infinity anywhere, so the series serialises.
        Assert.All(points, p =>
        {
            Assert.True(p.Index is null || double.IsFinite(p.Index.Value));
            Assert.True(p.PeriodReturn is null || double.IsFinite(p.PeriodReturn.Value));
            Assert.True(p.MwrTrailingYear is null || double.IsFinite(p.MwrTrailingYear.Value));
        });
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mutation caught: a cumulative factor below zero read as a value (an index of -100) or
    /// as a different reason.
    /// </summary>
    [Fact]
    public async Task Holdings_worth_less_than_nothing_have_no_index_and_say_why()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 10));
        // Bought with twice the cash it holds, so the account is overdrawn by the cash it lacks.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 20m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(security, 0m, Utc(2024, 3, 15));

        var result = await SeriesAsync(ledger, brokerage.Id, false, null, Utc(2024, 4, 30), ReportTimeBucket.Month);

        var points = Assert.Single(result.Series).Points;
        Assert.Equal(100.0, points[1].Index!.Value, 9);

        var march = points[2];
        Assert.Null(march.Index);
        Assert.Equal(ReturnsSeriesReasons.NegativeCumulativeGrowth, march.IndexUnavailableReason);
        Assert.Null(march.PeriodReturn);
        Assert.Equal(ReturnsSeriesReasons.NegativeCumulativeGrowth, march.PeriodReturnUnavailableReason);
        // The gain is still a figure: what it is worth less what went in.
        Assert.Equal(-2_000m, march.Gain);
        Assert.DoesNotContain("NaN", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// Mutation caught: a solver outcome mapped to the wrong reason (here a money-weighted
    /// solve of an opening value and a zero ending value, which has money in and none out).
    /// </summary>
    [Fact]
    public async Task A_money_weighted_solve_that_cannot_run_names_the_solvers_reason()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync(
            "Brokerage A", openingBalance: 1_000m, openedOn: new DateOnly(2023, 1, 1));
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 10m, 100m, Utc(2023, 1, 2));
        // Worthless after the year ending at the last point opens, so that year opens holding
        // the units at their cost and ends holding nothing.
        await ledger.AddSecurityPriceAsync(security, 0m, Utc(2024, 1, 15));

        var result = await SeriesAsync(ledger, brokerage.Id, false, null, Utc(2024, 6, 30), ReportTimeBucket.Year);

        var points = Assert.Single(result.Series).Points;
        Assert.Equal(2, points.Count);
        Assert.Equal(ReturnsSeriesReasons.UnderAYearCovered, points[0].MwrUnavailableReason);
        Assert.Null(points[1].MwrTrailingYear);
        Assert.Equal(ReturnsSeriesReasons.SingleSignedFlows, points[1].MwrUnavailableReason);
    }

    /// <summary>
    /// Mutation caught: a brokerage's marks before its own first funding read from the
    /// ledger's window, giving it an index or a gain there.
    /// </summary>
    [Fact]
    public async Task An_account_funded_after_the_ledger_reads_before_first_funding_for_itself_only()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var early = await ledger.AddInvestmentAccountAsync("Alpha");
        var late = await ledger.AddInvestmentAccountAsync("Beta");
        await ledger.AddTransactionPairAsync(early.Id, bank.Id, 5_000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(late.Id, bank.Id, 3_000m, Utc(2024, 6, 10));

        var result = await SeriesAsync(ledger, null, true, null, Utc(2024, 8, 31), ReportTimeBucket.Month);

        Assert.Equal(3, result.Series.Count);
        var all = result.Series[0];
        var first = result.Series[1];
        var second = result.Series[2];
        Assert.Equal("Alpha", first.AccountName);
        Assert.Equal("Beta", second.AccountName);
        Assert.Equal(8, all.Points.Count);

        // The ledger and the early account are invested from the first mark.
        Assert.All(all.Points, p => Assert.NotNull(p.Index));
        Assert.All(all.Points, p => Assert.NotNull(p.Gain));
        Assert.All(first.Points, p => Assert.NotNull(p.Index));

        // The late account has nothing to report until June, in every figure that needs a base.
        foreach (var p in second.Points.Take(5))
        {
            Assert.Null(p.Index);
            Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, p.IndexUnavailableReason);
            Assert.Null(p.PeriodReturn);
            Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, p.PeriodReturnUnavailableReason);
            Assert.Null(p.Gain);
            Assert.Equal(ReturnsSeriesReasons.BeforeFirstFunding, p.GainUnavailableReason);
            Assert.Equal(0, p.CoveredDays);
        }
        Assert.All(second.Points.Skip(5), p => Assert.Equal(100.0, p.Index!.Value, 9));
    }

    /// <summary>
    /// The 12 months ending at a point are valued at the instant before they open, as an
    /// explicit window is, and the rate is shown only when money was held there. Mutation
    /// caught: the opening valued AT the instant the year opens (so a funding on it reads as
    /// held), or the held test dropped (so a year funded inside it is annualized).
    /// </summary>
    [Theory]
    [InlineData(2025, 1, 10, false, true)]    // opens a microsecond after the funding: held, shown
    [InlineData(2025, 1, 9, true, false)]     // opens on the funding instant: valued before it, nothing held
    [InlineData(2025, 1, 8, false, false)]    // opens before the funding
    public async Task The_money_weighted_return_is_shown_when_money_was_held_as_its_year_opened(
        int year, int month, int day, bool lastInstantOfDay, bool shown)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 100m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(security, 120m, Utc(2024, 6, 1));
        var to = lastInstantOfDay ? EndOfDay(year, month, day) : Utc(year, month, day);

        var result = await SeriesAsync(ledger, brokerage.Id, false, null, to, ReportTimeBucket.Year);

        var last = Assert.Single(result.Series).Points[^1];
        Assert.Equal(to, last.PeriodEnd);
        if (shown)
        {
            Assert.NotNull(last.MwrTrailingYear);
            Assert.Null(last.MwrUnavailableReason);
            var expected = ReturnsCalculator.Xirr(
            [
                new ReturnsCalculator.CashFlow(to.AddTicks(TimeSpan.TicksPerMicrosecond).AddYears(-1), -10_000m),
                new ReturnsCalculator.CashFlow(to, 12_000m),
            ]);
            Assert.Equal(expected.Rate!.Value, last.MwrTrailingYear!.Value, 9);
        }
        else
        {
            Assert.Null(last.MwrTrailingYear);
            Assert.Equal(ReturnsSeriesReasons.UnderAYearCovered, last.MwrUnavailableReason);
        }
    }

    [Fact]
    public async Task A_scope_that_held_nothing_is_an_answer_with_no_index_and_a_reason()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddInvestmentAccountAsync("Brokerage A");

        var result = await SeriesAsync(ledger, null, true, null, EndOfDay(2024, 12, 31));

        // Not an error, and the untouched brokerage is not a line of reasons.
        var series = Assert.Single(result.Series);
        Assert.Equal("ledger", series.Scope);
        Assert.All(series.Points, p =>
        {
            Assert.Null(p.Index);
            Assert.NotNull(p.IndexUnavailableReason);
        });
    }
}
