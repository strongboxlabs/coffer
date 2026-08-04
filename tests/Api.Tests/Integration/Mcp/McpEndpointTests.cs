using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The MCP endpoint's security boundary (ADR-0063 §D7): <c>/mcp</c> is gated by
/// the authorization policy, so an unauthenticated JSON-RPC call is rejected
/// before any tool runs. Tool correctness is proven by the reporting/investment
/// repository tests; the end-to-end protocol round-trip is validated against a
/// real MCP client on dev. This test locks the gate so a future refactor can't
/// silently expose the financial tools anonymously.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpEndpointTests
{
    private readonly PostgresFixture _fixture;

    public McpEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly object InitializeRequest = new
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
    };

    private static HttpRequestMessage NewInitialize()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(InitializeRequest),
        };
        // Streamable-HTTP requires the client to accept both JSON and SSE.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    [Fact]
    public async Task Unauthenticated_call_is_rejected()
    {
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var response = await client.SendAsync(NewInitialize());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_call_passes_the_gate()
    {
        // Dev-auth factory adds X-Dev-Auth, so the request is authenticated.
        // We only assert it clears the authorization gate (not 401/403) — the
        // protocol body itself is the real client's job to validate on dev.
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(NewInitialize());

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
