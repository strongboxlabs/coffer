using System.Reflection;

using Coffer.Api.Contracts;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Unit.Mcp;

/// <summary>
/// ADR-0081 D1/D2 — the MCP write authorization choke point. These prove the two
/// things that make "read tokens can't write" true and keep it true:
/// <list type="number">
///   <item><see cref="McpWriteGuard.EnsureWritable"/> throws unless writes are
///   globally enabled AND the caller's token carries <c>coffer.write</c> — across
///   BOTH token shapes (OAuth scope claims and the manual-bearer "scope" claim);</item>
///   <item>every write tool on <see cref="McpWriteTools"/> is actually behind the
///   guard — it takes the guard first and calls it before touching any data — so a
///   future tool added without the guard fails the suite.</item>
/// </list>
/// Pure unit tests: the completeness check passes null repos, which the guard never
/// reaches because it throws first (a NullReferenceException instead would prove the
/// guard is not the first statement — and fail the test, which is the point).
/// </summary>
public sealed class McpWriteGuardTests
{
    // ---------- EnsureWritable: the authorization matrix ----------

    [Fact]
    public void Rejects_when_writes_globally_disabled_even_with_write_scope()
    {
        var guard = McpTestGuard.Build(writesEnabled: false,
            McpTestGuard.OAuthPrincipal(McpScopes.Read, McpScopes.Write));
        Assert.Throws<InvalidOperationException>(guard.EnsureWritable);
    }

    [Fact]
    public void Rejects_read_only_oauth_token()
    {
        var guard = McpTestGuard.Build(writesEnabled: true,
            McpTestGuard.OAuthPrincipal(McpScopes.Read));
        Assert.Throws<InvalidOperationException>(guard.EnsureWritable);
    }

    [Fact]
    public void Rejects_read_only_bearer_token()
    {
        var guard = McpTestGuard.Build(writesEnabled: true,
            McpTestGuard.BearerPrincipal(McpScopes.Read));
        Assert.Throws<InvalidOperationException>(guard.EnsureWritable);
    }

    [Fact]
    public void Rejects_unauthenticated_request()
    {
        var guard = McpTestGuard.Build(writesEnabled: true, user: null);
        Assert.Throws<InvalidOperationException>(guard.EnsureWritable);
    }

    [Fact]
    public void Allows_write_scoped_oauth_token()
    {
        var guard = McpTestGuard.Build(writesEnabled: true,
            McpTestGuard.OAuthPrincipal(McpScopes.Read, McpScopes.Write));
        guard.EnsureWritable();   // must not throw
    }

    [Fact]
    public void Allows_write_scoped_bearer_token()
    {
        var guard = McpTestGuard.Build(writesEnabled: true,
            McpTestGuard.BearerPrincipal(McpScopes.Read, McpScopes.Write));
        guard.EnsureWritable();   // must not throw
    }

    // ---------- Completeness: no write tool bypasses the guard ----------

    // Every tool the server exposes as a write, by its attribute — the set McpAuditFilter and
    // the ledger filter act on. Selecting by return type, as this once did, skipped the two
    // bulk recategorize tools, whose results are their own types.
    private static List<MethodInfo> WriteTools() =>
        typeof(McpWriteTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null)
            .ToList();

    [Fact]
    public void The_write_tool_catalog_is_every_tool_the_filters_treat_as_a_write()
    {
        var names = WriteTools()
            .Select(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!.Name)
            .ToHashSet();
        Assert.True(names.Count >= 15, $"found only {names.Count} write tools");   // not vacuous
        Assert.Equal(McpWriteTools.ToolNames.OrderBy(n => n), names.OrderBy(n => n));
    }

    [Fact]
    public void Every_write_tool_takes_the_guard_as_its_first_parameter()
    {
        var tools = WriteTools();
        Assert.NotEmpty(tools);   // a broken filter must not pass vacuously
        foreach (var m in tools)
        {
            var first = m.GetParameters().FirstOrDefault();
            Assert.True(first?.ParameterType == typeof(McpWriteGuard),
                $"{m.Name}: first parameter must be McpWriteGuard (ADR-0081 D1 — no write tool may bypass the guard).");
        }
    }

    [Fact]
    public void Every_write_tool_takes_the_ledger_it_writes_to_as_a_guid_named_ledgerId()
    {
        // The ledger filter reads exactly this argument and refuses a write without it
        // (FU-093); a tool that named it otherwise could never be called.
        foreach (var m in WriteTools())
            Assert.True(m.GetParameters().Any(p => p.Name == "ledgerId" && p.ParameterType == typeof(Guid)),
                $"{m.Name}: a write tool must take `Guid ledgerId` — McpLedgerWriteAuthFilter authorizes on it.");
    }

    // ---------- The ledger filter fails closed ----------

    private static Dictionary<string, System.Text.Json.JsonElement> Args(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;

    [Theory]
    [InlineData("{}")]                                   // no ledgerId
    [InlineData("{\"ledger\":\"" + "5b3d1f4e-9c1a-4e7b-8f0a-2d6c9e1b7a40" + "\"}")]   // misnamed
    [InlineData("{\"ledgerId\":\"not-a-guid\"}")]
    [InlineData("{\"ledgerId\":42}")]
    public async Task A_write_without_a_readable_ledgerId_is_refused(string arguments)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpLedgerWriteAuthFilter.AuthorizeAsync("delete_category", Args(arguments), authorizer: null, default));
        Assert.Contains("ledgerId", ex.Message);
    }

    [Fact]
    public async Task A_write_with_no_authorizer_to_ask_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpLedgerWriteAuthFilter.AuthorizeAsync("delete_category",
                Args("{\"ledgerId\":\"5b3d1f4e-9c1a-4e7b-8f0a-2d6c9e1b7a40\"}"), authorizer: null, default));
    }

    [Fact]
    public async Task A_read_tool_passes_without_a_ledger_check()
    {
        await McpLedgerWriteAuthFilter.AuthorizeAsync("list_ledgers", Args("{}"), authorizer: null, default);
    }

    public static IEnumerable<object[]> WriteToolNames() =>
        WriteTools().Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(WriteToolNames))]
    public async Task Every_write_tool_calls_the_guard_before_touching_data(string toolName)
    {
        var method = WriteTools().Single(m => m.Name == toolName);
        var readOnly = McpTestGuard.Build(writesEnabled: true,
            McpTestGuard.OAuthPrincipal(McpScopes.Read));

        // Guard first → it throws before any repo (null here) is dereferenced.
        var args = method.GetParameters()
            .Select((p, i) => i == 0
                ? (object?)readOnly
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();

        var task = (Task)method.Invoke(null, args)!;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
    }
}
