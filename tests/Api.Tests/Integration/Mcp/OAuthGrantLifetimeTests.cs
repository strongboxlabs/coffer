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
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// What a connection made through OAuth may do, and for how long (FU-087), and how the
/// authorization endpoint answers the prompts it used to get wrong (FU-095).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OAuthGrantLifetimeTests
{
    private readonly PostgresFixture _fixture;

    public OAuthGrantLifetimeTests(PostgresFixture fixture) => _fixture = fixture;

    private const string RedirectUri = "https://example.com/callback";
    // RFC 7636's example pair.
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    private static string AuthorizeQuery(string clientId, string? prompt = null, string scope = "coffer.read") =>
        "response_type=code&client_id=" + clientId
        + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
        + "&scope=" + Uri.EscapeDataString(scope)
        + "&code_challenge=" + CodeChallenge + "&code_challenge_method=S256&state=xyz"
        + (prompt is null ? "" : "&prompt=" + prompt);

    private ApiFactory NewFactory() =>
        new ApiFactory(_fixture).WithMcpEnabled().WithConfig("Api:Cookie:RequireHttps", "false");

    private static HttpClient NewClient(ApiFactory factory, bool cookies = true) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = cookies });

    private static async Task SeedClientAsync(ApiFactory factory, string clientId, bool revocation = true)
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId) is { } existing) await manager.DeleteAsync(existing);
        var descriptor = new OpenIddictApplicationDescriptor
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
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + "coffer.read",
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        if (revocation) descriptor.Permissions.Add(Permissions.Endpoints.Revocation);
        await manager.CreateAsync(descriptor);
    }

    private static async Task SignInAsync(HttpClient client) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/dev-login", null)).StatusCode);

    /// <summary>Consent and exchange the code, as a signed-in client; the refresh token.</summary>
    private static async Task<string> ConnectAsync(HttpClient client, string clientId, string scope = "coffer.read")
    {
        var get = await client.GetAsync("/oauth/authorize?" + AuthorizeQuery(clientId, scope: scope));
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        var location = get.Headers.Location!.OriginalString;
        Assert.StartsWith("/oauth/consent?", location);
        var ticket = QueryHelpers.ParseQuery(location[location.IndexOf('?')..])["consent_ticket"].ToString();

        var allow = await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = RedirectUri,
            ["scope"] = scope, ["code_challenge"] = CodeChallenge, ["code_challenge_method"] = "S256",
            ["state"] = "xyz", ["decision"] = "allow", ["consent_ticket"] = ticket,
        }));
        Assert.Equal(HttpStatusCode.Redirect, allow.StatusCode);
        var code = QueryHelpers.ParseQuery(allow.Headers.Location!.Query)["code"].ToString();

        var token = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId, ["code_verifier"] = CodeVerifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string clientId, string refreshToken) =>
        client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = clientId,
        }));

    /// <summary>Every grant <paramref name="clientId"/> holds.</summary>
    private static async Task<List<object>> GrantsAsync(IServiceProvider services, string clientId)
    {
        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = services.GetRequiredService<IOpenIddictAuthorizationManager>();
        var appId = await applications.GetIdAsync((await applications.FindByClientIdAsync(clientId))!);
        var list = new List<object>();
        await foreach (var a in authorizations.FindByApplicationIdAsync(appId!)) list.Add(a);
        return list;
    }

    /// <summary>Move every grant <paramref name="clientId"/> holds back past its age.</summary>
    private static async Task AgeGrantsPastMaxAsync(ApiFactory factory, string clientId)
    {
        using var scope = factory.Services.CreateScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        foreach (var authorization in await GrantsAsync(scope.ServiceProvider, clientId))
        {
            var descriptor = new OpenIddictAuthorizationDescriptor();
            await authorizations.PopulateAsync(descriptor, authorization);
            descriptor.CreationDate = DateTimeOffset.UtcNow - OAuthGrants.MaxAge - TimeSpan.FromDays(1);
            await authorizations.UpdateAsync(authorization, descriptor);
        }
    }

    private static string? QueryValue(Uri location, string key) =>
        QueryHelpers.ParseQuery(location.Query).TryGetValue(key, out var v) ? v.ToString() : null;

    // --- grant age (FU-087) --------------------------------------------------------

    [Fact]
    public async Task A_grant_past_ninety_days_cannot_refresh_and_is_revoked()
    {
        await using var factory = NewFactory();
        const string clientId = "grant-age-refresh";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);
        var refresh = await ConnectAsync(client, clientId);

        await AgeGrantsPastMaxAsync(factory, clientId);
        var refused = await RefreshAsync(client, clientId, refresh);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(OpenIddictConstants.Errors.InvalidGrant, body.GetProperty("error").GetString());
        Assert.Contains("90 days", body.GetProperty("error_description").GetString());

        using var scope = factory.Services.CreateScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        foreach (var authorization in await GrantsAsync(scope.ServiceProvider, clientId))
            Assert.True(await authorizations.HasStatusAsync(authorization, Statuses.Revoked));
    }

    [Fact]
    public async Task A_young_grant_still_refreshes()
    {
        await using var factory = NewFactory();
        const string clientId = "grant-age-young";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);
        var refresh = await ConnectAsync(client, clientId);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, clientId, refresh)).StatusCode);
    }

    [Fact]
    public async Task A_returning_client_past_ninety_days_is_asked_again()
    {
        await using var factory = NewFactory();
        const string clientId = "grant-age-return";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);
        await ConnectAsync(client, clientId);

        // Young: the consent is reused and a code issued straight away.
        var young = await client.GetAsync("/oauth/authorize?" + AuthorizeQuery(clientId));
        Assert.StartsWith(RedirectUri, young.Headers.Location!.OriginalString);

        // Past its age: not reused, so the user is asked again.
        await AgeGrantsPastMaxAsync(factory, clientId);
        var aged = await client.GetAsync("/oauth/authorize?" + AuthorizeQuery(clientId));
        Assert.StartsWith("/oauth/consent?", aged.Headers.Location!.OriginalString);
    }

    // --- prompts (FU-095) --------------------------------------------------------

    [Fact]
    public async Task Prompt_none_with_no_session_answers_login_required_to_the_client()
    {
        await using var factory = NewFactory();
        const string clientId = "prompt-none-anon";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory, cookies: false);

        var response = await client.GetAsync("/oauth/authorize?" + AuthorizeQuery(clientId, "none"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(RedirectUri, response.Headers.Location!.OriginalString);
        Assert.Equal(OpenIddictConstants.Errors.LoginRequired, QueryValue(response.Headers.Location, "error"));
    }

    [Fact]
    public async Task Prompt_none_before_consent_answers_consent_required_to_the_client()
    {
        await using var factory = NewFactory();
        const string clientId = "prompt-none-consent";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);

        var response = await client.GetAsync("/oauth/authorize?" + AuthorizeQuery(clientId, "none"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(RedirectUri, response.Headers.Location!.OriginalString);
        Assert.Equal(OpenIddictConstants.Errors.ConsentRequired, QueryValue(response.Headers.Location, "error"));
    }

    [Fact]
    public async Task Prompt_login_is_satisfied_by_a_fresh_sign_in_and_not_by_an_old_one()
    {
        await using var factory = NewFactory();
        const string clientId = "prompt-login";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);

        // Just signed in: the login page sent the user back here, prompt and all. This
        // used to send them to the login page again, for ever.
        var fresh = await client.GetAsync("/oauth/authorize?" + AuthorizeQuery(clientId, "login"));
        Assert.StartsWith("/oauth/consent?", fresh.Headers.Location!.OriginalString);

        await using (var db = _fixture.NewDbContext())
            await db.AuthSessions.Where(s => s.RevokedAt == null && s.AuthMethod == "dev")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-1)));

        var stale = await client.GetAsync("/oauth/authorize?" + AuthorizeQuery(clientId, "login"));
        Assert.StartsWith("/login", stale.Headers.Location!.OriginalString);
    }

    // --- revocation (FU-087) -----------------------------------------------------

    [Fact]
    public async Task A_client_revokes_its_own_refresh_token()
    {
        await using var factory = NewFactory();
        const string clientId = "revoke-own";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);
        var refresh = await ConnectAsync(client, clientId);

        var revoke = await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refresh, ["token_type_hint"] = "refresh_token", ["client_id"] = clientId,
        }));

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RefreshAsync(client, clientId, refresh)).StatusCode);
    }

    [Fact]
    public async Task Clients_registered_before_revocation_are_given_it_once()
    {
        await using var factory = NewFactory();
        const string clientId = "revoke-backfill";
        await SeedClientAsync(factory, clientId, revocation: false);
        using var scope = factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        await OAuthGrants.GrantRevocationToEveryClientAsync(applications, CancellationToken.None);
        var again = await OAuthGrants.GrantRevocationToEveryClientAsync(applications, CancellationToken.None);

        var application = await applications.FindByClientIdAsync(clientId);
        Assert.True(await applications.HasPermissionAsync(application!, Permissions.Endpoints.Revocation));
        Assert.Equal(0, again);
    }

    [Fact]
    public async Task A_client_registered_now_may_revoke_without_waiting_for_a_restart()
    {
        // The boot backfill covers clients that existed at startup; one registered after it
        // needs the permission from registration itself.
        await using var factory = NewFactory().WithConfig("Api:Mcp:MaxDynamicClients", "1000");
        using var client = NewClient(factory);
        var registered = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "registered-after-boot",
            redirect_uris = new[] { RedirectUri },
            token_endpoint_auth_method = "none",
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;

        using var scope = factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applications.FindByClientIdAsync(clientId);
        Assert.True(await applications.HasPermissionAsync(application!, Permissions.Endpoints.Revocation));
    }

    // --- the user's own list (FU-087) ---------------------------------------------

    [Fact]
    public async Task A_user_lists_and_disconnects_their_own_grants_and_no_one_elses()
    {
        await using var factory = NewFactory();
        const string clientId = "grants-list";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);
        var refresh = await ConnectAsync(client, clientId);

        // Someone else's grant for the same app.
        string othersGrant;
        using (var scope = factory.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var app = await applications.FindByClientIdAsync(clientId);
            var created = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
            {
                ApplicationId = await applications.GetIdAsync(app!),
                Subject = Guid.NewGuid().ToString(),
                Status = Statuses.Valid,
                Type = AuthorizationTypes.Permanent,
                CreationDate = DateTimeOffset.UtcNow,
                Scopes = { "coffer.read" },
            });
            othersGrant = (await authorizations.GetIdAsync(created))!;
        }

        var list = await client.GetFromJsonAsync<List<Coffer.Api.Endpoints.OAuthGrantsEndpoints.OAuthGrantSummary>>("/api/account/oauth-grants");
        var mine = Assert.Single(list!, g => g.AppName == "App " + clientId);
        Assert.False(mine.CanWrite);
        Assert.Equal(OAuthGrants.MaxAge, mine.ExpiresAt - mine.ConnectedAt);
        Assert.DoesNotContain(list!, g => g.Id == othersGrant);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.DeleteAsync($"/api/account/oauth-grants/{othersGrant}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/account/oauth-grants/{mine.Id}")).StatusCode);

        // Disconnected means its tokens are dead too.
        Assert.Equal(HttpStatusCode.BadRequest, (await RefreshAsync(client, clientId, refresh)).StatusCode);
        Assert.DoesNotContain(
            (await client.GetFromJsonAsync<List<Coffer.Api.Endpoints.OAuthGrantsEndpoints.OAuthGrantSummary>>("/api/account/oauth-grants"))!,
            g => g.Id == mine.Id);
    }

    [Fact]
    public async Task A_grant_past_its_age_is_not_listed()
    {
        await using var factory = NewFactory();
        const string clientId = "grants-list-aged";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);
        await ConnectAsync(client, clientId);
        const string path = "/api/account/oauth-grants";
        Assert.Single((await client.GetFromJsonAsync<List<Coffer.Api.Endpoints.OAuthGrantsEndpoints.OAuthGrantSummary>>(path))!,
            g => g.AppName == "App " + clientId);

        await AgeGrantsPastMaxAsync(factory, clientId);

        // Not revoked yet (nothing has touched it since), and still not listed: it can no
        // longer do anything, so it is not a connection to show.
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<Coffer.Api.Endpoints.OAuthGrantsEndpoints.OAuthGrantSummary>>(path))!,
            g => g.AppName == "App " + clientId);
    }

    // --- coffer.read at /mcp (FU-095) --------------------------------------------

    [Fact]
    public async Task A_client_that_asks_only_to_write_is_granted_read_too()
    {
        await using var factory = NewFactory();
        const string clientId = "write-only-ask";
        await SeedClientAsync(factory, clientId);
        using var client = NewClient(factory);
        await SignInAsync(client);

        await ConnectAsync(client, clientId, scope: McpScopes.Write);

        using var scope = factory.Services.CreateScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var grant = Assert.Single(await GrantsAsync(scope.ServiceProvider, clientId));
        var scopes = await authorizations.GetScopesAsync(grant);
        Assert.Contains(McpScopes.Write, scopes);
        Assert.Contains(McpScopes.Read, scopes);
    }

    private static HttpRequestMessage Initialize(string bearer)
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    [Fact]
    public async Task A_token_without_coffer_read_is_refused_at_mcp()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        async Task<string> IssueAsync(string scopes)
        {
            using var scope = factory.Services.CreateScope();
            var tokens = scope.ServiceProvider.GetRequiredService<McpTokensRepository>();
            var (plaintext, hash) = McpTokenService.Generate();
            await tokens.IssueAsync(ledger.UserId, "scope-test " + scopes, hash, scopes, null);
            return plaintext;
        }

        var writeOnly = await client.SendAsync(Initialize(await IssueAsync(McpScopes.Write)));
        var reader = await client.SendAsync(Initialize(await IssueAsync(McpScopes.Read)));

        // 403 insufficient_scope naming the scope, which a client re-authorizes on; a 401
        // would call a valid token invalid (RFC 6750 §3.1).
        Assert.Equal(HttpStatusCode.Forbidden, writeOnly.StatusCode);
        var challenge = writeOnly.Headers.WwwAuthenticate.ToString();
        Assert.Contains("error=\"insufficient_scope\"", challenge, StringComparison.Ordinal);
        Assert.Contains($"scope=\"{McpScopes.Read}\"", challenge, StringComparison.Ordinal);
        Assert.Contains("resource_metadata=", challenge, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, reader.StatusCode);
    }
}
