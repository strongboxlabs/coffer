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
/// memo/payee precedence preserved from PR #38 / #42.
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
            id => new AccountRef(Guid.NewGuid(), categories.Contains(id) ? "category" : "bank"),
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
    public void Map_emits_a_pair_for_a_single_category_split()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-1","acctid":"a-checking",
              "desc":"Fuel Stop","memo":"DOE,JOHN",
              "dt":"20191214","td":"20191214","stat":"X",
              "ol.orig-payee":"FUEL STOP 0000000000",
              "0.id":"s-0","0.acctid":"a-gas","0.samt":"4535","0.pamt":"-4535"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-gas"], "a-checking", "a-gas");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.NotNull(result.Header);
        Assert.Equal(2, result.Legs.Count);

        // Header carries the event envelope — payee/memo/status/external_id
        // — once. Curated Description wins over raw OFX ol.orig-payee per
        // PR #38; parent memo carries forward verbatim.
        Assert.Equal(TestLedgerId,    result.Header.LedgerId);
        Assert.Equal("Fuel Stop",        result.Header.Payee);
        Assert.Equal("DOE,JOHN", result.Header.Memo);
        // Migration 030: MD's raw "X" letter-code maps to the normalized
        // "cleared" state; the mapper also stamps cleared_at to satisfy
        // the DB CHECK (status='cleared') ⇔ (cleared_at IS NOT NULL).
        Assert.Equal("cleared", result.Header.Status);
        Assert.NotNull(result.Header.ClearedAt);
        Assert.Equal("t-1",           result.Header.ExternalId);

        var origin = result.Legs.Single(l => l.AccountId == accounts["a-checking"].Id);
        var counterpart = result.Legs.Single(l => l.AccountId == accounts["a-gas"].Id);

        // Origin posts the cash impact; counterpart mirrors with split_amount.
        Assert.Equal(-45.35m, origin.Amount);
        Assert.Equal(45.35m,  counterpart.Amount);

        // Posting pairing is structural: shared posting_index within the
        // header, distinct accounts. No counterparty_id denormalisation.
        Assert.Equal(0, origin.PostingIndex);
        Assert.Equal(0, counterpart.PostingIndex);
        Assert.Equal(result.Header.Id, origin.HeaderId);
        Assert.Equal(result.Header.Id, counterpart.HeaderId);

        // Single-split events: leg_memo is NULL on both sides — the view
        // falls back to header memo so the register doesn't echo the payee
        // (the PR #42 regression). MD's 0.desc default to parent desc is
        // not propagated as leg memo for single-leg events.
        Assert.Null(origin.LegMemo);
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
        // Hybrid: $1000 leaves CHECKING; $300 to RENT (category), $700 to
        // SAVINGS (non-category). Both legs become full pairs (one posting
        // each, distinct posting_index).
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
        // Regression: the mapper used to prefer ol.orig-payee (the raw OFX
        // original) over Description (what the user sees in MD after any
        // merge/cleanup). That surfaced as register rows showing the
        // bank's noisy original payee instead of the curated one. Fix:
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
        Assert.Equal("DOE,JOHN", result.Header.Memo);     // curated, not raw
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
        // After PR #38 / #42 the leg memo is per-leg on multi-splits and
        // NULL on single-splits. ADR-0022 keeps the same product
        // behaviour: legs on a multi-split paycheck carry their distinct
        // memo (Salary / Federal Tax / Medicare Tax) on both sides of
        // the pair.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"paycheck","acctid":"a-checking",
              "desc":"GD INFORMATION T","memo":"Electronic/ACH Credit","dt":"20260420",
              "0.id":"s0","0.acctid":"a-wages-base","0.samt":"-598671","0.pamt":"598671","0.desc":"Salary",
              "1.id":"s1","1.acctid":"a-tax-fed",   "1.samt":"93823",  "1.pamt":"-93823","1.desc":"Federal Tax",
              "2.id":"s2","2.acctid":"a-tax-medi",  "2.samt":"8727",   "2.pamt":"-8727", "2.desc":"Medicare Tax"
            }
            """);
        var accounts = AccountMap(
            categoryMdIds: ["a-wages-base", "a-tax-fed", "a-tax-medi"],
            "a-checking", "a-wages-base", "a-tax-fed", "a-tax-medi");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        // Header carries the umbrella memo ("Electronic/ACH Credit"); legs
        // carry the per-split memo.
        Assert.Equal("GD INFORMATION T",      result.Header!.Payee);
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
    public void Map_per_split_memo_appears_on_both_legs_of_a_posting()
    {
        // Symmetric posting: viewing the category-side register (e.g.
        // "Taxes:Federal Income Tax") should also surface the leg
        // memo. Both legs of one posting carry the same leg_memo.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"paycheck-2","acctid":"a-checking",
              "desc":"GD INFORMATION T","memo":"Electronic/ACH Credit","dt":"20260420",
              "0.id":"s0","0.acctid":"a-tax-fed","0.samt":"93823","0.pamt":"-93823","0.desc":"Federal Tax",
              "1.id":"s1","1.acctid":"a-tax-medi","1.samt":"8727","1.pamt":"-8727","1.desc":"Medicare Tax"
            }
            """);
        var accounts = AccountMap(
            categoryMdIds: ["a-tax-fed", "a-tax-medi"],
            "a-checking", "a-tax-fed", "a-tax-medi");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);

        var (origin, counterpart) = PairFor(result, accounts["a-checking"].Id, postingIndex: 0);

        Assert.Equal("Federal Tax", origin.LegMemo);
        Assert.Equal("Federal Tax", counterpart.LegMemo);
    }

    [Fact]
    public void Map_single_split_leaves_leg_memo_null_even_when_md_supplies_per_leg_desc()
    {
        // Regression from PR #38 (fixed in PR #42): MD often defaults a
        // single-split's `0.desc` to the parent's `desc` (payee). Naïvely
        // copying it to LegMemo would echo the payee into the memo column.
        // ADR-0022 keeps that fix: single-split rows leave leg_memo NULL
        // and the view falls back to header memo.
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
        Assert.Equal("DOE,JOHN", result.Header.Memo);
        Assert.All(result.Legs, l => Assert.Null(l.LegMemo));
    }

    [Fact]
    public void Map_uses_dt_for_posted_at_ignoring_dtentered()
    {
        // Regression: the mapper used to prefer `dtentered` (when the user
        // typed the transaction) over `dt` (the date they assigned). That
        // broke future-dated and back-dated entries.
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-time","acctid":"a-cash","desc":"x","dt":"20191214",
              "dtentered":"1576644200025",
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
              "dt":"20260518","dtentered":"1777431270862",
              "0.id":"s","0.acctid":"a-cat","0.samt":"44651","0.pamt":"-44651"
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
    public void ExtractTags_aggregates_txn_level_and_split_level_tags()
    {
        var txn = TxnFromJson("""
            {
              "obj_type":"txn","id":"t-tags","acctid":"a-cash","desc":"x","dt":"20240101",
              "tags":"vacation,2024",
              "0.id":"s0","0.acctid":"a-cat","0.samt":"100","0.pamt":"-100","0.tags":"vacation,reimbursable",
              "1.id":"s1","1.acctid":"a-other","1.samt":"50","1.pamt":"-50","1.tags":""
            }
            """);

        var tags = TransactionMapper.ExtractTags(txn);

        Assert.Equal(3, tags.Count);
        Assert.Contains("vacation",     tags);
        Assert.Contains("2024",         tags);
        Assert.Contains("reimbursable", tags);
    }

    [Fact]
    public void ExtractTags_returns_empty_when_nothing_tagged()
    {
        var txn = TxnFromJson("""
            {"obj_type":"txn","id":"t-bare","acctid":"a","desc":"x","dt":"20240101",
             "0.id":"s","0.acctid":"a","0.samt":"0","0.pamt":"0"}
            """);
        Assert.Empty(TransactionMapper.ExtractTags(txn));
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
              "desc":"Whole Foods","dt":"20240115","stat":"X",
              "ol_fitid_1":"T20240115-9912",
              "ol_fi_id":"FI-EASTBANK",
              "ol.match-status":"matched-by-fitid",
              "ol.match-type":"auto",
              "ol.orig-txn":"orig-feed-item-42",
              "0.id":"s","0.acctid":"a-groceries","0.samt":"4535","0.pamt":"-4535"
            }
            """);
        var accounts = AccountMap(categoryMdIds: ["a-groceries"], "a-checking", "a-groceries");

        var result = TransactionMapper.Map(txn, accounts, TestLedgerId, importSource: "test");

        Assert.Null(result.Skip);
        Assert.Equal("T20240115-9912", result.Header!.OnlineMatchFitid);
        Assert.Equal("FI-EASTBANK", result.Header.OnlineMatchFiId);
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
}
