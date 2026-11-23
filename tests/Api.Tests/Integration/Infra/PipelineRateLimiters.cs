using System.Reflection;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The endpoint limiters of ASP.NET Core's rate-limiting middleware in a started test
/// host's request pipeline, which <see cref="ApiFactory"/> disposes with the host.
/// </summary>
/// <remarks>
/// <para>The middleware builds a partitioned limiter for the endpoint policies and never
/// disposes it. That limiter runs a process-wide replenishment timer, and the timer holds
/// the limiter, the limiter's partitioner holds the middleware, and the middleware holds
/// the rest of the pipeline and through it the host. A production process builds one host
/// and keeps it for its life, so nothing is lost there; a test run builds a host per test
/// and would keep every one of them. Disposing the limiter stops its timer, which lets the
/// host go. The production rate limiting is unchanged: the test host runs the same
/// middleware, policies and limits.</para>
/// <para>The middleware is not reachable from the host's services, so it is found in the
/// built pipeline: from the test server's application, through each middleware's next
/// delegate. A middleware is reached as a delegate's target, a closure's field, or a
/// wrapper's inner middleware. The members read are the framework's own; when one is
/// renamed, <see cref="Find"/> fails rather than leave the limiter running, and
/// <c>DisposedHostTests</c> fails on a host that is still held.</para>
/// </remarks>
internal static class PipelineRateLimiters
{
    private const BindingFlags InstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private const string MiddlewareTypeName = "Microsoft.AspNetCore.RateLimiting.RateLimitingMiddleware";

    /// <summary>The endpoint limiter of every rate-limiting middleware in the started
    /// server's pipeline; at least one, or the pipeline was not walked as expected.</summary>
    /// <exception cref="InvalidOperationException">No rate-limiting middleware was found.</exception>
    public static IReadOnlyList<PartitionedRateLimiter<HttpContext>> Find(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var testServer = server as TestServer
            ?? throw new InvalidOperationException($"The test host's server is a {server.GetType()}, not a TestServer.");

        var wrapper = Property(testServer, "Application");
        var application = Field(wrapper, "_application");
        var pipeline = Field(application, "_application") as RequestDelegate
            ?? throw new InvalidOperationException("The test server's application has no request pipeline.");

        var limiters = new List<PartitionedRateLimiter<HttpContext>>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<object>();
        pending.Enqueue(pipeline);
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            if (!seen.Add(node)) continue;

            if (node is Delegate callback)
            {
                foreach (var single in callback.GetInvocationList())
                    if (single.Target is { } target) pending.Enqueue(target);
                continue;
            }

            if (node.GetType().FullName == MiddlewareTypeName)
            {
                limiters.Add(Field(node, "_endpointLimiter") as PartitionedRateLimiter<HttpContext>
                    ?? throw new InvalidOperationException("The rate-limiting middleware has no endpoint limiter."));
            }

            foreach (var value in FieldValues(node))
                if (IsPipelineNode(value)) pending.Enqueue(value);
        }

        if (limiters.Count == 0)
            throw new InvalidOperationException(
                "The test host's pipeline has no rate-limiting middleware where one was expected: its "
                + "endpoint limiter would keep the host after it is disposed. Walked: "
                + string.Join(", ", seen.Select(n => n.GetType().Name).Distinct(StringComparer.Ordinal)));
        return limiters;
    }

    /// <summary>A delegate, a compiler-generated closure, or a middleware (or a
    /// middleware's inner implementation): what links one middleware to the next.</summary>
    private static bool IsPipelineNode(object value)
    {
        if (value is Delegate) return true;
        var type = value.GetType();
        if (type.IsValueType || value is string) return false;
        return type.Name.Contains('<', StringComparison.Ordinal)
            || type.Name.EndsWith("Middleware", StringComparison.Ordinal)
            || type.Name.EndsWith("MiddlewareImpl", StringComparison.Ordinal)
            || type.Name.EndsWith("MiddlewareInternal", StringComparison.Ordinal);
    }

    private static IEnumerable<object> FieldValues(object node)
    {
        for (var type = node.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(InstanceFields))
                if (!field.FieldType.IsValueType && field.GetValue(node) is { } value)
                    yield return value;
    }

    private static object Field(object owner, string name)
    {
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, InstanceFields) is { } field)
                return field.GetValue(owner)
                    ?? throw new InvalidOperationException($"{owner.GetType()}.{name} is null.");
        throw new InvalidOperationException($"{owner.GetType()} has no field {name}.");
    }

    private static object Property(object owner, string name) =>
        (owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{owner.GetType()} has no property {name}."))
        .GetValue(owner)
        ?? throw new InvalidOperationException($"{owner.GetType()}.{name} is null.");
}
