using System.Text;

using Coffer.Api.Ingest;
using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// The generic delimited-file provider (ADR-0031 Phase 5).
/// </summary>
/// <remarks>
/// The fixture is SYNTHETIC but mirrors a real department-store card export byte for
/// byte in shape: UTF-8 with a BOM, TAB-delimited despite a .csv name, no header row, a
/// currency symbol inside the amount, a fixed-width space-padded description, and
/// amounts signed from the issuer's perspective. No real merchant, amount or account
/// data appears here — the account-ending row is included because that shape exists in
/// the wild, with invented digits.
/// </remarks>
public sealed class CsvGenericFileProviderTests
{
    private const string StoreCardMapping = """
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
    /// Four rows in the real shape: two purchases, a RETURN (negative in the file), and
    /// the account-ending line that a real statement carries.
    /// </summary>
    private static string StoreCardFile() =>
        "﻿"
        + Row("09/02/2026", "$41.18", "SAMPLE.COM              ANYTOWN      ST")
        + Row("09/03/2026", "$7.99", "SAMPLE STORE            ANYTOWN      ST")
        + Row("09/04/2026", "-$23.50", "SAMPLE STORE            ANYTOWN      ST")
        + Row("09/05/2026", "$0.00", "BALANCE TRANSFER FOR ACCT ENDING IN 0000");

    private static string Row(string date, string amount, string description) =>
        string.Join('\t', date, amount, description, "purchase") + "\n";

    private static async Task<FileResult> ParseAsync(string yaml, string body)
    {
        var mapping = CsvMappingValidator.Validate(yaml, out var errors);
        Assert.Empty(errors);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
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
        var result = await ParseAsync(StoreCardMapping, StoreCardFile());

        Assert.Empty(result.Errors);
        Assert.Equal(4, result.Transactions.Count);

        Assert.Equal(-41.18m, result.Transactions[0].Amount);
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
            StoreCardMapping.Replace("invert: true", "invert: false", StringComparison.Ordinal),
            StoreCardFile());

        Assert.Equal(41.18m, result.Transactions[0].Amount);
        Assert.Equal(-23.50m, result.Transactions[2].Amount);
    }

    [Fact]
    public async Task The_BOM_is_not_read_as_part_of_the_first_date()
    {
        // A UTF-8 BOM read as data puts three invisible bytes in front of the first
        // field — the date — so EVERY row fails to parse and the file looks corrupt
        // rather than the reader looking wrong.
        var result = await ParseAsync(StoreCardMapping, StoreCardFile());

        Assert.Empty(result.Errors);
        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            result.Transactions[0].PostedAt);
    }

    [Fact]
    public async Task A_currency_symbol_and_padding_do_not_reach_the_ledger()
    {
        var result = await ParseAsync(StoreCardMapping, StoreCardFile());

        // The description is a fixed 40-char field; the trailing padding is the
        // exporter's, not part of the merchant's name.
        var payee = result.Transactions[0].Payee!;
        Assert.Equal(payee.Trim(), payee);
        Assert.StartsWith("SAMPLE.COM", payee, StringComparison.Ordinal);

        // And the currency symbol was stripped rather than defeating the parse — an
        // exact decimal is the only proof of that worth having, since a failed parse
        // would have produced an error row instead.
        Assert.Equal(-41.18m, result.Transactions[0].Amount);
    }

    [Fact]
    public async Task A_delimited_file_surfaces_exactly_one_account_to_bind()
    {
        // Single-account-implicit, like QIF: the file carries no account header, so the
        // user binds the one block in the dialog.
        var result = await ParseAsync(StoreCardMapping, StoreCardFile());

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
        var first = await ParseAsync(StoreCardMapping, StoreCardFile());
        var second = await ParseAsync(StoreCardMapping, StoreCardFile());

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
        var body = StoreCardFile() + Row("not-a-date", "$5.00", "SAMPLE STORE");

        var result = await ParseAsync(StoreCardMapping, body);

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
                   + Row("09/02/2026", "$41.18", "SAMPLE.COM")
                   + "TOTAL\t$41.18\t\t\n";
        var yaml = StoreCardMapping.Replace("header_rows: 0", "header_rows: 2\nfooter_rows: 1",
            StringComparison.Ordinal);

        var result = await ParseAsync(yaml, body);

        // The preamble and the TOTAL line are not transactions, and must not be reported
        // as unreadable rows either — a warning for a line doing its job trains the
        // reader to ignore warnings.
        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Transactions);
        Assert.Equal(-41.18m, only.Amount);
    }

    [Fact]
    public async Task A_quoted_delimiter_inside_a_field_does_not_shift_the_columns()
    {
        // The reason this uses CsvHelper rather than a split(). A comma inside a quoted
        // merchant name silently moves the amount into the description for a hand-rolled
        // splitter — money corruption that parses cleanly.
        var yaml = StoreCardMapping
            .Replace("delimiter: tab", "delimiter: comma", StringComparison.Ordinal);
        var body = "09/02/2026,$41.18,\"SAMPLE, INC\",purchase\n";

        var result = await ParseAsync(yaml, body);

        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Transactions);
        Assert.Equal(-41.18m, only.Amount);
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
                   + "2026-09-02,SAMPLE STORE,41.18,\n"
                   + "2026-09-04,SAMPLE STORE,,23.50\n";

        var result = await ParseAsync(yaml, body);

        Assert.Empty(result.Errors);
        // Debit is money out; credit is money in. No invert involved — there is no sign
        // in the file to flip.
        Assert.Equal(-41.18m, result.Transactions[0].Amount);
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

        var result = await ParseAsync(yaml, "2026-09-02,SAMPLE STORE,41.18,23.50\n");

        // Picking one would be inventing an answer about money.
        Assert.Empty(result.Transactions);
        Assert.Contains(result.Errors, e => e.Message.Contains("only one can apply"));
    }

    [Theory]
    [InlineData("$1,234.56", 1234.56)]
    [InlineData("(45.00)", -45.00)]
    [InlineData("-$23.50", -23.50)]
    [InlineData("  12.00  ", 12.00)]
    public void Money_survives_the_decoration_real_exports_put_around_it(string raw, double expected)
    {
        Assert.True(CsvGenericFileProvider.TryMoney(raw, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("n/a")]
    public void Something_that_is_not_money_is_refused_rather_than_read_as_zero(string raw)
    {
        // Zero would be a silent, plausible, wrong amount.
        Assert.False(CsvGenericFileProvider.TryMoney(raw, out _));
    }
}
