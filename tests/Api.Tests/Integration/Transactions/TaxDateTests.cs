using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end behaviour of the tax / transaction date (`transacted_at`), read
/// through <c>resolved_transactions</c> — the override-aware view the register and
/// reports actually read.
/// </summary>
/// <remarks>
/// <para>This surface had NO server-side test coverage at all before migration 189,
/// which is how a real defect stayed invisible: the bank PATCH writes the
/// <c>txn_header_overrides</c> layer, where a null field means "leave this column
/// alone" (ADR-0003). A client sending null to CLEAR a tax date therefore cleared
/// nothing. A payload-level assertion cannot see that — only reading back through
/// the view can.</para>
/// <para>Migration 189 makes the base column NOT NULL and stores "no distinct tax
/// date" as the posted date, so clearing is expressed by sending the posted date
/// rather than a null. These tests pin that.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TaxDateTests
{
    private readonly PostgresFixture _fixture;

    public TaxDateTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    /// <summary>
    /// Effective tax date, as the register and reports see it. The view has no
    /// ledger_id of its own — header_id is already unique — so it is keyed on that.
    /// </summary>
    private async Task<DateTime> ResolvedTransactedAtAsync(Guid headerId)
    {
        await using var db = _fixture.NewServiceFactory().Create();
        return await db.Database
            .SqlQuery<DateTime>($"""
                SELECT DISTINCT transacted_at AS "Value"
                  FROM resolved_transactions
                 WHERE header_id = {headerId}
                """)
            .SingleAsync();
    }

    [Fact]
    public async Task Create_without_a_tax_date_stores_the_posted_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var posted = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = posted,
                TransactedAt = null,           // "no distinct tax date"
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -40.25m } },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var headerId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // NOT NULL since mig 189 — a null request value lands as the posted date,
        // which is the single representation of "same day".
        Assert.Equal(posted, await ResolvedTransactedAtAsync(headerId));
    }

    [Fact]
    public async Task Patch_sets_a_distinct_tax_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("dividends");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var posted = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var taxDate = new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Utc);
        var create = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = posted,
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = 120m } },
            });
        var headerId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // The case the field exists for: booked Dec 29, posted Jan 2.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest { TransactedAt = taxDate });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        Assert.Equal(taxDate, await ResolvedTransactedAtAsync(headerId));
    }

    [Fact]
    public async Task Clearing_a_tax_date_means_sending_the_posted_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("dividends");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var posted = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var create = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = posted,
                TransactedAt = new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = 120m } },
            });
        var headerId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        Assert.Equal(
            new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Utc),
            await ResolvedTransactedAtAsync(headerId));

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest { TransactedAt = posted });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        Assert.Equal(posted, await ResolvedTransactedAtAsync(headerId));
    }

    [Fact]
    public async Task Patching_a_null_tax_date_leaves_the_existing_one_alone()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("dividends");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var posted = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var taxDate = new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Utc);
        var create = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = posted,
                TransactedAt = taxDate,
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = 120m } },
            });
        var headerId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // Null on a PATCH field means "leave this column alone" (ADR-0003). This is
        // the exact behaviour that made a null-to-clear client silently no-op, so
        // it is pinned deliberately rather than left as folklore.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest { Payee = "Renamed" });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        Assert.Equal(taxDate, await ResolvedTransactedAtAsync(headerId));
    }

    /// <summary>
    /// The investment PATCH is wholesale-replace (ADR-0025): the body IS the new
    /// state of the world, so OMITTING a field clears it — the opposite of the
    /// bank PATCH's override-layer "leave it alone".
    /// </summary>
    /// <remarks>
    /// Pinned because the difference is invisible at the call site and cost a real
    /// bug: the web investment editor's draft carried no tax date at all, so every
    /// save omitted it, and editing an unrelated field on a transaction with a
    /// distinct tax date silently destroyed that tax date. Any client of this
    /// endpoint MUST send the tax date on every PATCH. If this test ever starts
    /// failing because the server learned to preserve an omitted tax date, that is
    /// a deliberate semantics change — not a test to relax.
    /// </remarks>
    [Fact]
    public async Task Investment_patch_omitting_the_tax_date_clears_it_to_the_posted_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var posted = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var taxDate = new DateTime(2025, 12, 29, 12, 0, 0, DateTimeKind.Utc);
        var create = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "buy",
                SecurityId = security,
                Shares = 10m,
                Price = 100m,
                PostedAt = posted,
                TransactedAt = taxDate,
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var headerId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        Assert.Equal(taxDate, await ResolvedTransactedAtAsync(headerId));

        // A PATCH that changes only the memo, with no TransactedAt — exactly the
        // payload the web editor used to send.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "buy",
                SecurityId = security,
                Shares = 10m,
                Price = 100m,
                PostedAt = posted,
                Memo = "edited",
            });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        // The tax date is GONE — collapsed to the posted date. This is the
        // documented behaviour of wholesale-replace, which is why the client is
        // the one that has to carry the value.
        Assert.Equal(posted, await ResolvedTransactedAtAsync(headerId));
    }

    /// <summary>
    /// The other half of the contract: a client that DOES send the tax date on
    /// every PATCH keeps it. This is what the web editor now does.
    /// </summary>
    [Fact]
    public async Task Investment_patch_carrying_the_tax_date_preserves_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var posted = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var taxDate = new DateTime(2025, 12, 29, 12, 0, 0, DateTimeKind.Utc);
        var create = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "buy",
                SecurityId = security,
                Shares = 10m,
                Price = 100m,
                PostedAt = posted,
                TransactedAt = taxDate,
            });
        var headerId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "buy",
                SecurityId = security,
                Shares = 10m,
                Price = 100m,
                PostedAt = posted,
                TransactedAt = taxDate,
                Memo = "edited",
            });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        Assert.Equal(taxDate, await ResolvedTransactedAtAsync(headerId));
    }
}
