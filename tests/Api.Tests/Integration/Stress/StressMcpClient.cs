using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>MCP over streamable HTTP for the stress lane, through the production authentication path.</summary>
internal static class StressMcpClient
{
    private static int _rpcId;

    /// <summary>The next JSON-RPC request id, unique across the lane's clients.</summary>
    public static int NextId() => Interlocked.Increment(ref _rpcId);

    /// <summary>
    /// A client holding a connected-apps bearer for <paramref name="ledger"/>'s user and
    /// an initialized MCP session. The transport is stateful, so every later call carries
    /// the session id initialize hands back.
    /// </summary>
    public static async Task<HttpClient> CreateAsync(
        TestDatabase database, ApiFactory factory, SyntheticLedger ledger, string tokenName)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(ledger);
        var (token, hash) = McpTokenService.Generate();
        await new McpTokensRepository(database.NewServiceFactory(), TimeProvider.System)
            .IssueAsync(ledger.UserId, tokenName, hash, McpScopes.Read, expiresAt: null);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Streamable HTTP requires the client to accept both JSON and SSE.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var init = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = NextId(),
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "coffer-stress", version = "1.0" },
            },
        });
        init.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Mcp-Session-Id", init.Headers.GetValues("Mcp-Session-Id").Single());

        using var initialized = await client.PostAsJsonAsync(
            "/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
        initialized.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A <c>tools/call</c> request for <paramref name="tool"/>.</summary>
    public static HttpRequestMessage ToolCall(string tool, object arguments) =>
        new(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = NextId(),
                method = "tools/call",
                @params = new { name = tool, arguments },
            }),
        };
}
