using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// A category tree three levels deep, through the real aggregation.
/// </summary>
/// <remarks>
/// <para>The failure this pins: a depth error renders unrelated categories
/// indented beneath whichever parent happens to be last.</para>
///
/// <para>The demo sample's category tree IS the starter taxonomy (ADR-0091), and
/// since the amendment of 2026-11-18 it carries one three-level branch. That
/// branch shows the shape in the sample, but this test does not lean on it: the
/// starter tree is capped at three levels and its deep branch is product
/// content that may change, while what is under test is the aggregation. So the
/// tree is built here, to the depth the test needs.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CategoryDepthReportTests : DatabaseTest
{
    public CategoryDepthReportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Rollup_carries_a_grandchild_into_every_ancestor()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");

        // Personal → Travel → Hotels. Three levels, built here rather than
        // borrowed from the starter taxonomy (see the remarks).
        var personal = await ledger.AddCategoryAsync("Personal", "expense");
        var travel = await ledger.AddCategoryAsync("Travel", "expense", personal.Id);
        var hotels = await ledger.AddCategoryAsync("Hotels", "expense", travel.Id);

        // Spend ONLY on the leaf, so every ancestor's figure can come from
        // nowhere but the roll-up.
        await ledger.AddTransactionPairAsync(hotels.Id, bank.Id, 200m, May);

        var result = await new ReportingRepository(Db.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            GroupBy = ReportGroupBy.Category,
            Rollup = true,
        });

        decimal AmountOf(string name) =>
            result.Rows.Single(r => r.GroupName == name).Amount!.Value;

        // The grandparent gets it too. A roll-up that stopped at one level
        // would leave Personal absent or at zero, and the table would show
        // money under a parent that does not account for it.
        Assert.Equal(200m, AmountOf("Hotels"));
        Assert.Equal(200m, AmountOf("Travel"));
        Assert.Equal(200m, AmountOf("Personal"));

        // The total is the true sum of postings, NOT the sum of the rows:
        // three rows carrying the same figure describe one posting.
        Assert.Equal(200m, result.Total);
    }

    [Fact]
    public async Task The_rows_carry_the_parent_chain_the_table_nests_by()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var personal = await ledger.AddCategoryAsync("Personal", "expense");
        var travel = await ledger.AddCategoryAsync("Travel", "expense", personal.Id);
        var hotels = await ledger.AddCategoryAsync("Hotels", "expense", travel.Id);
        await ledger.AddTransactionPairAsync(hotels.Id, bank.Id, 50m, May);

        var result = await new ReportingRepository(Db.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            GroupBy = ReportGroupBy.Category,
            Rollup = true,
        });

        ReportRow Row(string name) => result.Rows.Single(r => r.GroupName == name);

        // The SPA nests from these, so a chain that arrives flat renders flat
        // — the other half of the depth failure.
        Assert.Null(Row("Personal").ParentId);
        Assert.Equal(personal.Id, Row("Travel").ParentId);
        Assert.Equal(travel.Id, Row("Hotels").ParentId);
    }
}
