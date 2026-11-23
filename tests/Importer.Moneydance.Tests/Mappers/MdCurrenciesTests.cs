using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// The currencies of a Moneydance export (ADR-0114): the base currency is the one
/// <c>curr</c> item with <c>isbase</c> set, an account's currency is the code of the
/// <c>curr</c> item its <c>currid</c> names, and nothing that cannot be read defaults to
/// USD. Every fixture is synthetic.
/// </summary>
public sealed class MdCurrenciesTests
{
    private static MdExport Export(string items) => MdItemReader.ReadString($$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [ {{items}} ]
        }
        """);

    private const string Eur = """{"obj_type":"curr","id":"c-eur","currid":"EUR","name":"Euro","dec":"2","isbase":"1"}""";
    private const string Usd = """{"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2"}""";

    [Fact]
    public void The_base_currency_is_the_curr_item_marked_isbase()
    {
        using var export = Export(Usd + "," + Eur);
        var currencies = MdCurrencies.From(export);

        Assert.Equal("EUR", currencies.BaseCurrencyCode);
        Assert.Null(currencies.BaseCurrencyProblem);
    }

    [Theory]
    [InlineData("""{"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar"}""", "no currency")]
    [InlineData("""{"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","isbase":"1"},{"obj_type":"curr","id":"c-eur","currid":"EUR","name":"Euro","isbase":"1"}""", "2 currencies")]
    [InlineData("""{"obj_type":"curr","id":"c-sec","currid":"^SEC","name":"Fund A","type":"s","isbase":"1"}""", "is a security")]
    [InlineData("""{"obj_type":"curr","id":"c-odd","currid":"Gold","name":"Gold","isbase":"1"}""", "not a 3-letter")]
    public void A_base_currency_that_cannot_be_read_is_a_problem_not_a_default(string items, string expected)
    {
        using var export = Export(items);
        var currencies = MdCurrencies.From(export);

        Assert.Null(currencies.BaseCurrencyCode);
        Assert.Contains(expected, currencies.BaseCurrencyProblem, StringComparison.Ordinal);
        Assert.Contains(currencies.BaseCurrencyProblem!, currencies.Problems(export));
    }

    [Fact]
    public void Problems_name_each_account_that_would_be_imported_without_a_readable_currency()
    {
        using var export = Export(Eur + "," + """
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root"},
            {"obj_type":"acct","id":"a-ok","type":"b","name":"Account A","currid":"c-eur"},
            {"obj_type":"acct","id":"a-none","type":"b","name":"Account B"},
            {"obj_type":"acct","id":"a-gone","type":"c","name":"Account C","currid":"c-gone"},
            {"obj_type":"acct","id":"a-cat","type":"e","name":"Category A","currid":"c-sec"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","currid":"c-sec"},
            {"obj_type":"acct","id":"a-odd","type":"x","name":"Account D"},
            {"obj_type":"curr","id":"c-sec","currid":"^SEC","name":"Fund A","type":"s"}
            """);

        var problems = MdCurrencies.From(export).Problems(export);

        // The root, the security sub-account and the unknown type never become
        // accounts, so they need no currency; the three that would, and cannot, are named.
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("'Account B' has no currency", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'Account C' names a currency ('c-gone')", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'Category A' names the security 'Fund A'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_whose_currencies_all_read_has_no_problems()
    {
        using var export = Export(Eur + "," + Usd + "," + """
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-eur"},
            {"obj_type":"acct","id":"a-eu","type":"b","name":"Account A","currid":"c-eur"},
            {"obj_type":"acct","id":"a-us","type":"b","name":"Account B","currid":"c-usd"}
            """);
        var currencies = MdCurrencies.From(export);

        Assert.Empty(currencies.Problems(export));
        Assert.Equal("EUR", currencies.BaseCurrencyCode);
    }

    [Fact]
    public void The_demo_export_reads_as_a_dollar_file()
    {
        // ADR-0091: the generated Demo is the source of truth for the export's shape. The
        // tiny span keeps every account, security and price.
        using var export = DemoTestExports.TinyExport();
        var currencies = MdCurrencies.From(export);

        Assert.Equal("USD", currencies.BaseCurrencyCode);
        Assert.Empty(currencies.Problems(export));
    }

    // --- decimal places, and a security's relative currency ---------------------------
    //
    // Shapes from a real Moneydance export with a euro base: a currency's `dec` is the
    // places its amounts are stored at (the yen 0), and a security quoted in another
    // currency carries `rel_curr_id` (that currency's curr id) beside
    // `relative_to_currid` (its code). Values are synthetic.

    private const string Jpy = """{"obj_type":"curr","id":"c-jpy","currid":"JPY","name":"Yen","dec":"0"}""";

    private static string Security(string extra) =>
        """{"obj_type":"curr","id":"c-sec","currid":"X1","name":"Fund Q","type":"s","dec":"5" """ + extra + "}";

    [Fact]
    public void An_account_takes_its_currencys_decimal_places()
    {
        using var export = Export(Eur + "," + Jpy + "," + """
            {"obj_type":"acct","id":"a-y","type":"b","name":"Account Y","currid":"c-jpy"},
            {"obj_type":"acct","id":"a-e","type":"b","name":"Account E","currid":"c-eur"}
            """);
        var currencies = MdCurrencies.From(export);
        var accounts = export.AllItems.Where(i => i.ObjType == "acct").Select(Coffer.Importer.Moneydance.Json.Typed.MdAcct.From).ToList();

        Assert.Equal(new AccountCurrency("JPY", 0), currencies.RequireAccount(accounts[0]));
        Assert.Equal(new AccountCurrency("EUR", 2), currencies.RequireAccount(accounts[1]));
        Assert.Empty(currencies.Problems(export));
    }

    // ADR-0114 D15: the file's dec is accepted when it is at most the places Coffer keeps
    // for the code, so every amount fits exactly.
    [Theory]
    [InlineData("KWD", 3)]   // a 3-place code at its own places
    [InlineData("EUR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("EUR", 0)]   // fewer places than the code keeps: every amount still fits
    public void A_currency_whose_places_fit_its_code_resolves_at_the_files_places(string code, int dec)
    {
        using var export = Export(Eur + "," + $$"""
            {"obj_type":"curr","id":"c-k","currid":"{{code}}","name":"Money K","dec":"{{dec}}"},
            {"obj_type":"acct","id":"a-k","type":"b","name":"Account K","currid":"c-k"}
            """);
        var currencies = MdCurrencies.From(export);
        var account = Coffer.Importer.Moneydance.Json.Typed.MdAcct.From(export.AllItems.Single(i => i.Id == "a-k"));

        Assert.Empty(currencies.Problems(export));
        Assert.Equal(new AccountCurrency(code, dec), currencies.RequireAccount(account));
    }

    [Theory]
    [InlineData("""{"obj_type":"curr","id":"c-kwd","currid":"EUR","name":"Euro B","dec":"3"}""",
        "whose amounts the file keeps at 3 decimal places, and Coffer keeps EUR at 2")]
    [InlineData("""{"obj_type":"curr","id":"c-kwd","currid":"JPY","name":"Yen","dec":"2"}""",
        "whose amounts the file keeps at 2 decimal places, and Coffer keeps JPY at 0")]
    [InlineData("""{"obj_type":"curr","id":"c-kwd","currid":"KWD","name":"Dinar","dec":"4"}""",
        "whose amounts the file keeps at 4 decimal places, and Coffer keeps KWD at 3")]
    [InlineData("""{"obj_type":"curr","id":"c-kwd","currid":"KWD","name":"Dinar"}""", "records no decimal places")]
    public void A_currency_whose_places_do_not_fit_its_code_is_a_problem_naming_the_account_and_both_counts(
        string curr, string expected)
    {
        using var export = Export(Eur + "," + curr + "," + """
            {"obj_type":"acct","id":"a-k","type":"b","name":"Account K","currid":"c-kwd"}
            """);
        var currencies = MdCurrencies.From(export);

        var problem = Assert.Single(currencies.Problems(export));
        Assert.Contains("'Account K'", problem, StringComparison.Ordinal);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => currencies.RequireAccount(
            Coffer.Importer.Moneydance.Json.Typed.MdAcct.From(export.AllItems.Single(i => i.Id == "a-k"))));
    }

    // ADR-0114 D15: an account or ledger currency must be an ISO 4217 code Coffer accepts:
    // not an "N.A." entry, not CLF or UYW (4 places), not a code List One does not hold.
    [Theory]
    [InlineData("XAU")]   // an "N.A." entry
    [InlineData("CLF")]   // a fund unit with 4 places
    [InlineData("UYW")]
    [InlineData("QQQ")]   // three letters, not on ISO 4217 List One
    public void A_code_Coffer_does_not_accept_is_refused_as_an_account_currency(string code)
    {
        using var export = Export(Eur + "," + $$"""
            {"obj_type":"curr","id":"c-x","currid":"{{code}}","name":"Unit X","dec":"2"},
            {"obj_type":"acct","id":"a-x","type":"b","name":"Account X","currid":"c-x"}
            """);

        var problem = Assert.Single(MdCurrencies.From(export).Problems(export));
        Assert.Equal(
            $"The account 'Account X' is in 'Unit X', whose code '{code}' is not a currency Coffer accepts for an account.",
            problem);
    }

    [Theory]
    [InlineData("XAU")]
    [InlineData("CLF")]
    [InlineData("UYW")]
    [InlineData("QQQ")]
    public void A_code_Coffer_does_not_accept_is_refused_as_the_base_currency(string code)
    {
        using var export = Export($$"""
            {"obj_type":"curr","id":"c-x","currid":"{{code}}","name":"Unit X","dec":"2","isbase":"1"}
            """);
        var currencies = MdCurrencies.From(export);

        Assert.Null(currencies.BaseCurrencyCode);
        Assert.Equal(
            $"The export's base currency 'Unit X' has the code '{code}', which is not a currency Coffer accepts for a ledger.",
            currencies.BaseCurrencyProblem);
        Assert.Contains(currencies.BaseCurrencyProblem!, currencies.Problems(export));
    }

    // ADR-0114 D15: the loan mapper checks escrow and the fixed payment at the loan
    // currency's places, and the preview refuses a loan whose terms do not fit.
    [Theory]
    [InlineData(""","escrow_payment":"12.345","calc_pmt":"1" """, "has a loan escrow payment with 3 decimal places, and Coffer keeps EUR at 2")]
    [InlineData(""","calc_pmt":"0","monthly_pmt":"250.125" """, "has a loan fixed payment with 3 decimal places, and Coffer keeps EUR at 2")]
    public void A_loan_whose_terms_do_not_fit_its_currency_is_a_problem_naming_the_account(string terms, string expected)
    {
        using var export = Export(Eur + "," + Loan(terms));

        var problem = Assert.Single(MdCurrencies.From(export).Problems(export));
        Assert.Equal($"The account 'Loan L' {expected}.", problem);
    }

    [Theory]
    [InlineData(""","escrow_payment":"12.34","calc_pmt":"0","monthly_pmt":"250.10" """)]
    [InlineData(""","calc_pmt":"1","monthly_pmt":"250.125" """)]   // computed: monthly_pmt is not read
    public void A_loan_whose_terms_fit_its_currency_is_no_problem(string terms)
    {
        using var export = Export(Eur + "," + Loan(terms));

        Assert.Empty(MdCurrencies.From(export).Problems(export));
    }

    private static string Loan(string terms) => $$"""
        {"obj_type":"acct","id":"a-loan","type":"o","name":"Loan L","currid":"c-eur","int_rate":"4.0",
         "num_payments":"360","pmts_per_year":"12","init_principal":"10000000" {{terms}}}
        """;

    // ADR-0016: a non-category parent with no transactions of its own is a placeholder the
    // import does not create, so its currency and its loan terms are never read, and the
    // preview checks only the accounts the import creates.
    private const string PlaceholderChild = """
        {"obj_type":"acct","id":"a-child","type":"b","name":"Account A","parentid":"a-loan","currid":"c-eur"},
        {"obj_type":"txn","id":"t-1","acctid":"a-child","dt":"20260115","0.acctid":"a-child2","0.pamt":"-1234"},
        {"obj_type":"acct","id":"a-child2","type":"b","name":"Account B","currid":"c-eur"}
        """;

    [Theory]
    // Terms at more places than the loan's currency.
    [InlineData(""","escrow_payment":"12.345","calc_pmt":"1" """, "c-eur")]
    // A currency that cannot be read.
    [InlineData(""","calc_pmt":"1" """, "c-gone")]
    public void A_placeholder_parent_the_import_does_not_create_is_no_problem(string terms, string currId)
    {
        using var export = Export(Eur + "," + Loan(terms).Replace("c-eur", currId, StringComparison.Ordinal)
            + "," + PlaceholderChild);

        Assert.Empty(MdCurrencies.From(export).Problems(export));
    }

    [Fact]
    public void A_parent_with_transactions_of_its_own_is_imported_so_its_terms_are_checked()
    {
        using var export = Export(Eur + "," + Loan(""","escrow_payment":"12.345","calc_pmt":"1" """)
            + "," + PlaceholderChild + ","
            + """{"obj_type":"txn","id":"t-2","acctid":"a-loan","dt":"20260115","0.acctid":"a-child2","0.pamt":"-1234"}""");

        Assert.Equal(
            "The account 'Loan L' has a loan escrow payment with 3 decimal places, and Coffer keeps EUR at 2.",
            Assert.Single(MdCurrencies.From(export).Problems(export)));
    }

    // ADR-0114 D7, D15: a category is stamped with the reporting currency, and only a
    // posting from one category to another reads the primary category's own places
    // (MdAmounts.Posting). A category kept at more places than the reporting currency is
    // a problem only when such a posting exists, in a transaction the bank route imports
    // or in a reminder's template.
    private const string DinarCategories = """
        {"obj_type":"curr","id":"c-kwd","currid":"KWD","name":"Dinar","dec":"3"},
        {"obj_type":"acct","id":"a-eu","type":"b","name":"Account A","currid":"c-eur"},
        {"obj_type":"acct","id":"c-k","type":"e","name":"Category K","currid":"c-kwd"},
        {"obj_type":"acct","id":"c-e","type":"i","name":"Category E","currid":"c-eur"}
        """;

    // Two blank-named top-level leaf categories, as Moneydance's income and expense
    // "uncategorized" are: the first kept at the dinar's places.
    private const string BlankLeafCategories = """
        {"obj_type":"acct","id":"c-u1","type":"e","name":"","currid":"c-kwd"},
        {"obj_type":"acct","id":"c-u2","type":"i","name":"","currid":"c-eur"}
        """;

    [Theory]
    [InlineData("""{"obj_type":"txn","id":"t-1","acctid":"c-k","dt":"20260115","0.acctid":"c-e","0.pamt":"12345"}""")]
    [InlineData("""{"obj_type":"reminder","id":"r-1","sdt":"20260115","txn.acctid":"c-k","txn.0.acctid":"c-e","txn.0.pamt":"12345"}""")]
    public void A_category_that_pays_another_at_more_places_than_the_reporting_currency_is_a_problem(string posting)
    {
        using var export = Export(Eur + "," + DinarCategories + "," + posting);

        var problem = Assert.Single(MdCurrencies.From(export).Problems(export));
        Assert.Equal(
            "The category 'Category K' moves money to another category in 'Dinar' at 3 decimal places, "
            + "and Coffer keeps a category in the ledger's reporting currency, EUR, at 2.",
            problem);
    }

    [Theory]
    // Paired with a real account, either way round: read at the real side's places.
    [InlineData("""{"obj_type":"txn","id":"t-1","acctid":"a-eu","dt":"20260115","0.acctid":"c-k","0.pamt":"-1234"}""")]
    [InlineData("""{"obj_type":"txn","id":"t-1","acctid":"c-k","dt":"20260115","0.acctid":"a-eu","0.pamt":"12345"}""")]
    // The category at more places is the target, so the primary's places are the euro's.
    [InlineData("""{"obj_type":"txn","id":"t-1","acctid":"c-e","dt":"20260115","0.acctid":"c-k","0.pamt":"1234"}""")]
    // Investment-shaped: the investment route reads only the brokerage's places.
    [InlineData("""{"obj_type":"txn","id":"t-1","acctid":"c-k","dt":"20260115","invest.txntype":"MISCINC","0.acctid":"c-e","0.pamt":"12345"}""")]
    // A split on the primary itself is never imported.
    [InlineData("""{"obj_type":"txn","id":"t-1","acctid":"c-k","dt":"20260115","0.acctid":"c-k","0.pamt":"12345"}""")]
    // Every blank top-level leaf category becomes the one Uncategorized, so a split from
    // one onto another lands on its own primary and is never imported either.
    [InlineData(BlankLeafCategories + ","
        + """{"obj_type":"txn","id":"t-1","acctid":"c-u1","dt":"20260115","0.acctid":"c-u2","0.pamt":"12345"}""")]
    [InlineData(BlankLeafCategories + ","
        + """{"obj_type":"reminder","id":"r-1","sdt":"20260115","txn.acctid":"c-u1","txn.0.acctid":"c-u2","txn.0.pamt":"12345"}""")]
    // A transaction or a reminder the import skips whole: a split onto an account the
    // export does not hold, or a reminder with no start date.
    [InlineData("""{"obj_type":"txn","id":"t-1","acctid":"c-k","dt":"20260115","0.acctid":"c-e","0.pamt":"12345","1.acctid":"a-gone","1.pamt":"1"}""")]
    [InlineData("""{"obj_type":"reminder","id":"r-1","txn.acctid":"c-k","txn.0.acctid":"c-e","txn.0.pamt":"12345"}""")]
    public void A_category_at_more_places_whose_own_amounts_are_never_stored_is_no_problem(string posting)
    {
        using var export = Export(Eur + "," + DinarCategories + "," + posting);

        Assert.Empty(MdCurrencies.From(export).Problems(export));
    }

    [Fact]
    public void A_blank_leaf_category_that_pays_a_named_one_at_more_places_is_a_problem()
    {
        // The control for the blank-leaf cases above: the named category is its own account,
        // so this posting is imported at the blank one's places.
        using var export = Export(Eur + "," + DinarCategories + "," + BlankLeafCategories + "," + """
            {"obj_type":"txn","id":"t-1","acctid":"c-u1","dt":"20260115","0.acctid":"c-e","0.pamt":"12345"}
            """);

        Assert.Equal(
            "An unnamed category (c-u1) moves money to another category in 'Dinar' at 3 decimal places, "
            + "and Coffer keeps a category in the ledger's reporting currency, EUR, at 2.",
            Assert.Single(MdCurrencies.From(export).Problems(export)));
    }

    [Fact]
    public void A_category_that_pays_another_is_checked_against_the_reporting_currency_given()
    {
        using var export = Export(Eur + "," + """
            {"obj_type":"acct","id":"c-a","type":"e","name":"Category A","currid":"c-eur"},
            {"obj_type":"acct","id":"c-b","type":"e","name":"Category B","currid":"c-eur"},
            {"obj_type":"txn","id":"t-1","acctid":"c-a","dt":"20260115","0.acctid":"c-b","0.pamt":"1234"}
            """);
        var currencies = MdCurrencies.From(export);

        Assert.Empty(currencies.Problems(export));
        Assert.Empty(currencies.Problems(export, "KWD"));
        Assert.Equal(
            "The category 'Category A' moves money to another category in 'Euro' at 2 decimal places, "
            + "and Coffer keeps a category in the ledger's reporting currency, JPY, at 0.",
            Assert.Single(currencies.Problems(export, "JPY")));
    }

    [Fact]
    public void A_securitys_prices_are_in_its_relative_currency_when_it_has_one()
    {
        using var export = Export(Eur + "," + Usd + "," + Security(""","rel_curr_id":"c-usd","relative_to_currid":"USD" """));
        var currencies = MdCurrencies.From(export);

        Assert.Equal("USD", currencies.RequirePriceCurrency("c-sec"));
        Assert.Empty(currencies.Problems(export));
    }

    [Fact]
    public void A_securitys_prices_are_in_the_base_currency_when_it_has_no_relative_one()
    {
        using var export = Export(Eur + "," + Usd + "," + Security(""));

        Assert.Equal("EUR", MdCurrencies.From(export).RequirePriceCurrency("c-sec"));
    }

    [Theory]
    [InlineData(""","rel_curr_id":"c-gone","relative_to_currid":"USD" """, "is quoted in a currency ('c-gone')")]
    [InlineData(""","rel_curr_id":"c-other-sec" """, "is quoted in the security 'Fund R'")]
    [InlineData(""","rel_curr_id":"c-usd","relative_to_currid":"GBP" """, "is quoted in 'USD' by id and 'GBP' by code")]
    [InlineData(""","relative_to_currid":"USD" """, "names the relative currency 'USD' without the currency it is")]
    public void A_relative_currency_that_cannot_be_read_is_a_problem_naming_the_security(string extra, string expected)
    {
        using var export = Export(Eur + "," + Usd + "," + Security(extra) + "," + """
            {"obj_type":"curr","id":"c-other-sec","currid":"X2","name":"Fund R","type":"s","dec":"4"}
            """);
        var currencies = MdCurrencies.From(export);

        var problem = Assert.Single(currencies.Problems(export));
        Assert.Contains("'Fund Q'", problem, StringComparison.Ordinal);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => currencies.RequirePriceCurrency("c-sec"));
    }
}
