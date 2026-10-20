using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Backup;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// What the browser reads after a restore's restart (ADR-0061, amended
/// 2026-10-09): the outcome the boot recorded, by attempt id. Before this, the
/// browser took "/healthz answers" for success, and a restore that failed in
/// production and changed nothing was reported as complete.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RestoreOutcomeEndpointTests
{
    private readonly PostgresFixture _fixture;

    public RestoreOutcomeEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Answers_the_recorded_outcome_for_its_own_attempt_only()
    {
        var attempt = Guid.NewGuid();
        BootstrapRestoreStaging.RecordOutcome(new BootstrapRestoreStaging.RestoreOutcome(
            attempt, "failed", new DateTime(2026, 10, 9, 17, 50, 46, DateTimeKind.Utc),
            PreviousDatabase: null, Error: "database \"coffer\" is being accessed by other users"));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();   // anonymous
        using var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/restore-outcome/{attempt}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = (await resp.Content.ReadFromJsonAsync<RestoreOutcomeEndpoints.RestoreOutcomeDto>())!;
        Assert.Equal("failed", dto.Outcome);
        Assert.Contains("being accessed by other users", dto.Error);

        // Anyone without the id of THAT attempt learns nothing.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/restore-outcome/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Restore_readiness_reports_what_the_swap_needs()
    {
        // The fixture provisions the service role as db/init does; the function
        // must say so, and say exactly which half is missing when one is.
        await using var db = _fixture.NewServiceDbContext();
        var row = await db.RestoreReadiness().SingleAsync();
        Assert.Equal("coffer_service", row.RoleName);
        Assert.True(row.CanCreateDatabases);
        Assert.True(row.OwnsDatabase);
        Assert.Equal("c", row.LocaleProvider);
        Assert.Null(row.RestoreMarker);   // never restored into
    }
}
