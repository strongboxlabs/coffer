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

    /// <summary>Trade date for the allocation fixtures — before the 2026-06-01
    /// feed closes they price against, so the close is what the feeder uses.</summary>
    private static readonly DateTime Buy = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Seeded through real buys, not by writing into the <c>holdings</c> projection:
    /// the snapshot now values through the as-of feeder, which replays legs, so a
    /// position injected straight into the projection is invisible to it — and is a
    /// state production cannot reach anyway, since mig 068 maintains that projection
    /// from <c>txn_legs</c> by trigger.
    /// </summary>
    [Fact]
    public async Task Holdings_snapshot_values_at_the_latest_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        var vti = await ledger.AddSecurityAsync("Index Fund C", "IDXC", assetClass: "equity");
        var bnd = await ledger.AddSecurityAsync("Bond Index Fund B", "BNDB", assetClass: "fixed_income");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Buy);
        // A holdings ROW for each position, as an API write path creates before it
        // recomputes — the recompute refreshes existing rows and does not discover
        // positions from legs. Quantity/basis here are placeholders; the recompute
        // below re-derives both from the legs.
        await ledger.AddHoldingAsync(holdings, vti, quantity: 0m, costBasis: 0m);
        await ledger.AddHoldingAsync(holdings, bnd, quantity: 0m, costBasis: 0m);
        // IDXC: 10 sh at 100 → cost 1000; latest close 150 → MV 1500, gain +500.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, vti, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(vti, 100m, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddSecurityPriceAsync(vti, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        // BNDB: 20 sh at 80 → cost 1600, and NO feed close ever published.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, bnd, 20m, 80m, Buy);
        // Cost basis lives in the holdings projection, which migration 104 stopped
        // deriving by trigger — API write paths recompute it explicitly, so a raw
        // seed must too. Quantity and market value need none of this; they come
        // from the feeder, which replays the legs.
        await ledger.RecomputeHoldingsAsync();

        var snap = await NewRepo().HoldingsSnapshotAsync(ledger.LedgerId);

        var vtiRow = snap.Holdings.Single(h => h.SecurityId == vti);
        Assert.Equal(150m, vtiRow.LatestPrice);
        Assert.Equal(1500m, vtiRow.MarketValue);
        Assert.Equal(1000m, vtiRow.CostBasis);
        Assert.Equal(500m, vtiRow.UnrealizedGain);
        Assert.Equal(50m, vtiRow.UnrealizedGainPct);

        // A security with no feed close falls to its last TRADE price — a real
        // price observation — rather than being carried at cost basis as the
        // projection-reading version did. The two coincide for a single buy; they
        // part company after a second buy at a different price, and the feeder's
        // answer is the one returns and allocation already use.
        var bndRow = snap.Holdings.Single(h => h.SecurityId == bnd);
        Assert.Equal(80m, bndRow.LatestPrice);
        Assert.Equal(1600m, bndRow.MarketValue);
        Assert.Equal(1600m, bndRow.CostBasis);
        Assert.Equal(0m, bndRow.UnrealizedGain);

        Assert.Equal(3100m, snap.TotalMarketValue);
        Assert.Equal(2600m, snap.TotalCostBasis);
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
        // Bought, not injected. Allocation values through the same as-of feeder
        // returns uses, which replays LEGS — so a position seeded straight into the
        // holdings projection with no transaction behind it is a state production
        // cannot reach (mig 068 maintains holdings from txn_legs by trigger).
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, vti, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(vti, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // MV 1500
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, bnd, 5m, 100m, Buy);
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

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, balanced, 10m, 100m, Buy);
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
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, idxc, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(idxc, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)); // MV 1500
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, bndb, 5m, 100m, Buy);
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
        await ledger.AddInvestmentBuyAsync(one.Id, one.HoldingsAccountId!.Value, idxc, 10m, 100m, Buy);
        await ledger.AddInvestmentBuyAsync(two.Id, two.HoldingsAccountId!.Value, idxc, 5m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(idxc, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId, AllocationDimension.Account);

        Assert.Equal(2250m, alloc.TotalMarketValue); // 15 shares x 150
        Assert.Equal(1500m, alloc.Buckets.Single(b => b.Bucket == "Brokerage One").MarketValue);  // 10/15
        Assert.Equal(750m, alloc.Buckets.Single(b => b.Bucket == "Brokerage Two").MarketValue);   //  5/15
    }
    // ---- what the response now says about itself -----------------------------

    /// <summary>
    /// The failure this closes is SILENT: a multi_asset security with no sleeves
    /// cannot be looked through, so its whole value lands in one opaque bucket and
    /// every other bucket is understated — with nothing in the response saying so.
    /// One such fund at 66% of a portfolio reported equity at 8.5% against a true
    /// 35%, and the chart looked entirely plausible.
    /// </summary>
    [Fact]
    public async Task Allocation_names_a_multi_asset_security_it_could_not_decompose()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        // Sleeves are never configured for this one.
        var blend = await ledger.AddSecurityAsync("Blend Fund", "BLND", assetClass: "multi_asset");
        var stock = await ledger.AddSecurityAsync("Stock Fund", "STK", assetClass: "equity");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, blend, 30m, 100m, Buy);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, stock, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(blend, 100m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddSecurityPriceAsync(stock, 100m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId, AllocationDimension.AssetClass);

        var flagged = Assert.Single(alloc.UndecomposedMultiAssets);
        Assert.Equal(blend, flagged.SecurityId);
        Assert.Equal("BLND", flagged.Ticker);
        Assert.Equal(3000m, flagged.MarketValue);
        Assert.Equal(75m, flagged.PercentOfTotal);

        // Still bucketed, so the total stays whole — the point is that the caller
        // is told the bucket is opaque, not that the value vanishes.
        Assert.Equal(3000m, alloc.Buckets.Single(b => b.Bucket == "multi_asset").MarketValue);
        Assert.Equal(4000m, alloc.TotalMarketValue);
    }

    [Fact]
    public async Task Allocation_flags_an_undecomposable_security_on_every_dimension()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        var blend = await ledger.AddSecurityAsync("Blend Fund", "BLND", assetClass: "multi_asset");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, blend, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(blend, 100m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var repo = NewRepo();
        // The security dimension does not look through at all, but the security is
        // no less undecomposed — a warning that appears and vanishes as the caller
        // switches dimensions is a warning nobody trusts.
        foreach (var dimension in new[]
                 {
                     AllocationDimension.AssetClass,
                     AllocationDimension.Region,
                     AllocationDimension.Security,
                     AllocationDimension.VehicleType,
                 })
        {
            var alloc = await repo.AllocationAsync(ledger.LedgerId, dimension);
            Assert.Single(alloc.UndecomposedMultiAssets);
        }
    }

    [Fact]
    public async Task Allocation_reports_nothing_undecomposed_once_sleeves_exist()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        var balanced = await ledger.AddSecurityAsync("Balanced", "BAL", assetClass: "multi_asset");
        await ledger.AddSecurityComponentAsync(balanced, "equity", 60m);
        await ledger.AddSecurityComponentAsync(balanced, "fixed_income", 40m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, balanced, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(balanced, 100m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId, AllocationDimension.AssetClass);

        Assert.Empty(alloc.UndecomposedMultiAssets);
        Assert.Equal(600m, alloc.Buckets.Single(b => b.Bucket == "equity").MarketValue);
    }

    /// <summary>
    /// The identity that explains a discrepancy which sat unremarked across two
    /// published reports: an allocation total is SECURITIES, a returns total is
    /// securities plus cash, and nothing said so. Both now value through the same
    /// feeder at the same instant, so the two reconcile to the cent.
    /// </summary>
    [Fact]
    public async Task Allocation_total_plus_excluded_cash_equals_the_returns_portfolio_value()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var stock = await ledger.AddSecurityAsync("Stock Fund", "STK", assetClass: "equity");

        // $5,000 in, $3,000 of it invested — so $2,000 sits as uninvested cash,
        // which has no asset class and cannot be bucketed.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, stock, 30m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(stock, 120m, Utc(2024, 6, 1));

        var asOf = Utc(2024, 12, 31);
        var repo = NewRepo();
        var alloc = await repo.AllocationAsync(ledger.LedgerId, AllocationDimension.AssetClass, asOf);
        var returns = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null, nowUtc: asOf);

        Assert.Equal(3_600m, alloc.TotalMarketValue);      // 30 x 120
        Assert.Equal(2_000m, alloc.ExcludedBrokerageCash); // 5,000 - 3,000
        Assert.Equal(returns.EndValue, alloc.TotalMarketValue + alloc.ExcludedBrokerageCash);
    }

    [Fact]
    public async Task Allocation_values_a_past_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var stock = await ledger.AddSecurityAsync("Stock Fund", "STK", assetClass: "equity");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, stock, 30m, 100m, Utc(2024, 6, 1));
        await ledger.AddSecurityPriceAsync(stock, 120m, Utc(2024, 7, 1));

        var repo = NewRepo();
        // Before the buy: nothing is held, and the whole $5,000 is uninvested cash.
        var before = await repo.AllocationAsync(
            ledger.LedgerId, AllocationDimension.AssetClass, Utc(2024, 3, 1));
        Assert.Empty(before.Buckets);
        Assert.Equal(0m, before.TotalMarketValue);
        Assert.Equal(5_000m, before.ExcludedBrokerageCash);
        Assert.Equal(Utc(2024, 3, 1), before.AsOf);

        // After it: the position exists and the cash is down by its cost.
        var after = await NewRepo().AllocationAsync(
            ledger.LedgerId, AllocationDimension.AssetClass, Utc(2024, 12, 31));
        Assert.Equal(3_600m, after.TotalMarketValue);
        Assert.Equal(2_000m, after.ExcludedBrokerageCash);
    }

    [Fact]
    public async Task Reporting_responses_stamp_when_and_by_which_build()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 10));

        var before = DateTime.UtcNow.AddSeconds(-5);
        IReportProvenance[] results =
        [
            await NewRepo().HoldingsSnapshotAsync(ledger.LedgerId),
            await NewRepo().AllocationAsync(ledger.LedgerId),
            await NewRepo().IncomeAsync(
                ledger.LedgerId, null, null, null, null,
                InvestmentIncomeGroupBy.Security, ReportTimeBucket.None),
            await NewRepo().RealizedGainsAsync(ledger.LedgerId, null, null, null, null),
            await NewRepo().ReturnsAsync(ledger.LedgerId, null, null, null, Utc(2024, 12, 31)),
            await NewRepo().ReturnsCostEstimateAsync(ledger.LedgerId, null, null, null, Utc(2024, 12, 31)),
        ];

        // A consumer assembling one report from several calls has no other way to
        // tell a fresh figure from one carried over — which is exactly how four
        // "n/a" cells survived beside freshly-fetched rows in a published report.
        foreach (var r in results)
        {
            Assert.InRange(r.ComputedAt, before, DateTime.UtcNow.AddSeconds(5));
            Assert.False(string.IsNullOrWhiteSpace(r.EngineVersion));
            Assert.Contains("+", r.EngineVersion);   // semver+sha, not semver alone
        }
    }

    /// <summary>
    /// A position whose cost basis carries a long fractional scale must still be
    /// READABLE. This shipped broken in 0.63.0: holdings_cost_basis_as_of returned
    /// the walk's raw NUMERIC, and Npgsql threw
    /// <c>OverflowException: Numeric value does not fit in a System.Decimal</c> for
    /// every account big enough that the scale pushed it past 28-29 digits.
    /// </summary>
    /// <remarks>
    /// Scale enters through a SELL: a buy adds its leg amount, which is exact, but a
    /// sale subtracts <c>take x (amount / quantity)</c>, and that division rarely
    /// terminates. Magnitude then decides whether it throws, which is why production
    /// looked data-random — a $34K account was fine, a $196K one squeaked under at 28
    /// digits, a $2.98M one threw. The fixture below is deliberately large AND
    /// fractional so it lands past the limit rather than near it.
    /// </remarks>
    [Fact]
    public async Task Holdings_snapshot_bounds_scale_so_a_large_fractional_basis_is_readable()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Large", "LRG");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 500_000_000m, Utc(2020, 1, 10));
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, sec, 1096.909900000007m, 315_300.0m, Utc(2020, 1, 10));
        // The partial sale that introduces the non-terminating unit cost.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, sec, -333.333333333333m, 340_000m, Utc(2022, 6, 1));
        await ledger.AddSecurityPriceAsync(sec, 350_000m, Utc(2024, 1, 1));

        var snap = await NewRepo().HoldingsSnapshotAsync(
            ledger.LedgerId, accountId: null, asOfUtc: Utc(2026, 1, 1));

        var row = Assert.Single(snap.Holdings);
        // Money bounded to 4dp, shares to 12dp — the same convention the sibling
        // feeders (mig 172 / 200) round to, and what the holdings columns store.
        Assert.True(
            decimal.Round(row.CostBasis, 4) == row.CostBasis,
            $"cost basis must be bounded to 4dp, got {row.CostBasis}");
        Assert.True(
            decimal.Round(row.Quantity, 12) == row.Quantity,
            $"quantity must be bounded to 12dp, got {row.Quantity}");
        Assert.True(row.CostBasis > 200_000_000m);
        Assert.Equal(snap.TotalCostBasis, row.CostBasis);
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    // ---- holdings_snapshot as of an instant ----------------------------------

    /// <summary>
    /// The whole point of the parameter: quantity and market value at a PAST
    /// instant, which the current projection cannot express at all — it only ever
    /// describes now.
    /// </summary>
    [Fact]
    public async Task Holdings_snapshot_values_a_past_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW", assetClass: "equity");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 120m, Utc(2024, 3, 1));
        // A second buy AFTER the instant under test, so a snapshot that ignored the
        // as-of would report 30 shares rather than 10.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 20m, 130m, Utc(2024, 9, 1));
        await ledger.AddSecurityPriceAsync(sec, 150m, Utc(2024, 10, 1));

        var snap = await NewRepo().HoldingsSnapshotAsync(
            ledger.LedgerId, accountId: null, asOfUtc: Utc(2024, 6, 1));

        var row = Assert.Single(snap.Holdings);
        Assert.Equal(10m, row.Quantity);                 // not 30
        Assert.Equal(120m, row.LatestPrice);             // the March close, not October's
        Assert.Equal(1_200m, row.MarketValue);
        Assert.Equal(Utc(2024, 6, 1), snap.AsOf);
        Assert.Equal(1_200m, snap.TotalMarketValue);
    }

    /// <summary>
    /// FIFO cost basis AS OF the instant, not today's basis against a past market
    /// value. Migration 202 made the walk pure and as-of-bounded so this is exact:
    /// the second buy is after the instant under test, so its cost must not appear.
    /// </summary>
    [Fact]
    public async Task Holdings_snapshot_reports_cost_basis_as_of_a_past_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddHoldingAsync(holdings, sec, quantity: 0m, costBasis: 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 120m, Utc(2024, 3, 1));
        // A second buy AFTER the instant under test: +$2,600 of basis that must not
        // be counted at 2024-06-01, and would be if basis came from the projection.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 20m, 130m, Utc(2024, 9, 1));
        await ledger.RecomputeHoldingsAsync();

        var past = await NewRepo().HoldingsSnapshotAsync(
            ledger.LedgerId, accountId: null, asOfUtc: Utc(2024, 6, 1));

        var row = Assert.Single(past.Holdings);
        Assert.Equal(10m, row.Quantity);
        Assert.Equal(1_200m, row.MarketValue);
        Assert.Equal(1_000m, row.CostBasis);             // as of then, not 3,600
        Assert.Equal(200m, row.UnrealizedGain);
        Assert.Equal(20m, row.UnrealizedGainPct);
        Assert.Equal(1_000m, past.TotalCostBasis);
        Assert.Equal(200m, past.TotalUnrealizedGain);

        // And now reflects both buys — the projection the recompute wrote.
        var now = await NewRepo().HoldingsSnapshotAsync(ledger.LedgerId);
        Assert.Equal(3_600m, Assert.Single(now.Holdings).CostBasis);
    }

    [Fact]
    public async Task Holdings_snapshot_drops_a_position_closed_before_the_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var kept = await ledger.AddSecurityAsync("Kept", "KPT");
        var sold = await ledger.AddSecurityAsync("Sold", "SLD");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, kept, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sold, 20m, 50m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sold, -20m, 55m, Utc(2024, 5, 10));
        await ledger.AddSecurityPriceAsync(kept, 100m, Utc(2024, 2, 1));
        await ledger.AddSecurityPriceAsync(sold, 55m, Utc(2024, 2, 1));

        var repo = NewRepo();
        // Before the sale both are held; after it only one is.
        var before = await repo.HoldingsSnapshotAsync(
            ledger.LedgerId, accountId: null, asOfUtc: Utc(2024, 3, 1));
        Assert.Equal(2, before.Holdings.Count);

        var after = await NewRepo().HoldingsSnapshotAsync(
            ledger.LedgerId, accountId: null, asOfUtc: Utc(2024, 8, 1));
        Assert.Equal(kept, Assert.Single(after.Holdings).SecurityId);
    }

    /// <summary>
    /// The claim the whole change rests on, asserted rather than described:
    /// holdings_snapshot, allocation and returns now value through ONE feeder, so at
    /// the same instant the securities totals are identical and adding the cash
    /// allocation excludes gives the portfolio value returns reports.
    /// </summary>
    [Fact]
    public async Task Holdings_snapshot_allocation_and_returns_agree_at_the_same_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW", assetClass: "equity");

        // $5,000 in, $3,000 invested — $2,000 stays as cash, which has no asset
        // class and so is not a holding.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 30m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 120m, Utc(2024, 6, 1));

        var asOf = Utc(2024, 12, 31);
        var snap = await NewRepo().HoldingsSnapshotAsync(ledger.LedgerId, accountId: null, asOfUtc: asOf);
        var alloc = await NewRepo().AllocationAsync(ledger.LedgerId, AllocationDimension.AssetClass, asOf);
        var returns = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null, nowUtc: asOf);

        Assert.Equal(3_600m, snap.TotalMarketValue);
        Assert.Equal(snap.TotalMarketValue, alloc.TotalMarketValue);
        Assert.Equal(returns.EndValue, snap.TotalMarketValue + alloc.ExcludedBrokerageCash);
    }
}
