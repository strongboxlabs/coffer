using System.Net;
using System.Text.Json;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration;

/// <summary>
/// End-to-end checks for the operational probes. Both endpoints must be
/// reachable without authentication; <c>/readyz</c> additionally proves the
/// API can talk to Postgres (which DbUp already exercised on startup, but
/// the probe is the explicit contract the orchestrator gates on).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HealthEndpointsTests
{
    private readonly PostgresFixture _postgres;

    public HealthEndpointsTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Healthz_returns_200_and_alive_status_anonymously()
    {
        await using var factory = new ApiFactory(_postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("alive", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readyz_returns_200_when_db_is_reachable()
    {
        await using var factory = new ApiFactory(_postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
    }
}
