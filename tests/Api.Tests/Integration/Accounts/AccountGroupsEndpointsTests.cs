using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// End-to-end checks for the user-curated sidebar-tab endpoints
/// (migration 033, <c>/api/ledgers/{ledgerId}/account-groups</c>).
/// Pins the per-user scoping (Alice cannot see / mutate Bob's
/// groups), uniqueness behaviour, idempotency on membership writes,
/// and the cross-ledger guard on member accounts.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountGroupsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public AccountGroupsEndpointsTests(PostgresFixture fixture)
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

    private static async Task<Guid> CreateGroupAsync(HttpClient client, Guid ledgerId, string name)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/account-groups",
            new CreateAccountGroupRequest { Name = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Get_returns_empty_array_when_user_has_no_groups()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/account-groups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<AccountGroupSummary[]>())!;
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Post_creates_group_and_list_returns_it_with_member_ids()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var groupId = await CreateGroupAsync(client, ledger.LedgerId, "Pinned");

        // Empty group lists with no members.
        var listEmpty = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups"))
            .Content.ReadFromJsonAsync<AccountGroupSummary[]>())!;
        Assert.Single(listEmpty);
        Assert.Equal("Pinned", listEmpty[0].Name);
        Assert.Empty(listEmpty[0].MemberAccountIds);

        // Add two accounts; both come back in the listing.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}/members/{bank.Id}",
            content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}/members/{savings.Id}",
            content: null)).StatusCode);

        var listFull = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups"))
            .Content.ReadFromJsonAsync<AccountGroupSummary[]>())!;
        Assert.Single(listFull);
        Assert.Equal(2, listFull[0].MemberAccountIds.Count);
        Assert.Contains(bank.Id, listFull[0].MemberAccountIds);
        Assert.Contains(savings.Id, listFull[0].MemberAccountIds);
    }

    [Fact]
    public async Task Post_returns_422_on_duplicate_name_case_insensitive()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await CreateGroupAsync(client, ledger.LedgerId, "Pinned");

        var dup = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups",
            new CreateAccountGroupRequest { Name = "PINNED" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.StatusCode);
        using var doc = JsonDocument.Parse(await dup.Content.ReadAsStringAsync());
        Assert.Equal("account-group-name-conflict",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_returns_422_on_empty_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups",
            new CreateAccountGroupRequest { Name = "   " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-group-name-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_renames_a_group()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var groupId = await CreateGroupAsync(client, ledger.LedgerId, "Pinned");
        var response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}")
            {
                Content = JsonContent.Create(new PatchAccountGroupRequest { Name = "Watchlist" }),
            });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var rows = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups"))
            .Content.ReadFromJsonAsync<AccountGroupSummary[]>())!;
        Assert.Equal("Watchlist", rows[0].Name);
    }

    [Fact]
    public async Task Delete_drops_group_and_cascades_member_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var groupId = await CreateGroupAsync(client, ledger.LedgerId, "Pinned");
        await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}/members/{bank.Id}",
            content: null);

        var del = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var rows = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups"))
            .Content.ReadFromJsonAsync<AccountGroupSummary[]>())!;
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Member_add_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var groupId = await CreateGroupAsync(client, ledger.LedgerId, "Pinned");
        var first = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}/members/{bank.Id}",
            content: null);
        var second = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}/members/{bank.Id}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var rows = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups"))
            .Content.ReadFromJsonAsync<AccountGroupSummary[]>())!;
        Assert.Single(rows[0].MemberAccountIds);
    }

    [Fact]
    public async Task Member_add_returns_422_when_account_does_not_belong_to_the_ledger()
    {
        // Cross-ledger guard: an account id from a DIFFERENT ledger
        // (one the caller has no grant on) must be rejected. Under
        // RLS the SELECT against `accounts` returns 0 rows for that
        // ledger, so the repository's "account in ledger" check
        // fails → AccountNotInLedger.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var other = await SyntheticLedger.CreateAsync(_fixture);
        var otherAccount = await other.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var groupId = await CreateGroupAsync(client, ledger.LedgerId, "Pinned");
        var response = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}/members/{otherAccount.Id}",
            content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Member_remove_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var groupId = await CreateGroupAsync(client, ledger.LedgerId, "Pinned");
        // Removing a non-member is still a success — the endpoint is
        // idempotent so the SPA can fire-and-forget on right-click.
        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/account-groups/{groupId}/members/{bank.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Get_returns_422_ledger_not_visible_when_user_has_no_grant()
    {
        // Sanity: ledger-grant guard still fires on the new
        // endpoint. Bob has his own ledger; querying Alice's
        // ledger he has no grant on returns the same
        // ledger-not-visible 422 every other per-ledger endpoint
        // uses (no existence-leak via status code).
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.GetAsync(
            $"/api/ledgers/{alice.LedgerId}/account-groups");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }
}
