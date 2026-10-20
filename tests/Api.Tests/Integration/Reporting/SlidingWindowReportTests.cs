using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// A sliding date range actually filters (ADR-0104 D5).
/// </summary>
/// <remarks>
/// <see cref="ReportSpecWindowTests"/> proves the arithmetic; this proves it is
/// WIRED. A resolver that computes the right bounds and is never consulted
/// passes every unit test and ships a report that silently ignores its own date
/// range — the shape of defect that reads as green all the way to production.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SlidingWindowReportTests
{
    private readonly PostgresFixture _fixture;

    public SlidingWindowReportTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_sliding_window_excludes_spending_older_than_itself()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");

        // Dated relative to now, because the window is too: a fixed calendar
        // date here would drift out of the window as the suite ages and the
        // test would start failing on a date rather than on a change.
        var now = DateTime.UtcNow;
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 30m, now.AddMonths(-1));
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 500m, now.AddMonths(-10));

        var repo = new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System);

        var sliding = await repo.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            DateMode = ReportDateMode.Sliding,
            WindowCount = 3,
            WindowUnit = ReportWindowUnit.Month,
        });

        // The recent spend alone, not both: the ten-month-old spend is outside a
        // three-month window.
        Assert.Equal(30m, sliding.Total);

        // And the same ledger with no window still sees everything, so the
        // first assertion is about the window rather than about the seeding.
        var everything = await repo.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
        });
        Assert.Equal(530m, everything.Total);
    }
}
