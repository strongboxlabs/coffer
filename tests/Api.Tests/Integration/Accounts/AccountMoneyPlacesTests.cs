using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// An account's opening balance and a loan's money terms (principal, escrow and the fixed
/// payment) are typed figures in the account's currency, checked at its places
/// (ADR-0114 D15): one with more places is refused with
/// <c>amount-places-exceed-currency</c>, naming the field. On an edit the places are the
/// currency the edit leaves the account in, and a figure resent equal to its stored value
/// passes even when it does not fit.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountMoneyPlacesTests : DatabaseTest
{
    public AccountMoneyPlacesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string PlacesCode = "amount-places-exceed-currency";

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
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

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, SyntheticLedger ledger, CreateAccountRequest body) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts", body);

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, UpdateAccountRequest body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}")
        {
            Content = JsonContent.Create(body),
        });

    private static LoanTermsDto Terms(decimal principal, decimal escrow, decimal? fixedPayment = null) => new()
    {
        OriginalPrincipal = principal,
        AnnualInterestRate = 4m,
        PaymentCount = 360,
        PaymentsPerYear = 12,
        EscrowAmount = escrow,
        PaymentIsComputed = fixedPayment is null,
        FixedPayment = fixedPayment,
    };

    private static CreateAccountRequest Loan(string currencyCode, LoanTermsDto terms) => new()
    {
        Name = $"Loan {Guid.NewGuid():N}",
        AccountType = "loan",
        CurrencyCode = currencyCode,
        LoanTerms = terms,
    };

    private async Task<int> AccountCountAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking().CountAsync(a => a.LedgerId == ledgerId);
    }

    [Fact]
    public async Task An_opening_balance_finer_than_its_currency_is_refused_naming_the_field()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal((PlacesCode, "JPY", 0, "openingBalance"), await RefusalAsync(await CreateAsync(client, ledger,
            new CreateAccountRequest { Name = "Account A", AccountType = "bank", CurrencyCode = "JPY", OpeningBalance = 0.5m })));
        Assert.Equal((PlacesCode, "USD", 2, "openingBalance"), await RefusalAsync(await CreateAsync(client, ledger,
            new CreateAccountRequest { Name = "Account A", AccountType = "bank", CurrencyCode = "USD", OpeningBalance = 1.234m })));
        Assert.Equal(0, await AccountCountAsync(ledger.LedgerId));

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, ledger,
            new CreateAccountRequest { Name = "Account A", AccountType = "bank", CurrencyCode = "JPY", OpeningBalance = 100m })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, ledger,
            new CreateAccountRequest { Name = "Account B", AccountType = "bank", CurrencyCode = "KWD", OpeningBalance = 1.234m })).StatusCode);
    }

    [Fact]
    public async Task Each_loan_money_term_finer_than_its_currency_is_refused_naming_the_term()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal((PlacesCode, "JPY", 0, "loanTerms.originalPrincipal"),
            await RefusalAsync(await CreateAsync(client, ledger, Loan("JPY", Terms(1000000.5m, 0m)))));
        Assert.Equal((PlacesCode, "JPY", 0, "loanTerms.escrowAmount"),
            await RefusalAsync(await CreateAsync(client, ledger, Loan("JPY", Terms(1000000m, 0.5m)))));
        Assert.Equal((PlacesCode, "JPY", 0, "loanTerms.fixedPayment"),
            await RefusalAsync(await CreateAsync(client, ledger, Loan("JPY", Terms(1000000m, 0m, fixedPayment: 0.5m)))));
        Assert.Equal(0, await AccountCountAsync(ledger.LedgerId));

        Assert.Equal(HttpStatusCode.Created,
            (await CreateAsync(client, ledger, Loan("JPY", Terms(1000000m, 5000m, fixedPayment: 6000m)))).StatusCode);
        // A three-place currency takes a three-place escrow.
        Assert.Equal(HttpStatusCode.Created,
            (await CreateAsync(client, ledger, Loan("KWD", Terms(1000m, 1.234m)))).StatusCode);
        // A computed payment ignores a fixed payment, so it is not checked.
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, ledger, Loan("JPY", new LoanTermsDto
        {
            OriginalPrincipal = 1000000m, AnnualInterestRate = 4m, PaymentCount = 360,
            PaymentsPerYear = 12, PaymentIsComputed = true, FixedPayment = 0.5m,
        }))).StatusCode);
    }

    [Fact]
    public async Task A_stored_opening_balance_that_does_not_fit_passes_when_resent_and_is_refused_when_changed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        // Two places on a whole-unit currency: what the released writers, which held every
        // currency to two places, could store.
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 12.34m, currencyCode: "JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, ledger, account.Id,
            new UpdateAccountRequest { Name = "Account B", OpeningBalance = 12.34m })).StatusCode);
        Assert.Equal((PlacesCode, "JPY", 0, "openingBalance"), await RefusalAsync(await PatchAsync(client, ledger, account.Id,
            new UpdateAccountRequest { Name = "Account C", OpeningBalance = 12.35m })));

        await using var db = Db.NewDbContext();
        var stored = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(("Account B", 12.34m), (stored.Name, stored.OpeningBalance));
    }

    [Fact]
    public async Task An_edit_that_changes_the_currency_checks_the_opening_balance_at_the_new_currencys_places()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // At the old currency's places the figure fits; at the new one's it does not.
        Assert.Equal((PlacesCode, "JPY", 0, "openingBalance"), await RefusalAsync(await PatchAsync(client, ledger, account.Id,
            new UpdateAccountRequest { CurrencyCode = "JPY", OpeningBalance = 0.5m })));

        await using var db = Db.NewDbContext();
        var stored = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(("USD", 0m), (stored.CurrencyCode, stored.OpeningBalance));
    }

    [Fact]
    public async Task Stored_loan_terms_that_do_not_fit_pass_when_resent_and_a_changed_term_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var loan = await ledger.AddAccountAsync("Loan A", "loan", currencyCode: "JPY");
        await using (var seed = Db.NewDbContext())
        {
            // Two-place terms on a whole-unit loan, as the released editor could store them.
            seed.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loan.Id, LedgerId = ledger.LedgerId,
                OriginalPrincipal = 1000000.25m, AnnualInterestRate = 4m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12, EscrowAmount = 100.5m,
                PaymentIsComputed = true,
            });
            await seed.SaveChangesAsync();
        }
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, ledger, loan.Id,
            new UpdateAccountRequest { Name = "Loan B", LoanTerms = Terms(1000000.25m, 100.5m) })).StatusCode);
        Assert.Equal((PlacesCode, "JPY", 0, "loanTerms.escrowAmount"), await RefusalAsync(await PatchAsync(client, ledger, loan.Id,
            new UpdateAccountRequest { LoanTerms = Terms(1000000.25m, 100.75m) })));

        await using var db = Db.NewDbContext();
        Assert.Equal(100.5m, (await db.LoanTerms.AsNoTracking().SingleAsync(t => t.AccountId == loan.Id)).EscrowAmount);
    }
}
