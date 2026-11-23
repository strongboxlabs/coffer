using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Moneydance's per-side memo and tags, mapped onto the per-side model and read back
/// through the one inheritance rule from every account the transaction touches.
/// </summary>
/// <remarks>
/// <para>Moneydance's facts: on a one-split the entered side holds <c>memo</c> and
/// <c>0.tags</c>, the other side's memo is <c>0.desc</c> (a copy of <c>desc</c> unless
/// the user typed one) and its tags are <c>tags</c>. On a multi-split <c>memo</c> is the
/// transaction's, <c>N.desc</c> and <c>N.tags</c> are line N's, and <c>tags</c> is one set
/// shared by every line's other side.</para>
/// <para>Every test asserts the registers, not only the stored rows: what a side shows,
/// whether it is inherited, and whether the row is read-only.</para>
/// </remarks>
public sealed class MoneydanceNotesMappingTests
{
    private static readonly Guid LedgerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly AccountRef Checking = new(Guid.NewGuid(), "bank", AmountDecimals: 2);
    private static readonly AccountRef Savings = new(Guid.NewGuid(), "bank", AmountDecimals: 2);
    private static readonly AccountRef Groceries = new(Guid.NewGuid(), "category", AmountDecimals: 2);
    private static readonly AccountRef Household = new(Guid.NewGuid(), "category", AmountDecimals: 2);
    private static readonly Guid BrokerageHoldings = Guid.NewGuid();
    private static readonly AccountRef Brokerage = new(Guid.NewGuid(), "investment", HoldingsAccountId: BrokerageHoldings, AmountDecimals: 2);
    private static readonly Guid OtherHoldings = Guid.NewGuid();
    private static readonly AccountRef OtherBrokerage = new(Guid.NewGuid(), "investment", HoldingsAccountId: OtherHoldings, AmountDecimals: 2);
    private static readonly AccountRef Dividends = new(Guid.NewGuid(), "category", AmountDecimals: 2);
    private static readonly AccountRef Fees = new(Guid.NewGuid(), "category", AmountDecimals: 2);
    private static readonly SecurityRef Fund = new(Guid.NewGuid(), 4);

    private static readonly IReadOnlyDictionary<string, AccountRef> Accounts =
        new Dictionary<string, AccountRef>(StringComparer.Ordinal)
        {
            ["a-chk"] = Checking,
            ["a-sav"] = Savings,
            ["a-gro"] = Groceries,
            ["a-hh"] = Household,
            ["a-brk"] = Brokerage,
            ["a-brk2"] = OtherBrokerage,
            ["a-div"] = Dividends,
            ["a-fee"] = Fees,
        };

    private static readonly IReadOnlyDictionary<string, SecurityRef> Securities =
        new Dictionary<string, SecurityRef>(StringComparer.Ordinal) { ["a-sec"] = Fund };

    private static MdItem Item(string fragment) =>
        MdItemReader.ReadString($$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """).AllItems.Single();

    private static TransactionMapper.MapResult Bank(string fragment)
    {
        var result = TransactionMapper.Map(MdTxn.From(Item(fragment)), Accounts, LedgerId, "test");
        Assert.Null(result.Skip);
        return result;
    }

    private static RegisterView.Row Shown(TransactionMapper.MapResult r, AccountRef account, int posting = 0) =>
        RegisterView.Of(r.Header!, r.Legs, r.HeaderTags, r.LegTags, account.Id, posting,
            r.Header!.Action is null ? null : [Brokerage.Id, BrokerageHoldings]);

    private static TxnLegRow Leg(IReadOnlyList<TxnLegRow> legs, AccountRef account, int posting = 0) =>
        legs.Single(l => l.AccountId == account.Id && l.PostingIndex == posting);

    private static IReadOnlyList<string> TagsOn(IReadOnlyList<LegTagSeed> seeds, TxnLegRow leg) =>
        seeds.SingleOrDefault(s => s.LegId == leg.Id)?.Tags ?? [];

    // -------------------------------------------------------------------------
    // One-split, bank route
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("Grocer", "weekly shop", "Grocer", null)]          // 0.desc is Moneydance's copy of desc
    [InlineData("Grocer", "weekly shop", "weekly shop", null)]     // 0.desc equals the memo written
    [InlineData("Grocer", "weekly shop", "  ", null)]              // blank
    [InlineData("Grocer", "weekly shop", " Grocer ", null)]        // the copy, compared trimmed
    [InlineData(" Grocer ", "weekly shop", "Grocer", null)]        // either side trimmed
    [InlineData("Grocer", "weekly shop", "receipt 12", "receipt 12")] // the user's own
    public void A_one_split_keeps_its_other_sides_memo_only_where_the_user_typed_one(
        string desc, string memo, string splitDesc, string? expectedOwn)
    {
        var r = Bank($$"""
            {"obj_type":"txn","id":"t-1","acctid":"a-chk","desc":"{{desc}}","memo":"{{memo}}","dt":"20260105",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"{{splitDesc}}","0.samt":"500","0.pamt":"-500"}
            """);

        Assert.Null(r.Header!.Memo);
        Assert.Equal(Checking.Id, r.Header.OriginatingAccountId);
        Assert.Equal(memo, Leg(r.Legs, Checking).LegMemo);
        Assert.Equal(expectedOwn, Leg(r.Legs, Groceries).LegMemo);

        var entered = Shown(r, Checking);
        Assert.Equal(memo, entered.Memo);
        Assert.False(entered.MemoInherited);
        Assert.False(entered.IsReadOnly);

        var other = Shown(r, Groceries);
        Assert.Equal(expectedOwn ?? memo, other.Memo);
        Assert.Equal(expectedOwn is null, other.MemoInherited);
        Assert.False(other.IsReadOnly);
    }

    [Fact]
    public void A_one_split_puts_the_split_tags_on_the_entered_side_and_the_transaction_tags_on_the_other()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-2","acctid":"a-chk","desc":"Grocer","dt":"20260105","tags":"family,shared",
             "0.id":"s0","0.acctid":"a-gro","0.tags":"food","0.samt":"500","0.pamt":"-500"}
            """);

        Assert.Empty(r.HeaderTags);
        Assert.Equal(["food"], TagsOn(r.LegTags, Leg(r.Legs, Checking)));
        Assert.Equal(["family", "shared"], TagsOn(r.LegTags, Leg(r.Legs, Groceries)));
        Assert.Equal(["food"], Shown(r, Checking).Tags);
        Assert.Equal(["family", "shared"], Shown(r, Groceries).Tags);
        Assert.False(Shown(r, Groceries).TagsInherited);
    }

    [Fact]
    public void A_one_split_keeps_the_other_sides_tags_even_when_they_equal_the_entered_sides()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-3","acctid":"a-chk","desc":"Grocer","dt":"20260105","tags":"food",
             "0.id":"s0","0.acctid":"a-gro","0.tags":"food","0.samt":"500","0.pamt":"-500"}
            """);

        Assert.Equal(["food"], TagsOn(r.LegTags, Leg(r.Legs, Groceries)));
        Assert.False(Shown(r, Groceries).TagsInherited);
    }

    [Fact]
    public void A_one_split_with_only_transaction_tags_shows_them_on_the_other_side_and_inherited_on_the_entered_side()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-4","acctid":"a-chk","desc":"Grocer","dt":"20260105","tags":"family",
             "0.id":"s0","0.acctid":"a-gro","0.samt":"500","0.pamt":"-500"}
            """);

        Assert.Empty(TagsOn(r.LegTags, Leg(r.Legs, Checking)));
        var entered = Shown(r, Checking);
        Assert.Equal(["family"], entered.Tags);
        Assert.True(entered.TagsInherited);
    }

    [Fact]
    public void A_plain_real_to_real_transfer_follows_the_one_split_rule_from_both_accounts()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-5","acctid":"a-chk","desc":"Move","memo":"rainy day","dt":"20260105",
             "0.id":"s0","0.acctid":"a-sav","0.desc":"from checking","0.samt":"1000","0.pamt":"-1000"}
            """);

        Assert.Equal(Checking.Id, r.Header!.OriginatingAccountId);
        Assert.Equal("rainy day", Shown(r, Checking).Memo);
        Assert.Equal("from checking", Shown(r, Savings).Memo);
        Assert.False(Shown(r, Savings).IsReadOnly);
    }

    [Theory]
    [InlineData("  rainy day  ", null, "rainy day")]
    [InlineData("   ", "FEED MEMO", "FEED MEMO")]
    [InlineData("", " FEED MEMO ", "FEED MEMO")]
    [InlineData(" ", " ", null)]
    public void The_memo_written_is_trimmed_and_falls_back_to_the_feeds_only_when_blank(
        string memo, string? feedMemo, string? expected)
    {
        var feed = feedMemo is null ? "" : $"\"ol.orig-memo\":\"{feedMemo}\",";
        var r = Bank($$"""
            {"obj_type":"txn","id":"t-6","acctid":"a-chk","desc":"Grocer","memo":"{{memo}}",{{feed}}"dt":"20260105",
             "0.id":"s0","0.acctid":"a-gro","0.samt":"500","0.pamt":"-500"}
            """);

        Assert.Equal(expected, Leg(r.Legs, Checking).LegMemo);
        Assert.Null(r.Header!.Memo);
    }

    // -------------------------------------------------------------------------
    // Multi-split, bank route
    // -------------------------------------------------------------------------

    [Fact]
    public void A_multi_split_keeps_the_memo_on_the_header_and_each_line_on_its_entered_side()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-10","acctid":"a-chk","desc":"Store","memo":"weekend","dt":"20260105",
             "tags":"shared",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"food","0.tags":"weekly","0.samt":"300","0.pamt":"-300",
             "1.id":"s1","1.acctid":"a-hh","1.desc":"soap","1.samt":"200","1.pamt":"-200"}
            """);

        Assert.Equal("weekend", r.Header!.Memo);
        Assert.Empty(r.HeaderTags);
        Assert.Equal(Checking.Id, r.Header.OriginatingAccountId);
        Assert.Equal("food", Leg(r.Legs, Checking, 0).LegMemo);
        Assert.Equal("soap", Leg(r.Legs, Checking, 1).LegMemo);
        Assert.Null(Leg(r.Legs, Groceries, 0).LegMemo);
        Assert.Null(Leg(r.Legs, Household, 1).LegMemo);

        // Every line's own tags take the shared set: the other side is read-only.
        Assert.Equal(["weekly", "shared"], TagsOn(r.LegTags, Leg(r.Legs, Checking, 0)));
        Assert.Equal(["shared"], TagsOn(r.LegTags, Leg(r.Legs, Checking, 1)));
        Assert.Empty(TagsOn(r.LegTags, Leg(r.Legs, Groceries, 0)));
        Assert.Empty(TagsOn(r.LegTags, Leg(r.Legs, Household, 1)));

        var line0 = Shown(r, Checking, 0);
        Assert.Equal("food", line0.Memo);
        Assert.False(line0.IsReadOnly);

        var groceries = Shown(r, Groceries, 0);
        Assert.Equal("food", groceries.Memo);
        Assert.True(groceries.MemoInherited);
        Assert.True(groceries.IsReadOnly);
        Assert.Equal(["weekly", "shared"], groceries.Tags);

        var household = Shown(r, Household, 1);
        Assert.Equal("soap", household.Memo);
        Assert.Equal(["shared"], household.Tags);
        Assert.True(household.IsReadOnly);
    }

    [Fact]
    public void A_multi_split_line_with_no_desc_stays_blank_on_both_sides()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-11","acctid":"a-chk","desc":"Store","memo":"weekend","dt":"20260105",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"  ","0.samt":"300","0.pamt":"-300",
             "1.id":"s1","1.acctid":"a-hh","1.samt":"200","1.pamt":"-200"}
            """);

        Assert.Null(Shown(r, Checking, 0).Memo);
        Assert.Null(Shown(r, Groceries, 0).Memo);
        Assert.Null(Shown(r, Household, 1).Memo);
    }

    [Fact]
    public void A_multi_split_union_over_twenty_tags_keeps_every_tag()
    {
        var shared = string.Join(",", Enumerable.Range(1, 15).Select(i => $"s{i}"));
        var line = string.Join(",", Enumerable.Range(1, 10).Select(i => $"l{i}"));
        var r = Bank($$"""
            {"obj_type":"txn","id":"t-12","acctid":"a-chk","desc":"Store","dt":"20260105","tags":"{{shared}}",
             "0.id":"s0","0.acctid":"a-gro","0.tags":"{{line}}","0.samt":"300","0.pamt":"-300",
             "1.id":"s1","1.acctid":"a-hh","1.samt":"200","1.pamt":"-200"}
            """);

        Assert.Equal(25, TagsOn(r.LegTags, Leg(r.Legs, Checking, 0)).Count);
        Assert.Equal(25, Shown(r, Groceries, 0).Tags.Count);
    }

    [Fact]
    public void The_symmetric_multi_split_shows_each_line_in_the_category_register_as_its_own_read_only_row()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-13","acctid":"a-chk","desc":"Store","memo":"weekend","dt":"20260105",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"food","0.samt":"300","0.pamt":"-300",
             "1.id":"s1","1.acctid":"a-gro","1.desc":"snacks","1.samt":"200","1.pamt":"-200"}
            """);

        Assert.Equal(Checking.Id, r.Header!.OriginatingAccountId);
        var first = Shown(r, Groceries, 0);
        var second = Shown(r, Groceries, 1);
        Assert.Equal("food", first.Memo);
        Assert.Equal("snacks", second.Memo);
        Assert.True(first.IsReadOnly);
        Assert.True(second.IsReadOnly);
        Assert.False(Shown(r, Checking, 0).IsReadOnly);
    }

    [Fact]
    public void A_multi_split_with_a_real_account_on_the_other_side_shows_that_line_read_only_there()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-14","acctid":"a-chk","desc":"Payday","memo":"october","dt":"20260105",
             "tags":"income",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"cash back","0.samt":"-300","0.pamt":"300",
             "1.id":"s1","1.acctid":"a-sav","1.desc":"to savings","1.samt":"200","1.pamt":"-200"}
            """);

        var savings = Shown(r, Savings, 1);
        Assert.Equal("to savings", savings.Memo);
        Assert.Equal(["income"], savings.Tags);
        Assert.True(savings.IsReadOnly);
    }

    // -------------------------------------------------------------------------
    // Brokerage cash moves on the bank route
    // -------------------------------------------------------------------------

    [Fact]
    public void A_brokerage_home_cash_move_puts_every_value_on_the_brokerage_and_none_on_the_read_only_side()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-20","acctid":"a-brk","desc":"Sweep","memo":"monthly","dt":"20260105",
             "tags":"cash","qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
             "0.id":"s0","0.acctid":"a-chk","0.desc":"into checking","0.tags":"move","0.samt":"1000","0.pamt":"-1000"}
            """);

        Assert.Equal("transfer", r.Header!.Action);
        Assert.Equal(Brokerage.Id, r.Header.OriginatingAccountId);
        Assert.Null(r.Header.Memo);
        Assert.Equal("monthly · into checking", Leg(r.Legs, Brokerage).LegMemo);
        Assert.Null(Leg(r.Legs, Checking).LegMemo);
        Assert.Equal(["move", "cash"], TagsOn(r.LegTags, Leg(r.Legs, Brokerage)));
        Assert.Empty(TagsOn(r.LegTags, Leg(r.Legs, Checking)));

        var checking = Shown(r, Checking);
        Assert.True(checking.IsReadOnly);
        Assert.Equal("monthly · into checking", checking.Memo);
        Assert.Equal(["move", "cash"], checking.Tags);
    }

    [Fact]
    public void A_brokerage_home_cash_move_does_not_join_a_split_desc_that_is_the_payee()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-21","acctid":"a-brk","desc":"Sweep","memo":"monthly","dt":"20260105",
             "qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
             "0.id":"s0","0.acctid":"a-chk","0.desc":"Sweep","0.samt":"1000","0.pamt":"-1000"}
            """);

        Assert.Equal("monthly", Leg(r.Legs, Brokerage).LegMemo);
    }

    [Fact]
    public void A_bank_home_transfer_into_a_brokerage_stays_a_plain_one_split_owned_by_the_bank()
    {
        var r = Bank("""
            {"obj_type":"txn","id":"t-22","acctid":"a-chk","desc":"Fund","memo":"top up","dt":"20260105",
             "tags":"invest",
             "0.id":"s0","0.acctid":"a-brk","0.desc":"deposit","0.samt":"1000","0.pamt":"-1000"}
            """);

        Assert.Null(r.Header!.Action);
        Assert.Equal(Checking.Id, r.Header.OriginatingAccountId);
        Assert.Equal("top up", Leg(r.Legs, Checking).LegMemo);
        Assert.Equal("deposit", Leg(r.Legs, Brokerage).LegMemo);
        Assert.Equal(["invest"], TagsOn(r.LegTags, Leg(r.Legs, Brokerage)));
        Assert.False(Shown(r, Brokerage).IsReadOnly);
    }

    // -------------------------------------------------------------------------
    // Reminder templates
    // -------------------------------------------------------------------------

    private static ReminderMapper.MapResult Reminder(string fragment)
    {
        var item = Item(fragment);
        var result = ReminderMapper.Map(MdReminder.From(item), Accounts, LedgerId, "test", item.RawJson);
        Assert.Null(result.Skip);
        return result;
    }

    private static RegisterView.Row Shown(ReminderMapper.MapResult r, AccountRef account, int posting = 0) =>
        RegisterView.Of(r.Header!, r.Legs, r.HeaderTags ?? [], r.LegTags ?? [], account.Id, posting,
            r.Header!.Action is null ? null : [Brokerage.Id, BrokerageHoldings]);

    [Fact]
    public void A_one_split_template_takes_the_reminders_memo_over_the_transactions_and_imports_its_tags()
    {
        var r = Reminder("""
            {"obj_type":"reminder","id":"r-1","desc":"Rent","memo":"reminder memo","type":"txn","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1",
             "txn.acctid":"a-chk","txn.desc":"Rent","txn.memo":"txn memo","txn.tags":"home",
             "txn.0.id":"s0","txn.0.acctid":"a-hh","txn.0.desc":"landlord note","txn.0.tags":"fixed",
             "txn.0.samt":"1000","txn.0.pamt":"-1000"}
            """);

        Assert.Null(r.Header!.Memo);
        Assert.Equal(Checking.Id, r.Header.OriginatingAccountId);
        Assert.Equal("reminder memo", Leg(r.Legs, Checking).LegMemo);
        Assert.Equal("landlord note", Leg(r.Legs, Household).LegMemo);
        Assert.Equal(["fixed"], TagsOn(r.LegTags!, Leg(r.Legs, Checking)));
        Assert.Equal(["home"], TagsOn(r.LegTags!, Leg(r.Legs, Household)));
        Assert.Equal("reminder memo", Shown(r, Checking).Memo);
        Assert.Equal("landlord note", Shown(r, Household).Memo);
    }

    [Fact]
    public void A_template_falls_back_to_the_transaction_memo_and_compares_the_split_desc_to_it()
    {
        var r = Reminder("""
            {"obj_type":"reminder","id":"r-2","desc":"Rent","type":"txn","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1",
             "txn.acctid":"a-chk","txn.desc":"Rent","txn.memo":"txn memo",
             "txn.0.id":"s0","txn.0.acctid":"a-hh","txn.0.desc":"txn memo",
             "txn.0.samt":"1000","txn.0.pamt":"-1000"}
            """);

        Assert.Equal("txn memo", Leg(r.Legs, Checking).LegMemo);
        Assert.Null(Leg(r.Legs, Household).LegMemo);
        Assert.True(Shown(r, Household).MemoInherited);
    }

    [Fact]
    public void A_multi_split_template_places_its_values_like_a_live_multi_split()
    {
        var r = Reminder("""
            {"obj_type":"reminder","id":"r-3","desc":"Pay","type":"txn","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1",
             "txn.acctid":"a-chk","txn.desc":"Pay","txn.memo":"monthly","txn.tags":"bills",
             "txn.0.id":"s0","txn.0.acctid":"a-hh","txn.0.desc":"rent","txn.0.samt":"700","txn.0.pamt":"-700",
             "txn.1.id":"s1","txn.1.acctid":"a-gro","txn.1.desc":"food","txn.1.tags":"weekly",
             "txn.1.samt":"300","txn.1.pamt":"-300"}
            """);

        Assert.Equal("monthly", r.Header!.Memo);
        Assert.Empty(r.HeaderTags ?? []);
        Assert.Equal(["bills"], TagsOn(r.LegTags!, Leg(r.Legs, Checking, 0)));
        Assert.Equal(["weekly", "bills"], TagsOn(r.LegTags!, Leg(r.Legs, Checking, 1)));
        Assert.Equal("food", Shown(r, Groceries, 1).Memo);
        Assert.True(Shown(r, Groceries, 1).IsReadOnly);
    }

    [Fact]
    public void A_brokerage_cash_template_puts_every_value_on_the_brokerage()
    {
        var r = Reminder("""
            {"obj_type":"reminder","id":"r-4","desc":"Sweep","type":"txn","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1",
             "txn.acctid":"a-brk","txn.desc":"Sweep","txn.memo":"monthly","txn.tags":"cash",
             "txn.0.id":"s0","txn.0.acctid":"a-chk","txn.0.desc":"into checking",
             "txn.0.samt":"1000","txn.0.pamt":"-1000"}
            """);

        Assert.Equal("transfer", r.Header!.Action);
        Assert.Equal("monthly · into checking", Leg(r.Legs, Brokerage).LegMemo);
        Assert.Equal(["cash"], TagsOn(r.LegTags!, Leg(r.Legs, Brokerage)));
        Assert.Null(Leg(r.Legs, Checking).LegMemo);
        Assert.True(Shown(r, Checking).IsReadOnly);
    }

    // -------------------------------------------------------------------------
    // Investment route
    // -------------------------------------------------------------------------

    private static InvestmentTransactionMapper.HeaderLegsResult Invest(string fragment)
    {
        var result = InvestmentTransactionMapper.MapToHeaderAndLegs(
            MdTxn.From(Item(fragment)), Accounts, Securities, LedgerId, "test");
        Assert.Null(result.Skip);
        return result;
    }

    private static RegisterView.Row Shown(InvestmentTransactionMapper.HeaderLegsResult r, Guid account, int posting) =>
        RegisterView.Of(r.Header!, r.Legs, r.HeaderTags ?? [], r.LegTags ?? [], account, posting,
            [Brokerage.Id, BrokerageHoldings]);

    [Fact]
    public void A_one_posting_dividend_keeps_one_value_on_the_brokerage_leg_with_the_income_split_joined()
    {
        var r = Invest("""
            {"obj_type":"txn","id":"i-1","acctid":"a-brk","desc":"FUND A","memo":"q3","dt":"20260105",
             "tags":"income","invest.txntype":"div","xfer_type":"xfrtp_dividend",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"0","0.pamt":"0","0.tags":"fund",
             "1.id":"s1","1.acctid":"a-div","1.invest.splittype":"inc","1.desc":"qualified","1.tags":"tax",
             "1.samt":"-500","1.pamt":"500"}
            """);

        Assert.Single(r.Legs.Select(l => l.PostingIndex).Distinct());
        Assert.Null(r.Header!.Memo);
        Assert.Empty(r.HeaderTags ?? []);
        var cash = r.Legs.Single(l => l.AccountId == Brokerage.Id);
        var income = r.Legs.Single(l => l.AccountId == Dividends.Id);
        Assert.Equal("q3 · qualified", cash.LegMemo);
        Assert.Null(income.LegMemo);
        Assert.Equal(["income", "fund", "tax"], TagsOn(r.LegTags!, cash));
        Assert.Empty(TagsOn(r.LegTags!, income));

        var brokerage = Shown(r, Brokerage.Id, cash.PostingIndex);
        Assert.Equal("q3 · qualified", brokerage.Memo);
        Assert.False(brokerage.IsReadOnly);
        var category = Shown(r, Dividends.Id, income.PostingIndex);
        Assert.Equal("q3 · qualified", category.Memo);
        Assert.Equal(["income", "fund", "tax"], category.Tags);
        Assert.True(category.IsReadOnly);
    }

    [Fact]
    public void A_buy_with_a_fee_keeps_one_value_on_the_header_and_nothing_on_any_leg()
    {
        var r = Invest("""
            {"obj_type":"txn","id":"i-2","acctid":"a-brk","desc":"BUY FUND A","memo":"rebalance","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell","tags":"plan",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000",
             "1.id":"s1","1.acctid":"a-fee","1.invest.splittype":"fee","1.desc":"commission","1.tags":"cost",
             "1.samt":"500","1.pamt":"-500"}
            """);

        Assert.Equal("rebalance · commission", r.Header!.Memo);
        Assert.Equal(["plan", "cost"], r.HeaderTags);
        Assert.All(r.Legs, l => Assert.Null(l.LegMemo));
        Assert.Empty(r.LegTags ?? []);

        var feeLeg = r.Legs.Single(l => l.AccountId == Fees.Id);
        var fee = Shown(r, Fees.Id, feeLeg.PostingIndex);
        Assert.Equal("rebalance · commission", fee.Memo);
        Assert.Equal(["plan", "cost"], fee.Tags);
        Assert.True(fee.IsReadOnly);
        Assert.Equal("rebalance · commission", Shown(r, Brokerage.Id, 0).Memo);
    }

    [Fact]
    public void An_events_memos_join_each_distinct_one_once_however_far_apart()
    {
        // Three postings besides the security's, their texts a, b, a: the event keeps
        // "a · b" (the event rule, ADR-0108 D11), as migration 264 joins an event's memos, not "a · b · a".
        var r = Invest("""
            {"obj_type":"txn","id":"i-7","acctid":"a-brk","desc":"FUND A","dt":"20260105",
             "invest.txntype":"divx","xfer_type":"xfrtp_dividendxfr",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"0","0.pamt":"0",
             "1.id":"s1","1.acctid":"a-div","1.invest.splittype":"inc","1.desc":"a","1.samt":"-500","1.pamt":"500",
             "2.id":"s2","2.acctid":"a-fee","2.invest.splittype":"fee","2.desc":"b","2.samt":"20","2.pamt":"-20",
             "3.id":"s3","3.acctid":"a-chk","3.invest.splittype":"xfr","3.desc":" a ","3.samt":"480","3.pamt":"-480"}
            """);

        Assert.Equal(3, r.Legs.Select(l => l.PostingIndex).Distinct().Count());
        Assert.Equal("a · b", r.Header!.Memo);
        Assert.All(r.Legs, l => Assert.Null(l.LegMemo));
        var feeLeg = r.Legs.Single(l => l.AccountId == Fees.Id);
        Assert.Equal("a · b", Shown(r, Fees.Id, feeLeg.PostingIndex).Memo);
    }

    [Fact]
    public void An_investment_split_desc_equal_to_the_payee_but_not_the_memo_is_not_joined()
    {
        var r = Invest("""
            {"obj_type":"txn","id":"i-3","acctid":"a-brk","desc":"BUY FUND A","memo":"rebalance","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.desc":"security name",
             "0.samt":"100000","0.pamt":"-10000",
             "1.id":"s1","1.acctid":"a-fee","1.invest.splittype":"fee","1.desc":"BUY FUND A",
             "1.samt":"500","1.pamt":"-500"}
            """);

        // The security split's desc is never joined either: it names the security.
        Assert.Equal("rebalance", r.Header!.Memo);
    }

    [Fact]
    public void A_buy_funded_from_another_brokerage_is_owned_by_its_home_brokerage_and_read_only_in_the_funding_one()
    {
        var r = Invest("""
            {"obj_type":"txn","id":"i-4","acctid":"a-brk","desc":"BUY FUND A","memo":"from the other","dt":"20260105",
             "invest.txntype":"buyx","xfer_type":"xfrtp_buysellxfr","tags":"move",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000",
             "1.id":"s1","1.acctid":"a-brk2","1.invest.splittype":"xfr","1.desc":"funding","1.samt":"-10000","1.pamt":"10000"}
            """);

        Assert.Equal(Brokerage.Id, r.Header!.OriginatingAccountId);
        var postings = r.Legs.GroupBy(l => l.PostingIndex).ToList();
        Assert.All(postings, p => Assert.Contains(p, l => l.AccountId == Brokerage.Id));
        Assert.Equal("from the other · funding", r.Header.Memo);
        Assert.Equal(["move"], r.HeaderTags);

        var fundingLeg = r.Legs.Single(l => l.AccountId == OtherBrokerage.Id);
        Assert.Null(fundingLeg.LegMemo);
        var funding = Shown(r, OtherBrokerage.Id, fundingLeg.PostingIndex);
        Assert.True(funding.IsReadOnly);
        Assert.Equal("from the other · funding", funding.Memo);
    }

    [Fact]
    public void An_investment_xfr_to_a_bank_keeps_its_value_on_the_brokerage_leg()
    {
        var r = Invest("""
            {"obj_type":"txn","id":"i-5","acctid":"a-brk","desc":"Withdraw","memo":"cash out","dt":"20260105",
             "invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s0","0.acctid":"a-chk","0.invest.splittype":"xfr","0.desc":"to checking","0.tags":"cash",
             "0.samt":"1000","0.pamt":"-1000"}
            """);

        var cash = r.Legs.Single(l => l.AccountId == Brokerage.Id);
        Assert.Equal("cash out · to checking", cash.LegMemo);
        Assert.Equal(["cash"], TagsOn(r.LegTags!, cash));
        var checking = Shown(r, Checking.Id, 0);
        Assert.True(checking.IsReadOnly);
        Assert.Equal("cash out · to checking", checking.Memo);
        Assert.True(checking.MemoInherited);
    }

    [Fact]
    public void The_first_row_the_investment_mapper_emits_is_the_brokerage_cash_row()
    {
        // The header's payee is lifted from the first row, which must therefore be the
        // brokerage's cash row on every shape, whatever order Moneydance lists splits in.
        var txn = MdTxn.From(Item("""
            {"obj_type":"txn","id":"i-6","acctid":"a-brk","desc":"BUY FUND A","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s0","0.acctid":"a-fee","0.invest.splittype":"fee","0.desc":"commission","0.samt":"500","0.pamt":"-500",
             "1.id":"s1","1.acctid":"a-sec","1.invest.splittype":"sec","1.samt":"100000","1.pamt":"-10000"}
            """));

        var mapped = InvestmentTransactionMapper.Map(txn, Accounts, Securities, "test");

        Assert.Equal(Brokerage.Id, mapped.Rows[0].AccountId);
        Assert.Equal("BUY FUND A", mapped.Rows[0].FeedPayee);
    }
}
