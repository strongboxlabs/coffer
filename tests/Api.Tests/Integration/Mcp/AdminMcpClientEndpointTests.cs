using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenIddict.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The admin OAuth-client registration surface (ADR-0081 D5, <c>/api/admin/mcp/clients</c>):
/// list, revoke, and prune of the DCR clients that can reach <c>/mcp</c>. A registration is
/// shared by everyone who connects through it, so it carries no name of its own: who holds a
/// connection, and naming one, is <c>McpConnectionsTests</c>. Clients are created through the
/// real DCR endpoint, then managed via the admin endpoints (dev-auth is stamped admin). The
/// client cap is lifted so the shared test store doesn't hit it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminMcpClientEndpointTests : DatabaseTest
{
    public AdminMcpClientEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private ApiFactory Factory() =>
        new ApiFactory(Db).WithMcpEnabled().WithConfig("Api:Mcp:MaxDynamicClients", "1000");

    private static async Task<string> RegisterClientAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = name,
            redirect_uris = new[] { "https://example.com/cb" },
            token_endpoint_auth_method = "none",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("client_id").GetString()!;
    }

    private static Task<List<McpClientDto>?> ListClientsAsync(HttpClient client) =>
        client.GetFromJsonAsync<List<McpClientDto>>("/api/admin/mcp/clients");

    [Fact]
    public async Task Non_admin_cookie_cannot_list_clients()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = Factory().WithoutDevAuth();
        var cookie = await alice.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        var resp = await client.GetAsync("/api/admin/mcp/clients");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Client_lists_then_revoke_removes()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();   // dev-auth = admin

        var name = $"lifecycle-{Guid.NewGuid():N}";
        var clientId = await RegisterClientAsync(client, name);

        // Appears in the list.
        var mine = Assert.Single((await ListClientsAsync(client))!, c => c.ClientId == clientId);
        Assert.Equal(name, mine.DisplayName);

        // Revoke → gone from the list.
        var revoke = await client.DeleteAsync($"/api/admin/mcp/clients/{clientId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.DoesNotContain((await ListClientsAsync(client))!, c => c.ClientId == clientId);
    }

    [Fact]
    public async Task Prune_removes_a_client_with_no_authorizations()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var clientId = await RegisterClientAsync(client, $"prune-{Guid.NewGuid():N}");

        // Freshly registered, never consented → no authorizations → pruned.
        var prune = await client.PostAsync("/api/admin/mcp/clients/prune", content: null);
        Assert.Equal(HttpStatusCode.OK, prune.StatusCode);

        Assert.DoesNotContain((await ListClientsAsync(client))!, c => c.ClientId == clientId);
    }

    // --- registration time ---------------------------------------------------------------

    [Fact]
    public async Task A_client_registered_through_dcr_carries_when_it_registered()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var clientId = await RegisterClientAsync(client, $"stamped-{Guid.NewGuid():N}");

        var registeredAt = Assert.Single((await ListClientsAsync(client))!, c => c.ClientId == clientId).RegisteredAt;
        Assert.NotNull(registeredAt);
        Assert.InRange(registeredAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task A_client_registered_before_the_time_was_recorded_has_none_rather_than_a_made_up_one()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var clientId = $"unstamped-{Guid.NewGuid():N}";
        await SeedClientAsync(factory, clientId);   // no registered-at property, as before this change

        Assert.Null(Assert.Single((await ListClientsAsync(client))!, c => c.ClientId == clientId).RegisteredAt);
    }

    // --- live connections: the one rule for the count, the list and prune -----------------

    [Fact]
    public async Task A_clients_count_is_its_live_connections_only()
    {
        // The count used to include revoked and past-their-age authorizations, so a client
        // read "1 connection" while the connection list, which shows live ones, had none.
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var clientId = $"count-{Guid.NewGuid():N}";
        await SeedClientAsync(factory, clientId);
        await SeedAuthorizationAsync(factory, clientId, alice.UserId, Statuses.Valid, DateTimeOffset.UtcNow);
        await SeedAuthorizationAsync(factory, clientId, alice.UserId, Statuses.Revoked, DateTimeOffset.UtcNow);
        await SeedAuthorizationAsync(factory, clientId, alice.UserId, Statuses.Valid, PastItsAge());

        Assert.Equal(1, Assert.Single((await ListClientsAsync(client))!, c => c.ClientId == clientId).LiveConnections);
    }

    [Fact]
    public async Task Prune_removes_a_client_whose_sign_ins_are_all_dead_and_keeps_one_with_a_live_one()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var dead = $"dead-{Guid.NewGuid():N}";
        var live = $"live-{Guid.NewGuid():N}";
        await SeedClientAsync(factory, dead);
        var revoked = await SeedAuthorizationAsync(factory, dead, alice.UserId, Statuses.Revoked, DateTimeOffset.UtcNow);
        var expired = await SeedAuthorizationAsync(factory, dead, alice.UserId, Statuses.Valid, PastItsAge());
        await SeedClientAsync(factory, live);
        await SeedAuthorizationAsync(factory, live, alice.UserId, Statuses.Valid, DateTimeOffset.UtcNow);

        var prune = await client.PostAsync("/api/admin/mcp/clients/prune", content: null);
        Assert.Equal(HttpStatusCode.OK, prune.StatusCode);

        var after = (await ListClientsAsync(client))!;
        Assert.DoesNotContain(after, c => c.ClientId == dead);
        Assert.Contains(after, c => c.ClientId == live);
        // Its dead authorizations went with it, rather than being orphaned.
        using var scope = factory.Services.CreateScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        Assert.Null(await authorizations.FindByIdAsync(revoked));
        Assert.Null(await authorizations.FindByIdAsync(expired));
    }

    private static DateTimeOffset PastItsAge() =>
        DateTimeOffset.UtcNow - OAuthGrants.MaxAge - TimeSpan.FromDays(1);

    /// <summary>A client made directly through the manager: no registered-at property.</summary>
    private static async Task SeedClientAsync(ApiFactory factory, string clientId)
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = "App " + clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            RedirectUris = { new Uri("https://example.com/cb") },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + "coffer.read",
            },
        });
    }

    private static async Task<string> SeedAuthorizationAsync(
        ApiFactory factory, string clientId, Guid userId, string status, DateTimeOffset createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var app = await applications.FindByClientIdAsync(clientId);
        var created = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = await applications.GetIdAsync(app!),
            Subject = userId.ToString(),
            Status = status,
            Type = AuthorizationTypes.Permanent,
            CreationDate = createdAt,
            Scopes = { "coffer.read" },
        });
        return (await authorizations.GetIdAsync(created))!;
    }
}
