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
/// ADR-0008 defers a <c>monthly_account_balances</c> materialized view. The
/// net-worth series is capped at 600 points because every point is aggregated
/// live — two as-of feeder calls per point. Whether the view is worth building
/// is a measurement, and the numbers below are the input to that decision.
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
public sealed class NetWorthHistoryCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public NetWorthHistoryCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private static AccountsReportingRepository Build(AppDbContext db)
    {
        var balances = new AccountBalancesRepository(db, TimeProvider.System);
        var investment = new InvestmentReportingRepository(db, TimeProvider.System);
        return new AccountsReportingRepository(
            db, balances, investment, new OverviewRepository(db, balances, investment));
    }

    [Fact]
    public async Task Net_worth_history_prints_its_per_point_cost_and_agrees_across_buckets()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = Stopwatch.StartNew();
        await StressLedger.SeedAsync(Db, ledger, StressLedger.Scale.Default);
        seeded.Stop();

        await using var db = Db.NewDbContext();
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

    /// <summary>
    /// What the home screen's net-worth reads cost: the overview with and without a
    /// <c>since</c> comparison, and the thirteen-point series its net-worth widget asks
    /// for, at four times the default bank volume, at that volume with the quiet accounts
    /// a long-lived ledger gathers (<see cref="LongLivedAccounts"/>), and at the deep
    /// investment shape.
    /// </summary>
    /// <remarks>
    /// Run with the session's <c>statement_timeout</c> at the API's command timeout, so
    /// a read that would time out in the app fails here rather than finishing slowly.
    /// The agreement checks hold on any hardware: the comparison equals the series'
    /// point at the month-end, and the series' last point equals the overview.
    /// </remarks>
    [Theory]
    [InlineData("4x-default")]
    [InlineData("4x-long-lived")]
    [InlineData("deep")]
    public async Task The_home_screen_net_worth_reads_print_their_cost(string shape)
    {
        var scale = shape == "deep"
            ? StressLedger.Scale.Deep
            : StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 };
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = await StressLedger.SeedAsync(Db, ledger, scale);
        if (shape == "4x-long-lived") await LongLivedAccounts.AddAsync(ledger);

        await using var db = Db.NewDbContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET statement_timeout = '30s'");
        var balances = new AccountBalancesRepository(db, TimeProvider.System);
        var investment = new InvestmentReportingRepository(db, TimeProvider.System);
        var overview = new OverviewRepository(db, balances, investment);
        var reporting = new AccountsReportingRepository(db, balances, investment, overview);

        // A caller's today inside the seeded span, and the month-end before it.
        var today = new DateOnly(2026, 6, 15);
        var since = new DateOnly(2026, 5, 31);
        var from = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        async Task<(T Result, double Ms)> TimeAsync<T>(Func<Task<T>> read)
        {
            await read();   // warm
            var samples = new List<double>();
            T result = default!;
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                result = await read();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            return (result, samples[1]);
        }

        var (plain, plainMs) = await TimeAsync(() => overview.GetAsync(ledger.LedgerId, today));
        var (withSince, sinceMs) = await TimeAsync(() => overview.GetAsync(ledger.LedgerId, today, since));
        var (series, seriesMs) = await TimeAsync(() =>
            reporting.NetWorthHistoryAsync(ledger.LedgerId, from, to, ReportTimeBucket.Month, today));

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"home-load {shape}: seed {seeded.TotalSeconds:F1}s; overview {plainMs:F0} ms; overview+since {sinceMs:F0} ms; net-worth series ({series.Points.Count} points) {seriesMs:F0} ms (median of 3, warm)"));

        Assert.Equal(13, series.Points.Count);
        Assert.Contains(series.Points, p => p.NetWorth != 0m);
        var comparison = withSince.Comparison!;
        Assert.False(comparison.NoHistory);
        Assert.Equal(comparison.NetWorth.Then, series.Points.Single(p => DateOnly.FromDateTime(p.AsOf) == since).NetWorth);
        Assert.Equal(plain.NetWorth, series.Points[^1].NetWorth);
    }
}
