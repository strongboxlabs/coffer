using System.Reflection;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The MCP tools' reflection descriptors that Microsoft.Extensions.AI caches under the
/// MCP SDK's process-wide serializer options, which <see cref="ApiFactory"/> drops when it
/// disposes a host.
/// </summary>
/// <remarks>
/// <para>Each tool the host creates caches its descriptor in a static table keyed by the
/// tools' serializer options, the SDK's process-wide default instance, and the entry's key
/// captures the host's services. A key that lives for the process keeps its entries, so the
/// cache keeps every host that registered tools: one set of entries per host, none per
/// session or call. A production process builds one host and keeps it for its life; a test
/// run builds a host per test. Dropping the cached entries under the default options when a
/// host is disposed lets the host go; a host still running holds its tools, and the tools
/// their descriptors, so it loses nothing. The production registration is unchanged.</para>
/// <para>The table is the library's own static field. When it is renamed,
/// <see cref="Release"/> fails rather than leave the hosts held, and
/// <c>DisposedHostTests</c> fails on a host that is still held.</para>
/// </remarks>
internal static class McpToolDescriptorCache
{
    /// <summary>Drops every descriptor cached under the SDK's default serializer options.</summary>
    /// <exception cref="InvalidOperationException">The library's cache is not where it was.</exception>
    public static void Release()
    {
        var descriptor = typeof(Microsoft.Extensions.AI.AIFunctionFactory)
            .GetNestedType("ReflectionAIFunctionDescriptor", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Microsoft.Extensions.AI has no ReflectionAIFunctionDescriptor.");
        var cache = descriptor.GetField("_descriptorCache", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Microsoft.Extensions.AI's tool descriptor cache is not where it was.");
        var remove = cache.GetType().GetMethod("Remove", [typeof(System.Text.Json.JsonSerializerOptions)])
            ?? throw new InvalidOperationException($"The tool descriptor cache, a {cache.GetType()}, has no Remove.");
        remove.Invoke(cache, [ModelContextProtocol.McpJsonUtilities.DefaultOptions]);
    }
}
