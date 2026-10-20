using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>ReportGroupBy.Tag</c> — the dimension Tag Summary needs (FU-008,
/// ADR-0104), and the one that had to answer a question ADR-0103 created:
/// tags exist at header AND posting level, so a grouping must say which it
/// means.
/// </summary>
/// <remarks>
/// <para>It means both, like the tag FILTER does — but it attributes per
/// POSTING, which the filter deliberately does not. The filter asks "does any
/// leg of this header carry the tag", because someone asking where "Property
/// B" money went does not care which level recorded it. A grouping cannot
/// borrow that: charging a posting's amount to a tag belonging to a SIBLING
/// posting is exactly the confusion ADR-0103 split the levels to prevent.</para>
/// <para>These also guard against LINQ-translation regressions, which a build
/// cannot catch: the union and the posting-pair join either translate or throw
/// at first execution.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TagDimensionTests
{
    private readonly PostgresFixture _fixture;

    public TagDimensionTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private Task<ReportResult> SummarizeByTagAsync(Guid ledgerId) =>
        new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            GroupBy = ReportGroupBy.Tag,
        });

    [Fact]
    public async Task Header_tags_group_and_untagged_spending_keeps_its_own_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");

        var tagged = await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 100m, May);
        await ledger.AddTagAsync(tagged.FromTxnId, "Holiday");
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 40m, May);

        var rows = (await SummarizeByTagAsync(ledger.LedgerId)).Rows;

        Assert.Equal(100m, Assert.Single(rows, r => r.GroupName == "Holiday").Amount);
        // Untagged money is a row, not a silent omission. Without it this
        // report's total disagrees with the same period's spending report and
        // the reader cannot tell that the gap is "never tagged" from a bug.
        Assert.Equal(40m, Assert.Single(rows, r => r.GroupName == "(untagged)").Amount);
    }

    [Fact]
    public async Task A_leg_tag_charges_its_own_posting_and_not_its_sibling()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");

        // One transaction, two postings. Only the first is tagged — on the
        // ACCOUNT-side leg, where the importer puts a split's tags.
        var split = await ledger.AddMultiSplitAsync(
            bank.Id,
            // Negative: AddMultiSplitAsync puts the amount on the ORIGIN leg
            // and its negation on the category leg, so a spend from the bank
            // account is a negative origin and a positive expense.
            new[] { (groceries.Id, -60m), (dining.Id, -40m) },
            May);
        await ledger.AddLegTagAsync(split.OriginIds[0], "Reimbursable");

        var rows = (await SummarizeByTagAsync(ledger.LedgerId)).Rows;

        // The tagged posting alone, not the header's total: the dining posting
        // shares the header but not the tag. A header-level reading of this tag
        // would report the whole header and be wrong in
        // precisely the way the rental-property case in ADR-0103 described.
        Assert.Equal(60m, Assert.Single(rows, r => r.GroupName == "Reimbursable").Amount);
        Assert.Equal(40m, Assert.Single(rows, r => r.GroupName == "(untagged)").Amount);
    }

    [Fact]
    public async Task The_same_tag_at_both_levels_counts_a_posting_once()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");

        var split = await ledger.AddMultiSplitAsync(
            bank.Id,
            new[] { (groceries.Id, -75m), (groceries.Id, -25m) },
            May);
        // Same name at both levels on the SAME posting. The union behind this
        // dimension deduplicates (leg, tag) pairs; deduplicating the projected
        // row instead would be worse than this double-count, because two
        // equal-amount postings on one day under one tag would silently fold
        // into one.
        await ledger.AddTagAsync(split.OriginIds[0], "Audit");
        await ledger.AddLegTagAsync(split.OriginIds[0], "Audit");

        var rows = (await SummarizeByTagAsync(ledger.LedgerId)).Rows;

        // The header tag covers BOTH postings (it is a claim about the whole
        // transaction), so the header's total — and the leg tag adds nothing on top.
        Assert.Equal(100m, Assert.Single(rows, r => r.GroupName == "Audit").Amount);
        Assert.DoesNotContain(rows, r => r.GroupName == "(untagged)");
    }
}
