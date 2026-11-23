using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Errors;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// An investment transfer between the brokerage and a real account in another currency
/// records both amounts (ADR-0114 D13, ADR-0029): on transfer, buyx, sellx and divx the
/// transfer account's leg is the request's <c>transferAmount</c>, in that account's
/// currency, and the brokerage's leg of the transfer pair is the trade amount, never the
/// fee. It is required there, nonzero and opposite in sign to the brokerage's leg; on a
/// transfer in one currency it is dropped and the leg mirrors.
/// </summary>
/// <remarks>
/// Every transaction is written through the investment endpoints, or the investment
/// reminder endpoint, on synthetic ledgers; the legs are then read back as stored.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentTransferAmountTests : DatabaseTest
{
    public InvestmentTransferAmountTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(
        SyntheticLedger Ledger, Guid Brokerage, Guid Security, Guid OtherCurrencyBank,
        Guid SameCurrencyBank, Guid Income, Guid Fees);

    private async Task<Seed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "USD");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");
        var otherBank = await ledger.AddBankAccountAsync("Bank A", currencyCode: "EUR");
        var sameBank = await ledger.AddBankAccountAsync("Bank B", currencyCode: "USD");
        var income = await ledger.AddCategoryAsync("Category A", kind: "income");
        var fees = await ledger.AddCategoryAsync("Category B", kind: "expense");
        return new Seed(ledger, brokerage.Id, security, otherBank.Id, sameBank.Id, income.Id, fees.Id);
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, Seed seed, CreateInvestmentTransactionRequest request) =>
        client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions", request);

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private async Task<IReadOnlyList<TxnLegRow>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .OrderBy(l => l.PostingIndex).ThenBy(l => l.Id)
            .ToListAsync();
    }

    private static TxnLegRow TransferLeg(IReadOnlyList<TxnLegRow> legs, Guid accountId) =>
        Assert.Single(legs, l => l.AccountId == accountId && l.PostingRole == "transfer");

    /// <summary>The request each transfer-shape action sends to a bank in another
    /// currency: the trade or cash amount on the brokerage, and the bank's own figure.
    /// The share trades carry a fee, which the transfer pair must not take.</summary>
    private static CreateInvestmentTransactionRequest Request(Seed seed, string action, decimal transferAmount, Guid? bank = null)
    {
        var transferAccount = bank ?? seed.OtherCurrencyBank;
        return action switch
        {
            "transfer" => new()
            {
                BrokerageAccountId = seed.Brokerage, Action = action, PostedAt = PostedAt,
                Amount = -100m, TransferAccountId = transferAccount, TransferAmount = transferAmount,
            },
            "buyx" => new()
            {
                BrokerageAccountId = seed.Brokerage, Action = action, PostedAt = PostedAt,
                SecurityId = seed.Security, Shares = 2m, Price = 50m, Amount = 100m,
                FeeAccountId = seed.Fees, FeeAmount = 1.5m,
                TransferAccountId = transferAccount, TransferAmount = transferAmount,
            },
            "sellx" => new()
            {
                BrokerageAccountId = seed.Brokerage, Action = action, PostedAt = PostedAt,
                SecurityId = seed.Security, Shares = -2m, Price = 50m, Amount = 100m,
                FeeAccountId = seed.Fees, FeeAmount = 1.5m,
                TransferAccountId = transferAccount, TransferAmount = transferAmount,
            },
            "divx" => new()
            {
                BrokerageAccountId = seed.Brokerage, Action = action, PostedAt = PostedAt,
                SecurityId = seed.Security, Amount = 100m, CategoryAccountId = seed.Income,
                FeeAccountId = seed.Fees, FeeAmount = 1.5m,
                TransferAccountId = transferAccount, TransferAmount = transferAmount,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    /// <summary>A sellx needs shares to sell; the others need nothing first.</summary>
    private static async Task ArrangeAsync(HttpClient client, Seed seed, string action)
    {
        if (action != "sellx") return;
        await CreatedIdAsync(await CreateAsync(client, seed, new()
        {
            BrokerageAccountId = seed.Brokerage, Action = "buy", PostedAt = PostedAt.AddDays(-1),
            SecurityId = seed.Security, Shares = 2m, Price = 50m, Amount = 100m,
        }));
    }

    // The brokerage's leg of the transfer pair, by action: a transfer's own signed amount;
    // the trade amount coming in for a buyx or a divx and going out for a sellx.
    [Theory]
    [InlineData("transfer", 91.37, -100)]
    [InlineData("buyx", -91.37, 100)]
    [InlineData("sellx", 91.37, -100)]
    [InlineData("divx", 91.37, -100)]
    public async Task The_other_currency_leg_is_the_transfer_amount_and_the_brokerage_leg_the_trade_amount(
        string action, double transferAmount, double brokerageLeg)
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        await ArrangeAsync(client, seed, action);

        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, Request(seed, action, (decimal)transferAmount)));

        var legs = await LegsAsync(headerId);
        Assert.Equal((decimal)transferAmount, TransferLeg(legs, seed.OtherCurrencyBank).Amount);
        Assert.Equal((decimal)brokerageLeg, TransferLeg(legs, seed.Brokerage).Amount);
    }

    [Fact]
    public async Task A_memo_edit_resending_the_transfer_amount_keeps_the_other_leg_and_its_id()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, Request(seed, "transfer", 91.37m)));
        var before = TransferLeg(await LegsAsync(headerId), seed.OtherCurrencyBank);

        var patch = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{headerId}")
        {
            Content = JsonContent.Create(new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.Brokerage, Action = "transfer", PostedAt = PostedAt,
                Amount = -100m, TransferAccountId = seed.OtherCurrencyBank, TransferAmount = 91.37m,
                Memo = "Memo A",
            }),
        });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var after = TransferLeg(await LegsAsync(headerId), seed.OtherCurrencyBank);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(91.37m, after.Amount);
    }

    [Fact]
    public async Task An_investment_reminder_template_keeps_both_amounts()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                StartDate = new DateOnly(2026, 1, 15),
                Transaction = Request(seed, "transfer", 91.37m),
            });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var template = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == seed.Ledger.LedgerId && h.IsRecurringTemplate);
        var legs = await LegsAsync(template.Id);
        Assert.Equal(91.37m, TransferLeg(legs, seed.OtherCurrencyBank).Amount);
        Assert.Equal(-100m, TransferLeg(legs, seed.Brokerage).Amount);
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("buyx")]
    public async Task A_missing_zero_or_same_signed_transfer_amount_is_refused_and_nothing_is_written(string action)
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        // The brokerage's leg of the pair is negative on a transfer out, positive on a buyx.
        var sameSign = action == "transfer" ? -91.37m : 91.37m;
        CreateInvestmentTransactionRequest missing = action == "transfer"
            ? new()
            {
                BrokerageAccountId = seed.Brokerage, Action = action, PostedAt = PostedAt,
                Amount = -100m, TransferAccountId = seed.OtherCurrencyBank,
            }
            : new()
            {
                BrokerageAccountId = seed.Brokerage, Action = action, PostedAt = PostedAt,
                SecurityId = seed.Security, Shares = 2m, Price = 50m, Amount = 100m,
                TransferAccountId = seed.OtherCurrencyBank,
            };

        Assert.Equal(BusinessError.Codes.InvestmentTxnTransferAmountRequired,
            await CodeOf(await CreateAsync(client, seed, missing)));
        Assert.Equal(BusinessError.Codes.InvestmentTxnTransferAmountsNonzero,
            await CodeOf(await CreateAsync(client, seed, Request(seed, action, 0m))));
        Assert.Equal(BusinessError.Codes.InvestmentTxnTransferAmountsSign,
            await CodeOf(await CreateAsync(client, seed, Request(seed, action, sameSign))));

        await using var db = Db.NewDbContext();
        Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(h => h.LedgerId == seed.Ledger.LedgerId));
    }

    /// <summary>The refusal's code, currency, places and field.</summary>
    private static async Task<(string? Code, string? Currency, int Places, string? Field)> PlacesRefusalAsync(
        HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (root.GetProperty("code").GetString(), root.GetProperty("currencyCode").GetString(),
            root.GetProperty("places").GetInt32(), root.GetProperty("field").GetString());
    }

    // The transfer account's own figure is typed in its own currency, so it is held to that
    // currency's places (ADR-0114 D15): a whole-unit bank takes no fraction, on a live
    // create and on an investment reminder's template alike.
    [Fact]
    public async Task A_transfer_amount_finer_than_its_accounts_currency_is_refused_and_nothing_is_written()
    {
        var seed = await SeedAsync();
        var wholeUnitBank = (await seed.Ledger.AddBankAccountAsync("Bank C", currencyCode: "JPY")).Id;
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        Assert.Equal(("amount-places-exceed-currency", "JPY", 0, "transferAmount"),
            await PlacesRefusalAsync(await CreateAsync(client, seed, Request(seed, "transfer", 1.5m, bank: wholeUnitBank))));
        await using (var db = Db.NewDbContext())
        {
            Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(h => h.LedgerId == seed.Ledger.LedgerId));
        }

        // The anchor: a whole figure is taken.
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, Request(seed, "transfer", 15m, bank: wholeUnitBank)));
        Assert.Equal(15m, TransferLeg(await LegsAsync(headerId), wholeUnitBank).Amount);
    }

    [Fact]
    public async Task An_investment_reminder_transfer_amount_finer_than_its_accounts_currency_is_refused_and_nothing_is_written()
    {
        var seed = await SeedAsync();
        var wholeUnitBank = (await seed.Ledger.AddBankAccountAsync("Bank C", currencyCode: "JPY")).Id;
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        Task<HttpResponseMessage> CreateReminderAsync(decimal transferAmount) =>
            client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/reminders/investment",
                new CreateInvestmentReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                    StartDate = new DateOnly(2026, 1, 15),
                    Transaction = Request(seed, "transfer", transferAmount, bank: wholeUnitBank),
                });

        Assert.Equal(("amount-places-exceed-currency", "JPY", 0, "transferAmount"),
            await PlacesRefusalAsync(await CreateReminderAsync(1.5m)));
        await using (var db = Db.NewDbContext())
        {
            Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(h => h.LedgerId == seed.Ledger.LedgerId));
            Assert.False(await db.RecurringTransactions.AsNoTracking().AnyAsync(r => r.LedgerId == seed.Ledger.LedgerId));
        }

        // The anchor: a whole figure is taken.
        var created = await CreateReminderAsync(15m);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    // ADR-0114 D13: a mirrored zero pair that a currency change leaves across currencies is
    // representable, so a template rebuild that resends it unchanged passes the nonzero rule,
    // as the live edit does; a changed pair is held to it.
    [Fact]
    public async Task An_investment_reminder_zero_pair_a_currency_change_left_across_currencies_stays_editable_when_resent_unchanged()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        CreateInvestmentTransactionRequest Zero(decimal amount, decimal? transferAmount, string? memo = null) => new()
        {
            BrokerageAccountId = seed.Brokerage, Action = "transfer", PostedAt = PostedAt,
            Amount = amount, TransferAccountId = seed.SameCurrencyBank, TransferAmount = transferAmount, Memo = memo,
        };

        var created = await client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                StartDate = new DateOnly(2026, 1, 15),
                Transaction = Zero(0m, null),
            });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var currencyChange = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.SameCurrencyBank}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { CurrencyCode = "EUR" }),
        });
        Assert.True(currencyChange.IsSuccessStatusCode,
            $"{(int)currencyChange.StatusCode}: {await currencyChange.Content.ReadAsStringAsync()}");

        async Task<IReadOnlyList<TxnLegRow>> TemplateLegsAsync()
        {
            await using var db = Db.NewDbContext();
            var templateId = (await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId)).TemplateHeaderId!.Value;
            return await LegsAsync(templateId);
        }

        Task<HttpResponseMessage> EditAsync(CreateInvestmentTransactionRequest transaction) =>
            client.PatchAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{seriesId}/investment",
                new EditInvestmentReminderRequest { Transaction = transaction });

        // The memo edit resends the stored pair, now two amounts across currencies.
        var resent = await EditAsync(Zero(0m, 0m, memo: "Memo A"));
        Assert.True(resent.IsSuccessStatusCode, $"{(int)resent.StatusCode}: {await resent.Content.ReadAsStringAsync()}");
        var legs = await TemplateLegsAsync();
        Assert.Equal(0m, TransferLeg(legs, seed.Brokerage).Amount);
        Assert.Equal(0m, TransferLeg(legs, seed.SameCurrencyBank).Amount);

        // Changing it is a new pair across currencies, held to the rule.
        Assert.Equal(BusinessError.Codes.InvestmentTxnTransferAmountsNonzero,
            await CodeOf(await EditAsync(Zero(-100m, 0m))));
        legs = await TemplateLegsAsync();
        Assert.Equal(0m, TransferLeg(legs, seed.Brokerage).Amount);
        Assert.Equal(0m, TransferLeg(legs, seed.SameCurrencyBank).Amount);
    }

    // The anchor: in one currency the transfer pair still mirrors, and a transferAmount
    // sent on it is dropped (ADR-0029).
    [Fact]
    public async Task A_transfer_in_one_currency_mirrors_and_drops_a_sent_transfer_amount()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var headerId = await CreatedIdAsync(await CreateAsync(client, seed,
            Request(seed, "transfer", 91.37m, bank: seed.SameCurrencyBank)));

        var legs = await LegsAsync(headerId);
        Assert.Equal(-100m, TransferLeg(legs, seed.Brokerage).Amount);
        Assert.Equal(100m, TransferLeg(legs, seed.SameCurrencyBank).Amount);
    }
}
