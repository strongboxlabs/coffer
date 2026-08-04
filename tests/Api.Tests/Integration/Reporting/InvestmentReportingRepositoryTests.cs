using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Investment reporting reads (ADR-0063 §D5): holdings snapshot (market value vs
/// cost basis, no-price → carried at cost) + asset-class allocation, over seeded
/// holdings + prices.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentReportingRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentReportingRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    private InvestmentReportingRepository NewRepo() => new(_fixture.NewDbContext());

    [Fact]
    public async Task Holdings_snapshot_values_at_latest_price_and_carries_no_price_at_cost()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        var vti = await ledger.AddSecurityAsync("Index Fund C", "IDXC", assetClass: "equity");
        var bnd = await ledger.AddSecurityAsync("Bond Index Fund B", "BNDB", assetClass: "fixed_income");

        // IDXC: 10 sh, cost 1000, latest price 150 → MV 1500, gain +500.
        await ledger.AddHoldingAsync(holdings, vti, quantity: 10m, costBasis: 1000m);
        await ledger.AddSecurityPriceAsync(vti, 100m, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddSecurityPriceAsync(vti, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // latest
        // BNDB: 20 sh, cost 1600, NO price → carried at cost 1600, gain 0.
        await ledger.AddHoldingAsync(holdings, bnd, quantity: 20m, costBasis: 1600m);

        var snap = await NewRepo().HoldingsSnapshotAsync(ledger.LedgerId);

        var vtiRow = snap.Holdings.Single(h => h.SecurityId == vti);
        Assert.Equal(150m, vtiRow.LatestPrice);
        Assert.Equal(1500m, vtiRow.MarketValue);
        Assert.Equal(500m, vtiRow.UnrealizedGain);
        Assert.Equal(50m, vtiRow.UnrealizedGainPct);

        var bndRow = snap.Holdings.Single(h => h.SecurityId == bnd);
        Assert.Null(bndRow.LatestPrice);
        Assert.Equal(1600m, bndRow.MarketValue);   // carried at cost
        Assert.Equal(0m, bndRow.UnrealizedGain);

        Assert.Equal(3100m, snap.TotalMarketValue);   // 1500 + 1600
        Assert.Equal(2600m, snap.TotalCostBasis);     // 1000 + 1600
        Assert.Equal(500m, snap.TotalUnrealizedGain);
    }

    [Fact]
    public async Task Allocation_buckets_by_asset_class_with_percent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        var vti = await ledger.AddSecurityAsync("IDXC", "IDXC", assetClass: "equity");
        var bnd = await ledger.AddSecurityAsync("BNDB", "BNDB", assetClass: "fixed_income");
        await ledger.AddHoldingAsync(holdings, vti, 10m, 1000m);
        await ledger.AddSecurityPriceAsync(vti, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // MV 1500
        await ledger.AddHoldingAsync(holdings, bnd, 5m, 500m);
        await ledger.AddSecurityPriceAsync(bnd, 100m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // MV 500

        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId);

        Assert.Equal(2000m, alloc.TotalMarketValue);
        var equity = alloc.Buckets.Single(b => b.Bucket == "equity");
        var bond = alloc.Buckets.Single(b => b.Bucket == "fixed_income");
        Assert.Equal(1500m, equity.MarketValue);
        Assert.Equal(75m, equity.Percent);
        Assert.Equal(500m, bond.MarketValue);
        Assert.Equal(25m, bond.Percent);
    }

    [Fact]
    public async Task Allocation_decomposes_look_through_security_by_component_weights()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        // A balanced fund: multi_asset IS the look-through signal — 60% equity /
        // 40% fixed income via its sleeves.
        var balanced = await ledger.AddSecurityAsync(
            "Balanced Fund", "BAL", assetClass: "multi_asset");
        await ledger.AddSecurityComponentAsync(balanced, "equity", 60m);
        await ledger.AddSecurityComponentAsync(balanced, "fixed_income", 40m);

        await ledger.AddHoldingAsync(holdings, balanced, 10m, 1000m);
        await ledger.AddSecurityPriceAsync(balanced, 100m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // MV 1000

        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId, AllocationDimension.AssetClass);

        Assert.Equal(1000m, alloc.TotalMarketValue);
        // The single multi-asset holding decomposes into its sleeves — NOT 100%
        // counted under "multi_asset".
        Assert.DoesNotContain(alloc.Buckets, b => b.Bucket == "multi_asset");
        var equity = alloc.Buckets.Single(b => b.Bucket == "equity");
        var fixedIncome = alloc.Buckets.Single(b => b.Bucket == "fixed_income");
        Assert.Equal(600m, equity.MarketValue);
        Assert.Equal(400m, fixedIncome.MarketValue);
    }

    [Fact]
    public async Task Allocation_buckets_by_security()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        var idxc = await ledger.AddSecurityAsync("IDXC", "IDXC", assetClass: "equity");
        var bndb = await ledger.AddSecurityAsync("BNDB", "BNDB", assetClass: "fixed_income");
        await ledger.AddHoldingAsync(holdings, idxc, 10m, 1000m);
        await ledger.AddSecurityPriceAsync(idxc, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // MV 1500
        await ledger.AddHoldingAsync(holdings, bndb, 5m, 500m);
        await ledger.AddSecurityPriceAsync(bndb, 100m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // MV 500

        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId, AllocationDimension.Security);

        Assert.Equal(2000m, alloc.TotalMarketValue);
        Assert.Equal(1500m, alloc.Buckets.Single(b => b.Bucket == "IDXC").MarketValue);
        Assert.Equal(500m, alloc.Buckets.Single(b => b.Bucket == "BNDB").MarketValue);
    }

    [Fact]
    public async Task Allocation_buckets_by_account_apportioned_by_quantity()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var one = await ledger.AddInvestmentAccountAsync("Brokerage One");
        var two = await ledger.AddInvestmentAccountAsync("Brokerage Two");

        // Same security held in both brokerages: 10 shares in One, 5 in Two.
        // HeldIn resolves each holdings sibling back to its parent brokerage,
        // so the account dimension buckets by brokerage name.
        var idxc = await ledger.AddSecurityAsync("IDXC", "IDXC", assetClass: "equity");
        await ledger.AddHoldingAsync(one.HoldingsAccountId!.Value, idxc, 10m, 1000m);
        await ledger.AddHoldingAsync(two.HoldingsAccountId!.Value, idxc, 5m, 500m);
        await ledger.AddSecurityPriceAsync(idxc, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId, AllocationDimension.Account);

        Assert.Equal(2250m, alloc.TotalMarketValue); // 15 shares x 150
        Assert.Equal(1500m, alloc.Buckets.Single(b => b.Bucket == "Brokerage One").MarketValue);  // 10/15
        Assert.Equal(750m, alloc.Buckets.Single(b => b.Bucket == "Brokerage Two").MarketValue);   //  5/15
    }
}
