using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A transfer between two real accounts in different currencies records both amounts, each
/// leg in its own account's currency (ADR-0114 D13), through the one leg-pair builder the
/// bank create and the PATCH reshape share: the counterparty leg is the posting's
/// <c>counterpartyAmount</c> there, and the amount negated everywhere else.
/// </summary>
/// <remarks>
/// Every transaction is written through <c>POST /transactions</c> and edited through
/// <c>PATCH</c>, on synthetic ledgers; the legs are then read back as stored.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CrossCurrencyTransferTests : DatabaseTest
{
    public CrossCurrencyTransferTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(SyntheticLedger Ledger, Guid UsdBank, Guid EurBank, Guid UsdSavings, Guid Category);

    private async Task<Seed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        var savings = await ledger.AddBankAccountAsync("Bank C", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        return new Seed(ledger, usd.Id, eur.Id, savings.Id, category.Id);
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, Seed seed, Guid source, params TransactionPosting[] postings) =>
        client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = PostedAt,
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = postings,
        });

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Seed seed, Guid headerId, PatchTransactionRequest body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{headerId}")
        {
            Content = JsonContent.Create(body),
        });

    private async Task<IReadOnlyList<TxnLegRow>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .OrderBy(l => l.PostingIndex).ThenBy(l => l.Id)
            .ToListAsync();
    }

    private static decimal On(IReadOnlyList<TxnLegRow> legs, Guid accountId) =>
        Assert.Single(legs, l => l.AccountId == accountId).Amount;

    private async Task<int> HeaderCountAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledgerId);
    }

    [Fact]
    public async Task A_transfer_between_currencies_stores_each_legs_own_figure()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 91.37m }));

        var legs = await LegsAsync(headerId);
        Assert.Equal(2, legs.Count);
        Assert.Equal(-100m, On(legs, seed.UsdBank));
        // Its own figure, not the other leg negated.
        Assert.Equal(91.37m, On(legs, seed.EurBank));
    }

    // The anchor: a pair in one currency still carries one amount, so the other leg is its
    // negation, and a second amount sent on it is dropped. Likewise for a category.
    [Fact]
    public async Task A_transfer_in_one_currency_and_a_category_line_mirror_and_drop_a_second_amount()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var sameCurrency = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = -25.5m, CounterpartyAmount = 99m }));
        var sameLegs = await LegsAsync(sameCurrency);
        Assert.Equal(-25.5m, On(sameLegs, seed.UsdBank));
        Assert.Equal(25.5m, On(sameLegs, seed.UsdSavings));

        // A split of a category line and a transfer across currencies: each line by its rule.
        var split = await CreatedIdAsync(await CreateAsync(client, seed, seed.EurBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -12m, CounterpartyAmount = 99m },
            new TransactionPosting { CounterpartyAccountId = seed.UsdBank, Amount = -40m, CounterpartyAmount = 43.8m }));
        var splitLegs = await LegsAsync(split);
        Assert.Equal(12m, On(splitLegs, seed.Category));
        Assert.Equal(43.8m, On(splitLegs, seed.UsdBank));
        Assert.Equal(-52m, splitLegs.Where(l => l.AccountId == seed.EurBank).Sum(l => l.Amount));
    }

    [Fact]
    public async Task A_transfer_between_currencies_without_a_valid_second_amount_is_refused_and_writes_nothing()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        Assert.Equal("transaction-posting-counterparty-amount-required", await CodeOf(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m })));
        Assert.Equal("transaction-posting-two-amounts-nonzero", await CodeOf(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 0m })));
        Assert.Equal("transaction-posting-two-amounts-sign", await CodeOf(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = -91m })));
        // The refused line may follow a valid one: still nothing is written.
        Assert.Equal("transaction-posting-counterparty-amount-required", await CodeOf(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -5m },
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m })));

        Assert.Equal(0, await HeaderCountAsync(seed.Ledger.LedgerId));
    }

    // The second amount is typed in the counterparty's currency, so it is held to that
    // currency's places (ADR-0114 D15): a whole-unit account takes no fraction.
    [Fact]
    public async Task A_second_amount_finer_than_the_counterpartys_currency_is_refused_and_writes_nothing()
    {
        var seed = await SeedAsync();
        var wholeUnit = (await seed.Ledger.AddBankAccountAsync("Bank D", currencyCode: "JPY")).Id;
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var refused = await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = wholeUnit, Amount = -100m, CounterpartyAmount = 1.5m });
        var body = await refused.Content.ReadAsStringAsync();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)refused.StatusCode}: {body}");
        using (var doc = JsonDocument.Parse(body))
        {
            var root = doc.RootElement;
            Assert.Equal(("amount-places-exceed-currency", "JPY", 0, "postings[0].counterpartyAmount"),
                (root.GetProperty("code").GetString(), root.GetProperty("currencyCode").GetString(),
                    root.GetProperty("places").GetInt32(), root.GetProperty("field").GetString()));
        }
        Assert.Equal(0, await HeaderCountAsync(seed.Ledger.LedgerId));

        // The anchor: a whole figure is taken.
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = wholeUnit, Amount = -100m, CounterpartyAmount = 15000m }));
        Assert.Equal(15000m, On(await LegsAsync(headerId), wholeUnit));
    }

    [Fact]
    public async Task A_payee_edit_that_resends_the_postings_keeps_the_other_currencys_leg()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 91.37m }));
        var usdLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == seed.UsdBank);

        var response = await PatchAsync(client, seed, headerId, new PatchTransactionRequest
        {
            Payee = "Payee B",
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = seed.UsdBank,
                Items = [new TransactionPosting
                {
                    LegId = usdLeg.Id, CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 91.37m,
                }],
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var legs = await LegsAsync(headerId);
        Assert.Equal(-100m, On(legs, seed.UsdBank));
        Assert.Equal(91.37m, On(legs, seed.EurBank));

        // A changed second amount is written as sent.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, seed, headerId, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = seed.UsdBank,
                Items = [new TransactionPosting
                {
                    LegId = usdLeg.Id, CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 92m,
                }],
            },
        })).StatusCode);
        Assert.Equal(92m, On(await LegsAsync(headerId), seed.EurBank));
    }

    [Fact]
    public async Task The_same_edit_opened_from_the_other_currencys_register_keeps_the_first_leg()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 91.37m }));
        var eurLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == seed.EurBank);

        // From that register the source is its own leg, and the second amount the first one's.
        var response = await PatchAsync(client, seed, headerId, new PatchTransactionRequest
        {
            Payee = "Payee B",
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = seed.EurBank,
                Items = [new TransactionPosting
                {
                    LegId = eurLeg.Id, CounterpartyAccountId = seed.UsdBank, Amount = 91.37m, CounterpartyAmount = -100m,
                }],
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var legs = await LegsAsync(headerId);
        Assert.Equal(-100m, On(legs, seed.UsdBank));
        Assert.Equal(91.37m, On(legs, seed.EurBank));
    }

    [Fact]
    public async Task A_reshape_onto_another_currency_without_a_second_amount_is_refused_and_leaves_the_transaction()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -30m }));
        var usdLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == seed.UsdBank);

        // Repointing the line from the category to an account in another currency needs both.
        Assert.Equal("transaction-posting-counterparty-amount-required", await CodeOf(await PatchAsync(client, seed, headerId,
            new PatchTransactionRequest
            {
                Payee = "Payee B",
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = seed.UsdBank,
                    Items = [new TransactionPosting { LegId = usdLeg.Id, CounterpartyAccountId = seed.EurBank, Amount = -30m }],
                },
            })));

        var legs = await LegsAsync(headerId);
        Assert.Equal(30m, On(legs, seed.Category));
        await using var db = Db.NewDbContext();
        Assert.Equal("Payee A", (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId)).Payee);
    }

    // Moving a split's category line rebuilds every posting of the split through the
    // reshape, so the kept transfer to the other currency is resent with its stored far leg.
    [Fact]
    public async Task Recategorizing_a_splits_category_line_keeps_the_other_currencys_leg_of_its_transfer_line()
    {
        var seed = await SeedAsync();
        var target = await seed.Ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.EurBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -12m },
            new TransactionPosting { CounterpartyAccountId = seed.UsdBank, Amount = -40m, CounterpartyAmount = 43.8m }));

        await using (var db = Db.NewServiceFactory().Create())
        {
            var outcome = await new TransactionsRepository(db, TimeProvider.System)
                .RecategorizeSplitPostingsAsync(seed.Ledger.LedgerId, headerId, seed.Category, target.Id, dryRun: false);
            Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.Ok, outcome.Result);
            Assert.Equal(1, outcome.Moved);
        }

        var legs = await LegsAsync(headerId);
        Assert.DoesNotContain(legs, l => l.AccountId == seed.Category);
        Assert.Equal(12m, On(legs, target.Id));
        Assert.Equal(43.8m, On(legs, seed.UsdBank));
        Assert.Equal(-52m, legs.Where(l => l.AccountId == seed.EurBank).Sum(l => l.Amount));
    }

    // ---- the second-amount rule on an edit, and what it leaves alone (ADR-0114 D13) -----

    private static PatchTransactionRequest Reshape(Guid source, params TransactionPosting[] items) => new()
    {
        Payee = "Payee B",
        Postings = new PatchTransactionPostings { SourceAccountId = source, Items = items },
    };

    private async Task<string?> PayeeAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId)).Payee;
    }

    [Fact]
    public async Task An_edit_that_makes_either_amount_zero_or_gives_both_one_sign_is_refused_and_leaves_both_legs()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 91.37m }));
        var usdLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == seed.UsdBank);

        TransactionPosting Line(decimal amount, decimal? second) => new()
        {
            LegId = usdLeg.Id, CounterpartyAccountId = seed.EurBank, Amount = amount, CounterpartyAmount = second,
        };

        Assert.Equal("transaction-posting-two-amounts-nonzero",
            await CodeOf(await PatchAsync(client, seed, headerId, Reshape(seed.UsdBank, Line(-100m, 0m)))));
        Assert.Equal("transaction-posting-two-amounts-nonzero",
            await CodeOf(await PatchAsync(client, seed, headerId, Reshape(seed.UsdBank, Line(0m, 91.37m)))));
        Assert.Equal("transaction-posting-two-amounts-sign",
            await CodeOf(await PatchAsync(client, seed, headerId, Reshape(seed.UsdBank, Line(-100m, -91.37m)))));
        Assert.Equal("transaction-posting-counterparty-amount-required",
            await CodeOf(await PatchAsync(client, seed, headerId, Reshape(seed.UsdBank, Line(-100m, null)))));

        var legs = await LegsAsync(headerId);
        Assert.Equal((-100m, 91.37m), (On(legs, seed.UsdBank), On(legs, seed.EurBank)));
        Assert.Equal("Payee A", await PayeeAsync(headerId));
    }

    // The negatives: a posting with a category, or between accounts in one currency, carries
    // one amount, so none of these needs a second one, whatever the currencies around it.
    [Fact]
    public async Task An_expense_from_an_account_in_another_currency_than_the_reporting_one_needs_no_second_amount()
    {
        var seed = await SeedAsync();   // a ledger reported in USD
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.EurBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -12m }));
        var categoryLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == seed.Category);

        // The same line edited from the category's register, whose source is the category:
        // still one amount, and a second one sent on it is dropped.
        var response = await PatchAsync(client, seed, headerId, Reshape(seed.Category,
            new TransactionPosting { LegId = categoryLeg.Id, CounterpartyAccountId = seed.EurBank, Amount = 15m, CounterpartyAmount = 99m }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var legs = await LegsAsync(headerId);
        Assert.Equal((15m, -15m), (On(legs, seed.Category), On(legs, seed.EurBank)));
    }

    [Fact]
    public async Task A_transfer_in_one_currency_and_a_zero_line_of_a_split_need_no_second_amount()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        // A zero line beside a transfer in one currency: zero is still allowed where one
        // amount is carried (ADR-0025).
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = -25m },
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = 0m },
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = 0m }));
        var legs = await LegsAsync(headerId);
        Assert.Equal(6, legs.Count);
        Assert.Equal(25m, legs.Where(l => l.AccountId == seed.UsdSavings).Sum(l => l.Amount));

        var sourceLegs = legs.Where(l => l.AccountId == seed.UsdBank).OrderBy(l => l.PostingIndex).ToList();
        var response = await PatchAsync(client, seed, headerId, Reshape(seed.UsdBank,
            new TransactionPosting { LegId = sourceLegs[0].Id, CounterpartyAccountId = seed.UsdSavings, Amount = -30m },
            new TransactionPosting { LegId = sourceLegs[1].Id, CounterpartyAccountId = seed.Category, Amount = 0m }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(30m, (await LegsAsync(headerId)).Where(l => l.AccountId == seed.UsdSavings).Sum(l => l.Amount));
    }

    // ADR-0114 D13: a mirrored zero pair that a currency edit leaves
    // across currencies is representable, and an edit that resends it unchanged passes.
    [Fact]
    public async Task A_zero_pair_a_currency_edit_left_across_currencies_stays_editable_when_resent_unchanged()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = 0m }));
        var currencyEdit = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.UsdSavings}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { CurrencyCode = "EUR" }),
        });
        Assert.True(currencyEdit.IsSuccessStatusCode, $"{(int)currencyEdit.StatusCode}");
        var usdLeg = Assert.Single(await LegsAsync(headerId), l => l.AccountId == seed.UsdBank);

        var resent = await PatchAsync(client, seed, headerId, Reshape(seed.UsdBank,
            new TransactionPosting { LegId = usdLeg.Id, CounterpartyAccountId = seed.UsdSavings, Amount = 0m, CounterpartyAmount = 0m }));
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        Assert.Equal("Payee B", await PayeeAsync(headerId));

        // Changing it is a new pair across currencies, held to the rule.
        Assert.Equal("transaction-posting-two-amounts-nonzero", await CodeOf(await PatchAsync(client, seed, headerId,
            Reshape(seed.UsdBank,
                new TransactionPosting { LegId = usdLeg.Id, CounterpartyAccountId = seed.UsdSavings, Amount = -5m, CounterpartyAmount = 0m }))));
    }
}
