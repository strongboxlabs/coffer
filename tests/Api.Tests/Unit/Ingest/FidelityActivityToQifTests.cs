using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// The Fidelity CSV-to-QIF shim (ADR-0031 Phase 6).
/// </summary>
/// <remarks>
/// <para>Tested at the QIF it produces, not at the transactions that come out the far
/// end. The shim's whole job is "read this format correctly and say which verb each row
/// is"; turning a verb into a Coffer action is the QIF provider's job and has its own
/// tests. Asserting on the downstream hints would test both at once and report failures
/// in the wrong file.</para>
///
/// <para>Fixtures mirror the STRUCTURE of two real exports from the maintainer's account
/// — the same two blank preamble lines, the same 13 columns in the same order, the same
/// ragged legal boilerplate — with every value invented. The real files never enter the
/// repo.</para>
/// </remarks>
public sealed class FidelityActivityToQifTests
{
    /// <summary>The header in the order a REAL export uses.</summary>
    /// <remarks>
    /// <c>Price ($)</c> BEFORE <c>Quantity</c>. The widely-cited reference implementation
    /// expects the opposite, so reading by position turns a share count into a unit price.
    /// </remarks>
    private const string Header =
        "Run Date,Action,Symbol,Description,Type,Price ($),Quantity,Commission ($),"
        + "Fees ($),Accrued Interest ($),Amount ($),Cash Balance ($),Settlement Date";

    /// <summary>Prose containing commas and quotes, which parses as ragged rows.</summary>
    private const string Trailer =
        "\n"
        + "The data and information in this spreadsheet is provided to you solely for your use\n"
        + "\"informational purposes only, and is not intended to provide advice, nor should it\n"
        + "\"purposes. For more information on the data in this spreadsheet, go to Fidelity.com.\"\n"
        + "Date downloaded 09/11/2026 3:21 pm\n";

    private static string Row(
        string date, string action, string symbol, string description,
        string price, string quantity, string amount, string balance,
        string commission = "", string fees = "") =>
        $"{date},{action},{symbol},{description},Cash,{price},{quantity},{commission},"
        + $"{fees},,{amount},{balance},";

    private static string File(params string[] rows) =>
        "\n\n" + Header + "\n" + string.Join("\n", rows) + "\n" + Trailer;

    /// <summary>The nth record's field lines, without the terminator.</summary>
    private static string[] Record(string qif, int index) =>
        qif.Split('^', StringSplitOptions.RemoveEmptyEntries)[index]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('!'))
            .Select(l => l.Trim())
            .ToArray();

    private static string? Field(string qif, int index, char tag) =>
        Record(qif, index).FirstOrDefault(l => l.Length > 0 && l[0] == tag)?[1..];

    [Fact]
    public void The_boilerplate_at_both_ends_is_not_mistaken_for_data()
    {
        // Neither end can be handled by counting lines: two blank lines precede the
        // header, and the trailer is prose. The header is FOUND and the data STOPS at
        // the first row whose width differs.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "12.34", "1012.34"),
            Row("09/01/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "10.00", "1000.00")));

        Assert.StartsWith("!Type:Invst", result.Qif, StringComparison.Ordinal);
        Assert.Equal(2, result.SourceRows.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Data_STOPS_at_the_boilerplate_rather_than_scanning_past_it()
    {
        // Prose with the right number of commas parses as a well-formed row. Skipping
        // ragged lines and carrying on would import it; stopping at the first cannot.
        var result = FidelityActivityToQif.Convert(
            "\n\n" + Header + "\n"
            + Row("09/02/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                  "SAMPLE FUND", "", "0", "12.34", "1012.34") + "\n"
            + "\n"
            + "The data and information in this spreadsheet is provided to you solely\n"
            + "09/01/2026,NOT A TRANSACTION,X,X,Cash,1,1,,,,999.99,999.99,\n");

        Assert.Single(result.SourceRows);
    }

    [Fact]
    public void Columns_are_read_by_NAME_so_a_swapped_pair_cannot_transpose_money()
    {
        // By position, 250 shares would become a $250 unit price and $5 would become the
        // share count — a holding worth fifty times what it is.
        var swapped = "\n\n"
            + "Run Date,Action,Symbol,Description,Type,Quantity,Price ($),Commission ($),"
            + "Fees ($),Accrued Interest ($),Amount ($),Cash Balance ($),Settlement Date\n"
            + "09/02/2026,YOU BOUGHT SAMPLE FUND (SMPL),SMPL,SAMPLE FUND,Cash,"
            + "250,5.00,,,,-1250.00,100.00,\n"
            + Trailer;

        var result = FidelityActivityToQif.Convert(swapped);

        Assert.Equal("250", Field(result.Qif, 0, 'Q'));
        Assert.Equal("5", Field(result.Qif, 0, 'I'));
    }

    [Fact]
    public void A_fractional_share_count_survives_the_projection_at_full_precision()
    {
        // 0.000980392 is not invented: it is the fractional quantity this repo already
        // ships as a real-data fixture in both the QIF and the OFX integration tests.
        //
        // The projection is TRANSPORT, and what it drops here no later stage can recover,
        // because nothing downstream can see the CSV cell. Formatting shares as money
        // ("0.####") rounded this to 0.001 — a 2% overstatement carried straight into
        // ingest_shares NUMERIC(28,8), the NUMERIC(25,12) quantity it grows into, the FIFO
        // lots, and every per-share cost basis and realized gain derived from them.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.1975", "0.000980392", "-0.01", "100.00")));

        Assert.Equal("0.000980392", Field(result.Qif, 0, 'Q'));
        Assert.Equal("10.1975", Field(result.Qif, 0, 'I'));
    }

    [Fact]
    public void A_holding_smaller_than_a_rounding_step_is_not_erased()
    {
        // Below 0.00005 units, money formatting rendered Q as "0": the row passed the
        // `quantity != 0` guard, claimed to be a share purchase, and landed with no
        // shares at all.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "0.00004", "-0.01", "100.00")));

        Assert.Equal("0.00004", Field(result.Qif, 0, 'Q'));
    }

    [Theory]
    // Observed in the maintainer's own exports.
    [InlineData("DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", 0, 12.34, "Div")]
    [InlineData("REINVESTMENT SAMPLE FUND (SMPL) (Cash)", 1.234, -12.34, "ReinvDiv")]
    // Cash in and out with no shares: XIn/XOut, which the QIF provider deliberately
    // gives the BANK shape. A rollover is not a security transaction.
    [InlineData("ROLLOVER CASH DIRECT ROLLOVER FROM SAMPLE PLAN (Cash)", 0, 500.00, "XIn")]
    [InlineData("TRANSFER OF ASSETS ACAT DELIVER (Cash)", 0, -500.00, "XOut")]
    // ...but the SAME words with shares attached are a share movement.
    [InlineData("TRANSFER OF ASSETS ACAT RECEIVE", 10, 0.00, "ShrsIn")]
    [InlineData("TRANSFER OF ASSETS ACAT DELIVER", -10, 0.00, "ShrsOut")]
    // Reached by the SIGN fallback: neither string contains BUY, BOUGHT, SELL or SOLD.
    [InlineData("PURCHASE INTO CORE ACCOUNT SAMPLE FUND (SMPL) (Cash)", 500, -500.00, "Buy")]
    [InlineData("REDEMPTION FROM CORE ACCOUNT SAMPLE FUND (SMPL) (Cash)", -500, 500.00, "Sell")]
    // The reference's named rules.
    [InlineData("YOU BOUGHT SAMPLE FUND (SMPL)", 10, -100.00, "Buy")]
    [InlineData("YOU SOLD SAMPLE FUND (SMPL)", -10, 100.00, "Sell")]
    // MERGER MER is in BOTH name lists, so only the SIGN of quantity can decide it.
    [InlineData("MERGER MER SAMPLE FUND (SMPL)", 10, -100.00, "Buy")]
    [InlineData("MERGER MER SAMPLE FUND (SMPL)", -10, 100.00, "Sell")]
    // The sentence loses to the shares: what moved is what happened.
    [InlineData("YOU BOUGHT SAMPLE FUND (SMPL)", -10, 100.00, "Sell")]
    [InlineData("YOU SOLD SAMPLE FUND (SMPL)", 10, -100.00, "Buy")]
    // Fees and interest, by the reference's rules. Every one is a pure CASH row, which
    // is what makes the quantity guard below free.
    [InlineData("INTEREST EARNED (Cash)", 0, 1.23, "IntInc")]
    [InlineData("FOREIGN TAX WITHHELD (Cash)", 0, -1.23, "MiscExp")]
    // The Action cell is a SENTENCE CONTAINING THE FUND'S NAME, so a substring test for
    // "TAX" or "FEE" can read the security instead of the verb. These are the shapes that
    // matched the tax/fee rule before it required quantity == 0: FTEXX and FZEXX are
    // ordinary core positions and FTABX an ordinary holding, so this is not an exotic
    // file. Each one posted the cash with the wrong sign and dropped the shares.
    [InlineData("PURCHASE INTO CORE ACCOUNT FIDELITY TAX-EXEMPT MONEY MARKET (FTEXX)", 500, -500.00, "Buy")]
    [InlineData("REINVESTMENT FIDELITY TAX-FREE BOND FUND (FTABX)", 1.234, -12.34, "ReinvDiv")]
    [InlineData("YOU BOUGHT FIDELITY TAX-FREE BOND FUND (FTABX)", 10, -100.00, "Buy")]
    [InlineData("TRANSFER OF ASSETS ACAT DELIVER FIDELITY TAX-FREE BOND FUND", -10, -0.01, "ShrsOut")]
    // A fund whose name merely contains "FEE" is the same trap without the tax angle.
    [InlineData("PURCHASE INTO CORE ACCOUNT COFFEE GROWERS INDEX (COFEE)", 500, -500.00, "Buy")]
    public void Each_row_becomes_the_verb_the_reference_implementation_would_choose(
        string action, double quantity, double amount, string expected) =>
        Assert.Equal(expected, FidelityActivityToQif.QifVerbFor(
            action, "Cash", (decimal)quantity, (decimal)amount));

    [Fact]
    public void A_sentence_carrying_both_words_is_a_dividend_not_a_reinvestment()
    {
        // The reference tests DIVIDEND before REINVEST, and that order IS the rule.
        Assert.Equal("Div", FidelityActivityToQif.QifVerbFor(
            "REINVESTMENT OF DIVIDEND SAMPLE FUND (SMPL) (Cash)", "Cash", 1.234m, -12.34m));
    }

    [Theory]
    // Both orders appear in the wild, and the date sits MID-sentence, so it is neither a
    // prefix nor a column.
    [InlineData("PURCHASE INTO CORE ACCOUNT as of 2025-06-30 FIDELITY GOVT (SPAXX)", "06/30/2025")]
    [InlineData("PURCHASE INTO CORE ACCOUNT as of 06/30/2025 FIDELITY GOVT (SPAXX)", "06/30/2025")]
    [InlineData("DIVIDEND RECEIVED FIDELITY GOVT (SPAXX) (Cash)", null)]
    public void An_as_of_date_is_found_wherever_it_sits(string action, string? expected) =>
        Assert.Equal(expected, FidelityActivityToQif.EffectiveDate(action));

    [Fact]
    public void The_effective_date_beats_the_run_date()
    {
        // Run Date is when Fidelity PROCESSED the row. Ten of twenty-four rows in a real
        // export carry "as of" a day earlier, and using Run Date put every one of them a
        // day after the identical transaction already imported from the same plan's QIF
        // — close enough to look unrelated, which is worse than plainly duplicated.
        var result = FidelityActivityToQif.Convert(File(
            Row("07/01/2025", "PURCHASE INTO CORE ACCOUNT as of 2025-06-30 SAMPLE (SMPL)",
                "SMPL", "SAMPLE FUND", "1.00", "500", "-500.00", "1000.00")));

        Assert.Equal("06/30/2025", Field(result.Qif, 0, 'D'));
    }

    [Fact]
    public void A_row_with_no_as_of_keeps_its_run_date()
    {
        var result = FidelityActivityToQif.Convert(File(
            Row("07/01/2025", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "12.34", "1000.00")));

        Assert.Equal("07/01/2025", Field(result.Qif, 0, 'D'));
    }

    [Fact]
    public void A_cash_row_emits_no_share_count_even_though_the_column_is_filled()
    {
        // Quantity is present and ZERO on every cash-only row in a real export, so
        // "the column has a value" says nothing about whether shares moved.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "12.34", "1012.34")));

        Assert.Null(Field(result.Qif, 0, 'Q'));
        Assert.Null(Field(result.Qif, 0, 'I'));
        Assert.Equal("Div", Field(result.Qif, 0, 'N'));
        Assert.Equal("12.34", Field(result.Qif, 0, 'T'));
    }

    [Fact]
    public void Price_and_quantity_land_in_their_own_fields_on_the_ordinary_header()
    {
        // The swapped-header test proves names are consulted; this proves the STANDARD
        // header is read correctly too. Distinct values on purpose — 10 shares at $10
        // would survive being transposed, and did: a mutation forcing both columns to
        // fixed positions passed the whole suite.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "12.50", "3", "-37.50", "900.00")));

        Assert.Equal("3", Field(result.Qif, 0, 'Q'));
        Assert.Equal("12.5", Field(result.Qif, 0, 'I'));
    }

    [Fact]
    public void A_cash_move_names_no_security()
    {
        // XIn/XOut take the bank shape, where a security has no meaning. Emitting one
        // would describe a rollover as though a fund had been involved.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "ROLLOVER CASH DIRECT ROLLOVER (Cash)", "", "No Description",
                "", "0", "500.00", "1500.00")));

        Assert.Equal("XIn", Field(result.Qif, 0, 'N'));
        Assert.Null(Field(result.Qif, 0, 'Y'));
    }

    [Fact]
    public void The_stated_amount_is_taken_as_written_even_when_the_balance_sits_still()
    {
        // The shape that disproved the reference's balance rule, from a real export:
        // Fidelity settles a dividend and its sweep into the core fund together, and
        // stamps BOTH rows with the balance after the pair. Trusting the delta rewrote
        // every amount in the file to zero.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "PURCHASE INTO CORE ACCOUNT SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "1.00", "55.75", "-55.75", "55.75"),
            Row("09/02/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "55.75", "55.75")));

        Assert.Equal("55.75", Field(result.Qif, 0, 'T'));
        Assert.Equal("55.75", Field(result.Qif, 1, 'T'));
        // ...and no complaint, because there is nothing wrong with this file.
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Rows_that_mean_nothing_are_skipped_rather_than_converted()
    {
        // Three quirks, each learned from a real file: a pending trade carries the word
        // "Processing" where a balance belongs; FDRXX's bare CUSIP rides along on cash
        // rows carrying no money; and Fidelity emits filler rows with no date.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/03/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "10", "-100.00", "Processing"),
            Row("09/02/2026", "DIVIDEND RECEIVED (Cash)", "315994103", "No Description",
                "", "0", "0", "1000.00"),
            Row("", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL", "SAMPLE FUND",
                "", "0", "5.00", "1000.00"),
            Row("09/01/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "10.00", "1000.00")));

        Assert.Single(result.SourceRows);
        Assert.Equal("09/01/2026", Field(result.Qif, 0, 'D'));
    }

    [Fact]
    public void The_ticker_survives_in_the_memo_because_QIF_names_securities_by_NAME()
    {
        // Y carries the fund's name, which is what QIF means by a security. The ticker
        // is kept where a person can still see it rather than being invented as a name —
        // the file path resolves no securities, and provider_security_mappings learns
        // the pairing from one human Accept.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "10", "-100.00", "900.00")));

        Assert.Equal("SAMPLE FUND", Field(result.Qif, 0, 'Y'));
        Assert.Contains("[SMPL]", Field(result.Qif, 0, 'M')!, StringComparison.Ordinal);
    }

    [Fact]
    public void Commission_and_fees_add_into_the_one_fee_QIF_carries()
    {
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "10", "-104.95", "895.05", commission: "4.95", fees: "0.02")));

        Assert.Equal("4.97", Field(result.Qif, 0, 'O'));
    }

    [Fact]
    public void A_caret_in_a_description_cannot_end_the_record_early()
    {
        // '^' terminates a QIF record and the format is line-oriented, so neither it nor
        // a newline may survive into a field. A merchant name is user data.
        var result = FidelityActivityToQif.Convert(File(
            Row("09/02/2026", "YOU BOUGHT ACME^CORP", "ACME", "ACME^CORP",
                "10.00", "10", "-100.00", "900.00")));

        Assert.Single(result.SourceRows);

        // Asserted against the RAW document, not through Field(): Record() splits on '^',
        // so every value it can return is caret-free whether or not Sanitise ran, and
        // `DoesNotContain('^', Field(...))` could not fail for the reason it names.
        // Delete the .Replace('^', '-') from Sanitise and these go red on the real
        // regression rather than on an incidental null.
        Assert.DoesNotContain("ACME^CORP", result.Qif);

        // The caret became a dash and the record stayed whole — a surviving one would
        // have ended it early and stranded the M line in the next record.
        Assert.Equal("ACME-CORP", Field(result.Qif, 0, 'Y'));
        Assert.Contains("ACME-CORP", Field(result.Qif, 0, 'M')!);
    }

    [Fact]
    public void An_unreadable_file_says_which_export_it_wanted()
    {
        var result = FidelityActivityToQif.Convert(
            "Date,Investment,Transaction Type,Shares/Unit,Amount ($)\n"
            + "09/02/2026,SAMPLE FUND,Contributions,1.234,100.00\n");

        Assert.Empty(result.SourceRows);
        var error = Assert.Single(result.Errors);
        Assert.Equal("fidelity_no_header", error.Code);
        Assert.Contains("Activity & Orders", error.Message, StringComparison.Ordinal);
    }
}
