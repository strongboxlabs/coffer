using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// A top-N cut through the real aggregation: which kept rows a chart draws, the
/// rows that hold a rolled-up kept parent's own postings, and the rest, the money
/// no drawn row holds.
/// </summary>
/// <remarks>
/// <para>The property every case pins: for each period, the drawn rows plus the
/// rest equal the period's total. The rest is summed from the undrawn postings
/// rather than subtracted from the total, so a drawn set that overlaps, or one
/// that leaves money out, breaks the equality instead of hiding in the rest.</para>
/// <para>Each report runs over a window of three months whose middle month has no
/// postings, so the rest is checked where it is zero-filled as well.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TopNRestTests : DatabaseTest
{
    public TopNRestTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime First = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Third = new(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Periods = ["2026-01", "2026-02", "2026-03"];

    private Task<ReportResult> SummarizeAsync(
        Guid ledgerId, int? topN, bool rollup = false, ReportGroupBy groupBy = ReportGroupBy.Category) =>
        new ReportingRepository(Db.NewDbContext(), TimeProvider.System).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.Month,
            GroupBy = groupBy,
            FromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            Rollup = rollup,
            TopN = topN,
        });

    /// <summary>
    /// For each period the drawn rows plus the rest come to the period's total, and
    /// across the window to the result's total. Every row says whether it is drawn.
    /// </summary>
    private static void AssertDrawnPlusRestMakeThePeriodTotals(ReportResult result)
    {
        var rest = Assert.IsType<ReportRest>(result.Rest);
        var periodTotals = Assert.IsAssignableFrom<IReadOnlyList<ReportPeriodTotal>>(result.PeriodTotals);
        Assert.Equal(Periods, periodTotals.Select(p => p.Period).ToArray());
        Assert.Equal(Periods, ByPeriod(rest).Select(p => p.Period).ToArray());
        Assert.All(result.Rows, r => Assert.NotNull(r.Drawn));

        foreach (var period in periodTotals)
        {
            var drawn = result.Rows.Where(r => r.Drawn == true && r.Period == period.Period).Sum(r => r.Amount);
            Assert.Equal(period.Amount, drawn + RestIn(rest, period.Period));
        }
        Assert.Equal(result.Total, result.Rows.Where(r => r.Drawn == true).Sum(r => r.Amount) + rest.Total);
    }

    /// <summary>The rest's periods. The aggregation always fills them; only a mixed-currency wrapper empties them.</summary>
    private static IReadOnlyList<ReportPeriodTotal> ByPeriod(ReportRest rest) =>
        Assert.IsAssignableFrom<IReadOnlyList<ReportPeriodTotal>>(rest.ByPeriod);

    private static decimal RestIn(ReportRest rest, string period) =>
        ByPeriod(rest).Single(p => p.Period == period).Amount!.Value;

    /// <summary>
    /// The rest names one top-level category. Field by field: the record holds its
    /// path as a list, which record equality compares by reference.
    /// </summary>
    private static void AssertOnly(ReportRest rest, AccountRow category, bool isDirect)
    {
        var only = Assert.IsType<ReportRestGroup>(rest.Only);
        Assert.Equal(category.Id, only.GroupId);
        Assert.Equal(category.Name, only.GroupName);
        Assert.Equal(isDirect, only.IsDirect);
        Assert.Equal([category.Name], only.GroupPath);
    }

    private static void AssertGroups(ReportResult result, params AccountRow[] expected) =>
        Assert.Equal(
            expected.Select(c => c.Id).Order().ToArray(),
            result.Rows.Select(r => r.GroupId!.Value).Distinct().Order().ToArray());

    private static decimal OwnPostings(ReportResult flat, IEnumerable<AccountRow> categories, string period)
    {
        var ids = categories.Select(c => (Guid?)c.Id).ToHashSet();
        return flat.Rows.Where(r => r.Period == period && ids.Contains(r.GroupId)).Sum(r => r.Amount!.Value);
    }

    // ---- flat --------------------------------------------------------------

    private sealed record FlatLedger(
        SyntheticLedger Ledger, AccountRow Parent, AccountRow Child, AccountRow Second, AccountRow Third, AccountRow Fourth);

    /// <summary>
    /// Five categories with distinct totals. One is the parent of another and has
    /// postings of its own, which a flat report shows as a row of its own.
    /// </summary>
    private async Task<FlatLedger> SeedFlatAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var parent = await ledger.AddCategoryAsync("Category A");
        var child = await ledger.AddCategoryAsync("Category A1", parentId: parent.Id);
        var second = await ledger.AddCategoryAsync("Category B");
        var third = await ledger.AddCategoryAsync("Category C");
        var fourth = await ledger.AddCategoryAsync("Category D");

        await ledger.AddTransactionPairAsync(parent.Id, bank.Id, 30m, First);
        await ledger.AddTransactionPairAsync(parent.Id, bank.Id, 10m, Third);
        await ledger.AddTransactionPairAsync(child.Id, bank.Id, 200m, First);
        await ledger.AddTransactionPairAsync(child.Id, bank.Id, 100m, Third);
        await ledger.AddTransactionPairAsync(second.Id, bank.Id, 80m, First);
        await ledger.AddTransactionPairAsync(second.Id, bank.Id, 20m, Third);
        await ledger.AddTransactionPairAsync(third.Id, bank.Id, 50m, Third);
        await ledger.AddTransactionPairAsync(fourth.Id, bank.Id, 15m, First);
        return new FlatLedger(ledger, parent, child, second, third, fourth);
    }

    [Fact]
    public async Task Without_rollup_every_kept_row_is_drawn_and_the_rest_is_the_dropped_groups()
    {
        var seed = await SeedFlatAsync();
        var all = await SummarizeAsync(seed.Ledger.LedgerId, topN: null);

        // Every cut, from one group to more than there are.
        for (var topN = 1; topN <= 6; topN++)
        {
            var result = await SummarizeAsync(seed.Ledger.LedgerId, topN);
            AssertDrawnPlusRestMakeThePeriodTotals(result);
            // Flat rows never overlap, a parent's included: each holds its own postings.
            Assert.All(result.Rows, r => Assert.True(r.Drawn));
            Assert.DoesNotContain(result.Rows, r => r.IsDirect);
        }

        // Kept: the child and the second-largest group. The rest is the other three,
        // the parent's own postings among them, as their own rows report them.
        var cut = await SummarizeAsync(seed.Ledger.LedgerId, topN: 2);
        AssertGroups(cut, seed.Child, seed.Second);
        var rest = cut.Rest!;
        Assert.Equal(3, rest.Count);
        // A rest of more than one series names none of them.
        Assert.Null(rest.Only);
        var dropped = new[] { seed.Parent, seed.Third, seed.Fourth };
        foreach (var period in Periods)
            Assert.Equal(OwnPostings(all, dropped, period), RestIn(rest, period));
        Assert.Equal(0m, RestIn(rest, "2026-02"));
    }

    [Fact]
    public async Task Without_rollup_a_rest_of_one_dropped_group_is_named_by_it()
    {
        var seed = await SeedFlatAsync();

        // Every group but the smallest is kept.
        var cut = await SummarizeAsync(seed.Ledger.LedgerId, topN: 4);

        var rest = cut.Rest!;
        Assert.Equal(1, rest.Count);
        AssertOnly(rest, seed.Fourth, isDirect: false);
    }

    // ---- rolled up ---------------------------------------------------------

    private sealed record TreeLedger(
        SyntheticLedger Ledger,
        AccountRow A, AccountRow A1, AccountRow A2, AccountRow A3,
        AccountRow B, AccountRow B1, AccountRow B2,
        AccountRow C,
        AccountRow Q, AccountRow Q1, AccountRow Q1a, AccountRow Q2)
    {
        public IReadOnlyList<AccountRow> All => [A, A1, A2, A3, B, B1, B2, C, Q, Q1, Q1a, Q2];
    }

    /// <summary>
    /// Four top-level categories, every subtree total distinct in size:
    /// <list type="bullet">
    ///   <item>A parent with postings of its own and three children.</item>
    ///   <item>A parent with none of its own and two children.</item>
    ///   <item>A category with no children.</item>
    ///   <item>A parent with postings of its own, a child with postings of its own
    ///   and a child below it, and a child holding a refund larger than the
    ///   parent's subtree, which ranks the parent below two of its descendants.</item>
    /// </list>
    /// </summary>
    private async Task<TreeLedger> SeedTreeAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var a = await ledger.AddCategoryAsync("Category A");
        var a1 = await ledger.AddCategoryAsync("Category A1", parentId: a.Id);
        var a2 = await ledger.AddCategoryAsync("Category A2", parentId: a.Id);
        var a3 = await ledger.AddCategoryAsync("Category A3", parentId: a.Id);
        var b = await ledger.AddCategoryAsync("Category B");
        var b1 = await ledger.AddCategoryAsync("Category B1", parentId: b.Id);
        var b2 = await ledger.AddCategoryAsync("Category B2", parentId: b.Id);
        var c = await ledger.AddCategoryAsync("Category C");
        var q = await ledger.AddCategoryAsync("Category Q");
        var q1 = await ledger.AddCategoryAsync("Category Q1", parentId: q.Id);
        var q1a = await ledger.AddCategoryAsync("Category Q1a", parentId: q1.Id);
        var q2 = await ledger.AddCategoryAsync("Category Q2", parentId: q.Id);

        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 30m, First);
        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 10m, Third);
        await ledger.AddTransactionPairAsync(a1.Id, bank.Id, 200m, First);
        await ledger.AddTransactionPairAsync(a1.Id, bank.Id, 100m, Third);
        await ledger.AddTransactionPairAsync(a2.Id, bank.Id, 5m, First);
        await ledger.AddTransactionPairAsync(a3.Id, bank.Id, 50m, Third);
        await ledger.AddTransactionPairAsync(b1.Id, bank.Id, 80m, First);
        await ledger.AddTransactionPairAsync(b2.Id, bank.Id, 20m, Third);
        await ledger.AddTransactionPairAsync(c.Id, bank.Id, 15m, First);
        await ledger.AddTransactionPairAsync(q.Id, bank.Id, 12m, First);
        await ledger.AddTransactionPairAsync(q1.Id, bank.Id, 150m, Third);
        await ledger.AddTransactionPairAsync(q1a.Id, bank.Id, 40m, First);
        // A refund: a negative expense.
        await ledger.AddTransactionPairAsync(q2.Id, bank.Id, -170m, First);
        return new TreeLedger(ledger, a, a1, a2, a3, b, b1, b2, c, q, q1, q1a, q2);
    }

    /// <summary>No drawn subtree row lies below another, or below a parent drawn by its own postings.</summary>
    private static void AssertNoDrawnRowLiesBelowAnother(ReportResult result, TreeLedger seed)
    {
        var parentOf = seed.All.ToDictionary(c => c.Id, c => c.ParentId);
        var drawn = result.Rows.Where(r => r.Drawn == true).ToList();
        var whole = drawn.Where(r => !r.IsDirect).Select(r => r.GroupId!.Value).ToHashSet();
        foreach (var id in drawn.Select(r => r.GroupId!.Value).Distinct())
        {
            for (var up = parentOf[id]; up is { } ancestor; up = parentOf[ancestor])
                Assert.DoesNotContain(ancestor, whole);
        }
        // A parent drawn by its own postings is never also drawn whole.
        Assert.All(drawn.Where(r => r.IsDirect), r => Assert.DoesNotContain(r.GroupId!.Value, whole));
    }

    [Fact]
    public async Task Under_rollup_the_drawn_rows_and_the_rest_partition_every_period()
    {
        var seed = await SeedTreeAsync();
        var flat = await SummarizeAsync(seed.Ledger.LedgerId, topN: null);

        // Every cut, from one category to more than there are.
        for (var topN = 1; topN <= seed.All.Count + 1; topN++)
        {
            var result = await SummarizeAsync(seed.Ledger.LedgerId, topN, rollup: true);
            AssertDrawnPlusRestMakeThePeriodTotals(result);
            AssertNoDrawnRowLiesBelowAnother(result, seed);
        }

        // Kept: the first parent, its largest child, and the middle category of the
        // deep branch, which is drawn whole with the child below it.
        var cut = await SummarizeAsync(seed.Ledger.LedgerId, topN: 3, rollup: true);
        AssertGroups(cut, seed.A, seed.A1, seed.Q1);

        // The rest is the postings of every category no drawn row holds: the first
        // parent's other children, the second parent's whole subtree, the category
        // with no children, the deep parent's own postings and its refund child.
        var undrawn = new[] { seed.A2, seed.A3, seed.B, seed.B1, seed.B2, seed.C, seed.Q, seed.Q2 };
        var rest = cut.Rest!;
        foreach (var period in Periods)
            Assert.Equal(OwnPostings(flat, undrawn, period), RestIn(rest, period));

        // Six series: each of the two dropped children, the second parent with its
        // children as one, the category with no children, the deep parent's own
        // postings, and its refund child. More than one, so none is named.
        Assert.Equal(6, rest.Count);
        Assert.Null(rest.Only);
    }

    [Fact]
    public async Task Under_rollup_a_rest_of_one_dropped_subtree_is_named_at_its_highest_category()
    {
        // A category with no children, larger than a parent whose postings are all
        // on its two children. A cut of one keeps the first; the parent and its
        // children are the rest, one series.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var kept = await ledger.AddCategoryAsync("Category A");
        var parent = await ledger.AddCategoryAsync("Category B");
        var child = await ledger.AddCategoryAsync("Category B1", parentId: parent.Id);
        var other = await ledger.AddCategoryAsync("Category B2", parentId: parent.Id);
        await ledger.AddTransactionPairAsync(kept.Id, bank.Id, 500m, First);
        await ledger.AddTransactionPairAsync(child.Id, bank.Id, 20m, First);
        await ledger.AddTransactionPairAsync(other.Id, bank.Id, 10m, Third);

        var cut = await SummarizeAsync(ledger.LedgerId, topN: 1, rollup: true);

        AssertDrawnPlusRestMakeThePeriodTotals(cut);
        var rest = cut.Rest!;
        Assert.Equal(1, rest.Count);
        // Named by the parent, not by either child that holds the postings.
        AssertOnly(rest, parent, isDirect: false);
    }

    [Fact]
    public async Task Under_rollup_a_rest_of_one_categorys_own_postings_is_named_as_direct()
    {
        // A parent with postings of its own and two children, one of them a refund
        // that ranks the parent below both. A cut of two keeps the children, so the
        // parent's own postings are the whole rest.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var parent = await ledger.AddCategoryAsync("Category A");
        var child = await ledger.AddCategoryAsync("Category A1", parentId: parent.Id);
        var refund = await ledger.AddCategoryAsync("Category A2", parentId: parent.Id);
        await ledger.AddTransactionPairAsync(parent.Id, bank.Id, 40m, First);
        await ledger.AddTransactionPairAsync(child.Id, bank.Id, 300m, First);
        await ledger.AddTransactionPairAsync(refund.Id, bank.Id, -290m, Third);

        var cut = await SummarizeAsync(ledger.LedgerId, topN: 2, rollup: true);

        AssertDrawnPlusRestMakeThePeriodTotals(cut);
        Assert.DoesNotContain(cut.Rows, r => r.GroupId == parent.Id);
        var rest = cut.Rest!;
        Assert.Equal(1, rest.Count);
        AssertOnly(rest, parent, isDirect: true);
    }

    [Fact]
    public async Task A_rolled_up_kept_parent_with_postings_of_its_own_draws_them_on_rows_of_its_own()
    {
        var seed = await SeedTreeAsync();
        var flat = await SummarizeAsync(seed.Ledger.LedgerId, topN: null);
        var cut = await SummarizeAsync(seed.Ledger.LedgerId, topN: 3, rollup: true);

        // The kept parent's subtree rows are not drawn: its kept child is.
        var subtree = cut.Rows.Where(r => r.GroupId == seed.A.Id && !r.IsDirect).ToList();
        Assert.Equal(2, subtree.Count);
        Assert.All(subtree, r => Assert.False(r.Drawn));

        // Its own postings are, one row per period it has them in, under its own id,
        // name and parent, each carrying exactly what its own row reports without rollup.
        var direct = cut.Rows.Where(r => r.IsDirect).ToList();
        Assert.Equal(new[] { "2026-01", "2026-03" }, direct.Select(r => r.Period!).Order(StringComparer.Ordinal));
        Assert.All(direct, r =>
        {
            Assert.Equal(seed.A.Id, r.GroupId);
            Assert.Equal(seed.A.Name, r.GroupName);
            Assert.Equal(seed.A.ParentId, r.ParentId);
            Assert.True(r.Drawn);
            var own = Assert.Single(flat.Rows, f => f.GroupId == seed.A.Id && f.Period == r.Period);
            Assert.Equal(own.Amount, r.Amount);
        });

        // A kept category drawn whole needs no such row, though it has postings of its
        // own; and a parent the cut dropped has no rows at all.
        Assert.DoesNotContain(cut.Rows, r => r.GroupId == seed.Q1.Id && r.IsDirect);
        Assert.DoesNotContain(cut.Rows, r => r.GroupId == seed.Q.Id);
    }

    [Fact]
    public async Task A_cut_at_or_above_the_group_count_leaves_an_empty_rest()
    {
        var flat = await SeedFlatAsync();
        var tree = await SeedTreeAsync();

        foreach (var (ledgerId, rollup, groups) in new[]
                 {
                     (flat.Ledger.LedgerId, false, 5),
                     (tree.Ledger.LedgerId, true, tree.All.Count),
                 })
        {
            foreach (var topN in new[] { groups, groups + 10 })
            {
                var result = await SummarizeAsync(ledgerId, topN, rollup);
                AssertDrawnPlusRestMakeThePeriodTotals(result);
                var rest = result.Rest!;
                Assert.Equal(0, rest.Count);
                Assert.Null(rest.Only);
                Assert.Equal(0m, rest.Total);
                Assert.Equal(Periods, ByPeriod(rest).Select(p => p.Period).ToArray());
                Assert.All(ByPeriod(rest), p => Assert.Equal(0m, p.Amount));
            }
        }
    }

    [Fact]
    public async Task By_tag_the_rest_is_on_the_footing_of_the_tag_totals()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var first = await ledger.AddCategoryAsync("Category A");
        var second = await ledger.AddCategoryAsync("Category B");

        // A posting with two tags counts under each; one with none is "(untagged)".
        // A one-split's tags live on its bank leg, the real account a report reads it from.
        var twice = await ledger.AddTransactionPairAsync(first.Id, bank.Id, 100m, First);
        await ledger.AddLegTagAsync(twice.ToTxnId, "Tag A");
        await ledger.AddLegTagAsync(twice.ToTxnId, "Tag B");
        var once = await ledger.AddTransactionPairAsync(second.Id, bank.Id, 40m, First);
        await ledger.AddLegTagAsync(once.ToTxnId, "Tag B");
        await ledger.AddTransactionPairAsync(first.Id, bank.Id, 25m, Third);
        var later = await ledger.AddTransactionPairAsync(second.Id, bank.Id, 60m, Third);
        await ledger.AddLegTagAsync(later.ToTxnId, "Tag C");

        var all = await SummarizeAsync(ledger.LedgerId, topN: null, groupBy: ReportGroupBy.Tag);
        for (var topN = 1; topN <= 5; topN++)
            AssertDrawnPlusRestMakeThePeriodTotals(
                await SummarizeAsync(ledger.LedgerId, topN, groupBy: ReportGroupBy.Tag));

        // Kept: the tag on both of the first period's postings. The rest still counts
        // the twice-tagged posting under its other tag, as the period total does.
        var cut = await SummarizeAsync(ledger.LedgerId, topN: 1, groupBy: ReportGroupBy.Tag);
        var kept = Assert.Single(cut.Rows.Select(r => r.GroupName).Distinct());
        Assert.Equal("Tag B", kept);
        var rest = cut.Rest!;
        Assert.Equal(3, rest.Count);
        foreach (var period in Periods)
            Assert.Equal(
                all.Rows.Where(r => r.Period == period && r.GroupName != kept).Sum(r => r.Amount),
                RestIn(rest, period));
    }

    [Fact]
    public async Task On_a_ledger_in_two_currencies_every_figure_converts_at_the_posting_date_or_fails_closed()
    {
        // Two bank accounts in different currencies, three categories paid from them.
        // A cut of one leaves two groups, from both accounts, in the rest.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var home = await ledger.AddBankAccountAsync("Account A");
        var abroad = new AccountRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledger.LedgerId,
            Name = "Account B",
            AccountType = "bank",
            CurrencyCode = "EUR",
            OpeningBalance = 0m,
            IsActive = true,
        };
        await using (var db = ledger.NewDbContext())
        {
            db.Accounts.Add(abroad);
            await db.SaveChangesAsync();
        }
        var first = await ledger.AddCategoryAsync("Category A");
        var second = await ledger.AddCategoryAsync("Category B");
        var third = await ledger.AddCategoryAsync("Category C");
        await ledger.AddTransactionPairAsync(first.Id, home.Id, 100m, First);
        await ledger.AddTransactionPairAsync(second.Id, abroad.Id, 40m, First);
        await ledger.AddTransactionPairAsync(third.Id, home.Id, 25m, Third);

        // No EUR→USD rate yet. The EUR posting's row, and every figure holding it, is
        // null and names the pair and the posting's day (ADR-0114 D9); nothing sums the
        // USD postings alone.
        var day = DateOnly.FromDateTime(First);
        var withoutRate = await SummarizeAsync(ledger.LedgerId, topN: 1);
        var why = new RateUnavailable("EUR", "USD", day);
        Assert.Null(withoutRate.Total);
        Assert.Equal([why], withoutRate.RateUnavailable);
        Assert.Equal(2, withoutRate.Rest!.Count);
        Assert.Null(withoutRate.Rest.Total);
        Assert.Equal([why], withoutRate.Rest.RateUnavailable);
        Assert.Null(ByPeriod(withoutRate.Rest).Single(p => p.Period == "2026-01").Amount);
        Assert.Equal(25m, RestIn(withoutRate.Rest, "2026-03"));

        // Through the app's endpoint: the same answer, in the JSON.
        await using var factory = new ApiFactory(Db);
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/ledgers/{ledger.LedgerId}/reports/summary?measure=Spending&groupBy=Category&bucket=Month"
            + "&from=2026-01-01T00:00:00Z&to=2026-04-01T00:00:00Z&topN=1");
        Assert.Equal("USD", body.GetProperty("currencyCode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("total").ValueKind);
        var rest = body.GetProperty("rest");
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("total").ValueKind);
        var named = rest.GetProperty("rateUnavailable")[0];
        Assert.Equal("rate_unavailable", named.GetProperty("reason").GetString());
        Assert.Equal("EUR", named.GetProperty("fromCurrency").GetString());
        Assert.Equal("2026-01-15", named.GetProperty("date").GetString());

        // Through the MCP tool: the same.
        var mcp = await ReportingTools.TransactionSummary(
            new ReportingRepository(Db.NewDbContext(), TimeProvider.System),
            ledger.LedgerId,
            fromUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            toUtc: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            topN: 1);
        Assert.Null(mcp.Total);
        Assert.Null(mcp.Rest!.Total);

        // With the rate on the posting's day, as the fetcher writes it, it converts at
        // that day's rate.
        await using (var writer = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            writer.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                RateDate = day, Rate = 1.5m, Source = FxRateSource.Frankfurter,
            });
            writer.FxPairCoverage.Add(new FxPairCoverageRow
            {
                LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD", FetchedThroughDate = DateOnly.FromDateTime(Third),
            });
            await writer.SaveChangesAsync();
        }
        var converted = await SummarizeAsync(ledger.LedgerId, topN: 1);
        Assert.Equal(100m + 60m + 25m, converted.Total);
        Assert.Null(converted.RateUnavailable);
        Assert.Equal(60m + 25m, converted.Rest!.Total);
        Assert.Equal(60m, RestIn(converted.Rest, "2026-01"));
    }

    [Fact]
    public async Task Without_a_cut_no_row_is_marked_and_there_is_no_rest()
    {
        var seed = await SeedTreeAsync();

        foreach (var rollup in new[] { false, true })
        {
            var result = await SummarizeAsync(seed.Ledger.LedgerId, topN: null, rollup);
            Assert.Null(result.Rest);
            Assert.All(result.Rows, r => Assert.Null(r.Drawn));
            Assert.DoesNotContain(result.Rows, r => r.IsDirect);
        }
    }
}
