using System.Globalization;
using System.Text;

using Coffer.Api.Ingest;
using Coffer.Api.Ingest.Csv;
using Coffer.Api.Ingest.Qif;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// How the QIF provider reads its numeric fields (ADR-0114 D15; ADR-0042): a number that
/// is written but does not read is an error on its row naming the line, never a silent
/// zero; a comma reads only where it can be nothing but grouping, in thousands or
/// lakh-style, by the rule a CSV layout reads by; in money
/// whose currency has three places no comma reads; and every text still read reads to
/// the same figure as before, scale included, so the external id hashed from it is
/// unchanged.
/// </summary>
public sealed class QifFileProviderTests
{
    private static readonly Guid AccountId = Guid.Parse("0b4d7a1e-5c2f-4e8a-9a51-3d2e6f7c8b90");

    private static async Task<FileResult> ParseAsync(string qif, string? currencyCode = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(qif));
        return await new QifFileProvider().ParseAsync(
            stream,
            new FileIngestContext(
                LedgerId: Guid.NewGuid(),
                AccountId: AccountId,
                TriggeredByUserId: Guid.NewGuid(),
                CurrencyCode: currencyCode),
            default);
    }

    /// <summary>A bank file of a readable row on lines 2-5 and a row on lines 6-9 whose
    /// amount, on line 7, is <paramref name="amount"/>.</summary>
    private static string BankFile(string amount) =>
        "!Type:Bank\n"
        + "D01/05/2026\nT-12.00\nPSAMPLE STORE\n^\n"
        + $"D01/06/2026\nT{amount}\nPSAMPLE PAYER\n^\n";

    /// <summary>The parser this one replaced, kept as the reference for every text it
    /// read one way only.</summary>
    private static decimal? ByReference(string value) =>
        decimal.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    // ----- a number that does not read -----

    [Theory]
    [InlineData("abc")]
    [InlineData("12.34.56")]
    [InlineData("1 234.00")]
    [InlineData("(12.00)")]
    public async Task A_bank_amount_that_does_not_parse_is_an_error_naming_its_line(string amount)
    {
        var result = await ParseAsync(BankFile(amount), "USD");

        var row = Assert.Single(result.Transactions);
        Assert.Equal("SAMPLE STORE", row.Payee);
        var error = Assert.Single(result.Errors);
        Assert.Equal("qif_row_unreadable", error.Code);
        Assert.Equal($"Line 7: '{amount}' does not read as an amount. The row was skipped.", error.Message);
    }

    [Theory]
    [InlineData("Buy", "YSAMPLE FUND(SMPL)\nQ5\nI10.00\n")]
    [InlineData("XIn", "")]
    public async Task An_investment_amount_that_does_not_parse_is_an_error_naming_its_line(
        string action, string otherFields)
    {
        var qif = "!Type:Invst\n"
            + $"D01/05/2026\nN{action}\n{otherFields}T5O0.00\nMSample memo\n^\n";
        var tLine = 1 + Array.FindIndex(qif.Split('\n'), l => l.StartsWith('T'));

        var result = await ParseAsync(qif, "USD");

        Assert.Empty(result.Transactions);
        var error = Assert.Single(result.Errors);
        Assert.Equal("qif_row_unreadable", error.Code);
        Assert.Equal($"Line {tLine}: '5O0.00' does not read as an amount. The row was skipped.", error.Message);
        Assert.Equal(0, result.DiscoveredAccounts.Single().TransactionCount);
    }

    [Theory]
    [InlineData('O', "a commission")]
    [InlineData('Q', "a quantity")]
    [InlineData('I', "a price")]
    public async Task A_security_rows_other_number_that_does_not_parse_is_an_error_naming_its_line(
        char field, string noun)
    {
        var fields = new Dictionary<char, string> { ['Q'] = "5", ['I'] = "10.00", ['O'] = "1.00" };
        fields[field] = "n/a";
        var qif = "!Type:Invst\nD01/05/2026\nNBuy\nYSAMPLE FUND(SMPL)\n"
            + string.Concat(fields.Select(f => $"{f.Key}{f.Value}\n"))
            + "T51.00\n^\n";
        var line = 1 + Array.FindIndex(qif.Split('\n'), l => l == $"{field}n/a");

        var result = await ParseAsync(qif, "USD");

        Assert.Empty(result.Transactions);
        var error = Assert.Single(result.Errors);
        Assert.Equal($"Line {line}: 'n/a' does not read as {noun}. The row was skipped.", error.Message);
    }

    [Fact]
    public async Task A_record_with_no_amount_field_keeps_a_zero_amount()
    {
        // Absent is not unreadable: a record that carries neither T nor U has always
        // landed at zero, and still does.
        var result = await ParseAsync(
            "!Type:Bank\nD01/05/2026\nPSAMPLE STORE\n^\n"
            + "!Type:Invst\nD01/06/2026\nNXIn\nMSample memo\n^\n", "USD");

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Transactions.Count);
        Assert.All(result.Transactions, t => Assert.Equal(0m, t.Amount));
    }

    [Fact]
    public async Task An_unreadable_U_behind_a_readable_T_reads_from_T()
    {
        var result = await ParseAsync(
            "!Type:Bank\nD01/05/2026\nU-12,34\nT-12.34\nPSAMPLE STORE\n^\n"
            + "D01/06/2026\nT-5.00\nUgarbage\nPSAMPLE PAYER\n^\n", "USD");

        Assert.Empty(result.Errors);
        Assert.Equal([-12.34m, -5.00m], result.Transactions.Select(t => t.Amount));
    }

    [Fact]
    public async Task A_U_is_read_when_the_record_has_no_T()
    {
        var result = await ParseAsync(
            "!Type:Bank\nD01/05/2026\nU-12.34\nPSAMPLE STORE\n^\n"
            + "D01/06/2026\nUgarbage\nPSAMPLE PAYER\n^\n", "USD");

        Assert.Equal(-12.34m, Assert.Single(result.Transactions).Amount);
        Assert.Equal("Line 7: 'garbage' does not read as an amount. The row was skipped.",
            Assert.Single(result.Errors).Message);
    }

    // ----- the comma rule -----

    [Theory]
    [InlineData("12,34")]
    [InlineData("-12,3")]
    [InlineData("1,2345")]
    [InlineData("1,234,5")]
    [InlineData("1,234,56.78")]
    [InlineData(",123")]
    [InlineData("-,123")]
    [InlineData("1,,234")]
    [InlineData("1.234,567")]
    [InlineData("12.34,")]
    // Malformed lakh grouping: a last group of two, a first group starting with a zero,
    // a first group of three before groups of two.
    [InlineData("1,23,45")]
    [InlineData("01,23,456")]
    [InlineData("123,45,678")]
    public async Task A_comma_not_followed_by_exactly_three_digits_of_the_whole_part_is_an_error(string amount)
    {
        foreach (var currency in new string?[] { null, "USD", "JPY" })
        {
            var result = await ParseAsync(BankFile(amount), currency);

            Assert.Equal("SAMPLE STORE", Assert.Single(result.Transactions).Payee);
            var error = Assert.Single(result.Errors);
            Assert.Equal("qif_row_unreadable", error.Code);
            Assert.Equal(
                $"Line 7: '{amount}' does not read as an amount one way only: a comma not followed "
                + "by exactly three digits could be a decimal comma. The row was skipped.",
                error.Message);
        }
    }

    [Theory]
    [InlineData("1,234", "1234")]
    [InlineData("-1,234.56", "-1234.56")]
    [InlineData("1,234,567.891", "1234567.891")]
    [InlineData("12,345.60-", "-12345.60")]
    public async Task Comma_groups_of_three_read_as_thousands_in_an_account_of_fewer_than_three_places(
        string amount, string expected)
    {
        foreach (var currency in new string?[] { null, "USD", "JPY" })
        {
            var result = await ParseAsync(BankFile(amount), currency);

            Assert.Empty(result.Errors);
            Assert.Equal(expected,
                result.Transactions[1].Amount.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Commas that each group three digits but in groups no export writes, a first group
    /// longer than three digits or starting with a zero, are refused as a CSV layout
    /// refuses them in an account of fewer than three places, since the comma could as
    /// well be a decimal comma. A three-place account gives them its own reason.
    /// </summary>
    [Theory]
    [InlineData("1234,567")]
    [InlineData("0,123")]
    [InlineData("-01,234.50")]
    public async Task Commas_grouped_from_a_first_group_no_export_writes_are_an_error(string amount)
    {
        foreach (var currency in new string?[] { null, "USD", "JPY" })
        {
            var result = await ParseAsync(BankFile(amount), currency);

            Assert.Equal("SAMPLE STORE", Assert.Single(result.Transactions).Payee);
            var error = Assert.Single(result.Errors);
            Assert.Equal("qif_row_unreadable", error.Code);
            Assert.Equal($"Line 7: '{amount}' does not read as an amount. The row was skipped.", error.Message);
        }
    }

    [Theory]
    [InlineData("1,23,456.78", "123456.78")]
    [InlineData("-12,34,567", "-1234567")]
    [InlineData("12,34,56,789.00", "123456789.00")]
    [InlineData("1,23,456.78-", "-123456.78")]
    public async Task Lakh_grouping_reads_whole_in_an_account_of_fewer_than_three_places(
        string amount, string expected)
    {
        foreach (var currency in new string?[] { null, "USD", "JPY" })
        {
            var result = await ParseAsync(BankFile(amount), currency);

            Assert.Empty(result.Errors);
            Assert.Equal(expected,
                result.Transactions[1].Amount.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// QIF and a point-separated CSV layout read grouping by one rule: a text one reads,
    /// the other reads to the same figure, and a text one refuses, the other refuses.
    /// </summary>
    [Theory]
    [InlineData("1,234")]
    [InlineData("12,345,678.90")]
    [InlineData("1,23,456.78")]
    [InlineData("12,34,56,789.00")]
    [InlineData("-1,23,456")]
    [InlineData("1,23,45")]
    [InlineData("01,23,456")]
    [InlineData("123,45,678")]
    [InlineData("1234,567")]
    [InlineData("0,123")]
    [InlineData("12,34")]
    [InlineData("1,234,56")]
    [InlineData("1,,234")]
    public void Qif_and_a_point_layout_read_grouping_alike(string text)
    {
        var qif = QifFileProvider.ReadNumber(text, threePlaceMoney: false, out var qifValue);
        var csv = CsvGenericFileProvider.ReadMoney(text, CsvDecimalSeparator.Point, out var csvValue);

        Assert.Equal(csv == CsvGenericFileProvider.MoneyCell.Amount,
            qif == QifFileProvider.NumberReading.Read);
        if (qif == QifFileProvider.NumberReading.Read)
            Assert.Equal(csvValue.ToString(CultureInfo.InvariantCulture), qifValue.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1,234")]
    [InlineData("-1,234.567")]
    [InlineData("1,234,567")]
    [InlineData("1,23,456.78")]
    [InlineData("1,23,456.789")]
    [InlineData("0,123")]
    [InlineData("1234,567")]
    [InlineData("-01,234.50")]
    public async Task A_comma_in_money_is_an_error_when_the_account_currency_has_three_places(string amount)
    {
        var result = await ParseAsync(BankFile(amount), "KWD");

        Assert.Equal("SAMPLE STORE", Assert.Single(result.Transactions).Payee);
        Assert.Equal(
            $"Line 7: '{amount}' does not read as an amount one way only: in a KWD account, whose "
            + "currency has three decimal places, a comma followed by three digits could be a "
            + "decimal comma or a thousands separator. The row was skipped.",
            Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task In_a_three_place_account_a_commission_is_money_and_a_quantity_and_price_are_not()
    {
        const string grouped = "!Type:Invst\nD01/05/2026\nNBuy\nYSAMPLE FUND(SMPL)\n"
            + "Q1,500\nI2,000.5\nT3,000,750.000\n^\n";
        const string commission = "!Type:Invst\nD01/05/2026\nNBuy\nYSAMPLE FUND(SMPL)\n"
            + "Q5\nI10.000\nO1,000\nT1050.000\n^\n";

        var trade = await ParseAsync(grouped.Replace("T3,000,750.000", "T3000750.000", StringComparison.Ordinal), "KWD");
        Assert.Empty(trade.Errors);
        var row = Assert.Single(trade.Transactions);
        Assert.Equal(1500m, row.Shares);
        Assert.Equal(2000.5m, row.UnitPrice);

        var amount = await ParseAsync(grouped, "KWD");
        Assert.Empty(amount.Transactions);
        Assert.StartsWith("Line 7: '3,000,750.000' does not read as an amount one way only: in a KWD account",
            Assert.Single(amount.Errors).Message, StringComparison.Ordinal);

        var fee = await ParseAsync(commission, "KWD");
        Assert.Empty(fee.Transactions);
        Assert.StartsWith("Line 7: '1,000' does not read as a commission one way only: in a KWD account",
            Assert.Single(fee.Errors).Message, StringComparison.Ordinal);

        // The same commission reads as thousands in a two-place account.
        Assert.Equal(1000m, Assert.Single((await ParseAsync(commission, "USD")).Transactions).Fee);
    }

    [Fact]
    public async Task A_quantity_with_an_ambiguous_comma_is_an_error_in_any_account()
    {
        var result = await ParseAsync(
            "!Type:Invst\nD01/05/2026\nNBuy\nYSAMPLE FUND(SMPL)\nQ1,5\nI10.00\nT15.00\n^\n", "USD");

        Assert.Empty(result.Transactions);
        Assert.Equal(
            "Line 5: '1,5' does not read as a quantity one way only: a comma not followed by "
            + "exactly three digits could be a decimal comma. The row was skipped.",
            Assert.Single(result.Errors).Message);
    }

    // ----- every text read before reads the same -----

    /// <summary>
    /// Every amount the earlier parser read one way only reads to the same figure, its
    /// scale and its sign at either end included, and so hashes to the same external id: a
    /// different reading would give a re-imported row a new id and duplicate it.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("12")]
    [InlineData("12.3")]
    [InlineData("12.30")]
    [InlineData("12.300")]
    [InlineData("-12.30")]
    [InlineData("+12.30")]
    [InlineData("12.30-")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("0.000001")]
    [InlineData("1,234")]
    [InlineData("1,234.5")]
    [InlineData("-1,234.00")]
    [InlineData("1,234.00-")]
    [InlineData("1,234,567.891")]
    // Lakh grouping, which the parser before this one read by dropping every comma: a
    // re-imported lakh file keeps its rows' ids and is not duplicated.
    [InlineData("1,23,456.78")]
    [InlineData("-12,34,567")]
    [InlineData("1,23,456.70-")]
    public async Task A_text_the_earlier_parser_read_one_way_reads_the_same_and_hashes_the_same(string amount)
    {
        var expected = ByReference(amount)!.Value;

        foreach (var currency in new string?[] { null, "USD", "JPY" })
        {
            var row = (await ParseAsync(BankFile(amount), currency)).Transactions[1];

            Assert.Equal(expected.ToString(CultureInfo.InvariantCulture),
                row.Amount.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(
                QifFileProvider.SynthesizeExternalId(
                    AccountId, new DateTime(2026, 1, 6), "", "SAMPLE PAYER", null, null, expected, null),
                row.ExternalId);
        }
    }

    [Theory]
    [InlineData("  1,234.50  ")]
    [InlineData(" -12.30")]
    [InlineData("12.30- ")]
    public void Surrounding_spaces_read_as_the_earlier_parser_read_them(string text)
    {
        Assert.Equal(QifFileProvider.NumberReading.Read,
            QifFileProvider.ReadNumber(text, threePlaceMoney: false, out var value));
        Assert.Equal(ByReference(text)!.Value.ToString(CultureInfo.InvariantCulture),
            value.ToString(CultureInfo.InvariantCulture));
    }

    // ----- the external id hashes the amount as parsed -----

    [Fact]
    public async Task The_external_id_hashes_the_amount_as_parsed_and_the_provider_never_rounds()
    {
        // A figure finer than a whole-unit currency's places: the orchestrator rounds it at
        // the write, so the provider hands it on, and hashes it, as written.
        var result = await ParseAsync(
            "!Type:Bank\nD01/05/2026\nT-1234.5\nPSAMPLE STORE\n^\n"
            + "!Type:Invst\nD01/06/2026\nNWithdrwX\nT250.5\nMSample memo\n^\n", "JPY");

        Assert.Empty(result.Errors);
        var bank = result.Transactions[0];
        Assert.Equal("-1234.5", bank.Amount.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(
            QifFileProvider.SynthesizeExternalId(
                AccountId, new DateTime(2026, 1, 5), "", "SAMPLE STORE", null, null, -1234.5m, null),
            bank.ExternalId);
        Assert.NotEqual(
            QifFileProvider.SynthesizeExternalId(
                AccountId, new DateTime(2026, 1, 5), "", "SAMPLE STORE", null, null, -1235m, null),
            bank.ExternalId);

        var cash = result.Transactions[1];
        Assert.Equal("-250.5", cash.Amount.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(
            QifFileProvider.SynthesizeExternalId(
                AccountId, new DateTime(2026, 1, 6), "WithdrwX", null, null, null, 250.5m, "Sample memo"),
            cash.ExternalId);
    }
}
