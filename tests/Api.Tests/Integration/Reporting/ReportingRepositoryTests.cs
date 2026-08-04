using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The reporting aggregation (ADR-0063) over the override-aware
/// <c>resolved_transactions</c> view: income/expense-by-category-over-time with
/// top-N, and the sign convention (expense + , income −, normalized to positive
/// magnitudes). Seeds expense legs positive / income legs negative via the
/// balance-consistent pairing (cash leg signed so the account balance moves the
/// right way), matching the symmetric-posting model.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReportingRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public ReportingRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private async Task<(Guid Ledger, Guid Groceries, Guid Rent, Guid Salary)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var rent = await ledger.AddCategoryAsync("Rent", "expense");
        var salary = await ledger.AddCategoryAsync("Salary", "income");

        // Expense: money leaves the bank → bank leg negative, category leg positive.
        // The helper signs the "from" leg +amount, "to" leg −amount, so passing the
        // category as "from" yields the balance-correct (cat +, bank −).
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 100m, May);
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 50m, May);
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 1000m, May);
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 200m, Jun);
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 1000m, Jun);
        // Income: money enters the bank → bank leg positive, income category negative.
        await ledger.AddTransactionPairAsync(bank.Id, salary.Id, 5000m, May);

        return (ledger.LedgerId, groceries.Id, rent.Id, salary.Id);
    }

    private ReportingRepository NewRepo() => new(_fixture.NewDbContext());

    [Fact]
    public async Task Spending_by_month_and_category_sums_positive()
    {
        var (ledger, groceries, rent, _) = await SeedAsync();

        var result = await NewRepo().SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.Month,
        });

        // 2 categories × 2 months = 4 cells, all positive magnitudes.
        Assert.Equal(150m, Cell(result, "2026-05", groceries));
        Assert.Equal(1000m, Cell(result, "2026-05", rent));
        Assert.Equal(200m, Cell(result, "2026-06", groceries));
        Assert.Equal(1000m, Cell(result, "2026-06", rent));
        Assert.Equal(2350m, result.Total);
        Assert.All(result.Rows, r => Assert.True(r.Amount > 0));
    }

    [Fact]
    public async Task Top_n_keeps_only_the_biggest_categories()
    {
        var (ledger, _, rent, _) = await SeedAsync();

        var result = await NewRepo().SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            TopN = 1,   // Rent (2000) beats Groceries (350)
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal(rent, row.GroupId);
        Assert.Equal(2000m, row.Amount);
    }

    [Fact]
    public async Task Income_is_normalized_to_a_positive_magnitude()
    {
        var (ledger, _, _, salary) = await SeedAsync();

        var result = await NewRepo().SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger,
            Measure = ReportMeasure.Income,
            TimeBucket = ReportTimeBucket.None,
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal(salary, row.GroupId);
        Assert.Equal(5000m, row.Amount);
    }

    [Fact]
    public async Task Date_range_filters_the_window()
    {
        var (ledger, _, _, _) = await SeedAsync();

        var juneOnly = await NewRepo().SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            FromUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(1200m, juneOnly.Total);   // Jun: Groceries 200 + Rent 1000
    }

    private static decimal Cell(ReportResult r, string period, Guid categoryId) =>
        r.Rows.Single(x => x.Period == period && x.GroupId == categoryId).Amount;
}
