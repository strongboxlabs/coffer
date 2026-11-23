using System.Globalization;
using System.Text;

using Coffer.Api.Ingest;
using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// The generic delimited-file provider (ADR-0031 Phase 5).
/// </summary>
/// <remarks>
/// The fixture is SYNTHETIC and follows a card-issuer export shape: UTF-8 with a BOM,
/// TAB-delimited despite a .csv name, no header row, a currency symbol inside the amount,
/// a fixed-width space-padded description, and amounts signed from the issuer's
/// perspective. No real merchant, amount or account data appears here — the
/// account-ending row is included because issuer exports carry that shape, with
/// invented digits.
/// </remarks>
public sealed class CsvGenericFileProviderTests
{
    private const string CardIssuerMapping = """
        version: 1
        delimiter: tab
        header_rows: 0
        date:
          column: 1
          format: MM/dd/yyyy
        payee:
          column: 3
        amount:
          shape: signed
          column: 2
          invert: true
        """;

    /// <summary>
    /// Four rows in a card-issuer export's shape: two purchases, a RETURN (negative in the
    /// file), and an account-ending line such a statement can carry.
    /// </summary>
    private static string CardIssuerFile() =>
        "﻿"
        + Row("09/02/2026", "$12.34", "SAMPLE.COM              ANYTOWN      ST")
        + Row("09/03/2026", "$7.99", "SAMPLE STORE            ANYTOWN      ST")
        + Row("09/04/2026", "-$23.50", "SAMPLE STORE            ANYTOWN      ST")
        + Row("09/05/2026", "$0.00", "BALANCE TRANSFER FOR ACCT ENDING IN 0000");

    private static string Row(string date, string amount, string description) =>
        string.Join('\t', date, amount, description, "purchase") + "\n";

    /// <summary>Date, payee and a signed amount, in that order, as plain files have them.</summary>
    private static string PlainMapping(
        string delimiter = "comma", int headerRows = 1, int dateColumn = 1, int payeeColumn = 2) => $"""
        version: 1
        delimiter: {delimiter}
        header_rows: {headerRows}
        date:
          column: {dateColumn}
          format: yyyy-MM-dd
        payee:
          column: {payeeColumn}
        amount:
          shape: signed
          column: 3
        """;

    private static Task<FileResult> ParseAsync(string yaml, string body) =>
        ParseAsync(yaml, Encoding.UTF8.GetBytes(body));

    private static async Task<FileResult> ParseAsync(string yaml, byte[] body)
    {
        var mapping = CsvMappingValidator.Validate(yaml, out var errors);
        Assert.Empty(errors);
        using var stream = new MemoryStream(body);
        return await new CsvGenericFileProvider().ParseAsync(
            stream,
            new FileIngestContext(
                LedgerId: Guid.NewGuid(),
                AccountId: Guid.NewGuid(),
                TriggeredByUserId: Guid.NewGuid(),
                CsvMapping: mapping),
            default);
    }

    [Fact]
    public async Task A_purchase_becomes_money_out_and_a_return_becomes_money_in()
    {
        // THE assertion this provider exists to get right. The file signs from the
        // ISSUER's perspective — a purchase is positive because it increases what you
        // owe — while IngestedTransaction.Amount is signed from the user's perspective
        // on the account, where a purchase is money OUT. Inverting is uniform: it is not
        // applied to purchases and skipped for returns, it is applied to the column, and
        // the return comes out positive because it was negative in the file.
        var result = await ParseAsync(CardIssuerMapping, CardIssuerFile());

        Assert.Empty(result.Errors);
        Assert.Equal(4, result.Transactions.Count);

        Assert.Equal(-12.34m, result.Transactions[0].Amount);
        Assert.Equal(-7.99m, result.Transactions[1].Amount);
        // The return. Negative in the file, so positive here — money back.
        Assert.Equal(23.50m, result.Transactions[2].Amount);
        Assert.Equal(0m, result.Transactions[3].Amount);
    }

    [Fact]
    public async Task Without_invert_the_same_file_lands_every_amount_backwards()
    {
        // Keeps the test above from passing on a provider that ignores the flag, and
        // states the consequence: a whole statement of purchases recorded as income.
        var result = await ParseAsync(
            CardIssuerMapping.Replace("invert: true", "invert: false", StringComparison.Ordinal),
            CardIssuerFile());

        Assert.Equal(12.34m, result.Transactions[0].Amount);
        Assert.Equal(-23.50m, result.Transactions[2].Amount);
    }

    [Fact]
    public async Task The_BOM_is_not_read_as_part_of_the_first_date()
    {
        // A UTF-8 BOM read as data puts three invisible bytes in front of the first
        // field — the date — so EVERY row fails to parse and the file looks corrupt
        // rather than the reader looking wrong.
        var result = await ParseAsync(CardIssuerMapping, CardIssuerFile());

        Assert.Empty(result.Errors);
        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            result.Transactions[0].PostedAt);
    }

    [Fact]
    public async Task A_currency_symbol_and_padding_do_not_reach_the_ledger()
    {
        var result = await ParseAsync(CardIssuerMapping, CardIssuerFile());

        // The description is a fixed 40-char field; the trailing padding is the
        // exporter's, not part of the merchant's name.
        var payee = result.Transactions[0].Payee!;
        Assert.Equal(payee.Trim(), payee);
        Assert.StartsWith("SAMPLE.COM", payee, StringComparison.Ordinal);

        // And the currency symbol was stripped rather than defeating the parse — an
        // exact decimal is the only proof of that worth having, since a failed parse
        // would have produced an error row instead.
        Assert.Equal(-12.34m, result.Transactions[0].Amount);
    }

    [Fact]
    public async Task A_delimited_file_surfaces_exactly_one_account_to_bind()
    {
        // Single-account-implicit, like QIF: the file carries no account header, so the
        // user binds the one block in the dialog.
        var result = await ParseAsync(CardIssuerMapping, CardIssuerFile());

        var account = Assert.Single(result.DiscoveredAccounts);
        Assert.Equal(CsvGenericFileProvider.SingleAccountKey, account.ProviderAccountId);
        Assert.Equal(4, account.TransactionCount);
    }

    [Fact]
    public async Task Every_row_gets_an_id_that_cannot_match_anything()
    {
        // No dedup, implemented rather than absent. It cannot be NULL — mig 109 requires
        // an external_id on a non-manual row — so it is deliberately non-matching, which
        // guarantees the dedup lookup never hits and a re-import lands as new rows.
        var first = await ParseAsync(CardIssuerMapping, CardIssuerFile());
        var second = await ParseAsync(CardIssuerMapping, CardIssuerFile());

        var ids = first.Transactions.Select(t => t.ExternalId).ToList();
        Assert.Equal(4, ids.Distinct().Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        // The same file parsed twice shares no id with itself.
        Assert.Empty(ids.Intersect(second.Transactions.Select(t => t.ExternalId)));
    }

    [Fact]
    public async Task One_unreadable_row_is_reported_and_the_rest_still_import()
    {
        // A single malformed line must not cost the other rows. The message names the
        // line so it can be found in the file.
        var body = CardIssuerFile() + Row("not-a-date", "$5.00", "SAMPLE STORE");

        var result = await ParseAsync(CardIssuerMapping, body);

        Assert.Equal(4, result.Transactions.Count);
        var error = Assert.Single(result.Errors);
        Assert.Equal("csv_row_unreadable", error.Code);
        Assert.Contains("Line 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_and_footer_rows_are_skipped_by_count()
    {
        var body = "Statement for period ending 09/30/2026\n"
                   + "Date\tAmount\tDescription\tType\n"
                   + Row("09/02/2026", "$12.34", "SAMPLE.COM")
                   + "TOTAL\t$12.34\t\t\n";
        var yaml = CardIssuerMapping.Replace("header_rows: 0", "header_rows: 2\nfooter_rows: 1",
            StringComparison.Ordinal);

        var result = await ParseAsync(yaml, body);

        // The preamble and the TOTAL line are not transactions, and must not be reported
        // as unreadable rows either — a warning for a line doing its job trains the
        // reader to ignore warnings.
        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Transactions);
        Assert.Equal(-12.34m, only.Amount);
    }

    [Fact]
    public async Task A_quoted_delimiter_inside_a_field_does_not_shift_the_columns()
    {
        // The reason this uses CsvHelper rather than a split(). A comma inside a quoted
        // merchant name silently moves the amount into the description for a hand-rolled
        // splitter — money corruption that parses cleanly.
        var yaml = CardIssuerMapping
            .Replace("delimiter: tab", "delimiter: comma", StringComparison.Ordinal);
        var body = "09/02/2026,$12.34,\"SAMPLE, INC\",purchase\n";

        var result = await ParseAsync(yaml, body);

        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Transactions);
        Assert.Equal(-12.34m, only.Amount);
        Assert.Equal("SAMPLE, INC", only.Payee);
    }

    [Fact]
    public async Task The_debit_credit_shape_puts_each_column_on_its_own_side()
    {
        var yaml = """
            version: 1
            delimiter: comma
            header_rows: 1
            date:
              column: 1
              format: yyyy-MM-dd
            payee:
              column: 2
            amount:
              shape: debit_credit
              debit_column: 3
              credit_column: 4
            """;
        var body = "Date,Description,Debit,Credit\n"
                   + "2026-09-02,SAMPLE STORE,12.34,\n"
                   + "2026-09-04,SAMPLE STORE,,23.50\n";

        var result = await ParseAsync(yaml, body);

        Assert.Empty(result.Errors);
        // Debit is money out; credit is money in. No invert involved — there is no sign
        // in the file to flip.
        Assert.Equal(-12.34m, result.Transactions[0].Amount);
        Assert.Equal(23.50m, result.Transactions[1].Amount);
    }

    [Fact]
    public async Task A_row_with_both_a_debit_and_a_credit_is_refused_rather_than_guessed()
    {
        var yaml = """
            version: 1
            delimiter: comma
            date:
              column: 1
              format: yyyy-MM-dd
            payee:
              column: 2
            amount:
              shape: debit_credit
              debit_column: 3
              credit_column: 4
            """;

        var result = await ParseAsync(yaml, "2026-09-02,SAMPLE STORE,12.34,23.50\n");

        // Picking one would be inventing an answer about money.
        Assert.Empty(result.Transactions);
        Assert.Contains(result.Errors, e => e.Message.Contains("only one can apply"));
    }

    // ----- how the file splits into records and fields ----------------------------
    //
    // Each of these pins a reader option CsvGenericFileProvider sets explicitly, because
    // CsvHelper's default for it would change what a mapping means.

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task Records_split_the_same_whatever_the_line_ending(string lineEnd)
    {
        var body = string.Join(lineEnd,
            "Date,Payee,Amount", "2026-09-02,SAMPLE STORE,12.34", "2026-09-03,SAMPLE.COM,-7.99") + lineEnd;

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Empty(result.Errors);
        Assert.Equal([12.34m, -7.99m], result.Transactions.Select(t => t.Amount));
    }

    [Fact]
    public async Task The_last_row_needs_no_line_break()
    {
        var result = await ParseAsync(PlainMapping(), "Date,Payee,Amount\n2026-09-02,SAMPLE STORE,12.34");

        Assert.Empty(result.Errors);
        Assert.Equal(12.34m, Assert.Single(result.Transactions).Amount);
    }

    [Fact]
    public async Task A_UTF16_file_is_decoded_by_its_BOM()
    {
        var body = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("Date,Payee,Amount\n2026-09-02,SAMPLE STORE,12.34\n"))
            .ToArray();

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Transactions);
        Assert.Equal("SAMPLE STORE", only.Payee);
        Assert.Equal(12.34m, only.Amount);
    }

    [Fact]
    public async Task A_byte_that_is_not_UTF8_does_not_cost_the_row()
    {
        // 0xE9 is a Latin-1 'é'. Read as UTF-8 it is not a character; it becomes U+FFFD
        // and the rest of the row is unaffected.
        var body = Encoding.ASCII.GetBytes("Date,Payee,Amount\n2026-09-02,CAF")
            .Append((byte)0xE9)
            .Concat(Encoding.ASCII.GetBytes(" SAMPLE,12.34\n"))
            .ToArray();

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Transactions);
        Assert.Equal("CAF� SAMPLE", only.Payee);
        Assert.Equal(12.34m, only.Amount);
    }

    [Theory]
    [InlineData("tab", '\t')]
    [InlineData("semicolon", ';')]
    [InlineData("pipe", '|')]
    public async Task The_mapping_separator_is_the_only_one(string delimiter, char separator)
    {
        // More commas than separators on every line, so a reader that DETECTED the
        // separator would split on the commas.
        var body = string.Join(separator, "2026-09-02", "SAMPLE, STORE, INC, LLC", "12.34") + "\n";

        var result = await ParseAsync(PlainMapping(delimiter, headerRows: 0), body);

        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Transactions);
        Assert.Equal("SAMPLE, STORE, INC, LLC", only.Payee);
        Assert.Equal(12.34m, only.Amount);
    }

    [Fact]
    public async Task An_Excel_sep_line_is_a_header_row_not_an_instruction()
    {
        // `sep=;` is how Excel names a separator. The mapping names it here, so the line
        // is the first of the two header rows the mapping says to skip.
        var body = "sep=;\nDate,Payee,Amount\n2026-09-02,SAMPLE STORE,12.34\n";

        var result = await ParseAsync(PlainMapping(headerRows: 2), body);

        Assert.Empty(result.Errors);
        Assert.Equal("SAMPLE STORE", Assert.Single(result.Transactions).Payee);
    }

    [Fact]
    public async Task A_blank_line_in_the_preamble_counts_as_a_header_row()
    {
        // The author counts the lines above the data, the blank one included.
        var body = "Statement for period ending 09/30/2026\n\nDate,Payee,Amount\n"
                   + "2026-09-02,SAMPLE STORE,12.34\n";

        var result = await ParseAsync(PlainMapping(headerRows: 3), body);

        Assert.Empty(result.Errors);
        Assert.Equal(12.34m, Assert.Single(result.Transactions).Amount);
    }

    [Fact]
    public async Task An_error_names_the_line_the_row_starts_on()
    {
        // Line 1 is the header, the quoted payee spans lines 2 and 3, line 4 is blank,
        // and the bad row is on line 5: the line the author finds it on in an editor.
        var body = "Date,Payee,Amount\n"
                   + "2026-09-02,\"SAMPLE STORE\nANYTOWN\",12.34\n"
                   + "\n"
                   + "not-a-date,SAMPLE.COM,7.99\n";

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Equal("SAMPLE STORE\nANYTOWN", Assert.Single(result.Transactions).Payee);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Line 5:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_quotes_inside_a_quoted_field_are_one()
    {
        var result = await ParseAsync(PlainMapping(),
            "Date,Payee,Amount\n2026-09-02,\"SAMPLE \"\"STORE\"\"\",12.34\n");

        Assert.Empty(result.Errors);
        Assert.Equal("SAMPLE \"STORE\"", Assert.Single(result.Transactions).Payee);
    }

    [Fact]
    public async Task A_misplaced_quote_is_kept_as_text_and_the_rows_after_it_still_import()
    {
        // A quote inside an unquoted field is text; text after a closing quote joins the
        // value. Neither costs the row or any row after it.
        var body = "Date,Payee,Amount\n"
                   + "2026-09-02,SAMPLE\"STORE,12.34\n"
                   + "2026-09-03,\"SAMPLE\"STORE,7.99\n"
                   + "2026-09-04,SAMPLE.COM,1.00\n";

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Empty(result.Errors);
        Assert.Equal(["SAMPLE\"STORE", "SAMPLESTORE", "SAMPLE.COM"],
            result.Transactions.Select(t => t.Payee));
    }

    [Fact]
    public async Task A_quote_that_never_closes_runs_to_the_end_and_is_reported()
    {
        // The open quote takes the rest of the file into one field, so the row has no
        // amount. It is reported on the line it starts on rather than read as anything.
        var body = "Date,Payee,Amount\n"
                   + "2026-09-02,\"SAMPLE STORE,12.34\n"
                   + "2026-09-03,SAMPLE.COM,7.99\n";

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Empty(result.Transactions);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Line 2:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_of_different_lengths_are_each_read_on_their_own()
    {
        // A short row is reported; a long one is read for the columns the mapping names.
        // Neither stops the file.
        var body = "Date,Payee,Amount\n"
                   + "2026-09-02,SAMPLE STORE\n"
                   + "2026-09-03,SAMPLE.COM,7.99,extra,more\n";

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Equal(7.99m, Assert.Single(result.Transactions).Amount);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Line 2:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quote_after_leading_whitespace_is_part_of_the_value()
    {
        // RFC 4180: whitespace belongs to the field, so a field that starts with a space
        // is unquoted and its quotes are text.
        var result = await ParseAsync(PlainMapping(),
            "Date,Payee,Amount\n2026-09-02, \"SAMPLE STORE\" ,12.34\n");

        Assert.Empty(result.Errors);
        Assert.Equal("\"SAMPLE STORE\"", Assert.Single(result.Transactions).Payee);
    }

    [Fact]
    public async Task A_hash_and_a_backslash_are_data()
    {
        // No comment lines and no escape character: a row that starts with '#' is a row,
        // and a backslash is a character of the value.
        var result = await ParseAsync(PlainMapping(headerRows: 0, dateColumn: 2, payeeColumn: 1),
            "#1001 SAMPLE\\STORE,2026-09-02,12.34\n");

        Assert.Empty(result.Errors);
        Assert.Equal("#1001 SAMPLE\\STORE", Assert.Single(result.Transactions).Payee);
    }

    [Theory]
    [InlineData("$1,234.56", "point", "1234.56")]
    [InlineData("(45.00)", "point", "-45.00")]
    [InlineData("-$23.50", "point", "-23.50")]
    [InlineData("  12.00  ", "point", "12.00")]
    [InlineData("1 234.56", "point", "1234.56")]
    [InlineData("€1.234,56", "comma", "1234.56")]
    [InlineData("(45,00)", "comma", "-45.00")]
    [InlineData("-23,50 €", "comma", "-23.50")]
    [InlineData("  12,00  ", "comma", "12.00")]
    public void Money_survives_the_decoration_real_exports_put_around_it(
        string raw, string separator, string expected)
    {
        Assert.Equal(CsvGenericFileProvider.MoneyCell.Amount,
            CsvGenericFileProvider.ReadMoney(raw, SeparatorNamed(separator), out var value));
        Assert.Equal(expected, value.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("n/a")]
    [InlineData("-")]
    [InlineData("$")]
    public void A_cell_with_no_digits_is_empty_rather_than_read_as_zero(string raw)
    {
        // Zero would be a silent, plausible, wrong amount. Empty is what a debit/credit
        // layout's unused column holds, and a signed layout refuses it.
        foreach (var separator in Enum.GetValues<CsvDecimalSeparator>())
        {
            Assert.Equal(CsvGenericFileProvider.MoneyCell.Empty,
                CsvGenericFileProvider.ReadMoney(raw, separator, out _));
        }
    }

    // ----- the decimal separator (ADR-0114 D15) -----------------------------------
    //
    // Cells are synthetic and described by shape: "a comma followed by two digits",
    // "a point grouped in threes".

    private static CsvDecimalSeparator SeparatorNamed(string name) =>
        name == "comma" ? CsvDecimalSeparator.Comma : CsvDecimalSeparator.Point;

    [Theory]
    // A comma grouped in threes is a thousands separator.
    [InlineData("1,234", "1234")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("12,345,678.90", "12345678.90")]
    // Lakh grouping: a first group of one or two digits, groups of two, then a last
    // group of three. A version-1 layout always read these (ADR-0114 D15).
    [InlineData("1,23,456.78", "123456.78")]
    [InlineData("12,34,56,789.00", "123456789.00")]
    [InlineData("1,23,456", "123456")]
    // The figure keeps the scale it was written with, trailing zero included.
    [InlineData("7.50", "7.50")]
    [InlineData("-0.125", "-0.125")]
    [InlineData("12.", "12")]
    [InlineData("23.50-", "-23.50")]
    public void With_a_point_a_comma_only_groups_thousands(string raw, string expected)
    {
        Assert.Equal(CsvGenericFileProvider.MoneyCell.Amount,
            CsvGenericFileProvider.ReadMoney(raw, CsvDecimalSeparator.Point, out var value));
        Assert.Equal(expected, value.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    // A comma followed by digits is the decimal.
    [InlineData("12,34", "12.34")]
    [InlineData("1,5", "1.5")]
    [InlineData("1,234", "1.234")]
    [InlineData("7,50", "7.50")]
    // A point grouped in threes is a thousands separator.
    [InlineData("1.234", "1234")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("12.345.678,90", "12345678.90")]
    // Lakh grouping, mirrored: points as the groups, a comma decimal.
    [InlineData("1.23.456,78", "123456.78")]
    // Spaces and a no-break space as grouping are decoration, stripped.
    [InlineData("1 234,56", "1234.56")]
    [InlineData("1 234,56", "1234.56")]
    public void With_a_comma_the_comma_is_the_decimal_and_a_point_groups_thousands(
        string raw, string expected)
    {
        Assert.Equal(CsvGenericFileProvider.MoneyCell.Amount,
            CsvGenericFileProvider.ReadMoney(raw, CsvDecimalSeparator.Comma, out var value));
        Assert.Equal(expected, value.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    // A comma followed by two digits: the cell this setting exists for.
    [InlineData("12,34", "point")]
    [InlineData("1,234,56", "point")]
    [InlineData("1,23.45", "point")]
    [InlineData(",50", "point")]
    [InlineData("0,123", "point")]
    [InlineData("1.234.56", "point")]
    [InlineData("1.5,0", "point")]
    // Grouping that is neither in threes nor lakh: a last group of two, a first lakh
    // group of three, a first group starting with a zero.
    [InlineData("1,23,45", "point")]
    [InlineData("123,45,678", "point")]
    [InlineData("01,23,456", "point")]
    [InlineData("1.23.45,67", "comma")]
    // A point followed by two digits.
    [InlineData("12.34", "comma")]
    [InlineData("1.23,45", "comma")]
    // Two decimal separators.
    [InlineData("1,2,3", "comma")]
    [InlineData("1,234.56", "comma")]
    // A sign inside the figure.
    [InlineData("12-34", "point")]
    [InlineData("--5", "comma")]
    public void A_cell_that_does_not_read_one_way_only_is_unreadable(string raw, string separator)
    {
        Assert.Equal(CsvGenericFileProvider.MoneyCell.Unreadable,
            CsvGenericFileProvider.ReadMoney(raw, SeparatorNamed(separator), out _));
    }

    /// <summary>
    /// A version-2 layout naming its separator: date, payee, then a signed amount, or a
    /// debit and a credit column.
    /// </summary>
    private static string SeparatorMapping(string separator, bool debitCredit = false) => $"""
        version: 2
        delimiter: semicolon
        header_rows: 1
        date:
          column: 1
          format: dd.MM.yyyy
        payee:
          column: 2
        amount:
        {(debitCredit
            ? "  shape: debit_credit\n  debit_column: 3\n  credit_column: 4"
            : "  shape: signed\n  column: 3")}
          decimal_separator: {separator}
        """;

    [Fact]
    public async Task A_point_layout_refuses_a_comma_followed_by_two_digits_and_names_the_line()
    {
        // Invariant parsing read this cell as grouping and imported a far larger figure.
        var body = "Date,Payee,Amount\n"
                   + "2026-09-02,SAMPLE STORE,12.34\n"
                   + "2026-09-03,SAMPLE.COM,\"12,34\"\n"
                   + "2026-09-04,SAMPLE.COM,\"1,234.50\"\n";

        var result = await ParseAsync(PlainMapping(), body);

        Assert.Equal(["12.34", "1234.50"],
            result.Transactions.Select(t => t.Amount.ToString(CultureInfo.InvariantCulture)));
        var error = Assert.Single(result.Errors);
        Assert.Equal("csv_row_unreadable", error.Code);
        Assert.Equal(
            "Line 3: '12,34' is not an amount with a point as the decimal separator. The row was skipped.",
            error.Message);
    }

    [Fact]
    public async Task A_comma_layout_reads_the_comma_as_the_decimal_and_refuses_a_point_followed_by_two_digits()
    {
        var body = "Date;Payee;Amount\n"
                   + "02.09.2026;SAMPLE STORE;-12,34\n"
                   + "03.09.2026;SAMPLE.COM;1.234,50 €\n"
                   + "04.09.2026;SAMPLE.COM;12.34\n"
                   + "05.09.2026;SAMPLE.COM;1,2,3\n";

        var result = await ParseAsync(SeparatorMapping("comma"), body);

        Assert.Equal(["-12.34", "1234.50"],
            result.Transactions.Select(t => t.Amount.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(
            [
                "Line 4: '12.34' is not an amount with a comma as the decimal separator. The row was skipped.",
                "Line 5: '1,2,3' is not an amount with a comma as the decimal separator. The row was skipped.",
            ],
            result.Errors.Select(e => e.Message));
        Assert.All(result.Errors, e => Assert.Equal("csv_row_unreadable", e.Code));
    }

    [Fact]
    public async Task A_point_layout_written_as_version_2_reads_as_version_1_does()
    {
        var body = "Date;Payee;Amount\n02.09.2026;SAMPLE STORE;\"1,234.50\"\n03.09.2026;SAMPLE.COM;12,34\n";

        var result = await ParseAsync(SeparatorMapping("point"), body);

        Assert.Equal("1234.50", Assert.Single(result.Transactions).Amount.ToString(CultureInfo.InvariantCulture));
        Assert.StartsWith("Line 3: '12,34' is not an amount with a point",
            Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("point")]
    [InlineData("comma")]
    public async Task A_signed_cell_with_no_digits_is_not_an_amount_without_naming_the_separator(
        string separator)
    {
        // No separator reads a cell with no digits, so naming one would point at a
        // setting that cannot fix the row (ADR-0114 D15).
        var body = "Date;Payee;Amount\n"
                   + "02.09.2026;SAMPLE STORE;\n"
                   + "03.09.2026;SAMPLE.COM;n/a\n"
                   + "04.09.2026;SAMPLE.COM\n";

        var result = await ParseAsync(SeparatorMapping(separator), body);

        Assert.Empty(result.Transactions);
        Assert.Equal(
            [
                "Line 2: '' is not an amount. The row was skipped.",
                "Line 3: 'n/a' is not an amount. The row was skipped.",
                "Line 4: '' is not an amount. The row was skipped.",
            ],
            result.Errors.Select(e => e.Message));
        Assert.All(result.Errors, e => Assert.Equal("csv_row_unreadable", e.Code));
    }

    [Fact]
    public async Task A_debit_or_credit_cell_with_digits_that_do_not_read_is_a_row_error_not_an_empty_side()
    {
        // Read as absent, the unreadable debit would leave the row reported as having
        // no amount at all, or landed on the other column's value; neither names it.
        var body = "Date;Payee;Debit;Credit\n"
                   + "02.09.2026;SAMPLE STORE;12,34;\n"
                   + "03.09.2026;SAMPLE.COM;-;7.50\n"
                   + "04.09.2026;SAMPLE.COM;;\n";

        var result = await ParseAsync(SeparatorMapping("point", debitCredit: true), body);

        // A lone dash pads the unused column, and stays empty.
        Assert.Equal("7.50", Assert.Single(result.Transactions).Amount.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(
            [
                "Line 2: '12,34' is not an amount with a point as the decimal separator. The row was skipped.",
                "Line 4: neither the debit nor the credit column has an amount. The row was skipped.",
            ],
            result.Errors.Select(e => e.Message));
    }

    [Fact]
    public async Task A_comma_layout_reads_a_debit_and_a_credit_with_their_decimals()
    {
        var body = "Date;Payee;Debit;Credit\n"
                   + "02.09.2026;SAMPLE STORE;12,34;\n"
                   + "03.09.2026;SAMPLE.COM;;1.234,50\n";

        var result = await ParseAsync(SeparatorMapping("comma", debitCredit: true), body);

        Assert.Empty(result.Errors);
        Assert.Equal(["-12.34", "1234.50"],
            result.Transactions.Select(t => t.Amount.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task The_account_to_bind_counts_only_the_rows_that_read()
    {
        var body = "Date;Payee;Amount\n"
                   + "02.09.2026;SAMPLE STORE;12,34\n"
                   + "03.09.2026;SAMPLE.COM;12.34\n"
                   + "04.09.2026;SAMPLE.COM;7,50\n";

        var result = await ParseAsync(SeparatorMapping("comma"), body);

        Assert.Equal(2, Assert.Single(result.DiscoveredAccounts).TransactionCount);
        Assert.Single(result.Errors);
    }
}
