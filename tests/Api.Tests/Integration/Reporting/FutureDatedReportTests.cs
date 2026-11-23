using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Transactions dated after today (dimension 35).
/// </summary>
/// <remarks>
/// <para>Ordinary in a real ledger — a scheduled payment entered ahead, a
/// cheque written for next month — and invisible to fixtures that end in the
/// past and tests that date their rows backwards. A sliding window ends at the
/// instant it runs, so what happens to a posting beyond that end has to be
/// decided and asserted.</para>
///
/// <para>Driven relative to <c>UtcNow</c> rather than from a fixture: "the
/// future" is not a property a committed file can hold, because the file ages
/// and the dates stop being future.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class FutureDatedReportTests : DatabaseTest
{
    public FutureDatedReportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private async Task<(Guid LedgerId, Guid CategoryId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent", "expense");
        var now = DateTime.UtcNow;

        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 100m, now.AddDays(-5));
        // Next month's rent, entered ahead.
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 900m, now.AddDays(20));
        return (ledger.LedgerId, rent.Id);
    }

    private Task<ReportResult> RunAsync(ReportSpec spec) =>
        new ReportingRepository(Db.NewDbContext(), TimeProvider.System).SummarizeAsync(spec);

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
    public async Task With_no_today_the_resolved_window_ends_at_the_clocks_now()
    {
        // MCP sends no date and keeps the clock's instant.
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

    // ---- with the caller's today: the register's boundary, not the clock's instant ----

    private static DateTime Day(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static ReportSpec Sliding(Guid ledgerId, int count, ReportWindowUnit unit) => new()
    {
        LedgerId = ledgerId,
        Measure = ReportMeasure.Spending,
        TimeBucket = unit == ReportWindowUnit.Day ? ReportTimeBucket.Day : ReportTimeBucket.Month,
        DateMode = ReportDateMode.Sliding,
        WindowCount = count,
        WindowUnit = unit,
        WindowAlignToPeriod = true,
    };

    private Task<ReportResult> RunAsync(ReportSpec spec, DateOnly today) =>
        new ReportingRepository(Db.NewDbContext(), TimeProvider.System).SummarizeAsync(spec, today);

    [Fact]
    public async Task With_the_callers_today_a_window_ends_where_the_registers_scheduled_begins()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent", "expense");
        var today = new DateOnly(2026, 3, 12);

        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 100m, Day(2026, 3, 2));
        // Later today: not Scheduled, so counted. Tomorrow: Scheduled, so not.
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 40m, Day(2026, 3, 12).AddHours(15));
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 900m, Day(2026, 3, 13));

        var result = await RunAsync(Sliding(ledger.LedgerId, 3, ReportWindowUnit.Month), today);

        Assert.Equal(ScheduledBoundary.ScheduledFrom(today, TimeProvider.System), result.ToUtc);
        Assert.Equal(Day(2026, 1, 1), result.FromUtc);
        Assert.Equal(140m, result.Total);
    }

    [Fact]
    public async Task On_a_months_last_day_an_aligned_window_still_ends_with_that_month()
    {
        // The window ends at the next month's first instant, but it snaps from the
        // last instant before it: snapping from the boundary would make it a month
        // late, April through June.
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var result = await RunAsync(Sliding(ledger.LedgerId, 3, ReportWindowUnit.Month), new DateOnly(2026, 3, 31));

        Assert.Equal(Day(2026, 1, 1), result.FromUtc);
        Assert.Equal(Day(2026, 4, 1), result.ToUtc);
        Assert.Equal(["2026-01", "2026-02", "2026-03"], result.PeriodTotals!.Select(p => p.Period).ToArray());
    }

    [Fact]
    public async Task A_daily_aligned_window_counts_today_as_its_last_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var result = await RunAsync(Sliding(ledger.LedgerId, 7, ReportWindowUnit.Day), new DateOnly(2026, 3, 12));

        Assert.Equal(Day(2026, 3, 6), result.FromUtc);
        Assert.Equal(Day(2026, 3, 13), result.ToUtc);
        Assert.Equal(7, result.PeriodTotals!.Count);
        Assert.Equal("2026-03-12", result.PeriodTotals[^1].Period);
    }
}
