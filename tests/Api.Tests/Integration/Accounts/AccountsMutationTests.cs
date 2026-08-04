using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// Account create + edit surface (ADR-0050): <c>POST</c> and
/// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}</c>. Covers the
/// ADR-0017 discriminator invariants (category ⇔ category_kind), the
/// investment Holdings-sibling materialization (ADR-0019), validation, and the
/// system-account + cross-ledger guards. Atomic per-test ledger; shared-table
/// reads are scoped by the test's ledger id.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountsMutationTests
{
    private readonly PostgresFixture _fixture;

    public AccountsMutationTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task Create_bank_account_returns_201_and_appears_in_list()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest
            {
                Name = "Everyday Checking",
                AccountType = "bank",
                InstitutionName = "Bank X",
                CurrencyCode = "usd",   // normalized to USD
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<AccountSummary>();
        Assert.Equal("Everyday Checking", created!.Name);
        Assert.Equal("bank", created.AccountType);
        Assert.Equal("USD", created.CurrencyCode);
        Assert.Equal("Bank X", created.InstitutionName);
        Assert.False(created.IsSystem);
        Assert.True(created.IsActive);
        Assert.Null(created.HoldingsAccountId);

        var list = await client.GetFromJsonAsync<List<AccountSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/accounts");
        Assert.Contains(list!, a => a.Id == created.Id && a.Name == "Everyday Checking");
    }

    [Fact]
    public async Task Create_investment_account_materializes_system_holdings_sibling_hidden_from_list()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Brokerage One", AccountType = "investment" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<AccountSummary>();
        Assert.NotNull(created!.HoldingsAccountId);

        await using var db = _fixture.NewDbContext();
        var sibling = await db.Accounts.AsNoTracking()
            .SingleAsync(a => a.Id == created.HoldingsAccountId);
        Assert.True(sibling.IsSystem);
        Assert.Equal("investment", sibling.AccountType);
        Assert.Equal("Brokerage One Holdings", sibling.Name);
        Assert.Equal(ledger.LedgerId, sibling.LedgerId);

        var brokerage = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == created.Id);
        Assert.Equal(sibling.Id, brokerage.HoldingsAccountId);

        // The system sibling is internal machinery — never user-facing.
        var list = await client.GetFromJsonAsync<List<AccountSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/accounts");
        Assert.Contains(list!, a => a.Id == created.Id);
        Assert.DoesNotContain(list!, a => a.Id == created.HoldingsAccountId);
    }

    [Fact]
    public async Task Create_category_enforces_the_category_kind_invariant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        // Category without a kind → rejected.
        var noKind = await client.PostAsJsonAsync(url,
            new CreateAccountRequest { Name = "Rent", AccountType = "category" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noKind.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountCategoryKindInvalid, await ErrorCodeAsync(noKind));

        // Category with a valid kind → created.
        var ok = await client.PostAsJsonAsync(url,
            new CreateAccountRequest { Name = "Rent", AccountType = "category", CategoryKind = "expense" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("expense", (await ok.Content.ReadFromJsonAsync<AccountSummary>())!.CategoryKind);

        // Non-category carrying a kind → rejected.
        var bankWithKind = await client.PostAsJsonAsync(url,
            new CreateAccountRequest { Name = "Checking", AccountType = "bank", CategoryKind = "expense" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bankWithKind.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountCategoryKindInvalid, await ErrorCodeAsync(bankWithKind));
    }

    [Fact]
    public async Task Create_rejects_blank_name_unknown_type_and_bad_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        var blank = await client.PostAsJsonAsync(url,
            new CreateAccountRequest { Name = "   ", AccountType = "bank" });
        Assert.Equal(BusinessError.Codes.AccountNameRequired, await ErrorCodeAsync(blank));

        var badType = await client.PostAsJsonAsync(url,
            new CreateAccountRequest { Name = "Mystery", AccountType = "frobnicate" });
        Assert.Equal(BusinessError.Codes.AccountTypeInvalid, await ErrorCodeAsync(badType));

        var badCurrency = await client.PostAsJsonAsync(url,
            new CreateAccountRequest { Name = "Checking", AccountType = "bank", CurrencyCode = "US" });
        Assert.Equal(BusinessError.Codes.AccountCurrencyInvalid, await ErrorCodeAsync(badCurrency));
    }

    [Fact]
    public async Task Update_edits_general_attributes()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}",
            new UpdateAccountRequest { Name = "Renamed Checking", InstitutionName = "Bank Y", IsActive = false });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var list = await client.GetFromJsonAsync<List<AccountSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/accounts?includeInactive=true");
        var updated = Assert.Single(list!, a => a.Id == bank.Id);
        Assert.Equal("Renamed Checking", updated.Name);
        Assert.Equal("Bank Y", updated.InstitutionName);
        Assert.False(updated.IsActive);

        // Default list (active-only) now excludes it.
        var activeOnly = await client.GetFromJsonAsync<List<AccountSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/accounts");
        Assert.DoesNotContain(activeOnly!, a => a.Id == bank.Id);
    }

    [Fact]
    public async Task Update_rejects_a_system_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The Holdings sibling is system-managed → not user-editable.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.HoldingsAccountId}",
            new UpdateAccountRequest { Name = "Hacked Holdings" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountIsSystem, await ErrorCodeAsync(patch));
    }

    [Fact]
    public async Task Update_with_no_fields_returns_patch_empty()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}",
            new UpdateAccountRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountPatchEmpty, await ErrorCodeAsync(patch));
    }

    [Fact]
    public async Task Create_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.PostAsJsonAsync(
            $"/api/ledgers/{alice.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Intruder", AccountType = "bank" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));

        // And nothing was created in Alice's ledger.
        await using var db = _fixture.NewDbContext();
        Assert.Equal(0, await db.Accounts.AsNoTracking()
            .CountAsync(a => a.LedgerId == alice.LedgerId && a.Name == "Intruder"));
    }

    [Fact]
    public async Task Create_get_detail_and_edit_round_trip_metadata()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/accounts";

        var created = await client.PostAsJsonAsync(baseUrl, new CreateAccountRequest
        {
            Name = "Everyday Checking", AccountType = "bank", InstitutionName = "Bank X",
            AccountNumber = "12345678", RoutingNumber = "021000021",
            AccountUrl = "https://bank.example", Notes = "primary",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<AccountSummary>())!.Id;

        // GET detail returns the metadata the summary omits.
        var detail = await client.GetFromJsonAsync<AccountDetail>($"{baseUrl}/{id}");
        Assert.Equal("12345678", detail!.AccountNumber);
        Assert.Equal("021000021", detail.RoutingNumber);
        Assert.Equal("https://bank.example", detail.AccountUrl);
        Assert.Equal("primary", detail.Notes);

        // PATCH: an empty string clears (notes → null); other fields set.
        var patch = await client.PatchAsJsonAsync($"{baseUrl}/{id}",
            new UpdateAccountRequest { Notes = "", AccountNumber = "99" });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var after = await client.GetFromJsonAsync<AccountDetail>($"{baseUrl}/{id}");
        Assert.Null(after!.Notes);                 // cleared by ""
        Assert.Equal("99", after.AccountNumber);
        Assert.Equal("021000021", after.RoutingNumber);   // untouched (omitted from patch)
    }

    [Fact]
    public async Task Get_detail_rejects_unknown_account_in_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountNotInLedger, await ErrorCodeAsync(resp));
    }

    // ----- ADR-0050 slice 3: opening balance + opened-on + loan terms --------

    private static LoanTermsDto ValidTerms(Guid? interestId, Guid? escrowId) => new()
    {
        OriginalPrincipal = 500000m, AnnualInterestRate = 4.00m,
        PaymentCount = 360, PaymentsPerYear = 12, EscrowAmount = 500.00m,
        InterestAccountId = interestId, EscrowAccountId = escrowId, PaymentIsComputed = true,
    };

    [Fact]
    public async Task Loan_account_requires_terms_and_round_trips_them()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var interest = await ledger.AddCategoryAsync("mortgage-interest", "expense");
        var escrow = await ledger.AddBankAccountAsync("escrow");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        // No terms → rejected.
        var noTerms = await client.PostAsJsonAsync(url,
            new CreateAccountRequest { Name = "Mortgage", AccountType = "loan" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTerms.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountLoanTermsRequired, await ErrorCodeAsync(noTerms));

        // Complete terms + opening balance + opened-on → created and persisted.
        var ok = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Mortgage", AccountType = "loan",
            OpeningBalance = -400000.00m, OpenedOn = new DateOnly(2010, 1, 1),
            LoanTerms = ValidTerms(interest.Id, escrow.Id),
        });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var id = (await ok.Content.ReadFromJsonAsync<AccountSummary>())!.Id;

        var detail = await client.GetFromJsonAsync<AccountDetail>($"{url}/{id}");
        Assert.Equal(-400000.00m, detail!.OpeningBalance);
        Assert.Equal(new DateOnly(2010, 1, 1), detail.OpenedOn);
        Assert.NotNull(detail.LoanTerms);
        Assert.Equal(500000m, detail.LoanTerms!.OriginalPrincipal);
        Assert.Equal(4.00m, detail.LoanTerms.AnnualInterestRate);
        Assert.Equal(360, detail.LoanTerms.PaymentCount);
        Assert.Equal(12, detail.LoanTerms.PaymentsPerYear);
        Assert.Equal(500.00m, detail.LoanTerms.EscrowAmount);
        Assert.Equal(interest.Id, detail.LoanTerms.InterestAccountId);
        Assert.Equal(escrow.Id, detail.LoanTerms.EscrowAccountId);
        Assert.True(detail.LoanTerms.PaymentIsComputed);
    }

    // ----- ADR-0050 ext (mig 168): managed loan-payment reminder -------------

    [Fact]
    public async Task Loan_managed_payment_reminder_sets_up_links_and_reads_back()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var interest = await ledger.AddCategoryAsync("mortgage-interest", "expense");
        var escrow = await ledger.AddBankAccountAsync("escrow");
        var checking = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        var created = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Mortgage", AccountType = "loan",
            OpeningBalance = -400000.00m,
            LoanTerms = ValidTerms(interest.Id, escrow.Id),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var loanId = (await created.Content.ReadFromJsonAsync<AccountSummary>())!.Id;

        // No managed reminder yet.
        var before = await client.GetFromJsonAsync<AccountDetail>($"{url}/{loanId}");
        Assert.Null(before!.ManagedReminder);

        // Set one up — paying from checking, monthly on the 13th.
        var setup = await client.PostAsJsonAsync($"{url}/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(checking.Id, new DateOnly(2026, 1, 13)));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        // The account detail now reflects the link (cadence derived from
        // payments/year on the start day).
        var after = await client.GetFromJsonAsync<AccountDetail>($"{url}/{loanId}");
        Assert.NotNull(after!.ManagedReminder);
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=13", after.ManagedReminder!.Rrule);
        Assert.NotNull(after.ManagedReminder.NextDue);

        // One managed reminder per loan.
        var dup = await client.PostAsJsonAsync($"{url}/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(checking.Id, new DateOnly(2026, 1, 13)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.StatusCode);
        Assert.Equal(BusinessError.Codes.PaymentReminderExists, await ErrorCodeAsync(dup));
    }

    [Fact]
    public async Task Loan_payment_reminder_rejects_a_non_bank_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var interest = await ledger.AddCategoryAsync("mortgage-interest", "expense");
        var escrow = await ledger.AddBankAccountAsync("escrow");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        var created = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Mortgage", AccountType = "loan", OpeningBalance = -400000m,
            LoanTerms = ValidTerms(interest.Id, escrow.Id),
        });
        var loanId = (await created.Content.ReadFromJsonAsync<AccountSummary>())!.Id;

        // A category can't be the paying source.
        var resp = await client.PostAsJsonAsync($"{url}/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(interest.Id, new DateOnly(2026, 1, 13)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.PaymentReminderSourceInvalid, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Editing_loan_terms_updates_the_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var interest = await ledger.AddCategoryAsync("mortgage-interest", "expense");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        var created = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Mortgage", AccountType = "loan", LoanTerms = ValidTerms(interest.Id, null),
        });
        var id = (await created.Content.ReadFromJsonAsync<AccountSummary>())!.Id;

        var patch = await client.PatchAsJsonAsync($"{url}/{id}", new UpdateAccountRequest
        {
            LoanTerms = new LoanTermsDto
            {
                OriginalPrincipal = 500000m, AnnualInterestRate = 4.25m,
                PaymentCount = 180, PaymentsPerYear = 12, EscrowAmount = 1200m,
                InterestAccountId = interest.Id, PaymentIsComputed = false, FixedPayment = 4000m,
            },
        });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var detail = await client.GetFromJsonAsync<AccountDetail>($"{url}/{id}");
        Assert.Equal(4.25m, detail!.LoanTerms!.AnnualInterestRate);
        Assert.Equal(180, detail.LoanTerms.PaymentCount);
        Assert.False(detail.LoanTerms.PaymentIsComputed);
        Assert.Equal(4000m, detail.LoanTerms.FixedPayment);
    }

    [Fact]
    public async Task Loan_terms_validation_and_type_guards()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        // Non-loan account carrying terms → rejected.
        var bankWithTerms = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Checking", AccountType = "bank", LoanTerms = ValidTerms(null, null),
        });
        Assert.Equal(BusinessError.Codes.AccountLoanTermsNotAllowed, await ErrorCodeAsync(bankWithTerms));

        // Loan with a non-positive principal → invalid.
        var badPrincipal = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Mortgage", AccountType = "loan",
            LoanTerms = new LoanTermsDto
            {
                OriginalPrincipal = 0m, AnnualInterestRate = 4.00m, PaymentCount = 360, PaymentsPerYear = 12,
            },
        });
        Assert.Equal(BusinessError.Codes.AccountLoanTermsInvalid, await ErrorCodeAsync(badPrincipal));

        // Fixed payment selected but not supplied → invalid.
        var badFixed = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Mortgage", AccountType = "loan",
            LoanTerms = new LoanTermsDto
            {
                OriginalPrincipal = 500000m, AnnualInterestRate = 4.00m, PaymentCount = 360,
                PaymentsPerYear = 12, PaymentIsComputed = false, FixedPayment = null,
            },
        });
        Assert.Equal(BusinessError.Codes.AccountLoanTermsInvalid, await ErrorCodeAsync(badFixed));
    }

    [Fact]
    public async Task Opening_balance_round_trips_and_categories_must_be_zero()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/accounts";

        var bank = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Savings", AccountType = "bank",
            OpeningBalance = 2500.50m, OpenedOn = new DateOnly(2015, 6, 1),
        });
        var id = (await bank.Content.ReadFromJsonAsync<AccountSummary>())!.Id;
        var detail = await client.GetFromJsonAsync<AccountDetail>($"{url}/{id}");
        Assert.Equal(2500.50m, detail!.OpeningBalance);
        Assert.Equal(new DateOnly(2015, 6, 1), detail.OpenedOn);

        // A category cannot carry a non-zero opening balance.
        var badCategory = await client.PostAsJsonAsync(url, new CreateAccountRequest
        {
            Name = "Groceries", AccountType = "category", CategoryKind = "expense", OpeningBalance = 5m,
        });
        Assert.Equal(BusinessError.Codes.AccountOpeningBalanceInvalid, await ErrorCodeAsync(badCategory));
    }

    [Fact]
    public async Task Loan_payment_preview_computes_principal_interest_and_total()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/loan-payment-preview",
            new LoanPaymentPreviewRequest
            {
                OriginalPrincipal = 500000m, AnnualInterestRate = 4.00m,
                PaymentCount = 360, PaymentsPerYear = 12, EscrowAmount = 500.00m,
                PaymentIsComputed = true,
            });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = (await resp.Content.ReadFromJsonAsync<LoanPaymentPreviewResponse>())!;
        Assert.InRange(body.PeriodicPayment, 2380m, 2395m);   // P&I
        Assert.Equal(500.00m, body.EscrowAmount);
        Assert.InRange(body.TotalPayment, 2880m, 2895m);      // P&I + escrow
    }
}
