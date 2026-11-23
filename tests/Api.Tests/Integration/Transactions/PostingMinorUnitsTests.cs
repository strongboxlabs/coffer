using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A bank posting's typed amount is checked at the places of the currency it lands in
/// (ADR-0114 D15): the source account's, or, when the source is a category, the real side's,
/// which the category's leg mirrors (ADR-0114 D13), never the category's own stored code. A
/// figure with more places is refused with <c>amount-places-exceed-currency</c> and nothing
/// is written. On an edit, an amount resent equal to its stored leg passes even when it does
/// not fit, because stored amounts that do not fit are kept.
/// </summary>
/// <remarks>
/// Transactions are written through <c>POST</c> and <c>PATCH /transactions</c> on synthetic
/// ledgers. A leg finer than its currency is seeded directly, as the released writers, which
/// held every currency to two places, could store it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class PostingMinorUnitsTests : DatabaseTest
{
    public PostingMinorUnitsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string PlacesCode = "amount-places-exceed-currency";
    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, params TransactionPosting[] postings) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = PostedAt,
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = postings,
        });

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, SyntheticLedger ledger, Guid headerId, PatchTransactionRequest body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}")
        {
            Content = JsonContent.Create(body),
        });

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    /// <summary>The refusal's code, currency, places and field.</summary>
    private static async Task<(string? Code, string? Currency, int Places, string? Field)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (root.GetProperty("code").GetString(), root.GetProperty("currencyCode").GetString(),
            root.GetProperty("places").GetInt32(), root.GetProperty("field").GetString());
    }

    private async Task<IReadOnlyList<TxnLegRow>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
    }

    private async Task<int> HeaderCountAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledgerId);
    }

    private static decimal On(IReadOnlyList<TxnLegRow> legs, Guid accountId) =>
        Assert.Single(legs, l => l.AccountId == accountId).Amount;

    [Fact]
    public async Task A_typed_amount_finer_than_the_source_accounts_currency_is_refused_and_writes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var jpy = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var usd = await ledger.AddBankAccountAsync("Account B", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Assert.Equal((PlacesCode, "JPY", 0, "postings[0].amount"), await RefusalAsync(await CreateAsync(client, ledger, jpy.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.5m })));
        Assert.Equal((PlacesCode, "USD", 2, "postings[1].amount"), await RefusalAsync(await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1m },
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.234m })));
        Assert.Equal(0, await HeaderCountAsync(ledger.LedgerId));

        // Each currency at its own places: whole, two and three.
        await CreatedIdAsync(await CreateAsync(client, ledger, jpy.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -2m }));
        await CreatedIdAsync(await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.23m }));
    }

    [Fact]
    public async Task A_three_place_account_takes_a_three_place_amount()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var kwd = await ledger.AddBankAccountAsync("Account A", currencyCode: "KWD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var headerId = await CreatedIdAsync(await CreateAsync(client, ledger, kwd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.234m }));
        var legs = await LegsAsync(headerId);
        Assert.Equal(-1.234m, On(legs, kwd.Id));
        Assert.Equal(1.234m, On(legs, category.Id));
    }

    [Fact]
    public async Task From_the_category_register_the_amount_is_checked_at_the_paying_accounts_currency()
    {
        // The category is stamped with the reporting currency, which has two places; the
        // paying account's currency has none, and that is the currency the line is in.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var jpy = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, ledger, jpy.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -2m }));
        var categoryLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == category.Id);

        PatchTransactionRequest Edit(decimal amount) => new()
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = category.Id,
                Items = [new TransactionPosting { LegId = categoryLeg.Id, CounterpartyAccountId = jpy.Id, Amount = amount }],
            },
        };

        Assert.Equal((PlacesCode, "JPY", 0, "postings.items[0].amount"),
            await RefusalAsync(await PatchAsync(client, ledger, headerId, Edit(1.5m))));
        Assert.Equal(2m, On(await LegsAsync(headerId), category.Id));

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, ledger, headerId, Edit(3m))).StatusCode);
        var legs = await LegsAsync(headerId);
        Assert.Equal((3m, -3m), (On(legs, category.Id), On(legs, jpy.Id)));
    }

    [Fact]
    public async Task A_stored_leg_that_does_not_fit_passes_when_resent_and_is_refused_when_changed_or_new()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var jpy = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        var (bankLeg, _) = await ledger.AddTransactionPairAsync(jpy.Id, category.Id, -12.34m, PostedAt, payee: "Payee A");
        var headerId = await ledger.ResolveHeaderIdAsync(bankLeg);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        PatchTransactionRequest Edit(string payee, params TransactionPosting[] items) => new()
        {
            Payee = payee,
            Postings = new PatchTransactionPostings { SourceAccountId = jpy.Id, Items = items },
        };

        // Resent as stored, with a new payee: the figure is not the edit's to judge.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, ledger, headerId, Edit("Payee B",
            new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = category.Id, Amount = -12.34m }))).StatusCode);

        // A changed figure is typed, and is held to the currency's places.
        Assert.Equal((PlacesCode, "JPY", 0, "postings.items[0].amount"), await RefusalAsync(await PatchAsync(client, ledger, headerId,
            Edit("Payee C", new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = category.Id, Amount = -12.35m }))));
        // So is the same figure on a new line: only a kept line keeps its figure.
        Assert.Equal((PlacesCode, "JPY", 0, "postings.items[1].amount"), await RefusalAsync(await PatchAsync(client, ledger, headerId,
            Edit("Payee C",
                new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = category.Id, Amount = -12.34m },
                new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12.34m }))));
        Assert.Equal((PlacesCode, "JPY", 0, "postings[0].amount"), await RefusalAsync(await CreateAsync(client, ledger, jpy.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12.34m })));

        var legs = await LegsAsync(headerId);
        Assert.Equal((-12.34m, 12.34m), (On(legs, jpy.Id), On(legs, category.Id)));
        await using var db = Db.NewDbContext();
        Assert.Equal("Payee B", (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId)).Payee);
    }

    // ADR-0114 D13: an edit from the category register that repoints
    // the paying account to one in another currency is a move. The figure is kept and reads
    // in the new account's currency, so a figure resent unchanged is judged at that
    // currency's places, as a move's relabel is (ADR-0114 D15).
    [Fact]
    public async Task Repointing_the_paying_account_from_the_category_register_keeps_the_figure_at_the_new_currencys_places()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        var jpy = await ledger.AddBankAccountAsync("Account C", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12.34m }));
        var categoryLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == category.Id);

        PatchTransactionRequest Repoint(Guid account) => new()
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = category.Id,
                Items = [new TransactionPosting { LegId = categoryLeg.Id, CounterpartyAccountId = account, Amount = 12.34m }],
            },
        };

        Assert.Equal((PlacesCode, "JPY", 0, "postings.items[0].amount"),
            await RefusalAsync(await PatchAsync(client, ledger, headerId, Repoint(jpy.Id))));
        var legs = await LegsAsync(headerId);
        Assert.Equal((12.34m, -12.34m), (On(legs, category.Id), On(legs, usd.Id)));

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, ledger, headerId, Repoint(eur.Id))).StatusCode);
        legs = await LegsAsync(headerId);
        Assert.Equal((12.34m, -12.34m), (On(legs, category.Id), On(legs, eur.Id)));
        Assert.DoesNotContain(legs, l => l.AccountId == usd.Id);
    }

    [Fact]
    public void A_posting_between_two_categories_takes_the_categorys_own_code()
    {
        AccountsRepository.AccountActivity Category(string code) => new(true, "category", code);
        AccountsRepository.AccountActivity Real(string code) => new(true, "bank", code);

        Assert.Equal("JPY", PostingValidation.PlacesCurrency(Real("JPY"), Category("USD")));
        Assert.Equal("JPY", PostingValidation.PlacesCurrency(Category("USD"), Real("JPY")));
        Assert.Equal("USD", PostingValidation.PlacesCurrency(Category("USD"), Category("USD")));
        Assert.Equal("KWD", PostingValidation.PlacesCurrency(Real("KWD"), Real("JPY")));
    }
}
