using System.Text;
using System.Text.Json;

using Coffer.Api.Ingest;
using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// The Fidelity provider, which is a shim: convert to QIF, then delegate.
/// </summary>
/// <remarks>
/// These tests cover only what delegation adds — that the QIF path is actually reached,
/// that the result wears Fidelity's identity rather than the QIF provider's, and that
/// the trail back to the CSV survives the conversion. Reading the format is
/// <see cref="FidelityActivityToQifTests"/>; turning a QIF verb into a Coffer action is
/// the QIF provider's own business and has its own tests.
/// </remarks>
public sealed class FidelityActivityFileProviderTests
{
    private const string Header =
        "Run Date,Action,Symbol,Description,Type,Price ($),Quantity,Commission ($),"
        + "Fees ($),Accrued Interest ($),Amount ($),Cash Balance ($),Settlement Date";

    private static string File(params string[] rows) =>
        "\n\n" + Header + "\n" + string.Join("\n", rows) + "\n"
        + "\nThe data and information in this spreadsheet is provided to you solely\n";

    private static string Row(string date, string action, string symbol, string description,
        string price, string quantity, string amount, string balance) =>
        $"{date},{action},{symbol},{description},Cash,{price},{quantity},,,,{amount},{balance},";

    private static async Task<FileResult> ParseAsync(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return await new FidelityActivityFileProvider().ParseAsync(
            stream,
            new FileIngestContext(
                LedgerId: Guid.NewGuid(),
                AccountId: Guid.NewGuid(),
                TriggeredByUserId: Guid.NewGuid(),
                CsvMapping: null),
            default);
    }

    [Fact]
    public async Task A_csv_becomes_transactions_without_anyone_mentioning_QIF()
    {
        // The point of the shim: the user picks a CSV and gets rows. That QIF is the
        // vehicle is an implementation detail they never have to learn.
        var result = await ParseAsync(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "10", "-100.00", "900.00"),
            Row("09/01/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "12.34", "1000.00")));

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("buy", result.Transactions[0].Action);
        Assert.Equal("dividend_cash", result.Transactions[1].Action);
        Assert.Equal(10m, result.Transactions[0].Shares);
    }

    [Fact]
    public async Task The_rows_wear_Fidelitys_identity_not_the_QIF_providers()
    {
        // provider_security_mappings is keyed by provider, so a symbol learned here must
        // be remembered as Fidelity's rather than pooled with real QIF imports. The
        // discovered account has to agree, or the binding step has nothing to bind to.
        var result = await ParseAsync(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "10", "-100.00", "900.00")));

        Assert.Equal(FidelityActivityFileProvider.SingleAccountKey,
            Assert.Single(result.Transactions).ProviderAccountId);
        Assert.Equal(FidelityActivityFileProvider.SingleAccountKey,
            Assert.Single(result.DiscoveredAccounts).ProviderAccountId);
        Assert.Equal("investment", result.DiscoveredAccounts[0].AccountType);
    }

    [Fact]
    public async Task Every_row_keeps_both_the_csv_line_and_the_qif_it_became()
    {
        // A converted format loses the trail exactly when it is wanted — when a number
        // looks wrong. Keeping both halves is what makes "why is this row a buy?"
        // answerable without re-running anything.
        var result = await ParseAsync(File(
            Row("09/02/2026", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "10", "-100.00", "900.00")));

        var payload = Assert.Single(result.Transactions).RawProviderPayload;
        Assert.NotNull(payload);
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal("fidelity-activity-csv", doc.RootElement.GetProperty("source").GetString());
        Assert.Contains("YOU BOUGHT SAMPLE FUND",
            doc.RootElement.GetProperty("csv").GetString()!, StringComparison.Ordinal);
        Assert.Contains("NBuy",
            doc.RootElement.GetProperty("qif").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_converts_to_nothing_offers_no_account_to_bind()
    {
        // Offering an account for a file that produced nothing invites an import of zero
        // transactions, and the conversion error is the thing worth surfacing instead.
        var result = await ParseAsync("Date,Investment,Transaction Type,Shares/Unit,Amount ($)\n"
            + "09/02/2026,SAMPLE FUND,Contributions,1.234,100.00\n");

        Assert.Empty(result.Transactions);
        Assert.Empty(result.DiscoveredAccounts);
        Assert.Equal("fidelity_no_header", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task A_conversion_complaint_survives_delegation()
    {
        // Conversion errors happen in the shim, before QIF exists. They have to reach
        // the import result rather than being dropped on the way through — the QIF
        // provider has no idea the CSV ever existed.
        var result = await ParseAsync(File(
            Row("not-a-date", "YOU BOUGHT SAMPLE FUND (SMPL)", "SMPL", "SAMPLE FUND",
                "10.00", "10", "-100.00", "900.00"),
            Row("09/01/2026", "DIVIDEND RECEIVED SAMPLE FUND (SMPL) (Cash)", "SMPL",
                "SAMPLE FUND", "", "0", "12.34", "1000.00")));

        Assert.Contains(result.Errors, e => e.Code == "fidelity_row_unreadable");
        // ...and the readable row still imported.
        Assert.Single(result.Transactions);
        Assert.Equal("dividend_cash", result.Transactions[0].Action);
    }
}
