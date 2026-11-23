using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Tests for the ADR-0022 header + legs translation. Each MD txn maps to
/// one <see cref="TxnHeaderRow"/> carrying the event envelope plus two
/// <see cref="TxnLegRow"/> per MD split (one on each account; paired
/// structurally via shared <c>PostingIndex</c>). Tests assert header
/// fields once, leg count per posting, leg amount sign-pairing, and the
/// memo/payee precedence (ADR-0022).
/// </summary>
public sealed class TransactionMapperTests
{
    private static readonly Guid TestLedgerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

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

    /// <summary>
    /// Build an account-id lookup. Every entry passed via <paramref name="categoryMdIds"/>
    /// is registered as a <c>category</c>; everything else is treated as a real
    /// (non-category) account so cross-account-transfer logic kicks in.
    /// </summary>
    private static IReadOnlyDictionary<string, AccountRef> AccountMap(
        IEnumerable<string>? categoryMdIds = null,
        params string[] mdIds)
    {
        var categories = new HashSet<string>(categoryMdIds ?? [], StringComparer.Ordinal);
        return mdIds.ToDictionary(
            id => id,
            id => new AccountRef(Guid.NewGuid(), categories.Contains(id) ? "category" : "bank", AmountDecimals: 2),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Find one leg on each side of a posting (origin = primary account,
    /// counterpart = the other account). Uses <c>PostingIndex</c> as the
    /// structural pair key; siblings inside one header sharing the same
    /// posting_index are the two sides of that posting.
    /// </summary>
    private static (TxnLegRow Origin, TxnLegRow Counterpart) PairFor(
        TransactionMapper.MapResult result, Guid primaryAccountId, int postingIndex)
    {
        var origin = result.Legs.Single(l =>
            l.AccountId == primaryAccountId && l.PostingIndex == postingIndex);
        var counterpart = result.Legs.Single(l =>
            l.AccountId != primaryAccountId && l.PostingIndex == postingIndex);
        return (origin, counterpart);
    }

    [Fact]
    public void Map_stamps_the_home_account_as_the_originating_account()
    {
        // Migration 263: the Moneydance home (acctid), literally — on a
        // one-split to another real account and on a split whose lines go elsewhere.
        var transfer = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-own-1","acctid":"a-home","desc":"Payee A",
              "dt":"20240102","stat":" ",
              "0.id":"s-0","0.acctid":"a-other","0.samt":"-500","0.pamt":"500"
            }
            """);
        var split = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-own-2","acctid":"a-home","desc":"Payee A",
              "dt":"20240102","stat":" ",
              "0.id":"s-0","0.acctid":"a-cat","0.samt":"300","0.pamt":"-300",
              "1.id":"s-1","1.acctid":"a-other","1.samt":"200","1.pamt":"-200"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-home", "a-other", "a-cat");

        var one = TransactionMapper.Map(transfer, accounts, TestLedgerId, importSource: "test");
        var many = TransactionMapper.Map(split, accounts, TestLedgerId, importSource: "test");

        Assert.Equal(accounts["a-home"].Id, one.Header!.OriginatingAccountId);
        Assert.Equal(accounts["a-home"].Id, many.Header!.OriginatingAccountId);
    }

    [Fact]
    public void Map_emits_a_pair_for_a_single_category_split()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-1","acctid":"a-checking",
              "desc":"Fuel Stop","memo":"DOE,JOHN",
              "dt":"20191214","td":"20191214","stat":"X",
              "ol.orig-payee":"FUEL STOP 0000000000",
              "0.id":"s-0","0.acctid":"a-gas","0.samt":"4250","0.pamt":"-4250"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-gas"], "a-checking", "a-gas");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.NotNull(result.Header);
        Assert.Equal(2, result.Legs.Count);

        // Header carries the event envelope — payee/status/external_id — once.
        // Curated Description wins over raw OFX ol.orig-payee. A one-split has no
        // header memo: the memo is the entered side's own (below).
        Assert.Equal(TestLedgerId,    result.Header.LedgerId);
        Assert.Equal("Fuel Stop",        result.Header.Payee);
        Assert.Null(result.Header.Memo);
        // Migration 030: MD's raw "X" letter-code maps to the normalized
        // "cleared" state; the mapper also stamps cleared_at to satisfy
        // the DB CHECK (status='cleared') ⇔ (cleared_at IS NOT NULL).
        Assert.Equal("cleared", result.Header.Status);
        Assert.NotNull(result.Header.ClearedAt);
        Assert.Equal("t-1",           result.Header.ExternalId);

        var origin = result.Legs.Single(l => l.AccountId == accounts["a-checking"].Id);
        var counterpart = result.Legs.Single(l => l.AccountId == accounts["a-gas"].Id);

        // Origin posts the cash impact; counterpart mirrors with split_amount.
        Assert.Equal(-42.50m, origin.Amount);
        Assert.Equal(42.50m,  counterpart.Amount);

        // Posting pairing is structural: shared posting_index within the
        // header, distinct accounts. No counterparty_id denormalisation.
        Assert.Equal(0, origin.PostingIndex);
        Assert.Equal(0, counterpart.PostingIndex);
        Assert.Equal(result.Header.Id, origin.HeaderId);
        Assert.Equal(result.Header.Id, counterpart.HeaderId);

        // One-split: the entered side holds the memo; the other side has no
        // 0.desc of its own, so it holds nothing and shows the memo inherited.
        Assert.Equal("DOE,JOHN", origin.LegMemo);
        Assert.Null(counterpart.LegMemo);
    }

    [Fact]
    public void Map_seeds_reconciliation_per_leg_when_only_the_parent_side_is_cleared()
    {
        // Transfer Checking -> Savings, cleared on the Checking (parent) side
        // ("stat":"X") but still uncleared on the Savings (split) side
        // ("0.stat":"") — the per-account state MD tracks that the old
        // header-fan flattened onto both legs (ADR-0082).
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-xfer","acctid":"a-checking",
              "desc":"Move to savings","dt":"20191214","td":"20191214","stat":"X",
              "0.id":"s-0","0.acctid":"a-savings","0.samt":"10000","0.pamt":"-10000","0.stat":""
            }
            """);
        var accounts = AccountMap(null, "a-checking", "a-savings");   // both real

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        var (origin, counterpart) = PairFor(result, accounts["a-checking"].Id, postingIndex: 0);

        // Exactly one seed: the Checking (origin) leg is cleared; the Savings
        // (counterpart) leg gets NO seed -> reads uncleared. No flattening.
        var seed = Assert.Single(result.LegRecons);
        Assert.Equal(origin.Id, seed.LegId);
        Assert.Equal("cleared", seed.Status);
        Assert.NotNull(seed.ClearedAt);
        Assert.DoesNotContain(result.LegRecons, r => r.LegId == counterpart.Id);
    }

    [Fact]
    public void Map_seeds_the_counterparty_leg_when_only_the_split_side_is_cleared()
    {
        // Reverse: uncleared on Checking (parent), cleared on Savings (split).
        // The seed must land on the counterparty leg, not the origin.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-xfer2","acctid":"a-checking",
              "desc":"Move to savings","dt":"20191214","td":"20191214","stat":"",
              "0.id":"s-0","0.acctid":"a-savings","0.samt":"10000","0.pamt":"-10000","0.stat":"X"
            }
            """);
        var accounts = AccountMap(null, "a-checking", "a-savings");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        var (origin, counterpart) = PairFor(result, accounts["a-checking"].Id, postingIndex: 0);

        var seed = Assert.Single(result.LegRecons);
        Assert.Equal(counterpart.Id, seed.LegId);
        Assert.Equal("cleared", seed.Status);
        Assert.DoesNotContain(result.LegRecons, r => r.LegId == origin.Id);
    }

    [Fact]
    public void Map_emits_distinct_posting_indexes_for_multi_split_txn()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-2","acctid":"a-cash","desc":"Multi","dt":"20240101",
              "0.id":"s0","0.acctid":"a-rent",   "0.samt":"100000","0.pamt":"-100000",
              "1.id":"s1","1.acctid":"a-utility","1.samt":"15000", "1.pamt":"-15000",
              "2.id":"s2","2.acctid":"a-net",    "2.samt":"6000",  "2.pamt":"-6000"
            }
            """);
        var accounts = AccountMap(
            categoryMdIds: ["a-rent", "a-utility", "a-net"],
            "a-cash", "a-rent", "a-utility", "a-net");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.NotNull(result.Header);
        Assert.Equal(6, result.Legs.Count);              // 3 postings × 2 legs

        var originLegs = result.Legs
            .Where(l => l.AccountId == accounts["a-cash"].Id)
            .OrderBy(l => l.PostingIndex)
            .ToList();
        Assert.Equal(3, originLegs.Count);
        Assert.Equal(new[] { 0, 1, 2 }, originLegs.Select(l => l.PostingIndex).ToArray());

        // All origin legs reference the same header.
        Assert.All(originLegs, l => Assert.Equal(result.Header.Id, l.HeaderId));

        // Origin amounts mirror parent_amount; counterparts mirror split_amount.
        Assert.Equal(new[] { -1000.00m, -150.00m, -60.00m },
            originLegs.Select(l => l.Amount).ToArray());

        var rentLeg = result.Legs.Single(l => l.AccountId == accounts["a-rent"].Id);
        Assert.Equal(1000.00m, rentLeg.Amount);
        Assert.Equal(0, rentLeg.PostingIndex);
    }

    [Fact]
    public void Map_emits_a_pair_for_a_bank_to_bank_transfer()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"xfer-1","acctid":"a-checking",
              "desc":"Move to savings","dt":"20240315",
              "0.id":"s0","0.acctid":"a-savings","0.samt":"50000","0.pamt":"-50000"
            }
            """);
        var accounts = AccountMap(categoryMdIds: null, "a-checking", "a-savings");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.NotNull(result.Header);
        Assert.Equal(2, result.Legs.Count);

        var (origin, counterpart) = PairFor(result, accounts["a-checking"].Id, postingIndex: 0);

        Assert.Equal(-500.00m, origin.Amount);
        Assert.Equal(500.00m,  counterpart.Amount);
        Assert.Equal("xfer-1", result.Header.ExternalId);
    }

    [Fact]
    public void Map_emits_one_posting_per_split_with_distinct_indexes()
    {
        // Hybrid: one outflow leaves CHECKING, part to RENT (category), the
        // rest to SAVINGS (non-category). Both legs become full pairs (one
        // posting each, distinct posting_index).
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"hybrid","acctid":"a-checking","desc":"Hybrid","dt":"20240101",
              "0.id":"s0","0.acctid":"a-rent",   "0.samt":"30000","0.pamt":"-30000",
              "1.id":"s1","1.acctid":"a-savings","1.samt":"70000","1.pamt":"-70000"
            }
            """);
        var accounts = AccountMap(
            categoryMdIds: ["a-rent"],
            "a-checking", "a-rent", "a-savings");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.NotNull(result.Header);
        Assert.Equal(4, result.Legs.Count);              // 2 postings × 2 legs

        var rentLeg    = result.Legs.Single(l => l.AccountId == accounts["a-rent"].Id);
        var savingsLeg = result.Legs.Single(l => l.AccountId == accounts["a-savings"].Id);
        Assert.Equal(300.00m, rentLeg.Amount);
        Assert.Equal(700.00m, savingsLeg.Amount);

        // Distinct posting_index lets multiple postings to different
        // targets coexist within one header. Both postings share the
        // header but their indexes pair the two legs of each.
        Assert.Equal(0, rentLeg.PostingIndex);
        Assert.Equal(1, savingsLeg.PostingIndex);

        // All four legs reference the same header.
        Assert.All(result.Legs, l => Assert.Equal(result.Header.Id, l.HeaderId));
    }

    [Fact]
    public void Map_skips_investment_txn()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-inv","acctid":"a-broker","desc":"BUY","dt":"20240101",
              "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
              "0.id":"s0","0.acctid":"sec-x","0.samt":"100","0.pamt":"-100",
              "0.invest.splittype":"sec"
            }
            """);
        var accounts = AccountMap(categoryMdIds: null, "a-broker");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Header);
        Assert.Empty(result.Legs);
        Assert.Equal(TransactionMapper.SkipReason.InvestmentTxn, result.Skip);
    }

    [Fact]
    public void Map_skips_when_primary_account_was_filtered()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-orphan","acctid":"unknown-acct","desc":"x","dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Header);
        Assert.Empty(result.Legs);
        Assert.Equal(TransactionMapper.SkipReason.UnknownPrimaryAccount, result.Skip);
    }

    [Fact]
    public void Map_skips_when_a_split_references_a_filtered_account()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-bad-split","acctid":"a-cash","desc":"x","dt":"20240101",
              "0.id":"s","0.acctid":"vanished","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = AccountMap(categoryMdIds: null, "a-cash");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Header);
        Assert.Empty(result.Legs);
        Assert.Equal(TransactionMapper.SkipReason.UnknownSplitAccount, result.Skip);
    }

    [Fact]
    public void Map_prefers_user_curated_description_over_ol_orig_payee()
    {
        // ol.orig-payee is the raw OFX original; Description is what the
        // user sees in MD after any merge/cleanup. Preferring the original
        // shows the bank's noisy payee instead of the curated one, so
        // Description wins; ol.orig-payee is only a fallback for entries
        // with no Description set.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-merged","acctid":"a-cash",
              "desc":"Fuel Stop","memo":"DOE,JOHN",
              "ol.orig-payee":"FUEL STOP 0000000000","ol.orig-memo":"raw memo",
              "dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("Fuel Stop",          result.Header!.Payee);   // curated, not raw
        var (origin, _) = PairFor(result, accounts["a-cash"].Id, postingIndex: 0);
        Assert.Equal("DOE,JOHN", origin.LegMemo);                   // curated, not raw
    }

    [Fact]
    public void Map_falls_back_to_ol_orig_payee_when_description_is_blank()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-no-desc","acctid":"a-cash",
              "desc":"","ol.orig-payee":"FUEL STOP 0000000000",
              "dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("FUEL STOP 0000000000", result.Header!.Payee);
    }

    [Fact]
    public void Map_uses_description_as_payee_when_ol_payee_is_absent()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-manual","acctid":"a-cash",
              "desc":"Coffee","dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("Coffee", result.Header!.Payee);
    }

    [Fact]
    public void Map_carries_per_split_memo_on_each_origin_leg_when_multi_split()
    {
        // The leg memo is per-leg on multi-splits and NULL on
        // single-splits (ADR-0022): legs on a multi-split paycheck carry their distinct
        // memo (Salary / Federal Tax / Medicare Tax) on both sides of
        // the pair.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"paycheck","acctid":"a-checking",
              "desc":"EXAMPLE EMPLOYER","memo":"Electronic/ACH Credit","dt":"20260420",
              "0.id":"s0","0.acctid":"a-wages-base","0.samt":"-500000","0.pamt":"500000","0.desc":"Salary",
              "1.id":"s1","1.acctid":"a-tax-fed",   "1.samt":"75000",  "1.pamt":"-75000","1.desc":"Federal Tax",
              "2.id":"s2","2.acctid":"a-tax-medi",  "2.samt":"7250",   "2.pamt":"-7250", "2.desc":"Medicare Tax"
            }
            """);
        var accounts = AccountMap(
            categoryMdIds: ["a-wages-base", "a-tax-fed", "a-tax-medi"],
            "a-checking", "a-wages-base", "a-tax-fed", "a-tax-medi");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        // Header carries the umbrella memo ("Electronic/ACH Credit"); legs
        // carry the per-split memo.
        Assert.Equal("EXAMPLE EMPLOYER",      result.Header!.Payee);
        Assert.Equal("Electronic/ACH Credit", result.Header.Memo);

        var originLegs = result.Legs
            .Where(l => l.AccountId == accounts["a-checking"].Id)
            .OrderBy(l => l.PostingIndex)
            .ToList();
        Assert.Equal(3, originLegs.Count);
        Assert.Equal(
            new[] { "Salary", "Federal Tax", "Medicare Tax" },
            originLegs.Select(l => l.LegMemo).ToArray());
    }

    [Fact]
    public void Map_per_split_memo_sits_on_the_origin_leg_only()
    {
        // The category-side register (e.g. "Taxes:Federal Income Tax") shows the
        // line memo inherited from the origin leg: the opposite side of a split
        // line is read-only, so a copy there would only freeze a stale value.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"paycheck-2","acctid":"a-checking",
              "desc":"EXAMPLE EMPLOYER","memo":"Electronic/ACH Credit","dt":"20260420",
              "0.id":"s0","0.acctid":"a-tax-fed","0.samt":"75000","0.pamt":"-75000","0.desc":"Federal Tax",
              "1.id":"s1","1.acctid":"a-tax-medi","1.samt":"7250","1.pamt":"-7250","1.desc":"Medicare Tax"
            }
            """);
        var accounts = AccountMap(
            categoryMdIds: ["a-tax-fed", "a-tax-medi"],
            "a-checking", "a-tax-fed", "a-tax-medi");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);

        var (origin, counterpart) = PairFor(result, accounts["a-checking"].Id, postingIndex: 0);

        Assert.Equal("Federal Tax", origin.LegMemo);
        Assert.Null(counterpart.LegMemo);
    }

    [Fact]
    public void Map_single_split_ignores_a_split_desc_that_is_only_the_payee_copy()
    {
        // MD defaults a single-split's `0.desc` to the parent's `desc` (payee).
        // Storing that copy as the other side's own memo would echo the payee into
        // its memo column, so it is no value: the other side inherits the memo.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-single","acctid":"a-cash",
              "desc":"Fuel Stop","memo":"DOE,JOHN","dt":"20240101",
              "0.id":"s","0.acctid":"a-gas","0.samt":"500","0.pamt":"-500","0.desc":"Fuel Stop"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-gas"], "a-cash", "a-gas");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("Fuel Stop",          result.Header!.Payee);
        Assert.Null(result.Header.Memo);
        var (origin, counterpart) = PairFor(result, accounts["a-cash"].Id, postingIndex: 0);
        Assert.Equal("DOE,JOHN", origin.LegMemo);
        Assert.Null(counterpart.LegMemo);
    }

    [Fact]
    public void Map_uses_dt_for_posted_at_ignoring_dtentered()
    {
        // `dt` (the date the user assigned) wins over `dtentered` (when they
        // typed the transaction). Preferring `dtentered` would break
        // future-dated and back-dated entries.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-time","acctid":"a-cash","desc":"x","dt":"20191214",
              "dtentered":"1576801234567",
              "0.id":"s","0.acctid":"a-cat","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal(2019, result.Header!.PostedAt.Year);
        Assert.Equal(12,   result.Header.PostedAt.Month);
        Assert.Equal(14,   result.Header.PostedAt.Day);
        Assert.Equal(TimeSpan.Zero, result.Header.PostedAt.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, result.Header.PostedAt.Offset);
    }

    [Fact]
    public void Map_uses_dt_when_dt_is_in_the_future_relative_to_dtentered()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-future","acctid":"a-cash","desc":"City Utility",
              "dt":"20260518","dtentered":"1777000000000",
              "0.id":"s","0.acctid":"a-cat","0.samt":"12345","0.pamt":"-12345"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal(2026, result.Header!.PostedAt.Year);
        Assert.Equal(5,    result.Header.PostedAt.Month);
        Assert.Equal(18,   result.Header.PostedAt.Day);
    }

    [Theory]
    [InlineData(20240115, 2024, 1, 15)]
    [InlineData(19710101, 1971, 1, 1)]
    [InlineData(20191231, 2019, 12, 31)]
    public void ParseMdDate_handles_valid_dates(int yyyymmdd, int year, int month, int day)
    {
        var parsed = TransactionMapper.ParseMdDate(yyyymmdd);
        Assert.NotNull(parsed);
        Assert.Equal(year,  parsed!.Value.Year);
        Assert.Equal(month, parsed.Value.Month);
        Assert.Equal(day,   parsed.Value.Day);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99999999)]
    [InlineData(20240230)]
    [InlineData(20241301)]
    [InlineData(20240132)]
    public void ParseMdDate_returns_null_for_invalid_inputs(int yyyymmdd)
    {
        Assert.Null(TransactionMapper.ParseMdDate(yyyymmdd));
    }

    [Fact]
    public void ParseMdDate_returns_null_for_missing_input()
    {
        Assert.Null(TransactionMapper.ParseMdDate(null));
    }

    [Fact]
    public void Map_binds_each_splits_tags_to_that_splits_leg_with_the_shared_set_merged_in()
    {
        // The case per-posting tags exist for: one header, postings that are
        // about different things — a payment split across two projects, each
        // line tagged with its project. A union at header level cannot say
        // which posting "Project B" was about, which is the only question the
        // tag was recording. Moneydance's transaction-level set belongs to every
        // line's other side, which is read-only on a split, so each line takes it.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-split","acctid":"a-cash","desc":"supplies","dt":"20240101",
              "tags":"work",
              "0.id":"s0","0.acctid":"a-cat","0.samt":"100","0.pamt":"-100","0.tags":"Project A",
              "1.id":"s1","1.acctid":"a-cat","1.samt":"50","1.pamt":"-50","1.tags":"Project B"
            }
            """);

        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");
        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.NotNull(result.Header);
        Assert.Empty(result.HeaderTags);

        Assert.Equal(2, result.LegTags.Count);
        var byTag = result.LegTags.ToDictionary(x => x.Tags[0], x => x);
        Assert.Equal(["Project A", "work"], byTag["Project A"].Tags);
        Assert.Equal(["Project B", "work"], byTag["Project B"].Tags);

        // Two DIFFERENT legs, both origin legs this map actually produced —
        // otherwise the pairing is a tuple that happens to typecheck.
        Assert.NotEqual(byTag["Project A"].LegId, byTag["Project B"].LegId);
        var originLegIds = result.Legs.Where(l => l.AccountId == accounts["a-cash"].Id).Select(l => l.Id).ToHashSet();
        Assert.Contains(byTag["Project A"].LegId, originLegIds);
        Assert.Contains(byTag["Project B"].LegId, originLegIds);
    }

    [Fact]
    public void Map_writes_no_tags_when_nothing_is_tagged()
    {
        var txn = TxnFromJson("""
            {"obj_type":"txn","id":"t-bare","acctid":"a-cash","desc":"x","dt":"20240101",
             "0.id":"s","0.acctid":"a-cat","0.samt":"100","0.pamt":"-100"}
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Empty(result.HeaderTags);
        Assert.Empty(result.LegTags);
    }

    [Fact]
    public void Map_propagates_check_number_to_the_header()
    {
        // Per ADR-0022 check number is header-level (an event property),
        // not per-leg. Before ADR-0022 every paired row carried a
        // duplicated copy.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"chk-1","acctid":"a-checking",
              "desc":"Mortgage Co","dt":"20240115","chk":"1042",
              "0.id":"s","0.acctid":"a-mortgage","0.samt":"150000","0.pamt":"-150000"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-mortgage"], "a-checking", "a-mortgage");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("1042", result.Header!.CheckNumber);
    }

    [Fact]
    public void Map_preserves_ofx_online_match_identity_on_header()
    {
        // Migration 034 / mig 109: the OFX dedup composite key
        // (fitid + fi_id) round-trips onto the header columns so
        // SimpleFIN sync can dedup. The audit-only status / type /
        // orig_id columns were dropped in mig 109 (ADR-0035 §4)
        // and now live inside ProviderRawPayload as raw JSON.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"ofx-1","acctid":"a-checking",
              "desc":"Example Grocer","dt":"20240115","stat":"X",
              "ol_fitid_1":"T20240115-9912",
              "ol_fi_id":"FI-EXAMPLEBANK",
              "ol.match-status":"matched-by-fitid",
              "ol.match-type":"auto",
              "ol.orig-txn":"orig-feed-item-42",
              "0.id":"s","0.acctid":"a-groceries","0.samt":"4250","0.pamt":"-4250"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-groceries"], "a-checking", "a-groceries");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("T20240115-9912", result.Header!.OnlineMatchFitid);
        Assert.Equal("FI-EXAMPLEBANK", result.Header.OnlineMatchFiId);
        // The audit fields landed in ProviderRawPayload (JSON text).
        Assert.NotNull(result.Header.ProviderRawPayload);
        Assert.Contains("\"ol.match-status\":\"matched-by-fitid\"", result.Header.ProviderRawPayload);
        Assert.Contains("\"ol.match-type\":\"auto\"", result.Header.ProviderRawPayload);
        Assert.Contains("\"ol.orig-txn\":\"orig-feed-item-42\"", result.Header.ProviderRawPayload);
    }

    [Fact]
    public void Map_leaves_online_match_identity_null_when_txn_is_not_online_sourced()
    {
        // A purely manual / CSV-imported txn carries no `ol_*` fields
        // in the JSON. Header must serialise with NULLs on the OFX
        // dedup composite key columns so the partial index excludes
        // it (and the SPA doesn't render a "from feed" indicator).
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"manual-1","acctid":"a-checking",
              "desc":"Cash","dt":"20240115",
              "0.id":"s","0.acctid":"a-cash","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cash"], "a-checking", "a-cash");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Null(result.Header!.OnlineMatchFitid);
        Assert.Null(result.Header.OnlineMatchFiId);
    }

    [Fact]
    public void Map_normalizes_blank_check_number_to_null()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"chk-blank","acctid":"a-checking",
              "desc":"Cash","dt":"20240115","chk":"",
              "0.id":"s","0.acctid":"a-cash","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cash"], "a-checking", "a-cash");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Null(result.Header!.CheckNumber);
    }

    // -- feed originals (FU-024) ----------------------------------------------
    //
    // The header carries what the user curated in Moneydance; the bank's own
    // text (ol.orig-payee / ol.orig-memo) is only a fallback for a blank. When
    // the two differ, the feed's value is seeded into txn_header_originals —
    // the state the API's first edit would have left — and when they agree
    // nothing is seeded, since an originals row is what marks the feed text as
    // differing from the curated text (resolved_transactions.has_overrides).

    [Fact]
    public void Map_seeds_originals_when_the_feed_payee_differs()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-orig-payee","acctid":"a-cash",
              "desc":"Fuel Stop","memo":"curated memo",
              "ol.orig-payee":"FUEL STOP 0000000000",
              "dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        var seed = Assert.IsType<HeaderOriginalsSeed>(result.Originals);
        Assert.Equal(result.Header!.Id, seed.HeaderId);
        Assert.Equal("FUEL STOP 0000000000", seed.Payee);
        // No feed memo, so the original memo is the one written.
        Assert.Equal("curated memo", seed.Memo);
        // ADR-0114 D15: the amount as written on the originating account.
        Assert.Equal(-5.00m, seed.Amount);
    }

    [Fact]
    public void Map_seeds_the_split_amount_on_the_originating_account_not_one_line()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-orig-split","acctid":"a-cash",
              "desc":"Fuel Stop","ol.orig-payee":"FUEL STOP RAW","dt":"20240101",
              "0.id":"s0","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500",
              "1.id":"s1","1.acctid":"a-cat2","1.samt":"125","1.pamt":"-125"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat", "a-cat2"], "a-cash", "a-cat", "a-cat2");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        var seed = Assert.IsType<HeaderOriginalsSeed>(result.Originals);
        // Both lines on the account, not one, and not every leg (which sums to zero).
        Assert.Equal(-6.25m, seed.Amount);
    }

    [Fact]
    public void Map_seeds_originals_when_the_feed_memo_differs()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-orig-memo","acctid":"a-cash",
              "desc":"Fuel Stop","memo":"curated memo",
              "ol.orig-payee":"Fuel Stop","ol.orig-memo":"raw memo",
              "dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        var seed = Assert.IsType<HeaderOriginalsSeed>(result.Originals);
        Assert.Equal(result.Header!.Id, seed.HeaderId);
        Assert.Equal("Fuel Stop", seed.Payee);
        Assert.Equal("raw memo",  seed.Memo);
    }

    [Fact]
    public void Map_seeds_both_feed_values_when_both_differ()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-orig-both","acctid":"a-cash",
              "desc":"Fuel Stop","memo":"curated memo",
              "ol.orig-payee":"FUEL STOP 0000000000","ol.orig-memo":"raw memo",
              "dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        var seed = Assert.IsType<HeaderOriginalsSeed>(result.Originals);
        Assert.Equal("FUEL STOP 0000000000", seed.Payee);
        Assert.Equal("raw memo",             seed.Memo);
        // The transaction itself still carries the curated values: the payee on
        // the header, the memo on the entered side of this one-split.
        Assert.Equal("Fuel Stop",    result.Header!.Payee);
        Assert.Equal("curated memo", PairFor(result, accounts["a-cash"].Id, postingIndex: 0).Origin.LegMemo);
    }

    [Theory]
    // Feed values equal to the curated ones: the feed text does not differ.
    [InlineData("Fuel Stop", "curated memo", "Fuel Stop", "curated memo")]
    // No feed values at all.
    [InlineData("Fuel Stop", "curated memo", null, null)]
    // Blank feed values are no values.
    [InlineData("Fuel Stop", "curated memo", "  ", "")]
    // Blank curated payee: the feed payee IS the written payee (the fallback).
    [InlineData("", "curated memo", "FUEL STOP 0000000000", null)]
    // Blank curated memo: the feed memo IS the written memo (the fallback).
    [InlineData("Fuel Stop", "", null, "raw memo")]
    // Memos are stored trimmed, so surrounding white space is no difference.
    [InlineData("Fuel Stop", "curated memo", null, " curated memo ")]
    [InlineData("Fuel Stop", "", null, " raw memo ")]
    public void Map_seeds_no_originals_when_the_feed_agrees_with_what_was_written(
        string desc, string memo, string? origPayee, string? origMemo)
    {
        var olFields = (origPayee is null ? "" : $"\"ol.orig-payee\":\"{origPayee}\",")
                     + (origMemo  is null ? "" : $"\"ol.orig-memo\":\"{origMemo}\",");
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-orig-none","acctid":"a-cash",
              "desc":"{{desc}}","memo":"{{memo}}",{{olFields}}
              "dt":"20240101",
              "0.id":"s","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-cat"], "a-cash", "a-cat");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.NotNull(result.Header);
        Assert.Null(result.Originals);
    }

    // -- brokerage cash moves on the bank route -------------------------------
    //
    // A brokerage row reaches this mapper whenever it carries neither an
    // `invest.txntype` nor an investment xfer_type: QIF-origin cash rows
    // (qif_invst_action XOut / XIn / Cash / ContribX / MiscIncX with
    // xfrtp_bank) and plain rows with no xfer_type at all. ADR-0027 already
    // names these 'transfer'; the investment route's txntype "bank" writes
    // exactly that. Without it the register derives no action for a
    // brokerage row whose other side is a category, and the investment editor
    // opens on "Pick action...".

    private const string Brokerage      = "a-brokerage";
    private const string OtherBrokerage = "a-brokerage-2";
    private const string IncomeCat      = "a-income";
    private const string ExpenseCat     = "a-expense";
    private const string BlankCat       = "a-blank";
    private const string BankAcct       = "a-bank";

    /// <summary>
    /// Two brokerages (with Holdings siblings, as AccountImportStep wires
    /// them), a bank account, and three categories. The blank-named category
    /// is a category like any other by the time it reaches this mapper — its
    /// name is the account step's business, not this one's.
    /// </summary>
    private static IReadOnlyDictionary<string, AccountRef> BrokerageAccountMap() =>
        new Dictionary<string, AccountRef>(StringComparer.Ordinal)
        {
            [Brokerage]      = new(Guid.NewGuid(), "investment", HoldingsAccountId: Guid.NewGuid(), AmountDecimals: 2),
            [OtherBrokerage] = new(Guid.NewGuid(), "investment", HoldingsAccountId: Guid.NewGuid(), AmountDecimals: 2),
            [IncomeCat]      = new(Guid.NewGuid(), "category", AmountDecimals: 2),
            [ExpenseCat]     = new(Guid.NewGuid(), "category", AmountDecimals: 2),
            [BlankCat]       = new(Guid.NewGuid(), "category", AmountDecimals: 2),
            [BankAcct]       = new(Guid.NewGuid(), "bank", AmountDecimals: 2),
        };

    [Theory]
    [InlineData("XOut",     IncomeCat)]
    [InlineData("XOut",     ExpenseCat)]
    [InlineData("XOut",     BlankCat)]
    [InlineData("XOut",     BankAcct)]
    [InlineData("XOut",     OtherBrokerage)]
    [InlineData("XIn",      IncomeCat)]
    [InlineData("XIn",      ExpenseCat)]
    [InlineData("XIn",      BlankCat)]
    [InlineData("XIn",      BankAcct)]
    [InlineData("XIn",      OtherBrokerage)]
    [InlineData("Cash",     IncomeCat)]
    [InlineData("Cash",     ExpenseCat)]
    [InlineData("Cash",     BlankCat)]
    [InlineData("Cash",     BankAcct)]
    [InlineData("Cash",     OtherBrokerage)]
    [InlineData("ContribX", IncomeCat)]
    [InlineData("ContribX", ExpenseCat)]
    [InlineData("ContribX", BlankCat)]
    [InlineData("ContribX", BankAcct)]
    [InlineData("ContribX", OtherBrokerage)]
    [InlineData("MiscIncX", IncomeCat)]
    [InlineData("MiscIncX", ExpenseCat)]
    [InlineData("MiscIncX", BlankCat)]
    [InlineData("MiscIncX", BankAcct)]
    [InlineData("MiscIncX", OtherBrokerage)]
    public void Qif_brokerage_cash_row_with_one_split_is_a_transfer(string qifWord, string target)
    {
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-qif-cash","acctid":"{{Brokerage}}",
              "desc":"Cash move","memo":"note","dt":"20240101","chk":"{{qifWord}}",
              "qif_invst_action":"{{qifWord}}","xfer_type":"xfrtp_bank",
              "0.id":"s0","0.acctid":"{{target}}","0.invest.splittype":"xfr",
              "0.samt":"100","0.pamt":"-100"
            }
            """);
        Assert.False(txn.IsInvestmentShape);   // the premise: this IS the bank route
        var accounts = BrokerageAccountMap();

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("transfer", result.Header!.Action);
        var (origin, counterpart) = PairFor(result, accounts[Brokerage].Id, postingIndex: 0);
        Assert.Equal(accounts[target].Id, counterpart.AccountId);
        Assert.Equal("transfer", origin.PostingRole);
        Assert.Equal("transfer", counterpart.PostingRole);

        // Everything else the bank route writes is untouched: the QIF word
        // stays the check number verbatim, payee and memo are the curated ones,
        // the memo on the brokerage leg that holds a one-posting event's value.
        Assert.Equal(qifWord,     result.Header.CheckNumber);
        Assert.Equal("Cash move", result.Header.Payee);
        Assert.Equal("note",      origin.LegMemo);
        Assert.Null(counterpart.LegMemo);
    }

    [Theory]
    [InlineData(IncomeCat)]
    [InlineData(BankAcct)]
    public void Plain_brokerage_row_with_one_split_is_a_transfer(string target)
    {
        // No qif word, no xfer_type, no splittype: nothing on the row says
        // "investment", and it is still cash moving in or out of a brokerage.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-plain","acctid":"{{Brokerage}}",
              "desc":"Plain move","dt":"20240101",
              "0.id":"s0","0.acctid":"{{target}}","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = BrokerageAccountMap();

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("transfer", result.Header!.Action);
        Assert.All(result.Legs, l => Assert.Equal("transfer", l.PostingRole));
    }

    [Fact]
    public void Brokerage_transfer_keeps_tags_fitid_and_recon_seeds()
    {
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-kept","acctid":"{{Brokerage}}",
              "desc":"Cash move","dt":"20240101","stat":"X",
              "qif_invst_action":"XOut","xfer_type":"xfrtp_bank","tags":"one",
              "ol_fitid_1":"FIT-1","ol_fi_id":"FI-1",
              "0.id":"s0","0.acctid":"{{BankAcct}}","0.samt":"100","0.pamt":"-100",
              "0.stat":"X","0.tags":"two"
            }
            """);
        var accounts = BrokerageAccountMap();

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Equal("transfer", result.Header!.Action);
        // Every tag on the brokerage leg: the bank side of the event is read-only.
        Assert.Empty(result.HeaderTags);
        var (origin, counterpart) = PairFor(result, accounts[Brokerage].Id, postingIndex: 0);
        var legTag = Assert.Single(result.LegTags);
        Assert.Equal(origin.Id, legTag.LegId);
        Assert.Equal(["two", "one"], legTag.Tags);
        Assert.Equal("FIT-1", result.Header.OnlineMatchFitid);
        Assert.Equal("FI-1",  result.Header.OnlineMatchFiId);
        Assert.Equal(2, result.LegRecons.Count);
        Assert.Contains(result.LegRecons, r => r.LegId == origin.Id);
        Assert.Contains(result.LegRecons, r => r.LegId == counterpart.Id);
    }

    [Fact]
    public void Brokerage_row_with_more_than_one_split_stays_actionless()
    {
        // Fail closed. A single posting is unambiguously a cash move; two or
        // more could be anything (a split paycheck deposit, a fee netted
        // against a contribution), and 'transfer' would assert a shape nobody
        // read off the data.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-multi","acctid":"{{Brokerage}}",
              "desc":"Two ways","dt":"20240101",
              "qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
              "0.id":"s0","0.acctid":"{{IncomeCat}}","0.samt":"100","0.pamt":"-100",
              "1.id":"s1","1.acctid":"{{BankAcct}}","1.samt":"50","1.pamt":"-50"
            }
            """);
        var accounts = BrokerageAccountMap();

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal(4, result.Legs.Count);
        Assert.Null(result.Header!.Action);
        Assert.All(result.Legs, l => Assert.Null(l.PostingRole));
    }

    [Fact]
    public void Brokerage_row_counts_splits_after_dropping_self_references()
    {
        // The count is of EMITTABLE splits. A split back to the brokerage
        // itself never becomes a posting, so it cannot make a one-posting
        // cash move ambiguous.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-selfref","acctid":"{{Brokerage}}",
              "desc":"Cash move","dt":"20240101",
              "qif_invst_action":"XIn","xfer_type":"xfrtp_bank",
              "0.id":"s0","0.acctid":"{{Brokerage}}","0.samt":"0","0.pamt":"0",
              "1.id":"s1","1.acctid":"{{BankAcct}}","1.samt":"-100","1.pamt":"100"
            }
            """);
        var accounts = BrokerageAccountMap();

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Equal(2, result.Legs.Count);
        Assert.Equal("transfer", result.Header!.Action);
        Assert.All(result.Legs, l => Assert.Equal("transfer", l.PostingRole));
    }

    [Fact]
    public void Bank_home_xfrtp_bank_row_stays_actionless()
    {
        // Regression guard: the rule is about the brokerage, not about
        // xfrtp_bank. A checking-account row carries the same xfer_type on
        // every ordinary transfer and payment.
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-bank-home","acctid":"{{BankAcct}}",
              "desc":"Ordinary","dt":"20240101","xfer_type":"xfrtp_bank",
              "0.id":"s0","0.acctid":"{{ExpenseCat}}","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = BrokerageAccountMap();

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Null(result.Header!.Action);
        Assert.All(result.Legs, l => Assert.Null(l.PostingRole));
    }

    [Fact]
    public void Bank_home_row_into_a_brokerage_stays_actionless()
    {
        // The other end of the same money. Its home is the bank, so the bank
        // route's existing behaviour stands (the register derives 'Xfr' for a
        // two-real-account posting on its own).
        var txn = TxnFromJson($$"""
            {
              "obj_type":"txn","id":"t-bank-to-brokerage","acctid":"{{BankAcct}}",
              "desc":"Fund it","dt":"20240101","xfer_type":"xfrtp_bank",
              "0.id":"s0","0.acctid":"{{Brokerage}}","0.samt":"100","0.pamt":"-100"
            }
            """);
        var accounts = BrokerageAccountMap();

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Header!.Action);
        Assert.All(result.Legs, l => Assert.Null(l.PostingRole));
    }
}
