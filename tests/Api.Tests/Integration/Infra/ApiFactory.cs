using Coffer.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> bound to the API's
/// <c>Program</c> so test code drives a real HTTP pipeline. The factory
/// pre-sets the env vars <c>Program.cs</c> reads eagerly during host
/// build (so the registration-time dev-auth gate sees the test's
/// chosen mode), overlays runtime-bound config to point at the
/// per-collection <see cref="PostgresFixture"/>, and exposes
/// <see cref="WithService{TService}(Func{IServiceProvider, TService})"/>
/// for per-test DI substitution.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly PostgresFixture _postgres;
    private readonly List<Action<IServiceCollection>> _serviceOverrides = new();
    private readonly Dictionary<string, string?> _configOverrides = new();
    private bool _devAuthEnabled = true;
    private bool _mcpEnabled;

    public ApiFactory(PostgresFixture postgres)
    {
        _postgres = postgres;
        ApplyEnvOverrides();
    }

    /// <summary>
    /// Replace the registration of <typeparamref name="TService"/> with the
    /// supplied factory so the test can substitute a mock or stub. Returns
    /// the same factory for fluent chaining.
    /// </summary>
    public ApiFactory WithService<TService>(Func<IServiceProvider, TService> factory)
        where TService : class
    {
        _serviceOverrides.Add(services =>
        {
            services.RemoveAll(typeof(TService));
            services.AddScoped<TService>(factory);
        });
        return this;
    }

    /// <summary>
    /// Disable the dev-auth scheme for this factory. Tests that need to
    /// assert per-user authentication via the real cookie path (or that
    /// "no auth" returns 401) call this so dev-auth can't fall through
    /// and mask the real behaviour as the system user. Must be called
    /// before <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>
    /// — env vars are read by <c>Program.cs</c> at host build time.
    /// </summary>
    public ApiFactory WithoutDevAuth()
    {
        _devAuthEnabled = false;
        ApplyEnvOverrides();
        return this;
    }

    /// <summary>
    /// Enable the MCP server (ADR-0063) for this factory. MCP is off by default,
    /// so the <c>/mcp</c> endpoint + token-management endpoints + bearer scheme
    /// are only registered when this is set. Drives <c>COFFER_API__Mcp__Enabled</c>
    /// via env var because <c>Program.cs</c> reads the gate eagerly at host build
    /// (same mechanism as <see cref="WithoutDevAuth"/>). Must be called before
    /// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>.
    /// </summary>
    public ApiFactory WithMcpEnabled()
    {
        _mcpEnabled = true;
        ApplyEnvOverrides();
        return this;
    }

    /// <summary>
    /// Override a runtime-bound config key for this factory (applied after
    /// the in-memory defaults, so it wins). Must be called before
    /// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>.
    /// Used e.g. to set a low recovery-login rate limit so the limiter is
    /// exercisable.
    /// </summary>
    public ApiFactory WithConfig(string key, string? value)
    {
        _configOverrides[key] = value;
        return this;
    }

    /// <summary>
    /// Set the env vars that <c>Program.cs</c> reads eagerly during
    /// <see cref="WebApplicationFactory{TEntryPoint}"/>'s host build:
    /// <c>ASPNETCORE_ENVIRONMENT</c> for the registration-time
    /// <c>builder.Environment.IsDevelopment()</c> check, and
    /// <c>COFFER_API__DevAuth</c> for the matching <c>Api:DevAuth</c>
    /// option. The factory's <see cref="ConfigureWebHost"/> overlays
    /// (in-memory config) apply at <c>builder.Build()</c> which is
    /// AFTER the eager read; env vars are the only way to influence the
    /// pre-build code path.
    /// </summary>
    /// <remarks>
    /// Process-global mutation: safe inside the sequential
    /// <c>ApiCollection</c> (xUnit runs collection members one at a
    /// time). Each <see cref="ApiFactory"/> constructor + every
    /// <see cref="WithoutDevAuth"/> call rewrites these vars to known
    /// values, so a prior test's settings can't leak into the next
    /// test's host build.
    /// </remarks>
    private void ApplyEnvOverrides()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("COFFER_API__DevAuth",
            _devAuthEnabled ? "true" : "false");
        Environment.SetEnvironmentVariable("COFFER_API__Mcp__Enabled",
            _mcpEnabled ? "true" : "false");
        // ADR-0026 — Program.cs reads COFFER_MASTER_KEK_BASE64 eagerly
        // at startup and fails fast if missing. Pin a deterministic
        // test KEK so every host build starts. 32 zero bytes; the
        // test container doesn't store anything we care about
        // protecting.
        Environment.SetEnvironmentVariable("COFFER_MASTER_KEK_BASE64",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
    }

    /// <summary>
    /// When dev-auth is enabled for this factory, opt every request in via the
    /// <c>X-Dev-Auth</c> header the handler now requires (the per-request opt-in
    /// fix — DevAuth no longer authenticates silently). Factories created with
    /// <see cref="WithoutDevAuth"/> skip this and drive the real cookie path.
    /// </summary>
    protected override void ConfigureClient(System.Net.Http.HttpClient client)
    {
        base.ConfigureClient(client);
        if (_devAuthEnabled)
        {
            client.DefaultRequestHeaders.Add(DevAuthHandler.OptInHeader, "1");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Belt-and-suspenders: the env var above pinned env to Development
        // for Program.cs's eager read. UseEnvironment additionally signals
        // it via IWebHostBuilder for any host-time consumer.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Runtime-bound config (consumed via IOptions inside the
                // app); these don't need to ride env vars because nothing
                // reads them eagerly during Program.cs's startup.
                //
                // Both connection strings point at the same testcontainer
                // but with different role credentials. coffer_app for the
                // runtime AppDbContext (RLS applies; the interceptor SETs
                // app.user_id per request from the authenticated cookie),
                // coffer_service for the migrations + pre-auth + escalation
                // paths.
                ["Api:ConnectionString"]        = _postgres.AppConnectionString,
                ["Api:ServiceConnectionString"] = _postgres.ServiceConnectionString,
                // The fixture already applied every migration once (DbUp's
                // journal makes an app-boot run a no-op), so skip migrations on
                // boot. Crucially this ALSO disables the Program.cs bootstrap-
                // restore block (gated on !Migrations:Skip, ADR-0061): test
                // shards share this bin's data/restore-staging dir, so a
                // restore-endpoint test's staged .cofferbak must never trip a
                // clean-restore during another test's host startup — which
                // raced to a FileNotFound host crash (and, post-#326, would run
                // the schema wipe against the shared test DB).
                ["Migrations:Skip"]             = "true",
                ["Api:Fido2:RpId"]              = "localhost",
                ["Api:Fido2:Origins:0"]         = "http://localhost",
                // Recovery-login rate limit: lifted high in tests so the
                // shared loopback partition can't trip the fixed window
                // across cases (the limiter is exercised by a dedicated
                // test that sets its own low value).
                ["Api:Auth:RecoveryRateLimitPerMinute"] = "100000",
                // DCR rate limit likewise lifted so the shared loopback partition
                // can't trip across cases; a dedicated test sets its own low value.
                ["Api:Mcp:DcrRateLimitPerMinute"] = "100000",
                // Api:DevAuth deliberately omitted — it's set via env var
                // so the registration-time gate in Program.cs sees it.
            });

            // Per-test overrides win over the defaults above.
            if (_configOverrides.Count > 0)
                config.AddInMemoryCollection(_configOverrides);
        });

        builder.ConfigureServices(services =>
        {
            foreach (var apply in _serviceOverrides)
                apply(services);
        });
    }
}
