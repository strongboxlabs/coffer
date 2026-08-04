using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The MCP bearer-token spine end-to-end (ADR-0063 §D7): an interactive user
/// mints a token, the token authenticates <c>/mcp</c> (and only /mcp), and a
/// revoke kills it. Proves mint → SHA-256 store → present-as-bearer → hash →
/// validate → principal, plus the revocation path. Runs with dev-auth OFF so the
/// only thing that can authenticate /mcp is the bearer scheme itself.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpTokensEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public McpTokensEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    private static HttpRequestMessage McpInitialize(string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
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

    [Fact]
    public async Task Mint_then_authenticate_mcp_then_revoke()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();

        var cookie = await user.IssueSessionCookieAsync();
        using var session = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        session.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

        // Mint — plaintext returned once.
        var createResponse = await session.PostAsJsonAsync(
            "/api/account/mcp-tokens", new CreateMcpTokenRequest("Claude Desktop"));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var issued = (await createResponse.Content.ReadFromJsonAsync<IssuedMcpToken>())!;
        Assert.StartsWith("coffer_mcp_", issued.Token);
        Assert.Equal("coffer.read", issued.Scopes);

        // It shows up in the list (metadata only — no plaintext field exists there).
        var list = (await session.GetFromJsonAsync<List<McpTokenSummary>>("/api/account/mcp-tokens"))!;
        Assert.Single(list);
        Assert.Equal("Claude Desktop", list[0].Name);
        Assert.Equal(issued.Id, list[0].Id);

        // The bearer authenticates /mcp (no cookie on this client).
        using var bearer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var authed = await bearer.SendAsync(McpInitialize(issued.Token));
        Assert.NotEqual(HttpStatusCode.Unauthorized, authed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, authed.StatusCode);

        // Revoke, then the same bearer no longer authenticates.
        var deleteResponse = await session.DeleteAsync($"/api/account/mcp-tokens/{issued.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterRevoke = await bearer.SendAsync(McpInitialize(issued.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Garbage_bearer_is_rejected()
    {
        await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.SendAsync(McpInitialize("coffer_mcp_not-a-real-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_management_requires_an_interactive_session()
    {
        // The bearer scheme is not in the default policy, so even a valid MCP
        // token cannot reach the token-management surface — only a cookie can.
        await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync("/api/account/mcp-tokens");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
