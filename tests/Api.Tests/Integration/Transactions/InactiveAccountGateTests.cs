using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// API integration tests for the inactive-account 422 gate
/// (PR #132 follow-up). New transactions and reshape PATCHes that
/// target an account whose <c>is_active = false</c> must be rejected
/// with HTTP 422 + a stable error code so the SPA can place the
/// message next to the right field.
///
/// Each endpoint is exercised once per relevant account role:
///   * POST /transactions: source account, posting counterparty
///   * PATCH /transactions: reshape source, reshape counterparty
///   * POST /investment-transactions: brokerage, category,
///     transfer, fee account
///   * PATCH /investment-transactions: same set
///
/// Editing other fields on existing legs whose account is already
/// inactive (e.g. updating a payee on a historical transaction) is
/// intentionally allowed — those code paths never reach the gate
/// because they don't carry an account-id list.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InactiveAccountGateTests
{
    private readonly PostgresFixture _fixture;

    public InactiveAccountGateTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // ----- bank POST -----

    [Fact]
    public async Task Post_bank_transaction_rejects_inactive_source_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        await ledger.SetIsActiveAsync(bank.Id, false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -10m },
                },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("account-inactive", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_bank_transaction_rejects_inactive_counterparty()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        await ledger.SetIsActiveAsync(groceries.Id, false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -10m },
                },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("account-inactive", await ReadCodeAsync(response));
    }

    // ----- investment POST -----

    [Fact]
    public async Task Post_investment_transaction_rejects_inactive_brokerage()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var securityId = await ledger.AddSecurityAsync("Index Fund C", ticker: "IDXC");
        await ledger.SetIsActiveAsync(brokerage.Id, false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                Action = "buy",
                BrokerageAccountId = brokerage.Id,
                SecurityId = securityId,
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Shares = 10m,
                Price = 100m,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("investment-txn-brokerage-inactive", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_investment_transaction_rejects_inactive_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var securityId = await ledger.AddSecurityAsync("Index Fund C", ticker: "IDXC");
        var dividends = await ledger.AddCategoryAsync("dividends", kind: "income");
        await ledger.SetIsActiveAsync(dividends.Id, false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // dividend_cash carries a category posting (income side).
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                Action = "dividend_cash",
                BrokerageAccountId = brokerage.Id,
                SecurityId = securityId,
                CategoryAccountId = dividends.Id,
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Amount = 25m,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("investment-txn-category-inactive", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_investment_transaction_rejects_inactive_transfer_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var checking = await ledger.AddBankAccountAsync("Checking");
        await ledger.SetIsActiveAsync(checking.Id, false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // transfer carries a non-investment target account.
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                Action = "transfer",
                BrokerageAccountId = brokerage.Id,
                TransferAccountId = checking.Id,
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Amount = 1000m,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("investment-txn-transfer-inactive", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_investment_transaction_rejects_inactive_fee_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var securityId = await ledger.AddSecurityAsync("Index Fund C", ticker: "IDXC");
        var fees = await ledger.AddCategoryAsync("trading fees", kind: "expense");
        await ledger.SetIsActiveAsync(fees.Id, false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                Action = "buy",
                BrokerageAccountId = brokerage.Id,
                SecurityId = securityId,
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Shares = 10m,
                Price = 100m,
                FeeAmount = 4.95m,
                FeeAccountId = fees.Id,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("investment-txn-fee-account-inactive", await ReadCodeAsync(response));
    }

    // ----- "still allowed" path: existing inactive-account txn editable on other fields -----

    [Fact]
    public async Task Patch_bank_transaction_allows_editing_other_fields_when_account_is_inactive()
    {
        // Seed a transaction THEN deactivate. The PATCH that only
        // changes payee / memo / posted_at MUST be accepted —
        // historical preservation. Only postings-reshape PATCHes
        // that re-target accounts run through the gate.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Create the transaction while everything's active.
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Whole Foods",
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -42.50m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var headerId = createDoc.RootElement.GetProperty("headerId").GetGuid();

        // Now deactivate the bank account.
        await ledger.SetIsActiveAsync(bank.Id, false);

        // Header-field-only PATCH — should still succeed.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest { Payee = "Whole Foods Market" });

        Assert.True(patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");
    }
}
