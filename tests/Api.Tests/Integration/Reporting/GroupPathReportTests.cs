using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Every group a summary names carries its full name (ADR-0104 amendment
/// 2026-11-20): a category's path from its top-level category, so a subcategory
/// never reaches a reader as a bare leaf that two branches could both hold.
/// </summary>
/// <remarks>
/// The tree has two leaves with the same name under different parents, so a
/// path built from the wrong row, or a leaf name passed off as a path, fails.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class GroupPathReportTests : DatabaseTest
{
    public GroupPathReportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Tree(
        SyntheticLedger Ledger, AccountRow Root, AccountRow Mid, AccountRow Deep, AccountRow OtherRoot, AccountRow OtherLeaf);

    /// <summary>
    /// Category P → Category Q → Category L, and Category R → Category L: two
    /// leaves named alike. Money posts only on the two leaves.
    /// </summary>
    private async Task<Tree> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var root = await ledger.AddCategoryAsync("Category P");
        var mid = await ledger.AddCategoryAsync("Category Q", parentId: root.Id);
        var deep = await ledger.AddCategoryAsync("Category L", parentId: mid.Id);
        var otherRoot = await ledger.AddCategoryAsync("Category R");
        var otherLeaf = await ledger.AddCategoryAsync("Category L", parentId: otherRoot.Id);
        await ledger.AddTransactionPairAsync(deep.Id, bank.Id, 40m, May, payee: "Payee A");
        await ledger.AddTransactionPairAsync(otherLeaf.Id, bank.Id, 10m, May, payee: "Payee B");
        return new Tree(ledger, root, mid, deep, otherRoot, otherLeaf);
    }

    private Task<ReportResult> SummarizeAsync(
        Guid ledgerId, bool rollup, ReportGroupBy groupBy = ReportGroupBy.Category, int? topN = null) =>
        new ReportingRepository(Db.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.Month,
            GroupBy = groupBy,
            Rollup = rollup,
            TopN = topN,
        });

    [Fact]
    public async Task A_flat_row_carries_its_path_though_its_parents_have_no_row()
    {
        var t = await SeedAsync();

        var result = await SummarizeAsync(t.Ledger.LedgerId, rollup: false);

        // Flat, the parents have no postings and so no rows: the path is the only
        // place a reader can learn where the leaf sits.
        Assert.DoesNotContain(result.Rows, r => r.GroupId == t.Root.Id || r.GroupId == t.Mid.Id);
        var deep = Assert.Single(result.Rows, r => r.GroupId == t.Deep.Id);
        Assert.Equal(["Category P", "Category Q", "Category L"], deep.GroupPath);
        Assert.Equal("Category L", deep.GroupName);
        var other = Assert.Single(result.Rows, r => r.GroupId == t.OtherLeaf.Id);
        Assert.Equal(["Category R", "Category L"], other.GroupPath);
    }

    [Fact]
    public async Task A_rolled_up_row_carries_its_own_path()
    {
        var t = await SeedAsync();

        var result = await SummarizeAsync(t.Ledger.LedgerId, rollup: true);

        IReadOnlyList<string>? PathOf(AccountRow c) => Assert.Single(result.Rows, r => r.GroupId == c.Id).GroupPath;
        Assert.Equal(["Category P"], PathOf(t.Root));
        Assert.Equal(["Category P", "Category Q"], PathOf(t.Mid));
        Assert.Equal(["Category P", "Category Q", "Category L"], PathOf(t.Deep));
        Assert.Equal(["Category R"], PathOf(t.OtherRoot));
        Assert.Equal(["Category R", "Category L"], PathOf(t.OtherLeaf));
    }

    [Fact]
    public async Task Group_totals_carry_the_path()
    {
        var t = await SeedAsync();

        var result = await SummarizeAsync(t.Ledger.LedgerId, rollup: false);

        var totals = Assert.IsAssignableFrom<IReadOnlyList<ReportGroupTotal>>(result.GroupTotals);
        Assert.Equal(["Category P", "Category Q", "Category L"], Assert.Single(totals, g => g.GroupId == t.Deep.Id).GroupPath);
        Assert.Equal(["Category R", "Category L"], Assert.Single(totals, g => g.GroupId == t.OtherLeaf.Id).GroupPath);
    }

    [Fact]
    public async Task The_one_group_the_rest_folds_is_named_by_its_path()
    {
        var t = await SeedAsync();

        // Top 1 keeps the larger leaf, so the rest is the other one alone.
        var result = await SummarizeAsync(t.Ledger.LedgerId, rollup: false, topN: 1);

        var only = Assert.IsType<ReportRestGroup>(Assert.IsType<ReportRest>(result.Rest).Only);
        Assert.Equal(t.OtherLeaf.Id, only.GroupId);
        Assert.Equal(["Category R", "Category L"], only.GroupPath);
    }

    [Theory]
    [InlineData(ReportGroupBy.Payee)]
    [InlineData(ReportGroupBy.Account)]
    [InlineData(ReportGroupBy.Tag)]
    [InlineData(ReportGroupBy.CategoryKind)]
    public async Task Any_other_dimension_names_a_group_by_its_one_name(ReportGroupBy groupBy)
    {
        var t = await SeedAsync();

        var result = await SummarizeAsync(t.Ledger.LedgerId, rollup: false, groupBy: groupBy);

        Assert.NotEmpty(result.Rows);
        Assert.All(result.Rows, r => Assert.Equal([r.GroupName], r.GroupPath));
        Assert.All(result.GroupTotals!, g => Assert.Equal([g.GroupName], g.GroupPath));
    }
}
