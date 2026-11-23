using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the returns series, the scalar returns and the holdings snapshot cost when every
/// brokerage converts into the reporting currency (ADR-0114 D7), on the realistic stress
/// ledger and on one that has also gathered the sparse long-lived shape, read as the app
/// reads them: the app role under row-level security and the API's statement timeout.
/// </summary>
/// <remarks>
/// <para>Two modes per shape. <c>identity</c> keeps the ledger in USD, the currency every
/// seeded brokerage is in, so no rate is read: the single-currency path. <c>converted</c>
/// puts the ledger in EUR and stores a USD-to-EUR rate for every day of the seeded span,
/// covered through the server's today, so every brokerage's every value and flow converts at
/// its own day: the most rate work a read can do on these shapes.</para>
/// <para>No timing assertion, per this lane's rule: the figures are the output. Every read
/// runs under <c>statement_timeout</c> at the API's command timeout and a cancellation of the
/// same length, so a read the app would abandon fails here. Also asserted on any hardware:
/// the ledger series ends on an index, and the converted snapshot has a total, so no figure
/// measures a read that answered nothing.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentConversionCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public InvestmentConversionCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private const string StatementTimeout = "30s";

    private static readonly DateOnly Today = LongLivedLedger.Today;

    [Theory]
    [InlineData("4x-default", false)]
    [InlineData("4x-default", true)]
    [InlineData("4x-long-lived", false)]
    [InlineData("4x-long-lived", true)]
    public async Task The_converted_investment_reads_print_their_cost(string shape, bool converted)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: converted ? "EUR" : "USD");
        var scale = StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 };
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        Guid? active = null;
        if (shape == "4x-long-lived")
        {
            await LongLivedAccounts.AddAsync(ledger);
            active = (await LongLivedLedger.AddAsync(Db, ledger)).Brokerage;
        }
        var brokerage = active ?? seeded.BrokerageId;
        if (converted) await SeedDailyRatesAsync(ledger, "USD", "EUR");

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

        var figures = new List<string>();
        var failed = new List<string>();
        async Task<T?> TimeAsync<T>(string name, Func<InvestmentReportingRepository, CancellationToken, Task<T>> read)
            where T : class
        {
            async Task<T> Once()
            {
                await using var db = NewContext();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                return await read(new InvestmentReportingRepository(db, TimeProvider.System), limit.Token);
            }

            var watch = Stopwatch.StartNew();
            try
            {
                await Once();   // warm
                var samples = new List<double>();
                T result = null!;
                for (var i = 0; i < 3; i++)
                {
                    watch.Restart();
                    result = await Once();
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                figures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {samples[1]:F0} ms"));
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

        var monthly = await TimeAsync("series.ledger.month", (r, ct) =>
            r.ReturnsSeriesAsync(ledger.LedgerId, null, false, from, to, ReportTimeBucket.Month, Today, ct));
        await TimeAsync("series.ledger+accounts.month", (r, ct) =>
            r.ReturnsSeriesAsync(ledger.LedgerId, null, true, from, to, ReportTimeBucket.Month, Today, ct));
        await TimeAsync("series.account.month", (r, ct) =>
            r.ReturnsSeriesAsync(ledger.LedgerId, brokerage, false, from, to, ReportTimeBucket.Month, Today, ct));
        await TimeAsync("returns.ledger", (r, ct) =>
            r.ReturnsAsync(ledger.LedgerId, null, from, null, Today, ct));
        var snapshot = await TimeAsync("holdings.ledger", (r, ct) =>
            r.HoldingsSnapshotAsync(ledger.LedgerId, null, null, ct));
        await TimeAsync("holdings.ledger.past", (r, ct) =>
            r.HoldingsSnapshotAsync(ledger.LedgerId, null, new DateTime(2020, 6, 30, 23, 59, 59, DateTimeKind.Utc), ct));
        await TimeAsync("holdings.account", (r, ct) =>
            r.HoldingsSnapshotAsync(ledger.LedgerId, brokerage, null, ct));

        _output.WriteLine(
            $"investment-conversion {shape} {(converted ? "converted" : "identity")}: {string.Join("; ", figures)} (median of 3, warm)");
        Assert.True(failed.Count == 0, "reads that did not answer:\n  " + string.Join("\n  ", failed));

        // Vacuity: the ledger series ends on an index and the snapshot holds positions, so
        // no figure measures an empty read.
        Assert.NotNull(monthly!.Series[0].Points[^1].Index);
        Assert.NotEmpty(snapshot!.Holdings);
    }

    /// <summary>A rate for every day from the seeded span's start through the server's UTC
    /// today, with coverage through today, as the rate fetcher stores them (ADR-0114 D6).</summary>
    private async Task SeedDailyRatesAsync(SyntheticLedger ledger, string from, string to)
    {
        var serverToday = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var rows = new List<FxRateRow>();
        var i = 0;
        for (var day = new DateOnly(2008, 1, 1); day <= serverToday; day = day.AddDays(1), i++)
        {
            rows.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = to,
                RateDate = day, Rate = 0.85m + (i % 97) / 1000m, Source = FxRateSource.Frankfurter,
            });
        }
        db.FxRates.AddRange(rows);
        db.FxPairCoverage.Add(new FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = to, FetchedThroughDate = serverToday,
        });
        await db.SaveChangesAsync();
    }
}
