using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenIddict.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The connections people hold to <c>/mcp</c>: a user names and ends their own, and an admin
/// sees every one with its owner and when it was last used. Tokens and OAuth sign-ins are
/// exercised side by side because the point is that they behave alike.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpConnectionsTests : DatabaseTest
{
    public McpConnectionsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string RedirectUri = "https://example.com/callback";
    // RFC 7636's example pair.
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    // RequireHttps stays at its default: it is what puts the __Host- prefix on the session
    // cookie's name, and the cookie header these tests send carries that name.
    private ApiFactory NewFactory() =>
        new ApiFactory(Db).WithMcpEnabled().WithoutDevAuth();

    private static async Task<HttpClient> CookieClientAsync(ApiFactory factory, SyntheticLedger user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await user.IssueSessionCookieAsync()}");
        return client;
    }

    private async Task MakeAdminAsync(Guid userId)
    {
        await using var db = Db.NewDbContext();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, _ => true));
    }

    private static async Task SeedClientAsync(ApiFactory factory, string clientId)
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId) is { } existing) await manager.DeleteAsync(existing);
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = "App " + clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            RedirectUris = { new Uri(RedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + "coffer.read",
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        });
    }

    /// <summary>An OAuth authorization for <paramref name="userId"/>, made directly; its id.</summary>
    private static async Task<string> SeedGrantAsync(ApiFactory factory, string clientId, Guid userId)
    {
        await SeedClientAsync(factory, clientId);
        using var scope = factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var app = await applications.FindByClientIdAsync(clientId);
        var created = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = await applications.GetIdAsync(app!),
            Subject = userId.ToString(),
            Status = Statuses.Valid,
            Type = AuthorizationTypes.Permanent,
            CreationDate = DateTimeOffset.UtcNow,
            Scopes = { "coffer.read" },
        });
        return (await authorizations.GetIdAsync(created))!;
    }

    /// <summary>A personal token for <paramref name="userId"/>, made directly; its id and plaintext.</summary>
    private static async Task<(Guid Id, string Token)> SeedTokenAsync(ApiFactory factory, Guid userId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<McpTokensRepository>();
        var (plaintext, hash) = McpTokenService.Generate();
        var id = await tokens.IssueAsync(userId, name, hash, "coffer.read", null);
        return (id, plaintext);
    }

    /// <summary>The admin list; a failure carries the response body, which names the cause.</summary>
    private static async Task<List<McpConnectionDto>> ListConnectionsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/admin/mcp/connections");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<List<McpConnectionDto>>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private static HttpRequestMessage Initialize(string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05", capabilities = new { },
                    clientInfo = new { name = "coffer-tests", version = "1.0" },
                },
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    // --- a user's own connections ------------------------------------------------------

    [Fact]
    public async Task A_user_renames_their_own_token_and_no_one_elses()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var (aliceToken, _) = await SeedTokenAsync(factory, alice.UserId, "before");
        var (bobToken, _) = await SeedTokenAsync(factory, bob.UserId, "bobs");
        using var client = await CookieClientAsync(factory, alice);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(
            $"/api/account/mcp-tokens/{aliceToken}", new RenameConnectionRequest("  Laptop  "))).StatusCode);
        var list = (await client.GetFromJsonAsync<List<McpTokenSummary>>("/api/account/mcp-tokens"))!;
        Assert.Equal("Laptop", Assert.Single(list).Name);

        var others = await client.PatchAsJsonAsync(
            $"/api/account/mcp-tokens/{bobToken}", new RenameConnectionRequest("hijacked"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, others.StatusCode);
        Assert.Equal("mcp-token-not-found", await ProblemCodeAsync(others));

        await using var db = Db.NewDbContext();
        Assert.Equal("bobs", (await db.McpAccessTokens.AsNoTracking().SingleAsync(t => t.Id == bobToken)).Name);
    }

    [Fact]
    public async Task A_token_cannot_lose_its_name()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var (id, _) = await SeedTokenAsync(factory, alice.UserId, "keep");
        using var client = await CookieClientAsync(factory, alice);

        var blank = await client.PatchAsJsonAsync($"/api/account/mcp-tokens/{id}", new RenameConnectionRequest("   "));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal("mcp-token-name-required", await ProblemCodeAsync(blank));
        Assert.Equal("keep",
            Assert.Single((await client.GetFromJsonAsync<List<McpTokenSummary>>("/api/account/mcp-tokens"))!).Name);
    }

    [Fact]
    public async Task A_user_names_their_own_oauth_connection_clears_it_and_cannot_name_anothers()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var mine = await SeedGrantAsync(factory, "conn-user-mine", alice.UserId);
        var theirs = await SeedGrantAsync(factory, "conn-user-theirs", bob.UserId);
        using var client = await CookieClientAsync(factory, alice);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(
            $"/api/account/oauth-grants/{mine}", new RenameConnectionRequest(" Phone "))).StatusCode);
        var named = Assert.Single((await client.GetFromJsonAsync<List<OAuthGrantsEndpoints.OAuthGrantSummary>>(
            "/api/account/oauth-grants"))!);
        Assert.Equal("Phone", named.Label);
        // The app's own name stays: it says which software this is.
        Assert.Equal("App conn-user-mine", named.AppName);
        Assert.Null(named.LastUsedAt);

        // Blank clears it, falling back to the app's own name.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(
            $"/api/account/oauth-grants/{mine}", new RenameConnectionRequest(" "))).StatusCode);
        Assert.Null(Assert.Single((await client.GetFromJsonAsync<List<OAuthGrantsEndpoints.OAuthGrantSummary>>(
            "/api/account/oauth-grants"))!).Label);

        var others = await client.PatchAsJsonAsync(
            $"/api/account/oauth-grants/{theirs}", new RenameConnectionRequest("hijacked"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, others.StatusCode);
        Assert.Equal("oauth-grant-not-found", await ProblemCodeAsync(others));

        using var scope = factory.Services.CreateScope();
        var meta = await scope.ServiceProvider.GetRequiredService<OAuthConnectionMetaRepository>()
            .ListAsync(new[] { theirs });
        Assert.True(!meta.TryGetValue(theirs, out var theirMeta) || theirMeta.Label is null);
    }

    // --- the admin's view --------------------------------------------------------------

    [Fact]
    public async Task An_admin_sees_every_connection_with_its_owner_and_last_use()
    {
        var admin = await SyntheticLedger.CreateAsync(Db);
        await MakeAdminAsync(admin.UserId);
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var (aliceToken, aliceBearer) = await SeedTokenAsync(factory, alice.UserId, "alice token");
        var bobGrant = await SeedGrantAsync(factory, "conn-admin-view", bob.UserId);
        var usedAt = DateTime.UtcNow.AddMinutes(-5);
        using (var scope = factory.Services.CreateScope())
        {
            var meta = scope.ServiceProvider.GetRequiredService<OAuthConnectionMetaRepository>();
            Assert.True(await meta.SetLabelAsync(bobGrant, bob.UserId, "bob phone"));
            Assert.True(await meta.TouchAsync(bobGrant, bob.UserId, usedAt));
        }
        using var bearer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await bearer.SendAsync(Initialize(aliceBearer))).StatusCode);
        using var client = await CookieClientAsync(factory, admin);

        var all = await ListConnectionsAsync(client);

        var token = Assert.Single(all, c => c.Id == aliceToken.ToString());
        Assert.Equal(McpConnectionKinds.Token, token.Kind);
        Assert.Equal(alice.Username, token.User);
        Assert.Equal("alice token", token.Name);
        Assert.NotNull(token.LastUsedAt);   // the bearer call above

        var grant = Assert.Single(all, c => c.Id == bobGrant);
        Assert.Equal(McpConnectionKinds.OAuth, grant.Kind);
        Assert.Equal(bob.Username, grant.User);
        Assert.Equal("bob phone", grant.Name);
        Assert.Equal("bob phone", grant.Label);
        Assert.Equal("App conn-admin-view", grant.AppName);
        // The client id groups the sign-in under its app; a token belongs to no app.
        Assert.Equal("conn-admin-view", grant.ClientId);
        Assert.Null(token.ClientId);
        Assert.Equal(usedAt, grant.LastUsedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_connection_never_used_has_no_last_use_rather_than_a_made_up_one()
    {
        var admin = await SyntheticLedger.CreateAsync(Db);
        await MakeAdminAsync(admin.UserId);
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var (tokenId, _) = await SeedTokenAsync(factory, alice.UserId, "idle token");
        var grant = await SeedGrantAsync(factory, "conn-never-used", alice.UserId);
        using var client = await CookieClientAsync(factory, admin);

        var all = await ListConnectionsAsync(client);

        Assert.Null(Assert.Single(all, c => c.Id == tokenId.ToString()).LastUsedAt);
        Assert.Null(Assert.Single(all, c => c.Id == grant).LastUsedAt);
    }

    [Fact]
    public async Task A_non_admin_cannot_reach_the_admin_connection_routes()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var (token, _) = await SeedTokenAsync(factory, alice.UserId, "mine");
        using var client = await CookieClientAsync(factory, alice);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/mcp/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync(
            $"/api/admin/mcp/connections/token/{token}", new RenameConnectionRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(
            $"/api/admin/mcp/connections/token/{token}")).StatusCode);
    }

    [Fact]
    public async Task An_admin_renames_and_revokes_anyones_connection_and_the_revoke_is_audited()
    {
        var admin = await SyntheticLedger.CreateAsync(Db);
        await MakeAdminAsync(admin.UserId);
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var (tokenId, bearerToken) = await SeedTokenAsync(factory, alice.UserId, "alice token");
        var grantId = await SeedGrantAsync(factory, "conn-admin-act", alice.UserId);
        using var client = await CookieClientAsync(factory, admin);
        using var bearer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(
            $"/api/admin/mcp/connections/token/{tokenId}", new RenameConnectionRequest("renamed token"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(
            $"/api/admin/mcp/connections/oauth/{grantId}", new RenameConnectionRequest("renamed grant"))).StatusCode);
        var all = await ListConnectionsAsync(client);
        Assert.Equal("renamed token", Assert.Single(all, c => c.Id == tokenId.ToString()).Name);
        Assert.Equal("renamed grant", Assert.Single(all, c => c.Id == grantId).Name);

        // Revoking the token ends it at /mcp, not just in the list.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await bearer.SendAsync(Initialize(bearerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/admin/mcp/connections/token/{tokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.SendAsync(Initialize(bearerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/admin/mcp/connections/oauth/{grantId}")).StatusCode);

        all = await ListConnectionsAsync(client);
        Assert.DoesNotContain(all, c => c.Id == tokenId.ToString() || c.Id == grantId);

        // Ending someone else's access is on the admin audit, naming the kind and the owner.
        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AdminAuditRepository>().RecentAsync(500);
        var revoked = audit.Where(e => e.Action == AdminAuditActions.McpConnectionRevoked
                                       && e.ActorUserId == admin.UserId).ToList();
        Assert.Contains(revoked, e => e.Detail == $"token of user {alice.UserId}");
        Assert.Contains(revoked, e => e.Detail == $"oauth of user {alice.UserId}");
        Assert.DoesNotContain(revoked, e => e.Detail!.Contains(bearerToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_connection_or_kind_is_not_found()
    {
        var admin = await SyntheticLedger.CreateAsync(Db);
        await MakeAdminAsync(admin.UserId);
        await using var factory = NewFactory();
        using var client = await CookieClientAsync(factory, admin);

        foreach (var path in new[]
                 {
                     $"/api/admin/mcp/connections/token/{Guid.NewGuid()}",
                     "/api/admin/mcp/connections/token/not-a-guid",
                     "/api/admin/mcp/connections/oauth/no-such-authorization",
                     $"/api/admin/mcp/connections/carrier-pigeon/{Guid.NewGuid()}",
                 })
        {
            var rename = await client.PatchAsJsonAsync(path, new RenameConnectionRequest("x"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rename.StatusCode);
            Assert.Equal("mcp-connection-not-found", await ProblemCodeAsync(rename));

            var revoke = await client.DeleteAsync(path);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, revoke.StatusCode);
            Assert.Equal("mcp-connection-not-found", await ProblemCodeAsync(revoke));
        }
    }

    [Fact]
    public async Task Revoking_a_client_registration_takes_its_connection_records_with_it()
    {
        var admin = await SyntheticLedger.CreateAsync(Db);
        await MakeAdminAsync(admin.UserId);
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        var grantId = await SeedGrantAsync(factory, "conn-cascade", alice.UserId);
        using (var scope = factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<OAuthConnectionMetaRepository>()
                .SetLabelAsync(grantId, alice.UserId, "doomed"));
        using var client = await CookieClientAsync(factory, admin);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/admin/mcp/clients/conn-cascade")).StatusCode);

        // No orphan: the record went with the authorization, so a later stamp for that id is
        // refused rather than resurrecting a row for a connection that no longer exists.
        await using var db = Db.NewDbContext();
        Assert.False(await db.OAuthConnectionMeta.AnyAsync(m => m.AuthorizationId == grantId));
        using var again = factory.Services.CreateScope();
        Assert.False(await again.ServiceProvider.GetRequiredService<OAuthConnectionMetaRepository>()
            .TouchAsync(grantId, alice.UserId, DateTime.UtcNow));
    }

    // --- last used, delivered end to end ----------------------------------------------

    private static async Task<string> ConnectAsync(HttpClient signedIn, string clientId)
    {
        var query = "response_type=code&client_id=" + clientId
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&scope=coffer.read"
            + "&code_challenge=" + CodeChallenge + "&code_challenge_method=S256&state=xyz";
        var get = await signedIn.GetAsync("/oauth/authorize?" + query);
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        var location = get.Headers.Location!.OriginalString;
        Assert.StartsWith("/oauth/consent?", location);
        var ticket = QueryHelpers.ParseQuery(location[location.IndexOf('?')..])["consent_ticket"].ToString();

        var allow = await signedIn.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = RedirectUri,
            ["scope"] = "coffer.read", ["code_challenge"] = CodeChallenge, ["code_challenge_method"] = "S256",
            ["state"] = "xyz", ["decision"] = "allow", ["consent_ticket"] = ticket,
        }));
        Assert.Equal(HttpStatusCode.Redirect, allow.StatusCode);
        var code = QueryHelpers.ParseQuery(allow.Headers.Location!.Query)["code"].ToString();

        var token = await signedIn.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId, ["code_verifier"] = CodeVerifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    [Fact]
    public async Task A_real_oauth_request_to_mcp_stamps_last_use_and_a_refused_one_does_not()
    {
        // Asserts the stamp LANDS, through the real authorize/token flow and a real /mcp call:
        // a test that called the repository would pass while the middleware never ran.
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = NewFactory();
        await SeedClientAsync(factory, "conn-stamp");
        using var signedIn = await CookieClientAsync(factory, alice);
        var accessToken = await ConnectAsync(signedIn, "conn-stamp");

        var grant = Assert.Single((await signedIn.GetFromJsonAsync<List<OAuthGrantsEndpoints.OAuthGrantSummary>>(
            "/api/account/oauth-grants"))!);
        Assert.Null(grant.LastUsedAt);

        using var mcp = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var before = DateTime.UtcNow.AddSeconds(-1);
        var used = await mcp.SendAsync(Initialize(accessToken));
        Assert.NotEqual(HttpStatusCode.Unauthorized, used.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, used.StatusCode);

        var stamped = Assert.Single((await signedIn.GetFromJsonAsync<List<OAuthGrantsEndpoints.OAuthGrantSummary>>(
            "/api/account/oauth-grants"))!).LastUsedAt;
        Assert.NotNull(stamped);
        Assert.True(stamped!.Value >= before, "the stamp is the time of the call");

        // No credential: refused before the stamp, so it is not a use.
        await Task.Delay(50);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.SendAsync(Initialize(null))).StatusCode);
        Assert.Equal(stamped, Assert.Single((await signedIn.GetFromJsonAsync<List<OAuthGrantsEndpoints.OAuthGrantSummary>>(
            "/api/account/oauth-grants"))!).LastUsedAt);
    }
}
