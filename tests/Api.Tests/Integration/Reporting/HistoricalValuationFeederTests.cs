using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The migration-172 as-of valuation feeder (Track-2 historical valuations):
/// <c>holdings_market_value_as_of</c> and <c>account_balance_as_of</c>. The
/// load-bearing case is a valuation window spanning a stock split — quantity is
/// split-adjusted to the as-of instant, and the observed per-share price is
/// back-adjusted to the SAME basis, so a split leaves the value unchanged (the
/// double-count the split-adjusted-price fix prevents). Also exercises the
/// three price tiers: feed close, trade-execution fallback, and the pre-history
/// edge.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HistoricalValuationFeederTests
{
    private readonly PostgresFixture _fixture;

    public HistoricalValuationFeederTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HoldingsMarketValueAsOf_split_adjusts_both_quantity_and_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Splitter", "SPL");

        // Buy 100 @ $10 on 2024-01-10; 2-for-1 split on 2024-06-01; a feed close
        // of $6 arrives 2024-07-01 (post-split price).
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, sec, quantity: 100m, unitPrice: 10m, postedAt: Utc(2024, 1, 10));
        await ledger.AddSecuritySplitAsync(sec, ratio: 2m, splitAt: Utc(2024, 6, 1));
        await ledger.AddSecurityPriceAsync(sec, 6m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        async Task<HoldingsMarketValueAsOfRow?> ValueAt(DateTime asOf) =>
            (await db.HoldingsMarketValueAsOf(ledger.LedgerId, asOf, holdings, sec).ToListAsync())
            .SingleOrDefault();

        // (a) After the buy, before the split, no feed price yet: 100 shares,
        //     priced from the TRADE ($10) → $1,000.
        var t1 = await ValueAt(Utc(2024, 2, 1));
        Assert.NotNull(t1);
        Assert.Equal(100m, t1!.Quantity);
        Assert.Equal("trade", t1.PricedFrom);
        Assert.Equal(1000m, t1.MarketValue);

        // (b) After the split, still no feed price: quantity split-adjusts to
        //     200, and the pre-split $10 trade price back-adjusts to $5 → $1,000.
        //     A split must not change the value — this is the double-count the
        //     split-adjusted-price fix guards against (200 × raw $10 = $2,000).
        var t2 = await ValueAt(Utc(2024, 6, 15));
        Assert.NotNull(t2);
        Assert.Equal(200m, t2!.Quantity);
        Assert.Equal("trade", t2.PricedFrom);
        Assert.Equal(1000m, t2.MarketValue);

        // (c) After the feed close: 200 shares × the post-split feed $6 → $1,200
        //     (the feed price is already on the post-split basis; no adjustment).
        var t3 = await ValueAt(Utc(2024, 8, 1));
        Assert.NotNull(t3);
        Assert.Equal(200m, t3!.Quantity);
        Assert.Equal("feed", t3.PricedFrom);
        Assert.Equal(1200m, t3.MarketValue);

        // Before the position existed at all → no row.
        Assert.Null(await ValueAt(Utc(2023, 12, 1)));
    }

    [Fact]
    public async Task AccountBalanceAsOf_reflects_only_headers_up_to_the_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");

        // -$40 on 2024-01-10, -$25 on 2024-02-10 → running balance -40 then -65.
        await ledger.AddTransactionPairAsync(checking.Id, groceries.Id, -40m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(checking.Id, groceries.Id, -25m, Utc(2024, 2, 10));

        await using var db = _fixture.NewDbContext();
        async Task<decimal> BalanceAt(DateTime asOf) =>
            (await db.AccountBalanceAsOf(ledger.LedgerId, asOf, checking.Id).ToListAsync())
            .Single().Balance;

        Assert.Equal(0m,   await BalanceAt(Utc(2024, 1, 1)));    // before any txn → opening
        Assert.Equal(-40m, await BalanceAt(Utc(2024, 1, 15)));   // after the first only
        Assert.Equal(-65m, await BalanceAt(Utc(2024, 2, 15)));   // after both
    }

    [Fact]
    public async Task AccountBalanceAsOf_honors_a_posted_at_override_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");

        // -$40 on 2024-01-10, -$25 on 2024-02-10.
        await ledger.AddTransactionPairAsync(checking.Id, groceries.Id, -40m, Utc(2024, 1, 10));
        var (feb, _) = await ledger.AddTransactionPairAsync(checking.Id, groceries.Id, -25m, Utc(2024, 2, 10));

        // Edit the Feb transaction's date FORWARD to 2024-12-31 via a posted_at
        // override — the same column the balance recompute honors
        // (COALESCE(o.posted_at, h.posted_at)). Its effective date is now December.
        await ledger.SetHeaderOverrideAsync(feb, postedAt: Utc(2024, 12, 31));

        await using var db = _fixture.NewDbContext();
        async Task<decimal> BalanceAt(DateTime asOf) =>
            (await db.AccountBalanceAsOf(ledger.LedgerId, asOf, checking.Id).ToListAsync())
            .Single().Balance;

        // At 2024-03-01 the overridden txn's effective date (Dec 31) is still in the
        // FUTURE, so only the -$40 counts. The raw Feb-10 date would wrongly give -$65
        // (the mig-173 fix bounds/orders by the override-aware effective date).
        Assert.Equal(-40m, await BalanceAt(Utc(2024, 3, 1)));
        // Past the override date, both count.
        Assert.Equal(-65m, await BalanceAt(Utc(2025, 1, 1)));
    }
}
