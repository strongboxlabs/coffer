using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// End-to-end checks for <c>GET /api/ledgers/{ledgerId}/accounts</c>. Each
/// test mints its own <see cref="SyntheticLedger"/> + authenticated client
/// (the same cookie-auth pattern as <c>LedgersEndpointsTests</c>) so
/// per-user/per-ledger scoping is exercised end-to-end.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public AccountsEndpointsTests(PostgresFixture fixture)
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

    [Fact]
    public async Task Get_returns_accounts_that_belong_to_the_ledger_sorted_by_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        // Insert two accounts in deliberately non-alphabetical order to
        // assert the endpoint sorts by name (not by insertion order).
        await ledger.AddBankAccountAsync("zebra-checking");
        await ledger.AddBankAccountAsync("alpha-savings");
        await ledger.AddCategoryAsync("groceries", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = (await response.Content.ReadFromJsonAsync<AccountSummary[]>())!;
        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { "alpha-savings", "groceries", "zebra-checking" },
                     rows.Select(r => r.Name).ToArray());
        Assert.All(rows, r => Assert.Equal(ledger.LedgerId, r.LedgerId));
    }

    [Fact]
    public async Task Get_returns_422_ledger_not_visible_when_user_has_no_grant()
    {
        // Alice creates her own ledger; Bob has his own ledger + accounts
        // but no grant on Alice's. Bob's GET against Alice's ledger must
        // fail with the same ledger-not-visible code as
        // /api/ledgers/me/last-opened, so the API doesn't leak ledger
        // existence by status-code differentiation.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await alice.AddBankAccountAsync("alices-account");

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.GetAsync($"/api/ledgers/{alice.LedgerId}/accounts");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_returns_empty_array_when_ledger_has_no_accounts_yet()
    {
        // Fresh ledger, no accounts added — still 200 with []. Confirms
        // the empty-result path doesn't accidentally 404 or 422.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<AccountSummary[]>())!;
        Assert.Empty(rows);
    }

    [Fact]
    public async Task PatchTradeCommission_flips_flag_and_recomputes_existing_lot_cost_basis()
    {
        // End-to-end regression coverage for migration 088 (ADR-0032):
        // the commission-flip recompute trigger was removed in favor
        // of an explicit HasDbFunction-bound call from
        // AccountsRepository.SetIsTradeCommissionAsync. Before this
        // test, only SyntheticLedger.SetIsTradeCommissionAsync
        // exercised that flag (via a direct EF update that bypassed
        // the recompute), so the endpoint path was uncovered.
        //
        // Test exercises BOTH directions of the flip to prove the
        // recompute fires regardless of value-direction:
        //   1. Buy with $1 fee → lot starts at $650.10 from
        //      InvestmentPostings.BuildHoldingsImpact's placeholder
        //      (always folds fee into the lot insert; the recompute
        //      normalizes per the flag).
        //   2. PATCH enabled=false → recompute reads flag=FALSE,
        //      strips fee from basis → unit_cost converges to $650.00.
        //   3. PATCH enabled=true  → recompute reads flag=TRUE,
        //      folds fee back in → unit_cost converges to $650.10.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        var expenseCategory = await ledger.AddCategoryAsync("Trading Commission", kind: "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 650m,
                FeeAccountId = expenseCategory.Id,
                FeeAmount = 1.00m,
            });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        // Step 2: PATCH enabled=false. Brokerage flag is already
        // FALSE (default), but the API path still invokes the
        // recompute — that's the whole point of moving the call into
        // the repository. The recompute reads flag=FALSE → strips
        // fee from basis → unit_cost drops from the placeholder
        // $650.10 down to $650.00.
        var patchOff = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}/trade-commission",
            new PatchAccountTradeCommissionRequest { Enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, patchOff.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            var lot = await db.Lots.AsNoTracking()
                .SingleAsync(l => l.LedgerId == ledger.LedgerId);
            Assert.Equal(650.00m, lot.UnitCost);

            // The holding's cost_basis tracks the lot: fee stripped -> 10 @ 650.00.
            var holding = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == brokerage.HoldingsAccountId!.Value
                                  && h.SecurityId == securityId);
            Assert.Equal(6500.00m, holding.CostBasis);
        }

        // Step 3: PATCH enabled=true. Recompute reads flag=TRUE,
        // folds the fee back into basis → (6500 + 1) / 10 = 650.10.
        var patchOn = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}/trade-commission",
            new PatchAccountTradeCommissionRequest { Enabled = true });
        Assert.Equal(HttpStatusCode.NoContent, patchOn.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            var lot = await db.Lots.AsNoTracking()
                .SingleAsync(l => l.LedgerId == ledger.LedgerId);
            Assert.Equal(650.10m, lot.UnitCost);

            // cost_basis folds the fee back in -> 10 @ 650.10.
            var holding = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == brokerage.HoldingsAccountId!.Value
                                  && h.SecurityId == securityId);
            Assert.Equal(6501.00m, holding.CostBasis);
        }
    }

    [Fact]
    public async Task PatchTradeCommission_returns_422_on_non_investment_account()
    {
        // Regression guard: the recompute path now reads HoldingsAccountId
        // alongside the type check (in one query). A non-investment
        // account must still 422 — not throw because HoldingsAccountId
        // is null.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/trade-commission",
            new PatchAccountTradeCommissionRequest { Enabled = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Get_excludes_accounts_from_other_ledgers()
    {
        // Same user, two ledgers; the response for ledger A must omit
        // accounts that live in ledger B even though the user has grants
        // on both. SyntheticLedger.CreateAsync mints a fresh user per
        // call, so this test uses two AddBankAccountAsync calls on two
        // distinct ledgers and authenticates as the first one's user.
        var ledgerA = await SyntheticLedger.CreateAsync(_fixture);
        var ledgerB = await SyntheticLedger.CreateAsync(_fixture);
        await ledgerA.AddBankAccountAsync("a-account");
        await ledgerB.AddBankAccountAsync("b-account");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledgerA);

        var response = await client.GetAsync($"/api/ledgers/{ledgerA.LedgerId}/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<AccountSummary[]>())!;
        Assert.Single(rows);
        Assert.Equal("a-account", rows[0].Name);
    }

    // ---------- Inactive-accounts slice ----------

    [Fact]
    public async Task List_excludes_inactive_accounts_by_default()
    {
        // Two accounts; deactivate one via the PATCH endpoint, then
        // GET the default list and assert only the active remains.
        // Exercises both the new filter (in ListByLedgerAsync) and
        // the PATCH /active endpoint end-to-end.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var active = await ledger.AddBankAccountAsync("active-checking");
        var inactive = await ledger.AddBankAccountAsync("closed-savings");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var deactivateResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{inactive.Id}/active",
            new PatchAccountActiveRequest { Active = false });
        Assert.Equal(HttpStatusCode.NoContent, deactivateResp.StatusCode);

        var listResp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/accounts");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var rows = (await listResp.Content.ReadFromJsonAsync<AccountSummary[]>())!;
        Assert.Single(rows);
        Assert.Equal(active.Id, rows[0].Id);
        Assert.True(rows[0].IsActive);
    }

    [Fact]
    public async Task List_includes_inactive_when_query_param_set()
    {
        // Same setup as the default-exclude test, but with
        // ?includeInactive=true the list returns both accounts.
        // The IsActive flag on each row lets the SPA render them
        // differently (greyed / strikethrough for inactive).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var active = await ledger.AddBankAccountAsync("active-checking");
        var inactive = await ledger.AddBankAccountAsync("closed-savings");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{inactive.Id}/active",
            new PatchAccountActiveRequest { Active = false });

        var listResp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts?includeInactive=true");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var rows = (await listResp.Content.ReadFromJsonAsync<AccountSummary[]>())!;
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, r => r.Id == active.Id && r.IsActive);
        Assert.Contains(rows, r => r.Id == inactive.Id && !r.IsActive);
    }

    [Fact]
    public async Task PatchActive_reactivates_a_previously_inactive_account()
    {
        // Symmetric: PATCH active=false then active=true. The
        // re-activated account reappears in the default list.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var account = await ledger.AddBankAccountAsync("test-account");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{account.Id}/active",
            new PatchAccountActiveRequest { Active = false });

        var reactivateResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{account.Id}/active",
            new PatchAccountActiveRequest { Active = true });
        Assert.Equal(HttpStatusCode.NoContent, reactivateResp.StatusCode);

        var listResp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/accounts");
        var rows = (await listResp.Content.ReadFromJsonAsync<AccountSummary[]>())!;
        Assert.Single(rows);
        Assert.Equal(account.Id, rows[0].Id);
        Assert.True(rows[0].IsActive);
    }

    [Fact]
    public async Task PatchActive_returns_422_on_system_account()
    {
        // Holdings sibling accounts are system-managed and must not
        // be user-deactivatable. AddInvestmentAccountAsync creates
        // the brokerage + its IsSystem=true Holdings sibling; the
        // brokerage itself is NOT system, so we PATCH the sibling.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Test Brokerage");
        var holdingsSiblingId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{holdingsSiblingId}/active",
            new PatchAccountActiveRequest { Active = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
