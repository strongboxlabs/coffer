using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.Transactions;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// At scale, the planner reads a category page's foreign legs through the category's
/// account index, not a scan of the ledger's legs. The every-build test
/// (<c>CategoryRegisterConversionTests.The_foreign_leg_read_can_reach_the_category_through_its_account_index</c>)
/// holds that the statement can; whether the planner chooses to follows the table's size
/// and statistics, so it is checked here, on a seeded ledger planned on fresh statistics.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CategoryRegisterForeignLegPlanTests : DatabaseTest
{
    public CategoryRegisterForeignLegPlanTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task At_scale_the_foreign_leg_read_reaches_the_category_through_its_account_index()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await StressLedger.SeedLedgerAsync(Db, ledger, StressLedger.Scale.Smoke);
        var category = StressLedger.CategoryId(ledger.LedgerId, 1);
        await CategoryRegisterConversionTests.SeedForeignLegsAsync(Db, ledger, category);
        await using (var conn = Db.OpenServiceConnection())
        await using (var analyze = new Npgsql.NpgsqlCommand(
                         "ANALYZE txn_legs; ANALYZE txn_headers; ANALYZE accounts; ANALYZE txn_header_account_balances", conn))
            await analyze.ExecuteNonQueryAsync();

        var plan = await CategoryRegisterConversionTests.ForeignLegPlanAsync(Db, ledger, category);
        CategoryRegisterConversionTests.AssertReachesTheCategoryThroughItsAccountIndex(plan, Output);
    }
}
