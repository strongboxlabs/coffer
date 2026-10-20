using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Grouping by category KIND — the cash-flow dimension (FU-008 #3).
/// </summary>
/// <remarks>
/// <para>Money in against money out, per period. It needed a dimension of its
/// own because the kind was computed and then discarded: every cell already
/// carried it, since the MEASURE is an allow-list over it, but nothing could
/// group BY it. The first attempt shipped as "Cash flow" grouped by CATEGORY —
/// which is net-per-category under a familiar name — and was withdrawn.</para>
///
/// <para>The tests that matter here are about SIGNS. Income legs are stored
/// negative and expense legs positive, and <c>Net</c> negates uniformly without
/// branching on kind. A cash-flow report that inherited that would show income
/// and expenses with the same sign and be useless, or worse, plausible.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CashFlowDimensionTests
{
    private readonly PostgresFixture _fixture;

    public CashFlowDimensionTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private async Task<Guid> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var salary = await ledger.AddCategoryAsync("Salary", "income");
        var bonus = await ledger.AddCategoryAsync("Bonus", "income");
        var rent = await ledger.AddCategoryAsync("Rent", "expense");
        var food = await ledger.AddCategoryAsync("Groceries", "expense");

        // Income legs are stored NEGATIVE, expense legs positive.
        await ledger.AddTransactionPairAsync(salary.Id, bank.Id, -4_000m, May);
        await ledger.AddTransactionPairAsync(bonus.Id, bank.Id, -1_000m, May);
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 1_800m, May);
        await ledger.AddTransactionPairAsync(food.Id, bank.Id, 600m, May);
        // Next month, so a bucketed run has two columns to separate.
        await ledger.AddTransactionPairAsync(salary.Id, bank.Id, -4_000m, May.AddMonths(1));
        await ledger.AddTransactionPairAsync(rent.Id, bank.Id, 1_800m, May.AddMonths(1));
        return ledger.LedgerId;
    }

    private Task<ReportResult> RunAsync(Guid ledgerId, ReportMeasure measure,
        ReportTimeBucket bucket = ReportTimeBucket.None) =>
        new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = measure,
            TimeBucket = bucket,
            GroupBy = ReportGroupBy.CategoryKind,
        });

    [Fact]
    public async Task It_collapses_every_category_into_its_kind()
    {
        var ledgerId = await SeedAsync();

        var result = await RunAsync(ledgerId, ReportMeasure.Net);

        // Four categories, two kinds. Grouping by category would have given
        // four rows, which is the report that was withdrawn.
        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.GroupName == "income");
        Assert.Contains(result.Rows, r => r.GroupName == "expense");
    }

    [Fact]
    public async Task Income_and_expense_land_on_OPPOSITE_sides_under_Net()
    {
        // THE ASSERTION THIS DIMENSION EXISTS FOR. `Net` negates uniformly and
        // never branches on kind, which is right for a total and would be
        // useless here if it flattened the two — a cash-flow report whose two
        // rows share a sign answers nothing.
        var ledgerId = await SeedAsync();

        var result = await RunAsync(ledgerId, ReportMeasure.Net);
        var income = result.Rows.Single(r => r.GroupName == "income").Amount;
        var expense = result.Rows.Single(r => r.GroupName == "expense").Amount;

        // Every income leg over both months comes in; every expense leg goes out.
        Assert.Equal(9_000m, income);
        Assert.Equal(-4_200m, expense);
        Assert.True(income > 0 && expense < 0, "income and expenses must not share a sign");
        // And the two still sum to the report's own total, so the chart and
        // the headline figure cannot disagree.
        Assert.Equal(income + expense, result.Total);
    }

    [Fact]
    public async Task A_single_kind_measure_returns_only_that_kind()
    {
        // Spending is an allow-list over the same column the dimension groups
        // by, so the two have to agree: asking for expenses and grouping by
        // kind cannot produce an income row.
        var ledgerId = await SeedAsync();

        var result = await RunAsync(ledgerId, ReportMeasure.Spending);

        Assert.Equal("expense", Assert.Single(result.Rows).GroupName);
    }

    [Fact]
    public async Task Bucketed_it_gives_one_row_per_kind_per_period()
    {
        // The cash-flow shape proper: money in against money out, over time.
        var ledgerId = await SeedAsync();

        var result = await RunAsync(ledgerId, ReportMeasure.Net, ReportTimeBucket.Month);

        var may = result.Rows.Where(r => r.Period == "2026-05").ToList();
        var june = result.Rows.Where(r => r.Period == "2026-06").ToList();
        Assert.Equal(2, may.Count);
        Assert.Equal(2, june.Count);

        Assert.Equal(5_000m, may.Single(r => r.GroupName == "income").Amount);
        Assert.Equal(-2_400m, may.Single(r => r.GroupName == "expense").Amount);
        Assert.Equal(4_000m, june.Single(r => r.GroupName == "income").Amount);
        Assert.Equal(-1_800m, june.Single(r => r.GroupName == "expense").Amount);
    }

    [Fact]
    public async Task A_kind_carries_no_group_id_because_it_is_not_an_account()
    {
        // The client keys rows by groupId when it has one. A kind is not a row
        // in `accounts`, so it has none — and the name is the key, which is
        // also what gets rendered.
        var ledgerId = await SeedAsync();

        var result = await RunAsync(ledgerId, ReportMeasure.Net);

        Assert.All(result.Rows, r => Assert.Null(r.GroupId));
        Assert.All(result.Rows, r => Assert.Null(r.ParentId));
    }
}
