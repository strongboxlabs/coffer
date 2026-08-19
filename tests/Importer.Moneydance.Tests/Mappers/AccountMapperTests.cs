using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

public sealed class AccountMapperTests
{
    private static readonly Guid TestLedgerId = TestLedger.Id;

    [Theory]
    // Best-guess tax treatment from the account name (ADR-0066). Conservative:
    // unrecognized names (taxable brokerages) seed NULL for the user to set.
    [InlineData("Roth IRA", "investment", "tax_free")]               // Roth wins over IRA
    [InlineData("Rollover IRA", "investment", "tax_deferred")]
    [InlineData("Workplace 401(K)", "investment", "tax_deferred")]
    [InlineData("College 529 Plan", "investment", "other")]
    [InlineData("HSA", "investment", "other")]
    [InlineData("Brokerage A", "investment", null)]                  // taxable not name-detectable
    [InlineData("Checking", "bank", null)]
    [InlineData("Groceries", "category", null)]                       // categories never get one
    public void InferTaxStatus_best_guess_from_name(string name, string accountType, string? expected)
    {
        Assert.Equal(expected, AccountMapper.InferTaxStatus(name, accountType));
    }

    private static readonly AccountMapper.MapInputs EmptyInputs = new(
        AccountsWithOwnTransactions: new HashSet<string>(StringComparer.Ordinal),
        AccountsThatAreParents: new HashSet<string>(StringComparer.Ordinal));

    private static MdAcct AcctFromJson(string fragment)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """;
        return MdAcct.From(MdItemReader.ReadString(wrapped).AllItems.Single());
    }

    [Theory]
    [InlineData("b", "bank",        null)]
    [InlineData("c", "credit_card", null)]
    [InlineData("v", "investment",  null)]
    [InlineData("a", "asset",       null)]
    [InlineData("l", "liability",   null)]
    [InlineData("o", "loan",        null)]
    [InlineData("i", "category",    "income")]
    [InlineData("e", "category",    "expense")]
    public void TranslateType_handles_each_supported_md_code(string code, string expectedType, string? expectedKind)
    {
        var translated = AccountMapper.TranslateType(code);
        Assert.NotNull(translated);
        Assert.Equal(expectedType, translated!.Value.AccountType);
        Assert.Equal(expectedKind, translated.Value.CategoryKind);
    }

    [Theory]
    [InlineData("s")]   // security sub-account; handled by Map(), not TranslateType
    [InlineData("r")]   // global root; handled by Map()
    [InlineData("?")]   // anything unknown
    public void TranslateType_returns_null_for_codes_not_handled_by_mapper(string code)
    {
        Assert.Null(AccountMapper.TranslateType(code));
    }

    [Fact]
    public void Map_skips_root_account()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"r-1","name":"","type":"r","currid":"USD"}
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);
        Assert.Null(result.Row);
        Assert.Equal(AccountMapper.SkipReason.Root, result.Skip);
    }

    [Fact]
    public void Map_skips_security_sub_account()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"s-1","name":"IDXB","type":"s","currid":"sec-1"}
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);
        Assert.Null(result.Row);
        Assert.Equal(AccountMapper.SkipReason.SecuritySubAccount, result.Skip);
    }

    [Fact]
    public void Map_skips_unknown_type_code()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"x-1","name":"weird","type":"x","currid":"USD"}
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);
        Assert.Null(result.Row);
        Assert.Equal(AccountMapper.SkipReason.UnknownTypeCode, result.Skip);
    }

    [Fact]
    public void Map_translates_a_typical_bank_account_with_balance()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"b-1","name":"Northwind Checking","type":"b",
             "currid":"USD","sbal":"850317"}
            """);

        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("bank", result.Row!.AccountType);
        Assert.Null(result.Row.CategoryKind);
        Assert.Equal("Northwind Checking", result.Row.Name);
        Assert.Equal(8503.17m, result.Row.OpeningBalance);
        Assert.True(result.Row.IsActive);
        Assert.Equal("USD", result.Row.CurrencyCode);
        Assert.Equal("b-1", result.Row.ExternalId);
        Assert.Null(result.Row.ParentId);
    }

    [Fact]
    public void Map_translates_loan_with_negative_balance()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"o-1","name":"Maple Mortgage","type":"o",
             "currid":"USD","sbal":"-46164053"}
            """);

        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("loan", result.Row!.AccountType);
        Assert.Equal(-461640.53m, result.Row.OpeningBalance);
    }

    [Fact]
    public void Map_translates_an_income_category()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"i-1","name":"Salary","type":"i","currid":"USD","sbal":"123456"}
            """);

        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("category", result.Row!.AccountType);
        Assert.Equal("income", result.Row.CategoryKind);
        // Categories must have opening_balance = 0 regardless of MD's sbal.
        Assert.Equal(0m, result.Row.OpeningBalance);
    }

    [Fact]
    public void Map_translates_an_expense_category()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"e-1","name":"Groceries","type":"e","currid":"USD"}
            """);

        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("category", result.Row!.AccountType);
        Assert.Equal("expense", result.Row.CategoryKind);
    }

    [Fact]
    public void Map_marks_inactive_account_inactive()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"b-2","name":"Old Account","type":"b",
             "currid":"USD","is_inactive":"y"}
            """);

        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);
        Assert.NotNull(result.Row);
        Assert.False(result.Row!.IsActive);
    }

    [Fact]
    public void Map_falls_back_to_unnamed_for_blank_name()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"b-3","name":"","type":"b","currid":"USD"}
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);
        Assert.NotNull(result.Row);
        Assert.Equal("(unnamed)", result.Row!.Name);
    }

    [Fact]
    public void Map_drops_non_category_placeholder_with_no_own_transactions()
    {
        // "Checking" parents two Northwind accounts and has no transactions of its own.
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"placeholder","name":"Checking","type":"b","currid":"USD"}
            """);

        var inputs = new AccountMapper.MapInputs(
            AccountsWithOwnTransactions: new HashSet<string>(StringComparer.Ordinal),
            AccountsThatAreParents: new HashSet<string>(StringComparer.Ordinal) { "placeholder" });

        var result = AccountMapper.Map(md, inputs, TestLedgerId);

        Assert.Null(result.Row);
        Assert.Equal(AccountMapper.SkipReason.FakeNonCategoryPlaceholder, result.Skip);
    }

    [Fact]
    public void Map_keeps_non_category_parent_that_has_own_transactions()
    {
        // Non-category "parent with txns": kept (per ADR-0016) but children
        // still get parent_id=NULL because hierarchy is dropped for non-categories.
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"v-1","name":"Brokerage A IRA","type":"v","currid":"USD"}
            """);

        var inputs = new AccountMapper.MapInputs(
            AccountsWithOwnTransactions: new HashSet<string>(StringComparer.Ordinal) { "v-1" },
            AccountsThatAreParents: new HashSet<string>(StringComparer.Ordinal) { "v-1" });

        var result = AccountMapper.Map(md, inputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("investment", result.Row!.AccountType);
        Assert.Null(result.Row.ParentId);
    }

    [Fact]
    public void Map_keeps_category_parent_that_has_no_own_transactions()
    {
        // A category placeholder ("Bills" with sub-categories) is preserved —
        // hierarchy is meaningful for categories.
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"e-bills","name":"Bills","type":"e","currid":"USD"}
            """);

        var inputs = new AccountMapper.MapInputs(
            AccountsWithOwnTransactions: new HashSet<string>(StringComparer.Ordinal),
            AccountsThatAreParents: new HashSet<string>(StringComparer.Ordinal) { "e-bills" });

        var result = AccountMapper.Map(md, inputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("category", result.Row!.AccountType);
    }

    [Fact]
    public void ComputeInputs_walks_export_and_collects_both_sets()
    {
        const string json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"acct","id":"a-parent","name":"P","type":"e","currid":"USD"},
                {"obj_type":"acct","id":"a-child", "name":"C","type":"e","currid":"USD","parentid":"a-parent"},
                {"obj_type":"acct","id":"a-leaf",  "name":"L","type":"b","currid":"USD"},
                {"obj_type":"txn","id":"t-1","acctid":"a-leaf","desc":"x","dt":"20240101",
                 "0.id":"s","0.acctid":"a-child","0.samt":"100","0.pamt":"-100"}
              ]
            }
            """;
        var export = MdItemReader.ReadString(json);

        var inputs = AccountMapper.ComputeInputs(export);

        Assert.Contains("a-leaf",   inputs.AccountsWithOwnTransactions); // primary side of the txn
        Assert.Contains("a-child",  inputs.AccountsWithOwnTransactions); // split's other side
        Assert.Contains("a-parent", inputs.AccountsThatAreParents);
        Assert.DoesNotContain("a-leaf",   inputs.AccountsThatAreParents);
        Assert.DoesNotContain("a-child",  inputs.AccountsThatAreParents);
    }

    [Fact]
    public void Map_propagates_account_metadata_from_md()
    {
        // Regression for migration 012: MD's hide flag, comment, account
        // numbers (bank vs invst), institution name (bank vs inst), routing
        // number, and account URL were previously parsed and dropped.
        // Mig 106: MD's `hide` flag collapses into IsActive=false (single
        // lifecycle flag in Coffer).
        var md = AcctFromJson("""
            {
              "obj_type":"acct","id":"a-bank","name":"Northwind Checking",
              "type":"b","currid":"USD","sbal":"850317",
              "hide":"y",
              "comment":"household joint account",
              "bank_account_number":"123456789",
              "bank_name":"Northwind Bank, N.A.",
              "ofx_bank_id":"031176110",
              "inst_name":"Northwind Bank",
              "account_url":"https://northwind.example"
            }
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        var row = result.Row!;
        Assert.False(row.IsActive);
        Assert.Equal("household joint account", row.Notes);
        Assert.Equal("123456789",               row.AccountNumber);
        // bank_name takes precedence over inst_name when both present.
        Assert.Equal("Northwind Bank, N.A.",        row.InstitutionName);
        Assert.Equal("031176110",                row.RoutingNumber);
        Assert.Equal("https://northwind.example",   row.AccountUrl);
    }

    [Fact]
    public void Map_falls_back_to_invst_account_number_for_brokerage_accounts()
    {
        var md = AcctFromJson("""
            {
              "obj_type":"acct","id":"a-broker","name":"Brokerage A","type":"v","currid":"USD",
              "invst_account_number":"V-99887766",
              "inst_name":"Brokerage A Group"
            }
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("V-99887766",     result.Row!.AccountNumber);
        Assert.Equal("Brokerage A Group", result.Row.InstitutionName);
    }

    [Fact]
    public void Map_emits_null_metadata_when_md_fields_absent_or_empty()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"a-bare","name":"Bare","type":"b","currid":"USD","sbal":"0"}
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        var row = result.Row!;
        // Absent `hide` + absent `is_inactive` → active.
        Assert.True(row.IsActive);
        Assert.Null(row.Notes);
        Assert.Null(row.AccountNumber);
        Assert.Null(row.InstitutionName);
        Assert.Null(row.RoutingNumber);
        Assert.Null(row.AccountUrl);
        Assert.Null(row.OpenedOn);
    }

    // ---- opened_on (ADR-0050 / mig 127) -----------------------------------

    [Fact]
    public void Map_carries_the_md_start_date_onto_the_row()
    {
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"a-inv","name":"Brokerage A","type":"v",
             "currid":"USD","sbal":"100000","date_created":"20180314"}
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal(new DateOnly(2018, 3, 14), result.Row!.OpenedOn);
    }

    [Fact]
    public void Map_drops_the_start_date_for_categories()
    {
        // A category's opening balance is forced to 0 by a CHECK constraint, so
        // the as-of date of that balance carries no meaning — even when MD has one.
        var md = AcctFromJson("""
            {"obj_type":"acct","id":"c-1","name":"Groceries","type":"e",
             "currid":"USD","date_created":"20180314"}
            """);
        var result = AccountMapper.Map(md, EmptyInputs, TestLedgerId);

        Assert.NotNull(result.Row);
        Assert.Equal("category", result.Row!.AccountType);
        Assert.Equal(0m, result.Row.OpeningBalance);
        Assert.Null(result.Row.OpenedOn);
    }
}
