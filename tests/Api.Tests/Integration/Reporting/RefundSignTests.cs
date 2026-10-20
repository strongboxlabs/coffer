using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// A refund: a NEGATIVE amount posted to an expense category.
/// </summary>
/// <remarks>
/// <para>Dimension 7 of the fixture audit, and the one it ranks first. Income
/// and expense are properties of the CATEGORY, not of the posting, and either
/// kind can carry either sign — a refund to an expense category is negative, a
/// reversal of income is positive. Nothing in the reporting layer branches on
/// sign, and <c>Net</c> does not branch on kind at all; the familiar "income
/// up, costs down" reading is how those postings usually sum, not a rule the
/// report applies.</para>
///
/// <para>That paragraph was stated WRONGLY in review — as though the sign
/// followed from the kind — and no test contradicted it, because none existed.
/// These pin the behaviour so the next person reasoning about signs has
/// something that fails when they are wrong.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RefundSignTests
{
    private readonly PostgresFixture _fixture;

    public RefundSignTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private Task<ReportResult> SummarizeAsync(Guid ledgerId, ReportMeasure measure, bool rollup = false) =>
        new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = measure,
            TimeBucket = ReportTimeBucket.None,
            GroupBy = ReportGroupBy.Category,
            Rollup = rollup,
        });

    [Fact]
    public async Task A_refund_reduces_that_category_rather_than_adding_to_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var clothing = await ledger.AddCategoryAsync("Clothing", "expense");

        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, 100m, May);
        // Returned half of it.
        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, -40m, May);

        var result = await SummarizeAsync(ledger.LedgerId, ReportMeasure.Spending);

        // The purchase less the refund, not the two added together. Taking the
        // magnitude of each posting — the shape of
        // "expenses are positive" as a rule rather than an observation — would
        // report the refund as more spending.
        Assert.Equal(60m, result.Rows.Single(r => r.GroupName == "Clothing").Amount);
        Assert.Equal(60m, result.Total);
    }

    [Fact]
    public async Task A_category_refunded_past_its_spending_goes_negative()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var clothing = await ledger.AddCategoryAsync("Clothing", "expense");

        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, 20m, May);
        // A return of something bought BEFORE the window — perfectly ordinary,
        // and the case where "expenses are positive" stops being true at the
        // row level rather than just the posting level.
        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, -50m, May);

        var result = await SummarizeAsync(ledger.LedgerId, ReportMeasure.Spending);

        Assert.Equal(-30m, result.Rows.Single(r => r.GroupName == "Clothing").Amount);
    }

    [Fact]
    public async Task A_refund_in_a_later_month_leaves_that_month_negative()
    {
        // THE DISCRIMINATING CASE, and the realistic one: a return happens
        // after the purchase, usually in the next month. Netting happens
        // inside a (period, category) cell before the measure is applied, so a
        // purchase and its refund in the SAME month collapse to one positive
        // figure and a bug that took magnitudes would be invisible. Split
        // across months there are two cells, and the second is negative.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var clothing = await ledger.AddCategoryAsync("Clothing", "expense");

        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, 100m, May);
        await ledger.AddTransactionPairAsync(
            clothing.Id, bank.Id, -40m, May.AddMonths(1));

        var result = await new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.Month,
            GroupBy = ReportGroupBy.Category,
        });

        var byPeriod = result.Rows
            .Where(r => r.GroupName == "Clothing")
            .ToDictionary(r => r.Period!, r => r.Amount);

        Assert.Equal(100m, byPeriod["2026-05"]);
        Assert.Equal(-40m, byPeriod["2026-06"]);
        // And the range total is still the net of the two.
        Assert.Equal(60m, result.Total);
    }

    [Fact]
    public async Task Under_Both_a_refund_raises_net_instead_of_deepening_a_cost()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var salary = await ledger.AddCategoryAsync("Salary", "income");
        var clothing = await ledger.AddCategoryAsync("Clothing", "expense");

        await ledger.AddTransactionPairAsync(salary.Id, bank.Id, -1000m, May);
        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, 100m, May);

        var before = await SummarizeAsync(ledger.LedgerId, ReportMeasure.Net);

        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, -40m, May);
        var after = await SummarizeAsync(ledger.LedgerId, ReportMeasure.Net);

        // Net is income − expenses, so returning something IMPROVES it. The
        // measure negates uniformly and never looks at the category's kind,
        // which is exactly why this comes out right.
        Assert.Equal(before.Total + 40m, after.Total);
        Assert.Equal(900m, before.Total);
        Assert.Equal(940m, after.Total);
    }

    [Fact]
    public async Task A_refund_rolls_up_into_its_parent_as_a_reduction()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var personal = await ledger.AddCategoryAsync("Personal", "expense");
        var clothing = await ledger.AddCategoryAsync("Clothing", "expense", personal.Id);

        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, 100m, May);
        await ledger.AddTransactionPairAsync(clothing.Id, bank.Id, -40m, May);

        var result = await SummarizeAsync(ledger.LedgerId, ReportMeasure.Spending, rollup: true);

        // The parent inherits the NET of its child, not the gross. A roll-up
        // that summed magnitudes would make a parent larger than the money
        // that ever moved through it.
        Assert.Equal(60m, result.Rows.Single(r => r.GroupName == "Personal").Amount);
        Assert.Equal(60m, result.Rows.Single(r => r.GroupName == "Clothing").Amount);
    }
}
