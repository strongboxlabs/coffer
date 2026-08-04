using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Per-shape tests for the investment translation under the symmetric-postings
/// model (ADR-0019). Each MD investment shape produces 2-4 rows organised
/// in pairs (cash + holdings, cash + income, cash + xfr); holdings-side
/// rows carry security_id/quantity/unit_price/commission. Tests assert
/// row counts per pair, the per-row amounts (sign-flipped pair invariant),
/// the correct account assignment (brokerage vs. Holdings sibling vs.
/// category vs. external), and the holdings-delta + lot side-effects.
/// </summary>
public sealed class InvestmentTransactionMapperTests
{
    private const string Brokerage = "a-brokerage";
    private const string SecAcct = "a-secsub";
    private const string IncomeCat = "a-divincome";
    private const string FeeCat = "a-feeexp";
    private const string OtherAcct = "a-other";

    private static MdTxn TxnFromJson(string fragment)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """;
        return MdTxn.From(MdItemReader.ReadString(wrapped).AllItems.Single());
    }

    private sealed record Lookups(
        IReadOnlyDictionary<string, AccountRef> Accounts,
        IReadOnlyDictionary<string, SecurityRef> SecuritiesByAcct,
        Guid SecurityId,
        Guid HoldingsId);

    private static Lookups BuildLookups(int shareDecimals = 4)
    {
        var brokerageId = Guid.NewGuid();
        var holdingsId  = Guid.NewGuid();
        var otherId     = Guid.NewGuid();
        var incomeId    = Guid.NewGuid();
        var feeId       = Guid.NewGuid();
        var securityId  = Guid.NewGuid();

        return new Lookups(
            Accounts: new Dictionary<string, AccountRef>(StringComparer.Ordinal)
            {
                [Brokerage] = new(brokerageId, "investment", HoldingsAccountId: holdingsId),
                [OtherAcct] = new(otherId,    "bank"),
                [IncomeCat] = new(incomeId,   "category"),
                [FeeCat]    = new(feeId,      "category"),
            },
            SecuritiesByAcct: new Dictionary<string, SecurityRef>(StringComparer.Ordinal)
            {
                [SecAcct] = new(securityId, shareDecimals),
            },
            SecurityId: securityId,
            HoldingsId: holdingsId);
    }

    // Test convention: shares scale by 10^4 (samt), cash scales by 10^2 (pamt = cents).
    // E.g., 100 shares at $1.00 each ⇒ samt=1,000,000 and pamt=-10,000.

    [Fact]
    public void Buy_emits_cash_holdings_pair_with_lot_at_grosscost()
    {
        // 100 shares × $1.00 = $100 cost. No fee → no group on cash side.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buy-1","acctid":"{{Brokerage}}",
              "desc":"BUY IDXB","dt":"20240115",
              "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"1000000","0.pamt":"-10000"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(2, result.Rows.Count);

        var cash     = result.Rows.Single(r => r.AccountId == lookups.Accounts[Brokerage].Id);
        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);

        // Sign-flipped pair, balanced.
        Assert.Equal(-100.00m, cash.FeedAmount);
        Assert.Equal(100.00m,  holdings.FeedAmount);
        Assert.Equal(cash.Id,     holdings.CounterpartyId);
        Assert.Equal(holdings.Id, cash.CounterpartyId);

        // Security metadata lives on the holdings-side row only.
        Assert.Null(cash.SecurityId);
        Assert.Equal(lookups.SecurityId, holdings.SecurityId);
        Assert.Equal(100m,    holdings.Quantity);
        Assert.Equal(1.00m,   holdings.UnitPrice);
        Assert.Equal("buy",   cash.InvestmentAction);
        Assert.Equal("buy",   holdings.InvestmentAction);

        // Single-leg event → no group; lot owned by holdings-side row.
        Assert.Null(cash.TxnGroupId);
        Assert.Null(holdings.TxnGroupId);

        Assert.NotNull(result.HoldingDelta);
        Assert.Equal(lookups.HoldingsId, result.HoldingDelta!.AccountId);
        Assert.Equal(100m,    result.HoldingDelta.QuantityDelta);
        Assert.Equal(100.00m, result.HoldingDelta.CostBasisDelta);

        Assert.NotNull(result.NewLot);
        Assert.Equal(holdings.Id, result.NewLot!.LegId);
        Assert.Equal(100m,        result.NewLot.Quantity);
        Assert.Equal(1.00m,       result.NewLot.UnitCost);
    }

    [Fact]
    public void Buy_uses_per_security_share_decimals_for_quantity_scaling()
    {
        // Regression for the per-security precision bug: mutual funds with
        // dec=5 had their share counts silently 10× wrong because the mapper
        // hardcoded 10^4. Same MD samt of 1,000,000 should mean 100 shares
        // when dec=4, 10 shares when dec=5, 1 share when dec=6.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buy-mf","acctid":"{{Brokerage}}",
              "dt":"20240115",
              "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"1000000","0.pamt":"-10000"
            }
            """);

        // dec=4: 1,000,000 / 10^4 = 100 shares (the prior hardcoded behaviour).
        var stockLookups = BuildLookups(shareDecimals: 4);
        var stockResult = InvestmentTransactionMapper.Map(
            txn, stockLookups.Accounts, stockLookups.SecuritiesByAcct, "test");
        Assert.Null(stockResult.Skip);
        Assert.Equal(100m, stockResult.HoldingDelta!.QuantityDelta);
        Assert.Equal(100m, stockResult.Rows.Single(r => r.AccountId == stockLookups.HoldingsId).Quantity);

        // dec=5: 1,000,000 / 10^5 = 10 shares (mutual-fund precision; the
        // case the old hardcoded divisor mis-scaled by 10×).
        var mfLookups = BuildLookups(shareDecimals: 5);
        var mfResult = InvestmentTransactionMapper.Map(
            txn, mfLookups.Accounts, mfLookups.SecuritiesByAcct, "test");
        Assert.Null(mfResult.Skip);
        Assert.Equal(10m, mfResult.HoldingDelta!.QuantityDelta);
        Assert.Equal(10m, mfResult.Rows.Single(r => r.AccountId == mfLookups.HoldingsId).Quantity);
        // Unit price reflects the corrected quantity: $100 / 10sh = $10/share.
        Assert.Equal(10.00m, mfResult.Rows.Single(r => r.AccountId == mfLookups.HoldingsId).UnitPrice);
    }

    [Fact]
    public void Buy_with_fee_emits_two_pairs_and_apportioned_lot_unit_cost()
    {
        // 100 shares × $1.00 = $100 cost, plus $5.00 commission. 4 rows in 2
        // pairs: sec leg ($100) + fee leg ($5). Cost basis = $105 per IRS,
        // lot.unit_cost apportioned: 105/100 = 1.05.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buy-fee","acctid":"{{Brokerage}}",
              "dt":"20240115",
              "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"1000000","0.pamt":"-10000",
              "1.id":"f","1.acctid":"{{FeeCat}}","1.invest.splittype":"fee",
              "1.samt":"500","1.pamt":"-500"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(4, result.Rows.Count);

        var brokerageCashRows = result.Rows
            .Where(r => r.AccountId == lookups.Accounts[Brokerage].Id)
            .ToList();
        Assert.Equal(2, brokerageCashRows.Count);

        // Cash legs share a group; total = -$105.
        var groupId = brokerageCashRows[0].TxnGroupId;
        Assert.NotNull(groupId);
        Assert.All(brokerageCashRows, r => Assert.Equal(groupId, r.TxnGroupId));
        Assert.Equal(-105.00m, brokerageCashRows.Sum(r => r.FeedAmount));

        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);
        var feeRow   = result.Rows.Single(r => r.AccountId == lookups.Accounts[FeeCat].Id);
        Assert.Equal(100.00m, holdings.FeedAmount);
        Assert.Equal(5.00m,   feeRow.FeedAmount);
        // Fee is on its own paired row; no inline commission field per
        // ADR-0019 Rule 5 (migration 046 dropped txn_legs.commission).

        // Lot cost basis includes commission per IRS.
        Assert.Equal(105.00m, result.HoldingDelta!.CostBasisDelta);
        Assert.Equal(1.05m,   result.NewLot!.UnitCost);
    }

    [Fact]
    public void Sell_emits_cash_holdings_pair_with_negative_quantity()
    {
        // 50 shares × $11.00 = $550 proceeds.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"sell-1","acctid":"{{Brokerage}}",
              "dt":"20240220",
              "invest.txntype":"sell","xfer_type":"xfrtp_buysell",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"-500000","0.pamt":"55000"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(2, result.Rows.Count);

        var cash     = result.Rows.Single(r => r.AccountId == lookups.Accounts[Brokerage].Id);
        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);
        Assert.Equal(550.00m,  cash.FeedAmount);
        Assert.Equal(-550.00m, holdings.FeedAmount);
        Assert.Equal("sell", cash.InvestmentAction);
        Assert.Equal(-50m, holdings.Quantity);
        // Regression: ComputeUnitPrice used to divide positive cash by
        // signed qty, so Sells produced negative unit_price ($-11) and
        // qty × price came out positive while amount was negative. Unit
        // price is now always a magnitude — direction lives in qty + amount.
        Assert.Equal(11.00m, holdings.UnitPrice);

        Assert.Equal(-50m, result.HoldingDelta!.QuantityDelta);
        Assert.Equal(0m,   result.HoldingDelta.CostBasisDelta);    // lot-closing deferred
        Assert.Null(result.NewLot);                                 // no new lot on sell
    }

    [Fact]
    public void Buyx_emits_two_pairs_so_residual_lands_on_brokerage()
    {
        // 100 shares × $1.00 = $100; cash transferred from OtherAcct.
        // Two pairs (sec + xfr) so any sec.pamt/-xfr.pamt residual is
        // captured on the brokerage cash side. For this clean buyx
        // (-100 sec + +100 xfr = 0), the brokerage's net cash impact is
        // zero — but it's the per-row sum that's zero, not "no rows".
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buyx-1","acctid":"{{Brokerage}}",
              "dt":"20240301",
              "invest.txntype":"buyx","xfer_type":"xfrtp_buysellxfr",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"1000000","0.pamt":"-10000",
              "1.id":"xfr","1.acctid":"{{OtherAcct}}","1.invest.splittype":"xfr",
              "1.samt":"-10000","1.pamt":"10000"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(4, result.Rows.Count);

        // Brokerage gets two cash rows that sum to zero (-100 from sec + +100 from xfr).
        var brokerageRows = result.Rows
            .Where(r => r.AccountId == lookups.Accounts[Brokerage].Id)
            .ToList();
        Assert.Equal(2, brokerageRows.Count);
        Assert.Equal(0m, brokerageRows.Sum(r => r.FeedAmount));

        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);
        var other    = result.Rows.Single(r => r.AccountId == lookups.Accounts[OtherAcct].Id);
        Assert.Equal(100.00m,  holdings.FeedAmount);
        Assert.Equal(-100.00m, other.FeedAmount);
        Assert.Equal("buyx",         holdings.InvestmentAction);
        Assert.Equal("transfer", other.InvestmentAction);
        Assert.Equal(100m,           holdings.Quantity);
        Assert.NotNull(result.HoldingDelta);
        Assert.NotNull(result.NewLot);
    }

    [Fact]
    public void MapToHeaderAndLegs_seeds_reconciliation_per_leg_role_on_buyx()
    {
        // buyx: brokerage cleared (parent "X"), external cash UNCLEARED (the
        // xfr split's own "1.stat":" "). Per ADR-0082 the brokerage cash legs
        // follow the parent stat (cleared), the external-cash leg follows its
        // OWN split stat (uncleared -> no overlay row, no flattening), and the
        // Holdings/security leg is never reconciled (no row) even though its
        // sec split carries "X".
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buyx-recon","acctid":"{{Brokerage}}","dt":"20240301","stat":"X",
              "invest.txntype":"buyx","xfer_type":"xfrtp_buysellxfr",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec","0.samt":"1000000","0.pamt":"-10000","0.stat":"X",
              "1.id":"xfr","1.acctid":"{{OtherAcct}}","1.invest.splittype":"xfr","1.samt":"-10000","1.pamt":"10000","1.stat":" "
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.MapToHeaderAndLegs(
            txn, lookups.Accounts, lookups.SecuritiesByAcct,
            ledgerId: Guid.NewGuid(), importSource: "test");

        Assert.Null(result.Skip);
        var seeded = result.LegRecons.ToDictionary(r => r.LegId, r => r.Status);

        // External-cash leg: its own xfr stat is uncleared -> NO seed (the old
        // header-fan would have marked it cleared from the brokerage's parent).
        var extLeg = result.Legs.Single(l => l.AccountId == lookups.Accounts[OtherAcct].Id);
        Assert.False(seeded.ContainsKey(extLeg.Id));

        // Holdings/security leg is never reconciled -> NO seed.
        var holdingsLeg = result.Legs.Single(l => l.AccountId == lookups.HoldingsId);
        Assert.False(seeded.ContainsKey(holdingsLeg.Id));

        // Brokerage cash legs follow the parent stat -> cleared.
        var brokerageLegs = result.Legs.Where(l => l.AccountId == lookups.Accounts[Brokerage].Id).ToList();
        Assert.NotEmpty(brokerageLegs);
        Assert.All(brokerageLegs, l =>
        {
            Assert.True(seeded.ContainsKey(l.Id), "brokerage cash leg should carry a recon seed");
            Assert.Equal("cleared", seeded[l.Id]);
        });
    }

    [Fact]
    public void Sellx_with_residual_proceeds_keeps_change_on_brokerage()
    {
        // Real-world pattern: sale grosses $12,345.69 but only $12,345.66
        // is transferred to the other account. The 3-cent residual stays
        // on the brokerage's cash side, matching MD's pamt accounting.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"sellx-residual","acctid":"{{Brokerage}}",
              "dt":"20240301",
              "invest.txntype":"sellx","xfer_type":"xfrtp_buysellxfr",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"-1000000","0.pamt":"1234569",
              "1.id":"xfr","1.acctid":"{{OtherAcct}}","1.invest.splittype":"xfr",
              "1.samt":"1234566","1.pamt":"-1234566"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        var brokerageRows = result.Rows
            .Where(r => r.AccountId == lookups.Accounts[Brokerage].Id)
            .ToList();
        // Two cash rows on brokerage: +12,345.69 (sec proceeds) − 12,345.66 (xfr out) = +0.03 residual.
        Assert.Equal(0.03m, brokerageRows.Sum(r => r.FeedAmount));
    }

    [Fact]
    public void Sellx_emits_two_pairs_with_clean_amounts_balancing()
    {
        // 50 shares × $11 = $550; proceeds transferred cleanly to OtherAcct.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"sellx-1","acctid":"{{Brokerage}}",
              "dt":"20240301",
              "invest.txntype":"sellx","xfer_type":"xfrtp_buysellxfr",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"-500000","0.pamt":"55000",
              "1.id":"xfr","1.acctid":"{{OtherAcct}}","1.invest.splittype":"xfr",
              "1.samt":"55000","1.pamt":"-55000"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);
        var other    = result.Rows.Single(r => r.AccountId == lookups.Accounts[OtherAcct].Id);
        Assert.Equal(-550.00m, holdings.FeedAmount);
        Assert.Equal(550.00m,  other.FeedAmount);
        Assert.Equal("sellx",       holdings.InvestmentAction);
        Assert.Equal("transfer", other.InvestmentAction);
        Assert.Equal(-50m, holdings.Quantity);
        Assert.Null(result.NewLot);
        // Brokerage cash rows net to zero for clean sellx.
        var brokerageRows = result.Rows
            .Where(r => r.AccountId == lookups.Accounts[Brokerage].Id)
            .ToList();
        Assert.Equal(0m, brokerageRows.Sum(r => r.FeedAmount));
    }

    [Fact]
    public void Div_cash_emits_single_pair_with_security_pinned_to_cash_row()
    {
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"div-1","acctid":"{{Brokerage}}",
              "dt":"20240315",
              "invest.txntype":"div","xfer_type":"xfrtp_dividend",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"0","0.pamt":"0",
              "1.id":"inc","1.acctid":"{{IncomeCat}}","1.invest.splittype":"inc",
              "1.samt":"-12345","1.pamt":"12345"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(2, result.Rows.Count);

        var cash   = result.Rows.Single(r => r.AccountId == lookups.Accounts[Brokerage].Id);
        var income = result.Rows.Single(r => r.AccountId == lookups.Accounts[IncomeCat].Id);
        Assert.Equal(123.45m,  cash.FeedAmount);
        Assert.Equal(-123.45m, income.FeedAmount);
        Assert.Equal("dividend_cash", cash.InvestmentAction);

        // security_id pinned to cash row keeps dividend in the per-security
        // register; quantity/price are NULL because the cash side of a
        // dividend has no per-share semantics (only the buy side of a
        // DivReinvest does). Earlier code wrote 0/0 here, which the
        // SecurityDetailPage rendered as "$0.00 · 0 sh" noise.
        Assert.Equal(lookups.SecurityId, cash.SecurityId);
        Assert.Null(cash.Quantity);
        Assert.Null(cash.UnitPrice);

        Assert.Null(result.HoldingDelta);
        Assert.Null(result.NewLot);
    }

    [Fact]
    public void Divr_reinvest_emits_four_rows_in_two_pairs_with_grouped_cash_legs()
    {
        // The marquee 4-row case: dividend reinvested. Two pairs:
        //   inc pair: cash +$9.89 ↔ income -$9.89
        //   sec pair: cash -$9.89 ↔ holdings +$9.89 (10 shares @ $0.989)
        // Both cash rows share a txn_group_id; net cash on brokerage = 0.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"divr-1","acctid":"{{Brokerage}}",
              "dt":"20240320",
              "invest.txntype":"divr","xfer_type":"xfrtp_dividend",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"100000","0.pamt":"-989",
              "1.id":"inc","1.acctid":"{{IncomeCat}}","1.invest.splittype":"inc",
              "1.samt":"-989","1.pamt":"989"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(4, result.Rows.Count);

        var brokerageCashRows = result.Rows
            .Where(r => r.AccountId == lookups.Accounts[Brokerage].Id)
            .ToList();
        Assert.Equal(2, brokerageCashRows.Count);
        Assert.Equal(0m, brokerageCashRows.Sum(r => r.FeedAmount));   // net 0

        // Both cash legs share a group.
        var groupId = brokerageCashRows[0].TxnGroupId;
        Assert.NotNull(groupId);
        Assert.All(brokerageCashRows, r => Assert.Equal(groupId, r.TxnGroupId));
        Assert.All(brokerageCashRows, r => Assert.Equal("dividend_reinvest", r.InvestmentAction));

        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);
        var income   = result.Rows.Single(r => r.AccountId == lookups.Accounts[IncomeCat].Id);
        Assert.Equal(9.89m,   holdings.FeedAmount);
        Assert.Equal(-9.89m,  income.FeedAmount);
        Assert.Equal(10m,     holdings.Quantity);
        Assert.Equal(0.989m,  holdings.UnitPrice);
        Assert.Null(holdings.TxnGroupId);   // counterparties ungrouped
        Assert.Null(income.TxnGroupId);

        // Holdings delta + lot tied to the holdings-side row.
        Assert.NotNull(result.HoldingDelta);
        Assert.Equal(lookups.HoldingsId, result.HoldingDelta!.AccountId);
        Assert.Equal(10m,    result.HoldingDelta.QuantityDelta);
        Assert.Equal(9.89m,  result.HoldingDelta.CostBasisDelta);

        Assert.NotNull(result.NewLot);
        Assert.Equal(holdings.Id, result.NewLot!.LegId);
        Assert.Equal(0.989m,      result.NewLot.UnitCost);
    }

    [Fact]
    public void Divx_emits_inc_pair_plus_xfer_pair_with_grouped_cash_legs()
    {
        // Dividend received and immediately transferred out. 4 rows:
        //   inc pair: cash +$500 ↔ income -$500
        //   xfr pair: cash -$500 ↔ other +$500
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"divx-1","acctid":"{{Brokerage}}",
              "dt":"20240325",
              "invest.txntype":"divx","xfer_type":"xfrtp_dividendxfr",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"0","0.pamt":"0",
              "1.id":"inc","1.acctid":"{{IncomeCat}}","1.invest.splittype":"inc",
              "1.samt":"-50000","1.pamt":"50000",
              "2.id":"xfr","2.acctid":"{{OtherAcct}}","2.invest.splittype":"xfr",
              "2.samt":"50000","2.pamt":"-50000"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(4, result.Rows.Count);

        var brokerageCashRows = result.Rows
            .Where(r => r.AccountId == lookups.Accounts[Brokerage].Id)
            .ToList();
        Assert.Equal(2, brokerageCashRows.Count);
        Assert.Equal(0m, brokerageCashRows.Sum(r => r.FeedAmount));

        var groupId = brokerageCashRows[0].TxnGroupId;
        Assert.NotNull(groupId);
        Assert.All(brokerageCashRows, r => Assert.Equal(groupId, r.TxnGroupId));

        var income = result.Rows.Single(r => r.AccountId == lookups.Accounts[IncomeCat].Id);
        var other  = result.Rows.Single(r => r.AccountId == lookups.Accounts[OtherAcct].Id);
        Assert.Equal(-500.00m, income.FeedAmount);
        Assert.Equal(500.00m,  other.FeedAmount);
        Assert.Equal("transfer", other.InvestmentAction);
    }

    [Fact]
    public void Bank_transfer_emits_cash_other_pair_no_security()
    {
        // $1000 transferred out of brokerage to OtherAcct.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"bank-1","acctid":"{{Brokerage}}",
              "dt":"20240401",
              "invest.txntype":"bank","xfer_type":"xfrtp_bank",
              "0.id":"xfr","0.acctid":"{{OtherAcct}}","0.invest.splittype":"xfr",
              "0.samt":"100000","0.pamt":"-100000"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(2, result.Rows.Count);
        var cash  = result.Rows.Single(r => r.AccountId == lookups.Accounts[Brokerage].Id);
        var other = result.Rows.Single(r => r.AccountId == lookups.Accounts[OtherAcct].Id);
        Assert.Equal(-1000.00m, cash.FeedAmount);
        Assert.Equal(1000.00m,  other.FeedAmount);
        Assert.Equal("transfer", cash.InvestmentAction);
        Assert.Equal("transfer",  other.InvestmentAction);
        Assert.Null(cash.SecurityId);
        Assert.DoesNotContain(result.Rows, r => r.AccountId == lookups.HoldingsId);
        Assert.Null(result.HoldingDelta);
        Assert.Null(result.NewLot);
    }

    [Fact]
    public void Misc_income_emits_one_pair_per_inc_leg()
    {
        // SHORT-TERM CAP GAIN distribution.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"inc-1","acctid":"{{Brokerage}}",
              "dt":"20240410","desc":"SHORT-TERM CAP GAIN",
              "invest.txntype":"inc","xfer_type":"xfrtp_miscincexp",
              "0.id":"sec","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"0","0.pamt":"0",
              "1.id":"inc","1.acctid":"{{IncomeCat}}","1.invest.splittype":"inc",
              "1.samt":"-42107","1.pamt":"42107"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(2, result.Rows.Count);                         // 1 inc leg → 1 pair

        var cash   = result.Rows.Single(r => r.AccountId == lookups.Accounts[Brokerage].Id);
        var income = result.Rows.Single(r => r.AccountId == lookups.Accounts[IncomeCat].Id);
        Assert.Equal(421.07m,  cash.FeedAmount);
        Assert.Equal(-421.07m, income.FeedAmount);
        Assert.Equal("misc", cash.InvestmentAction);
        Assert.Equal(lookups.SecurityId, cash.SecurityId);
        // Same NULL-on-cash-side contract as the dividend_cash test above.
        Assert.Null(cash.Quantity);
        Assert.Null(cash.UnitPrice);
        Assert.Null(result.HoldingDelta);
    }

    [Fact]
    public void Skip_when_not_an_investment_txn()
    {
        var txn = TxnFromJson($$"""
            {"obj_type":"txn","id":"x","acctid":"{{Brokerage}}","desc":"x","dt":"20240101",
             "0.id":"s","0.acctid":"{{IncomeCat}}","0.samt":"100","0.pamt":"-100"}
            """);
        var lookups = BuildLookups();
        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");
        Assert.Equal(InvestmentTransactionMapper.SkipReason.NotInvestmentTxn, result.Skip);
    }

    [Fact]
    public void Skip_when_brokerage_has_no_holdings_sibling()
    {
        // AccountImportStep is supposed to wire HoldingsAccountId on every
        // investment account; if it didn't, the mapper has nowhere to put
        // holdings-side legs and refuses to fabricate them.
        var lookups = BuildLookups();
        var brokerageWithoutSibling = lookups.Accounts[Brokerage] with { HoldingsAccountId = null };
        var accounts = lookups.Accounts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        accounts[Brokerage] = brokerageWithoutSibling;

        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buy-x","acctid":"{{Brokerage}}","dt":"20240115",
              "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"1000","0.pamt":"-100"
            }
            """);

        var result = InvestmentTransactionMapper.Map(
            txn, accounts, lookups.SecuritiesByAcct, "test");
        Assert.Equal(InvestmentTransactionMapper.SkipReason.BrokerageMissingHoldingsSibling, result.Skip);
    }

    [Fact]
    public void Skip_when_security_acct_not_in_lookup()
    {
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buy-x","acctid":"{{Brokerage}}","dt":"20240115",
              "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
              "0.id":"s","0.acctid":"unknown-sec-acct","0.invest.splittype":"sec",
              "0.samt":"100000","0.pamt":"-100000"
            }
            """);
        var lookups = BuildLookups();
        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");
        Assert.Equal(InvestmentTransactionMapper.SkipReason.UnknownSecurity, result.Skip);
    }

    [Fact]
    public void Skip_when_unknown_shape_pair()
    {
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"weird","acctid":"{{Brokerage}}","dt":"20240101",
              "invest.txntype":"madeup","xfer_type":"xfrtp_madeup",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"1","0.pamt":"-1"
            }
            """);
        var lookups = BuildLookups();
        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");
        Assert.Equal(InvestmentTransactionMapper.SkipReason.UnknownShape, result.Skip);
    }

    [Fact]
    public void Untyped_dividend_with_xfr_split_is_treated_as_divx()
    {
        // QIF-imported "IntIncX"-style: xfer_type=xfrtp_dividend (canonical
        // for div/divr) but the txn carries both inc and xfr splits — the
        // user's QIF source called these "Interest Income with Transfer."
        // The classifier's cross-validation rule (xfrtp_dividend with xfr
        // split → divx) promotes the action so the xfr pair lands; without
        // it the txn would import as a plain div and the destination
        // account's balance would be short.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"intincx","acctid":"{{Brokerage}}",
              "dt":"19981231","xfer_type":"xfrtp_dividend",
              "0.id":"inc","0.acctid":"{{IncomeCat}}","0.invest.splittype":"inc",
              "0.samt":"-25271","0.pamt":"25271",
              "1.id":"xfr","1.acctid":"{{OtherAcct}}","1.invest.splittype":"xfr",
              "1.samt":"25271","1.pamt":"-25271"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        // 4 rows: inc pair (cash + income) + xfr pair (cash + other).
        Assert.Equal(4, result.Rows.Count);

        // OtherAcct receives the dividend cash transfer.
        var other = result.Rows.Single(r => r.AccountId == lookups.Accounts[OtherAcct].Id);
        Assert.Equal(252.71m,        other.FeedAmount);
        Assert.Equal("transfer",  other.InvestmentAction);

        // Brokerage cash rows net to zero (income +252.71, transfer -252.71).
        var brokerageRows = result.Rows
            .Where(r => r.AccountId == lookups.Accounts[Brokerage].Id)
            .ToList();
        Assert.Equal(0m, brokerageRows.Sum(r => r.FeedAmount));
    }

    [Fact]
    public void Self_referential_sellx_books_proceeds_and_balances()
    {
        // SellX whose xfr target is the primary brokerage itself
        // (share-class exchange / fee-funding sale). The sale produces real
        // cash that funds the paired buy or fee leg (which lives in its own
        // header / split), so the proceeds GENUINELY sit in the brokerage:
        // the sec pair books cash +proceeds / Holdings -proceeds, the
        // self-loop xfr leg is skipped, and the header BALANCES. Regression
        // guard for ADR-0052 D4 (a prior version zeroed the cash, dropping
        // the proceeds and unbalancing every self-ref buyx/sellx).
        var lookups = BuildLookups();
        var primaryAcctId = lookups.Accounts[Brokerage].Id;
        // Xfr target = primary brokerage (self-ref).
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"sellx-self","acctid":"{{Brokerage}}",
              "dt":"20100803","xfer_type":"xfrtp_buysellxfr",
              "0.id":"xfr","0.acctid":"{{Brokerage}}","0.invest.splittype":"xfr",
              "0.samt":"118364","0.pamt":"-118364",
              "1.id":"sec","1.acctid":"{{SecAcct}}","1.invest.splittype":"sec",
              "1.samt":"-247930","1.pamt":"118364"
            }
            """);

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        // Two rows: brokerage cash carrying the proceeds, paired with the
        // Holdings sibling that carries the share reduction.
        Assert.Equal(2, result.Rows.Count);

        var brokerageCash = result.Rows.Single(r => r.AccountId == primaryAcctId);
        Assert.Equal(1183.64m, brokerageCash.FeedAmount);             // proceeds land in brokerage cash

        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);
        Assert.Equal(-1183.64m, holdings.FeedAmount);                 // asset out
        Assert.Equal(-24.793m,  holdings.Quantity);                   // shares disposed
        // Per ADR-0027: `sellx` is a first-class action; the self-ref
        // edge case books the proceeds normally and does not downgrade
        // the action's semantic. (Was "sell" pre-refactor.)
        Assert.Equal("sellx",   holdings.InvestmentAction);

        // Double-entry: the whole header sums to zero.
        Assert.Equal(0m, result.Rows.Sum(r => r.FeedAmount));
    }

    [Fact]
    public void Self_referential_buyx_books_payment_and_balances()
    {
        // Buy side of a self-ref share-class exchange: shares acquired,
        // cash paid out of the same brokerage. sec.pamt < 0 (cash out), so
        // the sec pair books cash -payment / Holdings +payment; the
        // self-loop xfr leg is skipped; the header balances. Companion to
        // the sellx regression guard above (ADR-0052 D4).
        var lookups = BuildLookups();
        var primaryAcctId = lookups.Accounts[Brokerage].Id;
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"buyx-self","acctid":"{{Brokerage}}",
              "dt":"20100803","xfer_type":"xfrtp_buysellxfr",
              "0.id":"xfr","0.acctid":"{{Brokerage}}","0.invest.splittype":"xfr",
              "0.samt":"-118364","0.pamt":"118364",
              "1.id":"sec","1.acctid":"{{SecAcct}}","1.invest.splittype":"sec",
              "1.samt":"247930","1.pamt":"-118364"
            }
            """);

        var result = InvestmentTransactionMapper.Map(
            txn, lookups.Accounts, lookups.SecuritiesByAcct, "test");

        Assert.Null(result.Skip);
        Assert.Equal(2, result.Rows.Count);

        var brokerageCash = result.Rows.Single(r => r.AccountId == primaryAcctId);
        Assert.Equal(-1183.64m, brokerageCash.FeedAmount);            // payment leaves cash

        var holdings = result.Rows.Single(r => r.AccountId == lookups.HoldingsId);
        Assert.Equal(1183.64m, holdings.FeedAmount);                  // asset in
        Assert.Equal(24.793m,  holdings.Quantity);                    // shares acquired
        Assert.Equal("buyx",   holdings.InvestmentAction);

        // Double-entry: the whole header sums to zero.
        Assert.Equal(0m, result.Rows.Sum(r => r.FeedAmount));
    }

    [Fact]
    public void MiscIncome_with_single_split_returns_one_header()
    {
        // Baseline: MD MiscInc with one inc split — single-posting,
        // single header.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"misc-1","acctid":"{{Brokerage}}",
              "desc":"Broker rebate","dt":"20240301",
              "invest.txntype":"inc","xfer_type":"xfrtp_miscincexp",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"0","0.pamt":"0",
              "1.id":"inc","1.acctid":"{{IncomeCat}}","1.invest.splittype":"inc",
              "1.samt":"-2000","1.pamt":"2000"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.MapToHeaderAndLegs(
            txn, lookups.Accounts, lookups.SecuritiesByAcct,
            ledgerId: Guid.NewGuid(), importSource: "test");

        Assert.NotNull(result.Header);
        Assert.Equal("misc", result.Header!.Action);
        // External_id is the MD txn id, unsuffixed.
        Assert.Equal("misc-1", result.Header.ExternalId);
        // Single posting (posting_index=0) with 2 legs.
        Assert.Equal(2, result.Legs.Count);
        Assert.All(result.Legs, l => Assert.Equal(0, l.PostingIndex));
    }

    [Fact]
    public void MiscIncome_with_fee_split_returns_single_header_with_two_postings()
    {
        // MD's `inc` txn with both `inc` and `fee` splits is the standard
        // MiscInc-with-fee shape (user-creatable in MD UI). Pre-061 this
        // shape was fanned out into 2 separate single-posting headers
        // ("Path B") under a single-posting MiscInc invariant. The
        // invariant was based on a misread of MD's data — see
        // docs/moneydance-investment-actions.md and migration 061.
        // Post-061 behavior: one header, two postings.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"misc-2","acctid":"{{Brokerage}}",
              "desc":"Quarterly statement","dt":"20240401",
              "invest.txntype":"inc","xfer_type":"xfrtp_miscincexp",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"0","0.pamt":"0",
              "1.id":"i1","1.acctid":"{{IncomeCat}}","1.invest.splittype":"inc",
              "1.samt":"-1000","1.pamt":"1000",
              "2.id":"f1","2.acctid":"{{FeeCat}}","2.invest.splittype":"fee",
              "2.samt":"500","2.pamt":"-500"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.MapToHeaderAndLegs(
            txn, lookups.Accounts, lookups.SecuritiesByAcct,
            ledgerId: Guid.NewGuid(), importSource: "test");

        Assert.NotNull(result.Header);
        Assert.Equal("misc", result.Header!.Action);
        Assert.Equal("misc-2", result.Header.ExternalId);

        // Two postings (income + fee) under one header. Each posting
        // = 2 legs sharing the same posting_index.
        var postingIndexes = result.Legs.Select(l => l.PostingIndex).Distinct().OrderBy(i => i).ToList();
        Assert.Equal(new[] { 0, 1 }, postingIndexes);
        Assert.Equal(4, result.Legs.Count);

        // posting_role on the legs reflects the split type from MD.
        var incomeLegs = result.Legs.Where(l => l.PostingRole == "income").ToList();
        var feeLegs    = result.Legs.Where(l => l.PostingRole == "fee").ToList();
        Assert.Equal(2, incomeLegs.Count);
        Assert.Equal(2, feeLegs.Count);
    }

    [Fact]
    public void Exp_txntype_maps_to_misc_action_with_income_posting_role()
    {
        // MD's `exp` txntype emits `[sec, exp]` (no fee) or `[sec, fee, exp]`
        // (with fee). Per ADR-0027: MD's `inc` AND `exp` splittypes BOTH
        // stamp posting_role='income' (the "main category" role).
        // Direction (income vs expense) is the sign on the brokerage-
        // cash-side amount, not the role. Only MD's `fee` splittype
        // stamps posting_role='fee'.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"exp-1","acctid":"{{Brokerage}}",
              "desc":"Custodial fee","dt":"20240515",
              "invest.txntype":"exp","xfer_type":"xfrtp_miscincexp",
              "0.id":"s","0.acctid":"{{SecAcct}}","0.invest.splittype":"sec",
              "0.samt":"0","0.pamt":"0",
              "1.id":"exp1","1.acctid":"{{FeeCat}}","1.invest.splittype":"exp",
              "1.samt":"1500","1.pamt":"-1500"
            }
            """);
        var lookups = BuildLookups();

        var result = InvestmentTransactionMapper.MapToHeaderAndLegs(
            txn, lookups.Accounts, lookups.SecuritiesByAcct,
            ledgerId: Guid.NewGuid(), importSource: "test");

        Assert.NotNull(result.Header);
        Assert.Equal("misc", result.Header!.Action);
        // Single posting (posting_index=0) with 2 legs — both carry
        // posting_role='income'. The brokerage-cash-side leg has
        // amount=-1500 (negative = expense direction); the category
        // side has amount=+1500.
        Assert.Equal(2, result.Legs.Count);
        Assert.All(result.Legs, l => Assert.Equal(0, l.PostingIndex));
        Assert.All(result.Legs, l => Assert.Equal("income", l.PostingRole));
        // -1500 MD minor units → -15.00 decimal; sign discriminates expense.
        var cashLeg = result.Legs.Single(l => l.Amount < 0);
        Assert.Equal(-15m, cashLeg.Amount);
    }
}
