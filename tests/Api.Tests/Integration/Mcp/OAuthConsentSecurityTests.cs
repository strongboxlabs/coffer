using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenIddict.Abstractions;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The MCP authorization server after the 2026-10-12 security audit (H1, M1, H2).
/// </summary>
/// <remarks>
/// H1: anonymous client registration accepted <c>javascript://localhost/…</c> (the check
/// was "https OR IsLoopback", and IsLoopback reads only the host), and with form_post,
/// approving consent ran that script on Coffer's origin — confirmed by test before the
/// fix. M1: a post to /oauth/authorize without decision=deny issued a code, so a page on
/// a sibling subdomain could get one; and the consent screen showed a client name taken
/// from the link. H2: a disabled user's app kept refreshing.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OAuthConsentSecurityTests
{
    private readonly PostgresFixture _fixture;

    public OAuthConsentSecurityTests(PostgresFixture fixture) => _fixture = fixture;

    private const string RedirectUri = "https://example.com/callback";
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    private ApiFactory Factory() =>
        new ApiFactory(_fixture).WithMcpEnabled().WithConfig("Api:Cookie:RequireHttps", "false");

    private static async Task SeedClientAsync(
        WebApplicationFactory<Program> factory, string clientId, string redirectUri = RedirectUri,
        string displayName = "Registered App")
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId) is not null) return;
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = displayName,
            RedirectUris = { new Uri(redirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + "coffer.read",
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        });
    }

    private static async Task<HttpClient> SignedInClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/dev-login", content: null)).StatusCode);
        return client;
    }

    private static Dictionary<string, string> AuthorizeParameters(
        string clientId, string redirectUri = RedirectUri, string codeChallenge = CodeChallenge) => new()
    {
        ["response_type"] = "code",
        ["client_id"] = clientId,
        ["redirect_uri"] = redirectUri,
        ["scope"] = "coffer.read",
        ["code_challenge"] = codeChallenge,
        ["code_challenge_method"] = "S256",
        ["state"] = "xyz",
    };

    private static string Query(Dictionary<string, string> parameters) =>
        string.Join('&', parameters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    /// <summary>GET authorize with no prior consent; return the redirect and its ticket.</summary>
    private static async Task<(string Location, string Ticket)> ConsentRedirectAsync(
        HttpClient client, Dictionary<string, string> parameters, string extraQuery = "")
    {
        var get = await client.GetAsync("/oauth/authorize?" + Query(parameters) + extraQuery);
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        var location = get.Headers.Location!.OriginalString;
        Assert.StartsWith("/oauth/consent?", location);
        var ticket = QueryHelpers.ParseQuery(location[location.IndexOf('?')..])["consent_ticket"].ToString();
        return (location, ticket);
    }

    private static Task<HttpResponseMessage> PostDecisionAsync(
        HttpClient client, Dictionary<string, string> parameters, string? decision, string? ticket)
    {
        var form = new Dictionary<string, string>(parameters);
        if (decision is not null) form["decision"] = decision;
        if (ticket is not null) form["consent_ticket"] = ticket;
        return client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form));
    }

    private static bool CarriesACode(HttpResponseMessage response) =>
        response.Headers.Location?.OriginalString.Contains("code=", StringComparison.Ordinal) == true;

    // --- H1: redirect URIs and response modes -------------------------------

    [Theory]
    [InlineData("javascript://localhost/%0Aalert(document.domain)", false)]
    [InlineData("javascript://127.0.0.1/%0Aalert(1)", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("ftp://localhost/callback", false)]
    [InlineData("http://example.com/callback", false)]
    [InlineData("https://example.com/callback", true)]
    [InlineData("http://127.0.0.1:33418/callback", true)]
    [InlineData("http://localhost/callback", true)]
    public async Task Registration_accepts_only_https_or_http_on_localhost(string redirectUri, bool accepted)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "probe",
            redirect_uris = new[] { redirectUri },
            token_endpoint_auth_method = "none",
        });

        Assert.Equal(accepted ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Discovery_no_longer_offers_form_post()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var discovery = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        var modes = discovery.GetProperty("response_modes_supported").EnumerateArray()
            .Select(m => m.GetString()).ToList();
        Assert.Contains("query", modes);
        Assert.DoesNotContain("form_post", modes);
    }

    [Fact]
    public async Task Authorize_refuses_a_client_registered_with_a_javascript_redirect_before_the_rule()
    {
        // Seeded through the manager, as a registration made before the fix would be.
        const string evil = "javascript://localhost/%0Aalert(1)";
        await using var factory = Factory();
        await SeedClientAsync(factory, "legacy-js-client", evil);
        using var client = await SignedInClientAsync(factory);

        var response = await client.GetAsync("/oauth/authorize?" + Query(AuthorizeParameters("legacy-js-client", evil)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(CarriesACode(response));
    }

    // --- M1: consent needs its ticket ----------------------------------------

    [Fact]
    public async Task Allow_without_a_ticket_issues_no_code()
    {
        await using var factory = Factory();
        await SeedClientAsync(factory, "consent-no-ticket");
        using var client = await SignedInClientAsync(factory);

        var response = await PostDecisionAsync(client, AuthorizeParameters("consent-no-ticket"), "allow", ticket: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(CarriesACode(response));
    }

    [Fact]
    public async Task A_missing_decision_is_not_an_allow()
    {
        await using var factory = Factory();
        await SeedClientAsync(factory, "consent-no-decision");
        using var client = await SignedInClientAsync(factory);
        var parameters = AuthorizeParameters("consent-no-decision");
        var (_, ticket) = await ConsentRedirectAsync(client, parameters);

        var response = await PostDecisionAsync(client, parameters, decision: null, ticket);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(CarriesACode(response));
    }

    [Fact]
    public async Task A_ticket_approves_one_request_only()
    {
        await using var factory = Factory();
        await SeedClientAsync(factory, "consent-replay");
        using var client = await SignedInClientAsync(factory);
        var parameters = AuthorizeParameters("consent-replay");
        var (_, ticket) = await ConsentRedirectAsync(client, parameters);

        var first = await PostDecisionAsync(client, parameters, "allow", ticket);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.True(CarriesACode(first));

        var replay = await PostDecisionAsync(client, parameters, "allow", ticket);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task A_ticket_cannot_approve_a_different_code_challenge()
    {
        // Swapping in an attacker's PKCE challenge after the user looked would hand the
        // code to whoever holds that verifier.
        await using var factory = Factory();
        await SeedClientAsync(factory, "consent-swap");
        using var client = await SignedInClientAsync(factory);
        var (_, ticket) = await ConsentRedirectAsync(client, AuthorizeParameters("consent-swap"));

        var swapped = AuthorizeParameters("consent-swap", codeChallenge: "aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789-_aBcDe");
        var response = await PostDecisionAsync(client, swapped, "allow", ticket);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(CarriesACode(response));
    }

    [Fact]
    public async Task The_consent_page_shows_what_the_server_registered_not_what_the_link_says()
    {
        await using var factory = Factory();
        await SeedClientAsync(factory, "consent-names", displayName: "Registered App");
        using var client = await SignedInClientAsync(factory);

        var (location, ticket) = await ConsentRedirectAsync(
            client, AuthorizeParameters("consent-names"),
            extraQuery: "&client_name=Claude&consent_ticket=forged");

        // The link's own client_name and consent_ticket are dropped; ours is the only one.
        var query = QueryHelpers.ParseQuery(location[location.IndexOf('?')..]);
        Assert.False(query.ContainsKey("client_name"));
        Assert.Single(query["consent_ticket"]);
        Assert.NotEqual("forged", ticket);

        var info = await client.GetFromJsonAsync<JsonElement>($"/api/oauth/consent/{ticket}");
        Assert.Equal("Registered App", info.GetProperty("clientName").GetString());
        Assert.Equal("example.com", info.GetProperty("redirectHost").GetString());
    }

    [Fact]
    public async Task Another_user_cannot_read_a_consent_ticket()
    {
        // Two real sessions, dev auth off: with it on, every request would also carry
        // the dev system user, who owns the ticket, and the check would prove nothing.
        await using var factory = Factory().WithoutDevAuth();
        await SeedClientAsync(factory, "consent-other-user");
        using var owner = await PlainCookieClientAsync(factory, await SyntheticLedger.CreateAsync(_fixture));
        using var stranger = await PlainCookieClientAsync(factory, await SyntheticLedger.CreateAsync(_fixture));
        var (_, ticket) = await ConsentRedirectAsync(owner, AuthorizeParameters("consent-other-user"));

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/oauth/consent/{ticket}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/oauth/consent/{ticket}")).StatusCode);
    }

    /// <summary>A cookie session for <paramref name="user"/>; RequireHttps is off here, so
    /// the cookie keeps its bare name.</summary>
    private static async Task<HttpClient> PlainCookieClientAsync(ApiFactory factory, SyntheticLedger user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={await user.IssueSessionCookieAsync()}");
        return client;
    }

    // --- H2: a disabled user's app stops refreshing ---------------------------

    [Fact]
    public async Task A_disabled_users_app_can_no_longer_refresh()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = Factory();
        await SeedClientAsync(factory, "disabled-refresh");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={await user.IssueSessionCookieAsync()}");

        var parameters = AuthorizeParameters("disabled-refresh");
        var (_, ticket) = await ConsentRedirectAsync(client, parameters);
        var allowed = await PostDecisionAsync(client, parameters, "allow", ticket);
        var code = QueryHelpers.ParseQuery(allowed.Headers.Location!.Query)["code"].ToString();
        var token = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
            ["client_id"] = "disabled-refresh", ["code_verifier"] = CodeVerifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var refreshToken = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;

        // The flag alone, without the revocation that disabling also does: the
        // exchange itself must refuse a disabled subject.
        await using (var db = _fixture.NewDbContext())
            await db.Users.Where(u => u.Id == user.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, _ => true));

        var refreshed = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = "disabled-refresh",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, refreshed.StatusCode);
        Assert.Equal("invalid_grant",
            (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
