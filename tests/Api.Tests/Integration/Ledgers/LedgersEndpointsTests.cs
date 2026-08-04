using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ledgers;

/// <summary>
/// End-to-end checks for the ledger management + auto-open endpoints.
/// Each test mints its own SyntheticLedger and authenticates via a real
/// cookie session (issued by <see cref="SyntheticLedger.IssueSessionCookieAsync"/>)
/// so per-user visibility is exercised — dev-auth would always
/// authenticate as the system user, which would let one test see
/// another's ledgers.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LedgersEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public LedgersEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Build an HTTP client whose Cookie header authenticates as
    /// <paramref name="ledger"/>'s test user. The cookie is a real
    /// <c>auth_sessions</c> row inserted directly so the test bypasses
    /// the WebAuthn ceremonies (those have their own coverage).
    /// </summary>
    /// <remarks>
    /// <see cref="WebApplicationFactoryClientOptions.HandleCookies"/> is
    /// turned off so an explicit <c>Cookie</c> header on
    /// <see cref="HttpClient.DefaultRequestHeaders"/> reaches the server
    /// untouched. With the default (cookie container on), the container
    /// can swallow or override manually-set headers and the request
    /// arrives without a cookie — leaving the dev-auth handler to win
    /// authentication as the bootstrap system user instead of the
    /// per-test user the cookie was minted for.
    /// </remarks>
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
    public async Task Get_returns_only_ledgers_the_authenticated_user_can_see()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var aliceClient = await AuthedClientAsync(factory, alice);

        var response = await aliceClient.GetAsync("/api/ledgers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = (await response.Content.ReadFromJsonAsync<LedgerSummary[]>())!;
        Assert.Contains(rows, r => r.Id == alice.LedgerId);
        Assert.DoesNotContain(rows, r => r.Id == bob.LedgerId);
    }

    [Fact]
    public async Task AnonymousRequest_authenticatesAsSystemUser_whenDevAuthEnabled()
    {
        // Default factory has dev-auth on. SyntheticLedger grants the
        // system user owner on every test ledger, so an anonymous
        // request lands on the system user and the new ledger appears
        // in the response. Pins the dev-auth fallback path so a future
        // refactor can't silently break the local-dev experience.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();   // dev-auth → system user

        var response = await client.GetAsync("/api/ledgers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<LedgerSummary[]>())!;
        Assert.Contains(rows, r => r.Id == ledger.LedgerId);
    }

    [Fact]
    public async Task AnonymousRequest_returns401_whenDevAuthDisabled()
    {
        // The flip side of the previous test: with WithoutDevAuth, the
        // dev-auth scheme isn't registered (registration-time gate in
        // Program.cs), so an anonymous request to a RequireAuthorization
        // endpoint returns 401 — proving the gate is what keeps dev-auth
        // out of production. Adding the synthetic ledger first so the
        // env-var setup path is the same as the comparison test above.
        await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var response = await client.GetAsync("/api/ledgers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_creates_a_ledger_with_the_caller_as_owner_and_returns_201()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var name = $"family-{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync(
            "/api/ledgers", new CreateLedgerRequest { Name = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var created = (await response.Content.ReadFromJsonAsync<LedgerSummary>())!;
        Assert.Equal(name, created.Name);
        Assert.Equal("owner", created.Role);

        // Owner grant landed for the authenticated user.
        await using var db = _fixture.NewDbContext();
        var role = await db.UserLedgerGrants.AsNoTracking()
            .Where(g => g.LedgerId == created.Id && g.UserId == alice.UserId)
            .Select(g => g.Role)
            .SingleOrDefaultAsync();
        Assert.Equal("owner", role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_rejects_empty_name_with_422_and_code(string name)
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var response = await client.PostAsJsonAsync(
            "/api/ledgers", new CreateLedgerRequest { Name = name });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-name-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetLastOpened_returns_204_when_unset()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var response = await client.GetAsync("/api/ledgers/me/last-opened");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task PutLastOpened_then_GetLastOpened_round_trips_the_value()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var put = await client.PutAsync(
            $"/api/ledgers/me/last-opened/{alice.LedgerId}", content: null);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync("/api/ledgers/me/last-opened");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var ledger = (await get.Content.ReadFromJsonAsync<LedgerSummary>())!;
        Assert.Equal(alice.LedgerId, ledger.Id);
    }

    [Fact]
    public async Task PutLastOpened_returns_422_with_code_when_user_has_no_grant_on_the_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        // Try to mark Bob's ledger as Alice's last-opened.
        var response = await client.PutAsync(
            $"/api/ledgers/me/last-opened/{bob.LedgerId}", content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetLastOpened_clears_stale_value_when_grant_revoked_since_last_login()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        // Set last-opened, then revoke Alice's grant directly so the
        // stored id becomes stale.
        await client.PutAsync($"/api/ledgers/me/last-opened/{alice.LedgerId}", content: null);
        await using (var db = _fixture.NewDbContext())
        {
            // The synthetic ledger granted owner to both alice and the
            // system user; revoking alice still leaves system as owner
            // so the >=1 owner constraint trigger is satisfied.
            await db.UserLedgerGrants
                .Where(g => g.UserId == alice.UserId && g.LedgerId == alice.LedgerId)
                .ExecuteDeleteAsync();
        }

        var response = await client.GetAsync("/api/ledgers/me/last-opened");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The stored value got cleared so a subsequent read doesn't pay
        // the same lookup.
        await using var db2 = _fixture.NewDbContext();
        var stored = await db2.Users.AsNoTracking()
            .Where(u => u.Id == alice.UserId)
            .Select(u => u.LastOpenedLedgerId)
            .SingleAsync();
        Assert.Null(stored);
    }
}
