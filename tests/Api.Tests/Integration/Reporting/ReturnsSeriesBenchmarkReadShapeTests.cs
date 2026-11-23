using System.Data;
using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The SHAPE of a returns series drawn with a benchmark (ADR-0114 D3), which its cost follows:
/// one statement for the benchmark's row, coverage and first growth day, one for its growth
/// over every series' days, and its conversions inside the series' one rate load, whatever
/// the range, the interval and the number of series. No timing assertion, as for
/// <see cref="ReturnsSeriesReadShapeTests"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReturnsSeriesBenchmarkReadShapeTests : DatabaseTest
{
    public ReturnsSeriesBenchmarkReadShapeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Sent(string Sql, IsolationLevel? Isolation);

    private sealed class Recorder : DbCommandInterceptor
    {
        public List<Sent> Statements { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Statements.Add(new Sent(command.CommandText, command.Transaction?.IsolationLevel));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Two USD brokerages with a flow every month for a year, a EUR fund to compare
    /// with, its growth for four years, and EUR→USD for every day of them.</summary>
    private async Task<SyntheticLedger> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var a = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var b = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 10_000m, Utc(2024, 1, 5));
        await ledger.AddInvestmentBuyAsync(a.Id, a.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 5));
        await ledger.AddTransactionPairAsync(b.Id, bank.Id, 5_000m, Utc(2024, 1, 6));
        await ledger.AddInvestmentBuyAsync(b.Id, b.HoldingsAccountId!.Value, sec, 50m, 50m, Utc(2024, 1, 6));
        for (var month = 2; month <= 12; month++)
        {
            await ledger.AddTransactionPairAsync(a.Id, bank.Id, 100m, Utc(2024, month, 12));
            await ledger.AddTransactionPairAsync(b.Id, bank.Id, 50m, Utc(2024, month, 20));
            await ledger.AddSecurityPriceAsync(sec, 50m + month, Utc(2024, month, 25));
        }

        await BenchmarkSeed.AddBenchmarkAsync(Db, ledger, "BMKE", "EUR");
        await BenchmarkSeed.SeedGrowthAsync(
            Db, ledger, "BMKE", new DateOnly(2023, 12, 25), new DateOnly(2027, 12, 31),
            day => 1m + ((day.DayNumber % 5) - 2) * 0.001m);
        await BenchmarkSeed.SeedRatesAsync(
            Db, ledger, "EUR", "USD", new DateOnly(2023, 12, 25), new DateOnly(2027, 12, 31),
            new DateOnly(2027, 12, 31), day => 1.05m + (day.DayNumber % 13) / 100m);
        return ledger;
    }

    private async Task<(ReturnsSeriesResult Result, List<Sent> Statements)> ReadAsync(
        SyntheticLedger ledger, bool includeAccounts, ReportTimeBucket interval, DateTime to, string? benchmark)
    {
        var recorder = new Recorder();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(recorder)
            .Options);
        var result = await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
            ledger.LedgerId, null, includeAccounts, Utc(2024, 1, 1), to, interval, Utc(2030, 1, 1),
            benchmark: benchmark);
        return (result, recorder.Statements);
    }

    private static int Count(List<Sent> statements, string table) =>
        statements.Count(s => s.Sql.Contains(table, StringComparison.Ordinal));

    /// <summary>
    /// The benchmark adds two statements whatever is asked: the lookup (the only one naming
    /// <c>ledger_benchmarks</c>) and the growth read (the only other one naming
    /// <c>benchmark_growth</c>). Its rates ride the series' one rate load, which reads the
    /// pair coverage once, so a series over four years of months with a line per account sends
    /// as many rate statements as one over one year, and the whole read as many statements as
    /// the series alone plus those two.
    /// </summary>
    /// <remarks>Mutation caught: a growth read per series or per point, a second rate load for
    /// the benchmark, a statement outside the snapshot.</remarks>
    [Fact]
    public async Task The_benchmark_reads_its_growth_in_one_statement_and_rides_the_one_rate_load()
    {
        var ledger = await SeedAsync();

        var years = await ReadAsync(ledger, false, ReportTimeBucket.Year, Utc(2024, 12, 31), "BMKE");
        var months = await ReadAsync(ledger, false, ReportTimeBucket.Month, Utc(2024, 12, 31), "BMKE");
        var longMonths = await ReadAsync(ledger, false, ReportTimeBucket.Month, Utc(2027, 12, 31), "BMKE");
        var withAccounts = await ReadAsync(ledger, true, ReportTimeBucket.Month, Utc(2027, 12, 31), "BMKE");
        var plain = await ReadAsync(ledger, true, ReportTimeBucket.Month, Utc(2027, 12, 31), null);

        Assert.Equal(48, Assert.Single(longMonths.Result.Series).Points.Count);
        Assert.Equal(3, withAccounts.Result.Series.Count);
        // Something was drawn, so nothing below passes on a read that skipped the benchmark.
        Assert.All(withAccounts.Result.Series, s => Assert.NotNull(s.Points[^1].Benchmark));

        foreach (var (name, read) in new[]
                 {
                     ("years", years), ("months", months), ("long", longMonths), ("with accounts", withAccounts),
                 })
        {
            Assert.True(Count(read.Statements, "ledger_benchmarks") == 1, $"{name}: benchmark lookups");
            Assert.True(Count(read.Statements, "benchmark_growth") == 2, $"{name}: statements naming benchmark_growth");
            Assert.True(Count(read.Statements, "fx_pair_coverage") == 1, $"{name}: rate loads");
            Assert.All(read.Statements, s => Assert.Equal(IsolationLevel.RepeatableRead, s.Isolation));
        }

        // The statements do not grow with the period ends or the range.
        Assert.Equal(months.Statements.Count, years.Statements.Count);
        Assert.Equal(months.Statements.Count, longMonths.Statements.Count);
        Assert.Equal(Count(months.Statements, "fx_rates"), Count(longMonths.Statements, "fx_rates"));
        Assert.Equal(Count(months.Statements, "fx_rates"), Count(withAccounts.Statements, "fx_rates"));
        // The same series with no benchmark converts nothing, so it loads no rate; with one it
        // sends the lookup, the growth read and the one rate load, and nothing else.
        static int RateStatements(List<Sent> s) =>
            s.Count(x => x.Sql.Contains("fx_rates", StringComparison.Ordinal)
                         || x.Sql.Contains("fx_pair_coverage", StringComparison.Ordinal));
        Assert.Equal(0, RateStatements(plain.Statements));
        Assert.InRange(RateStatements(withAccounts.Statements), 1, 3);
        Assert.Equal(plain.Statements.Count + 2 + RateStatements(withAccounts.Statements), withAccounts.Statements.Count);
    }
}
