using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Transactions dated after today (dimension 35 of the fixture audit).
/// </summary>
/// <remarks>
/// <para>Ordinary in a real ledger — a scheduled payment entered ahead, a
/// cheque written for next month — and a case the reporting layer had never
/// met, because every fixture ends in the past and every test dated its rows
/// backwards. A sliding window ends at the instant it runs, so what happens to
/// a posting beyond that end was unasserted rather than decided.</para>
///
/// <para>Driven relative to <c>UtcNow</c> rather than from a fixture: "the
/// future" is not a property a committed file can hold, because the file ages
/// and the dates stop being future.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class FutureDatedReportTests
{
    private readonly PostgresFixture _fixture;

    public FutureDatedReportTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(Guid LedgerId, Guid CategoryId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent", "expense");
        var now = DateTime.UtcNow;

        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 100m, now.AddDays(-5));
        // Next month's rent, entered ahead.
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 900m, now.AddDays(20));
        return (ledger.LedgerId, rent.Id);
    }

    private Task<ReportResult> RunAsync(ReportSpec spec) =>
        new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System).SummarizeAsync(spec);

    [Fact]
    public async Task A_sliding_window_stops_at_now_and_leaves_the_future_out()
    {
        var (ledgerId, _) = await SeedAsync();

        var result = await RunAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            DateMode = ReportDateMode.Sliding,
            WindowCount = 3,
            WindowUnit = ReportWindowUnit.Month,
        });

        // The past spend alone, not both. "Last 3 months" ends today, so next month's rent is
        // outside it — and a spending figure that silently included money not
        // yet spent would be wrong in the direction nobody checks.
        Assert.Equal(100m, result.Total);
    }

    [Fact]
    public async Task A_static_range_that_reaches_into_the_future_includes_it()
    {
        var (ledgerId, _) = await SeedAsync();
        var now = DateTime.UtcNow;

        var result = await RunAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            FromUtc = now.AddMonths(-1),
            ToUtc = now.AddMonths(2),
        });

        // Asked for explicitly, so it is included. The window decides, not the
        // calendar: excluding it here would make a stated range a suggestion.
        Assert.Equal(1000m, result.Total);
    }

    [Fact]
    public async Task The_resolved_window_a_sliding_report_reports_ends_now()
    {
        var (ledgerId, _) = await SeedAsync();
        var before = DateTime.UtcNow;

        var result = await RunAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.Month,
            DateMode = ReportDateMode.Sliding,
            WindowCount = 3,
            WindowUnit = ReportWindowUnit.Month,
            WindowAlignToPeriod = true,
        });

        // The table draws its columns from these bounds, so a window that
        // claimed to reach past now would add empty future columns to every
        // sliding report.
        Assert.NotNull(result.ToUtc);
        Assert.True(result.ToUtc >= before, "the resolved end is before the call began");
        Assert.True(result.ToUtc <= DateTime.UtcNow, "the resolved end is in the future");
        Assert.NotNull(result.FromUtc);
        Assert.True(result.FromUtc < result.ToUtc);
    }
}
