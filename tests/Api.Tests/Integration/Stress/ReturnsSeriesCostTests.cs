using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the returns series costs on the dense stress ledger and on one that has also
/// gathered the long-lived shape, read as the app reads it: the app role under row-level
/// security and the API's statement timeout.
/// </summary>
/// <remarks>
/// <para>Three shapes, as <see cref="LongLivedReadCostTests"/> takes: the default seed, four
/// times its bank volume, and that with <see cref="LongLivedAccounts"/> and
/// <see cref="LongLivedLedger"/> added. The reads are the ones a Reports page asks for: the
/// ledger's series by month, quarter and year, the same with a series per brokerage, and one
/// brokerage's own. The ledger-wide read values the portfolio once over every flow instant of
/// every brokerage and every period end, so what it costs is the feeders' pass over the
/// history plus the money-weighted solve at each period end past the first year, which the
/// series prints on its own beside the whole.</para>
/// <para>No tight timing assertion, per this lane's rule: the figures are the output. What
/// bounds a read is the app's own limit, applied to the whole of it and not only to its
/// statements. Every read runs under <c>statement_timeout</c> at the API's command timeout,
/// and under a cancellation that fires after the same time, so the money-weighted solves that
/// run in this process, which no statement timeout covers, are bounded too: a read that would
/// be abandoned in the app fails here rather than finishing slowly. Also asserted, on any
/// hardware: every read answered with an index at its last point, and that point is the scalar
/// report's growth for the same window. A read whose figure stands out is explained and fixed
/// in its own change, with a guard on its plan (<c>ReturnsSeriesReadShapeTests</c>).</para>
/// <para>The HTTP read joins <see cref="LongLivedReadCostTests"/>' list with the endpoint that
/// serves it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsSeriesCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public ReturnsSeriesCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private const string StatementTimeout = "30s";

    private static readonly DateOnly Today = LongLivedLedger.Today;

    [Theory]
    [InlineData("1x-default")]
    [InlineData("4x-default")]
    [InlineData("4x-long-lived")]
    public async Task The_returns_series_prints_its_cost(string shape)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = shape == "1x-default"
            ? StressLedger.Scale.Default
            : StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 };
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        Guid? active = null;
        if (shape == "4x-long-lived")
        {
            await LongLivedAccounts.AddAsync(ledger);
            active = (await LongLivedLedger.AddAsync(Db, ledger)).Brokerage;
        }
        var brokerage = active ?? seeded.BrokerageId;

        var appConnection = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString;
        AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(appConnection)
            .AddInterceptors(new FixedUserConnectionInterceptor(ledger.UserId))
            .Options);

        var from = new DateTime(2009, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(Today.Year, Today.Month, Today.Day, 23, 59, 59, DateTimeKind.Utc);

        async Task<ReturnsSeriesResult> ReadAsync(
            Guid? accountId, bool includeAccounts, ReportTimeBucket interval)
        {
            await using var db = NewContext();
            // The whole read, the in-process solves included, is bounded by the same
            // limit the statements are.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
                ledger.LedgerId, accountId, includeAccounts, from, to, interval, Today, limit.Token);
        }

        var figures = new List<string>();
        var failed = new List<string>();
        async Task<ReturnsSeriesResult?> TimeAsync(
            string name, Guid? accountId, bool includeAccounts, ReportTimeBucket interval)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await ReadAsync(accountId, includeAccounts, interval);   // warm
                var samples = new List<double>();
                ReturnsSeriesResult result = null!;
                for (var i = 0; i < 3; i++)
                {
                    watch.Restart();
                    result = await ReadAsync(accountId, includeAccounts, interval);
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                var lines = result.Series.Count;
                var points = result.Series[0].Points.Count;
                var withMwr = result.Series[0].Points.Count(p => p.MwrTrailingYear is not null);
                figures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name} {samples[1]:F0} ms ({lines} series x {points} points, {withMwr} with a money-weighted return)"));
                return result;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or DbUpdateException
                or OperationCanceledException)
            {
                failed.Add($"{name}: {e.Message}");
                figures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name} FAILED after {watch.Elapsed.TotalMilliseconds:F0} ms"));
                return null;
            }
        }

        var monthly = await TimeAsync("ledger.month", null, false, ReportTimeBucket.Month);
        await TimeAsync("ledger.quarter", null, false, ReportTimeBucket.Quarter);
        await TimeAsync("ledger.year", null, false, ReportTimeBucket.Year);
        await TimeAsync("ledger+accounts.month", null, true, ReportTimeBucket.Month);
        await TimeAsync("account.month", brokerage, false, ReportTimeBucket.Month);

        _output.WriteLine($"returns-series {shape}: {string.Join("; ", figures)} (median of 3, warm)");
        Assert.True(failed.Count == 0, "reads that did not answer:\n  " + string.Join("\n  ", failed));

        // Vacuity: the ledger series ends on an index, and that index is the scalar report's
        // growth for the same window, so no figure measures an empty or a broken series.
        var ledgerLine = monthly!.Series[0];
        var last = ledgerLine.Points[^1];
        Assert.NotNull(last.Index);
        await using var scalarDb = NewContext();
        var scalar = await new InvestmentReportingRepository(scalarDb, TimeProvider.System).ReturnsAsync(
            ledger.LedgerId, null, from, null, last.PeriodEnd);
        Assert.NotNull(scalar.TimeWeightedReturn);
        Assert.Equal(
            scalar.TimeWeightedReturn!.Value,
            Math.Pow(last.Index!.Value / 100.0, 1.0 / scalar.TimeWeightedCoveredYears!.Value) - 1.0,
            9);
    }
}
