using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A row an import rounded carries the bank's figure and the rounded mark on its register
/// row (ADR-0114 D15), read through the originals sidecar: on the account's register and
/// on the ledger entries page the To review widget reads, for a bank row and an investment
/// row alike. The figure is text with every digit kept, a trailing zero included.
/// </summary>
/// <remarks>
/// The rows are written by the QIF import into synthetic ledgers. Each negative finds its
/// own row by header id in the same response before it reads the field.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RegisterRoundedAtImportTests : DatabaseTest
{
    public RegisterRoundedAtImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>Two bank rows: one finer than its currency, written with a trailing zero
    /// past the figure's own places, and one that fits.</summary>
    private const string BankQif =
        "!Type:Bank\nD01/05/2026\nT-12.3450\nPSAMPLE STORE\n^\nD01/06/2026\nT-5.00\nPSAMPLE PAYEE\n^\n";

    /// <summary>A withdrawal finer than its currency, signed by its action.</summary>
    private const string PlanQif = "!Type:Invst\nD02/15/2026\nNWithdrwX\nT100.0050\nMWithdrawal\n^\n";

    [Fact]
    public async Task A_rounded_rows_bank_figure_reaches_the_register_and_the_entries_page()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var plan = await ledger.AddInvestmentAccountAsync("plan");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ImportAsync(client, ledger, BankQif, bank.Id);
        await ImportAsync(client, ledger, PlanQif, plan.Id);

        Guid rounded, fits, planRow;
        await using (var db = Db.NewDbContext())
        {
            var headers = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .Select(h => new { h.Id, h.Payee, h.OriginatingAccountId })
                .ToListAsync();
            rounded = headers.Single(h => h.Payee == "SAMPLE STORE").Id;
            fits = headers.Single(h => h.Payee == "SAMPLE PAYEE").Id;
            planRow = headers.Single(h => h.OriginatingAccountId == plan.Id).Id;
        }

        var bankRegister = await RegisterRowsAsync(client, ledger, bank.Id);
        AssertRounded(bankRegister[rounded], "-12.3450");
        AssertNotRounded(bankRegister[fits]);
        AssertRounded((await RegisterRowsAsync(client, ledger, plan.Id))[planRow], "-100.0050");

        var entries = await EntryRowsAsync(client, ledger);
        AssertRounded(entries[(bank.Id, rounded)], "-12.3450");
        AssertNotRounded(entries[(bank.Id, fits)]);
        AssertRounded(entries[(plan.Id, planRow)], "-100.0050");
    }

    private static void AssertRounded(JsonElement row, string bankFigure)
    {
        Assert.Equal(JsonValueKind.String, row.GetProperty("originalAmount").ValueKind);
        Assert.Equal(bankFigure, row.GetProperty("originalAmount").GetString());
        Assert.True(row.GetProperty("roundedAtImport").GetBoolean());
        Assert.True(row.GetProperty("needsReview").GetBoolean());
    }

    private static void AssertNotRounded(JsonElement row)
    {
        Assert.True(!row.TryGetProperty("originalAmount", out var figure) || figure.ValueKind == JsonValueKind.Null);
        Assert.False(row.GetProperty("roundedAtImport").GetBoolean());
    }

    private static async Task ImportAsync(HttpClient client, SyntheticLedger ledger, string qif, Guid accountId)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(qif));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "export.qif");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent("qif"), "providerAccountId");
        var response = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/qif/import", content);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The rows of an account's register page, by header.</summary>
    private static async Task<IReadOnlyDictionary<Guid, JsonElement>> RegisterRowsAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId)
    {
        var page = await GetAsync(client,
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&limit=100");
        return page.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("txn"))
            .ToDictionary(t => t.GetProperty("headerId").GetGuid());
    }

    /// <summary>The ledger entries awaiting review, the To review widget's read, by
    /// account and header.</summary>
    private static async Task<IReadOnlyDictionary<(Guid AccountId, Guid HeaderId), JsonElement>> EntryRowsAsync(
        HttpClient client, SyntheticLedger ledger)
    {
        var page = await GetAsync(client,
            $"/api/ledgers/{ledger.LedgerId}/transactions/entries?needs_review=true&limit=50");
        return page.GetProperty("entries").EnumerateArray()
            .ToDictionary(
                e => (e.GetProperty("accountId").GetGuid(), e.GetProperty("entry").GetProperty("txn").GetProperty("headerId").GetGuid()),
                e => e.GetProperty("entry").GetProperty("txn"));
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }
}
