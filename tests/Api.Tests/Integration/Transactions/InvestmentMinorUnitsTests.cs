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
/// Investment amounts and fees are typed in the brokerage's currency and checked at its
/// ISO 4217 places (ADR-0114 D15): one with more places is refused with
/// <c>amount-places-exceed-currency</c>, naming the currency, its places and the field,
/// never rounded. A figure the API computes, the price-times-shares fallback and an
/// in-kind lot's cost, is rounded once at those places, half away from zero.
/// </summary>
/// <remarks>
/// Every transaction is written through the investment and reminder endpoints on synthetic
/// ledgers. A whole-unit currency (no places), a two-place one and a three-place one are
/// each used where the rule differs from two places.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentMinorUnitsTests : DatabaseTest
{
    public InvestmentMinorUnitsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(SyntheticLedger Ledger, AccountRow Brokerage, Guid Security, Guid Fees);

    private async Task<Seed> SeedAsync(string currency)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: currency);
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");
        var fees = await ledger.AddCategoryAsync("Category A", kind: "expense");
        return new Seed(ledger, brokerage, security, fees.Id);
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static CreateInvestmentTransactionRequest Buy(
        Seed seed, decimal shares, decimal price, decimal? amount, decimal? fee = null) => new()
    {
        BrokerageAccountId = seed.Brokerage.Id, Action = "buy", PostedAt = PostedAt,
        SecurityId = seed.Security, Shares = shares, Price = price, Amount = amount,
        FeeAccountId = fee is null ? null : seed.Fees, FeeAmount = fee,
    };

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, Seed seed, CreateInvestmentTransactionRequest request) =>
        client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions", request);

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
    }

    /// <summary>Asserts the places refusal and what it names.</summary>
    private static async Task AssertPlacesRefusalAsync(HttpResponseMessage response, string currency, int places, string field)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(BusinessError.Codes.AmountPlacesExceedCurrency, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(currency, doc.RootElement.GetProperty("currencyCode").GetString());
        Assert.Equal(places, doc.RootElement.GetProperty("places").GetInt32());
        Assert.Equal(field, doc.RootElement.GetProperty("field").GetString());
    }

    private async Task<decimal> BrokerageLegAsync(Guid headerId, Guid brokerageId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == brokerageId && l.PostingRole == "security")
            .Select(l => l.Amount)
            .SingleAsync();
    }

    private async Task<int> HeaderCountAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledgerId);
    }

    [Fact]
    public async Task A_whole_unit_brokerage_refuses_a_fractional_amount_or_fee_and_takes_a_whole_one()
    {
        var seed = await SeedAsync("JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        await AssertPlacesRefusalAsync(await CreateAsync(client, seed, Buy(seed, 10m, 100m, 1000.5m)), "JPY", 0, "amount");
        await AssertPlacesRefusalAsync(await CreateAsync(client, seed, Buy(seed, 10m, 100m, 1000m, fee: 0.5m)), "JPY", 0, "feeAmount");
        Assert.Equal(0, await HeaderCountAsync(seed.Ledger.LedgerId));

        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, Buy(seed, 10m, 100m, 1000m)));
        Assert.Equal(-1000m, await BrokerageLegAsync(headerId, seed.Brokerage.Id));
    }

    [Fact]
    public async Task A_three_place_brokerage_takes_an_amount_with_three_places_as_typed()
    {
        var seed = await SeedAsync("KWD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, Buy(seed, 1m, 1.234m, 1.234m)));

        Assert.Equal(-1.234m, await BrokerageLegAsync(headerId, seed.Brokerage.Id));
    }

    // Before ADR-0114 D15 a typed amount was rounded to two places silently; it is refused.
    [Fact]
    public async Task A_two_place_brokerage_refuses_a_typed_amount_with_three_places()
    {
        var seed = await SeedAsync("USD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        await AssertPlacesRefusalAsync(await CreateAsync(client, seed, Buy(seed, 1m, 10m, 10.005m)), "USD", 2, "amount");
        Assert.Equal(0, await HeaderCountAsync(seed.Ledger.LedgerId));
    }

    // Shares times a price that lands on half a whole unit: rounded once, away from zero, to
    // the whole-unit currency's places. Two-place rounding would keep the half, which the
    // database's three-place ceiling accepts.
    [Fact]
    public async Task The_price_times_shares_fallback_rounds_at_the_brokerages_places()
    {
        var seed = await SeedAsync("JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, Buy(seed, 3m, 100.5m, amount: null)));

        Assert.Equal(-302m, await BrokerageLegAsync(headerId, seed.Brokerage.Id));
    }

    // A third of a whole basis moves: its cost is rounded to the whole unit, where two-place
    // rounding would leave a fraction the three-place ceiling accepts.
    [Fact]
    public async Task An_in_kind_move_between_whole_unit_brokerages_carries_a_whole_lot_cost()
    {
        var seed = await SeedAsync("JPY");
        var dest = await seed.Ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        await CreatedIdAsync(await CreateAsync(client, seed, Buy(seed, 3m, 1000m / 3m, 1000m)));

        var moveId = await CreatedIdAsync(await CreateAsync(client, seed, new()
        {
            BrokerageAccountId = seed.Brokerage.Id, Action = "transfer_shares", PostedAt = PostedAt.AddDays(1),
            SecurityId = seed.Security, Shares = 1m, TransferAccountId = dest.Id,
        }));

        await using var db = Db.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == moveId).ToListAsync();
        Assert.Equal(-333m, Assert.Single(legs, l => l.AccountId == seed.Brokerage.HoldingsAccountId).Amount);
        Assert.Equal(333m, Assert.Single(legs, l => l.AccountId == dest.HoldingsAccountId).Amount);
    }

    // A reminder's template is built by the same validation as a live create.
    [Fact]
    public async Task An_investment_reminder_template_in_a_whole_unit_brokerage_refuses_a_fractional_fee()
    {
        var seed = await SeedAsync("JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                StartDate = new DateOnly(2026, 1, 15),
                Transaction = Buy(seed, 10m, 100m, 1000m, fee: 0.5m),
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(BusinessError.Codes.AmountPlacesExceedCurrency, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await HeaderCountAsync(seed.Ledger.LedgerId));
    }

    // A stored figure that does not fit its currency is kept, so an edit that resends it
    // unchanged passes, and one that changes it to another figure that does not fit is
    // refused (the anchor). The stored figure is the state an install upgraded from before
    // ADR-0114 D15 holds: a two-place amount, entered while every currency took two places,
    // on a brokerage in a whole-unit currency.
    [Fact]
    public async Task An_edit_resending_a_stored_amount_that_does_not_fit_passes_and_a_changed_one_is_refused()
    {
        var seed = await SeedAsync("USD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreatedIdAsync(await CreateAsync(client, seed, Buy(seed, 2m, 6.17m, 12.34m)));
        await using (var db = Db.NewDbContext())
        {
            await db.Accounts
                .Where(a => a.Id == seed.Brokerage.Id || a.Id == seed.Brokerage.HoldingsAccountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, "JPY"));
        }

        async Task<HttpResponseMessage> PatchAsync(decimal amount) =>
            await client.SendAsync(new HttpRequestMessage(
                HttpMethod.Patch, $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{headerId}")
            {
                Content = JsonContent.Create(new PatchInvestmentTransactionRequest
                {
                    BrokerageAccountId = seed.Brokerage.Id, Action = "buy", PostedAt = PostedAt,
                    SecurityId = seed.Security, Shares = 2m, Price = 6.17m, Amount = amount,
                    Memo = "Memo A",
                }),
            });

        var unchanged = await PatchAsync(12.34m);
        Assert.True(unchanged.IsSuccessStatusCode, await unchanged.Content.ReadAsStringAsync());
        Assert.Equal(-12.34m, await BrokerageLegAsync(headerId, seed.Brokerage.Id));

        await AssertPlacesRefusalAsync(await PatchAsync(12.35m), "JPY", 0, "amount");
        Assert.Equal(-12.34m, await BrokerageLegAsync(headerId, seed.Brokerage.Id));
    }

    // The reminder edit rebuilds its template from the whole shape the editor resends, so
    // a schedule or memo edit carries the stored amount and fee unchanged: they pass, as on
    // the PATCH above, and a changed figure that does not fit is refused (the anchor). The
    // stored figures are the upgraded state: two-place, on a whole-unit brokerage.
    [Fact]
    public async Task A_reminder_edit_resending_a_stored_figure_that_does_not_fit_passes_and_a_changed_one_is_refused()
    {
        var seed = await SeedAsync("USD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var created = await client.PostAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                StartDate = new DateOnly(2026, 1, 15),
                Transaction = Buy(seed, 2m, 6.17m, 12.34m, fee: 0.25m),
            });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        await using (var db = Db.NewDbContext())
        {
            await db.Accounts
                .Where(a => a.Id == seed.Brokerage.Id || a.Id == seed.Brokerage.HoldingsAccountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, "JPY"));
        }

        Task<HttpResponseMessage> EditAsync(decimal amount, decimal fee) =>
            client.PatchAsJsonAsync($"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/investment",
                new EditInvestmentReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=20",
                    Transaction = new CreateInvestmentTransactionRequest
                    {
                        BrokerageAccountId = seed.Brokerage.Id, Action = "buy", PostedAt = PostedAt,
                        SecurityId = seed.Security, Shares = 2m, Price = 6.17m, Amount = amount,
                        FeeAccountId = seed.Fees, FeeAmount = fee, Memo = "Memo A",
                    },
                });

        var unchanged = await EditAsync(12.34m, 0.25m);
        Assert.True(unchanged.IsSuccessStatusCode, await unchanged.Content.ReadAsStringAsync());

        foreach (var (amount, fee) in new[] { (12.35m, 0.25m), (12.34m, 0.5m) })
        {
            var changed = await EditAsync(amount, fee);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
            using var doc = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
            Assert.Equal(BusinessError.Codes.AmountPlacesExceedCurrency, doc.RootElement.GetProperty("code").GetString());
        }
    }
}
