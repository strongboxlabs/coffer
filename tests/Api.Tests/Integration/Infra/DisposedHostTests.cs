using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// A test host that has been disposed is garbage: nothing process-wide still holds it.
/// </summary>
/// <remarks>
/// <para>Every endpoint test boots its own host, so a host that outlives its dispose is
/// kept for the rest of the run, with its container, caches and pipeline. A run of a
/// few hundred tests then grows by a host per test, and every full collection walks the
/// whole pile, so each later test runs slower than it does alone.</para>
/// <para>What holds a disposed host is something process-wide that the host fills and
/// never empties: a timer it starts and does not stop (the framework rate limiter's
/// replenishment timer, see <see cref="PipelineRateLimiters"/>, or the HTTP client
/// factory's handler-expiry and cleanup timers, see <see cref="HttpClientFactoryTimers"/>),
/// or a static cache whose entry captures the host's services (the MCP tools' descriptor
/// cache, see <see cref="McpToolDescriptorCache"/>). <see cref="ApiFactory"/> releases all
/// three when it disposes a host. Both cases are booted: the MCP server is registered only
/// when it is enabled. Each host also creates a named HTTP client, so its factory has a
/// handler whose expiry timer is running when the host is disposed; the hosts run the
/// default handler lifetime, as every test host does.</para>
/// <para>Two holders let go on their own and are kept out of the judgement. Work the host
/// started can still be unwinding as it is disposed, so the host is given time to settle,
/// less than a handler lifetime. The first host a process builds can be held for the life
/// of the process by singletons created during its build, which keep that build's
/// execution context; that is one host, not one per test, so each case boots a host first
/// and judges the one after it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DisposedHostTests : DatabaseTest
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    public DisposedHostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_disposed_host_is_collected(bool mcp)
    {
        _ = await BootAndDisposeAsync(mcp);
        var services = await BootAndDisposeAsync(mcp);

        var settling = System.Diagnostics.Stopwatch.StartNew();
        while (services.IsAlive && settling.Elapsed < Settle)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (services.IsAlive) await Task.Delay(250);
        }

        Assert.False(services.IsAlive,
            $"A disposed host's service provider is still reachable {Settle.TotalSeconds:0} s after "
            + "its dispose: something process-wide that the host filled still holds it.");
    }

    /// <summary>Boots a host, serves one request, disposes it, and returns a weak
    /// reference to its root service provider. Not inlined, so no local of the caller's
    /// frame keeps the host alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> BootAndDisposeAsync(bool mcp)
    {
        var factory = new ApiFactory(Db);
        if (mcp) factory.WithMcpEnabled();
        using (var client = factory.CreateClient())
        {
            using var response = await client.GetAsync(
                new Uri(Coffer.Api.Endpoints.HealthEndpoints.LivenessPath, UriKind.Relative));
            response.EnsureSuccessStatusCode();
        }
        // A named client, as the app's own outbound calls make: its handler is what the
        // HTTP client factory tracks, and its expiry timer is what would hold the host.
        using (var outbound = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DisposedHostTests)))
        {
            Assert.NotNull(outbound);
        }
        var services = new WeakReference(factory.Services);
        await factory.DisposeAsync();
        return services;
    }
}
