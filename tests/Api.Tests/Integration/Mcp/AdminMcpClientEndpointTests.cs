using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The admin OAuth-client management surface (ADR-0081 D5, <c>/api/admin/mcp/clients</c>):
/// list, per-client write grant, revoke, and prune of the DCR clients that can reach
/// <c>/mcp</c>. Clients are created through the real DCR endpoint, then managed via the
/// admin endpoints (dev-auth is stamped admin). The client cap is lifted so the shared
/// test store doesn't hit it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminMcpClientEndpointTests
{
    private readonly PostgresFixture _fixture;

    public AdminMcpClientEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private ApiFactory Factory() =>
        new ApiFactory(_fixture).WithMcpEnabled().WithConfig("Api:Mcp:MaxDynamicClients", "1000");

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
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = Factory().WithoutDevAuth();
        var cookie = await alice.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

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
}
