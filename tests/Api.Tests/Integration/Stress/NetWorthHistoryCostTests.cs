using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What a net-worth history costs at ledger scale, and whether its buckets agree
/// with each other.
/// </summary>
/// <remarks>
/// <para>
/// This exists to answer one scheduled question and then be kept. ADR-0008
/// deferred a <c>monthly_account_balances</c> materialized view on the grounds
/// that the feature it would serve did not exist yet; that feature shipped, and
/// the series had to be capped at 600 points precisely because every point is
/// aggregated live — two as-of feeder calls per point. Whether the view is worth
/// building is a measurement nobody had taken, so the deferral has been
/// re-argued from the same unmeasured premise ever since. The numbers below are
/// the input to that decision.
/// </para>
/// <para>
/// <b>No timing assertion</b>, per this lane's standing rule: it runs on whatever
/// hardware invokes it, so a bound would either be so loose it catches nothing or
/// so tight it fails on a laptop. The printed figures are the output. What IS
/// asserted is agreement between buckets — a property that holds on any hardware
/// and would catch the bucketing error a rewrite onto a materialized view could
/// plausibly introduce.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NetWorthHistoryCostTests
{
    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NetWorthHistoryCostTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static AccountsReportingRepository Build(AppDbContext db)
    {
        var balances = new AccountBalancesRepository(db);
        var investment = new InvestmentReportingRepository(db);
        return new AccountsReportingRepository(
            db, balances, investment, new OverviewRepository(db, balances, investment));
    }

    [Fact]
    public async Task Net_worth_history_prints_its_per_point_cost_and_agrees_across_buckets()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var seeded = Stopwatch.StartNew();
        await StressLedger.SeedAsync(_fixture, ledger, StressLedger.Scale.Default);
        seeded.Stop();

        await using var db = _fixture.NewDbContext();
        // Npgsql's 30s default would cut off the thing being measured.
        db.Database.SetCommandTimeout(600);
        var repository = Build(db);

        // The seeded ledger spans roughly 2010 -> 2033; ask for the whole of it so
        // the month series lands near the 600-point cap rather than in a corner
        // where per-point cost is dominated by fixed setup.
        var from = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2034, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var monthly = Stopwatch.StartNew();
        var months = await repository.NetWorthHistoryAsync(
            ledger.LedgerId, from, to, ReportTimeBucket.Month);
        monthly.Stop();

        var yearly = Stopwatch.StartNew();
        var years = await repository.NetWorthHistoryAsync(
            ledger.LedgerId, from, to, ReportTimeBucket.Year);
        yearly.Stop();

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seed:    {seeded.Elapsed.TotalSeconds,7:F1}s"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"monthly: {monthly.Elapsed.TotalSeconds,7:F1}s over {months.Points.Count,4} points"
            + $"  = {monthly.Elapsed.TotalMilliseconds / Math.Max(1, months.Points.Count),8:F1} ms/point"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"yearly:  {yearly.Elapsed.TotalSeconds,7:F1}s over {years.Points.Count,4} points"
            + $"  = {yearly.Elapsed.TotalMilliseconds / Math.Max(1, years.Points.Count),8:F1} ms/point"));

        // Vacuity guards. A series of zeros would satisfy every agreement check
        // below while measuring nothing, and that is exactly how a stress test
        // ends up certifying an empty ledger.
        Assert.True(months.Points.Count > 250,
            $"expected a long monthly series from the seeded horizon, got {months.Points.Count}");
        Assert.True(years.Points.Count > 20,
            $"expected a multi-decade yearly series, got {years.Points.Count}");
        Assert.Contains(months.Points, p => p.NetWorth != 0m);

        // The real assertion: a year-end point and the month-end point for the
        // same instant are the same question, so they must return the same
        // answer. Independent of hardware, and it is the property a rewrite onto
        // a pre-aggregated monthly table is most likely to break.
        var monthByDate = months.Points.ToDictionary(p => p.AsOf);
        var compared = 0;
        foreach (var yearPoint in years.Points)
        {
            if (!monthByDate.TryGetValue(yearPoint.AsOf, out var monthPoint)) continue;
            Assert.Equal(monthPoint.NetWorth, yearPoint.NetWorth);
            compared++;
        }

        // Without this the loop above could compare nothing and pass — the same
        // shape as an idempotency test that never looks at the leftovers.
        Assert.True(compared > 20,
            $"only {compared} year-ends lined up with a month-end; the agreement "
            + "check compared almost nothing and would pass against a broken series");
    }
}
