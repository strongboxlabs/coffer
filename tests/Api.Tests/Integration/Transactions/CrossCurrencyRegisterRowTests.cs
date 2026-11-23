using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A register row on a transfer between currencies carries the other leg's own figure and
/// currency (ADR-0114 D14, "The register row"): the bank row as its counterparty amount,
/// the investment event on its transfer slot (ADR-0028 Slot 6), and the ledger-wide
/// entries page, which the home screen's To review and Recent transactions draw, as the
/// row its account's register shows. A transfer in one currency and a category row mirror,
/// so they carry none.
/// </summary>
/// <remarks>
/// Every transaction is written through <c>POST /transactions</c> or
/// <c>POST /investment-transactions</c> on synthetic ledgers, and read back through the
/// register and entries routes. Each negative finds its own row in the same response by
/// header id before it reads the field, so a missing row can never pass as a null figure.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CrossCurrencyRegisterRowTests : DatabaseTest
{
    public CrossCurrencyRegisterRowTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The dollar side of the transfer between currencies.</summary>
    private const decimal DollarLeg = -100m;

    /// <summary>The euro side of the same transfer: its own figure, not the dollar side
    /// negated.</summary>
    private const decimal EuroLeg = 91.37m;

    private sealed record Seed(
        SyntheticLedger Ledger, Guid UsdBank, Guid EurBank, Guid UsdSavings, Guid Category, Guid Brokerage);

    private async Task<Seed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        var savings = await ledger.AddBankAccountAsync("Bank C", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "USD");
        return new Seed(ledger, usd.Id, eur.Id, savings.Id, category.Id, brokerage.Id);
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, Seed seed, Guid source, TransactionPosting posting)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = PostedAt,
                Payee = "Payee A",
                SourceAccountId = source,
                Postings = [posting],
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    private static async Task<Guid> CreateInvestmentAsync(
        HttpClient client, Seed seed, CreateInvestmentTransactionRequest request)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>The one-row entries of an account's register page, by header.</summary>
    private static async Task<IReadOnlyDictionary<Guid, JsonElement>> RegisterRowsAsync(
        HttpClient client, Seed seed, Guid accountId)
    {
        var page = await GetAsync(client,
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions?account_id={accountId}&limit=100");
        return page.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("txn"))
            .ToDictionary(t => t.GetProperty("headerId").GetGuid());
    }

    /// <summary>Every ledger-wide entry's row, by account and header.</summary>
    private static async Task<IReadOnlyDictionary<(Guid AccountId, Guid HeaderId), JsonElement>> EntryRowsAsync(
        HttpClient client, Seed seed)
    {
        var page = await GetAsync(client, $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/entries?limit=50");
        return page.GetProperty("entries").EnumerateArray()
            .ToDictionary(
                e => (e.GetProperty("accountId").GetGuid(), e.GetProperty("entry").GetProperty("txn").GetProperty("headerId").GetGuid()),
                e => e.GetProperty("entry").GetProperty("txn"));
    }

    private static decimal? DecimalOf(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static string? StringOf(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void AssertOtherLeg(JsonElement row, decimal? amount, string? currency)
    {
        Assert.Equal(amount, DecimalOf(row, "counterpartyAmount"));
        Assert.Equal(currency, StringOf(row, "counterpartyCurrencyCode"));
    }

    /// <summary>
    /// The three bank shapes on one ledger: a transfer between currencies, a transfer in
    /// one currency and a category row, all from the dollar bank.
    /// </summary>
    private static async Task<(Guid Across, Guid Within, Guid Categorized)> SeedBankRowsAsync(
        HttpClient client, Seed seed) =>
    (
        await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = DollarLeg, CounterpartyAmount = EuroLeg }),
        await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = -40m }),
        await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -25m })
    );

    /// <summary>
    /// Each side of a transfer between currencies shows the other side's own figure in its
    /// currency; the transfer in one currency and the category row, on the same page, show
    /// none. Mutation caught: the projection leaving the field unmapped (the positives
    /// fail), or mapping it on every row (the negatives fail).
    /// </summary>
    [Fact]
    public async Task Each_register_row_of_a_transfer_between_currencies_carries_the_other_legs_figure()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var (across, within, categorized) = await SeedBankRowsAsync(client, seed);

        var dollar = await RegisterRowsAsync(client, seed, seed.UsdBank);
        Assert.Equal(DollarLeg, dollar[across].GetProperty("amount").GetDecimal());
        AssertOtherLeg(dollar[across], EuroLeg, "EUR");
        AssertOtherLeg(dollar[within], null, null);
        AssertOtherLeg(dollar[categorized], null, null);

        var euro = await RegisterRowsAsync(client, seed, seed.EurBank);
        Assert.Equal(EuroLeg, euro[across].GetProperty("amount").GetDecimal());
        AssertOtherLeg(euro[across], DollarLeg, "USD");

        // The other ends of the mirrored rows show none either.
        var savings = await RegisterRowsAsync(client, seed, seed.UsdSavings);
        AssertOtherLeg(savings[within], null, null);
        var category = await RegisterRowsAsync(client, seed, seed.Category);
        AssertOtherLeg(category[categorized], null, null);
    }

    /// <summary>
    /// The ledger-wide entries page carries the other leg's figure on both accounts' entries
    /// of the transfer between currencies, and none on the mirrored ones. Mutation caught:
    /// the entries page assembling rows without the field.
    /// </summary>
    [Fact]
    public async Task The_entries_page_carries_the_other_legs_figure_as_each_register_does()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var (across, within, categorized) = await SeedBankRowsAsync(client, seed);

        var entries = await EntryRowsAsync(client, seed);

        AssertOtherLeg(entries[(seed.UsdBank, across)], EuroLeg, "EUR");
        AssertOtherLeg(entries[(seed.EurBank, across)], DollarLeg, "USD");
        AssertOtherLeg(entries[(seed.UsdBank, within)], null, null);
        AssertOtherLeg(entries[(seed.UsdSavings, within)], null, null);
        AssertOtherLeg(entries[(seed.UsdBank, categorized)], null, null);
    }

    /// <summary>
    /// An investment transfer to a bank in another currency carries the bank's own figure
    /// on the brokerage event's transfer slot, and the bank's row carries the brokerage's;
    /// a transfer to a bank in the brokerage's currency, on the same register page, carries
    /// none. Mutation caught: the event projection dropping the transfer slot's other leg,
    /// or keeping it on a transfer in one currency.
    /// </summary>
    [Fact]
    public async Task An_investment_transfer_between_currencies_carries_the_bank_leg_on_its_transfer_slot()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var across = await CreateInvestmentAsync(client, seed, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = seed.Brokerage, Action = "transfer", PostedAt = PostedAt,
            Amount = DollarLeg, TransferAccountId = seed.EurBank, TransferAmount = EuroLeg,
        });
        var within = await CreateInvestmentAsync(client, seed, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = seed.Brokerage, Action = "transfer", PostedAt = PostedAt,
            Amount = -40m, TransferAccountId = seed.UsdSavings,
        });

        var brokerage = await RegisterRowsAsync(client, seed, seed.Brokerage);
        Assert.Equal(seed.EurBank, brokerage[across].GetProperty("transferAccountId").GetGuid());
        Assert.Equal(EuroLeg, DecimalOf(brokerage[across], "transferAmount"));
        Assert.Equal("EUR", StringOf(brokerage[across], "transferCurrencyCode"));
        Assert.Equal(seed.UsdSavings, brokerage[within].GetProperty("transferAccountId").GetGuid());
        Assert.Null(DecimalOf(brokerage[within], "transferAmount"));
        Assert.Null(StringOf(brokerage[within], "transferCurrencyCode"));

        var euro = await RegisterRowsAsync(client, seed, seed.EurBank);
        AssertOtherLeg(euro[across], DollarLeg, "USD");
        var savings = await RegisterRowsAsync(client, seed, seed.UsdSavings);
        AssertOtherLeg(savings[within], null, null);
    }
}
