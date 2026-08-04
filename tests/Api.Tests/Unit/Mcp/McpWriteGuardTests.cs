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

    private static List<MethodInfo> WriteTools() =>
        typeof(McpWriteTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Task<McpWriteResult>))
            .ToList();

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
