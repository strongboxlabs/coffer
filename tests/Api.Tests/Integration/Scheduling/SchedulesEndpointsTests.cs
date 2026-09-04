using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Scheduling;

/// <summary>
/// The generic per-ledger schedule surface (mig 136):
/// <c>GET/PUT /api/ledgers/{id}/schedules/{jobType}</c> for quote-refresh and
/// snapshot job types.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SchedulesEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public SchedulesEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Theory]
    [InlineData("quote-refresh", 19)]
    [InlineData("snapshot", 3)]
    public async Task Schedule_defaults_then_round_trips(string jobType, int defaultHour)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = $"/api/ledgers/{ledger.LedgerId}/schedules/{jobType}";

        // Default: disabled, the job type's default hour.
        var initial = (await (await client.GetAsync(path)).Content.ReadFromJsonAsync<ScheduleDto>())!;
        Assert.False(initial.Enabled);
        Assert.Equal(defaultHour, initial.HourLocal);

        // Enable at 07:30 → next_run_at set + in the future.
        var put = await client.PutAsJsonAsync(path, new ScheduleDto(Enabled: true, HourLocal: 7, MinuteLocal: 30));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<ScheduleDto>())!;
        Assert.True(saved.Enabled);
        Assert.NotNull(saved.NextRunAt);
        Assert.True(saved.NextRunAt > DateTime.UtcNow);

        var after = (await (await client.GetAsync(path)).Content.ReadFromJsonAsync<ScheduleDto>())!;
        Assert.True(after.Enabled);
        Assert.Equal(7, after.HourLocal);
        Assert.Equal(30, after.MinuteLocal);
    }

    /// <summary>
    /// The health a panel needs comes back over the wire, and re-enabling gives the job
    /// a fresh budget rather than a single attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as one test through the ENDPOINT rather than two against the repository,
    /// because the defect this closes had a working write path and a read path that
    /// stopped at ToDto. A repository-level assertion would have passed throughout.
    /// </para>
    /// <para>
    /// The re-enable half is a live behaviour fix, not presentation: consecutive_failures
    /// survived a re-enable, so a job auto-disabled at five came back holding five and
    /// the next single failure computed six and switched it straight off again. Turning
    /// it back on bought one attempt, not five.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_reaches_the_wire_and_re_enabling_restores_the_full_budget()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Put the row in the state the scheduler leaves behind when it gives up.
        await using (var seed = _fixture.NewDbContext())
        {
            seed.ScheduledJobs.Add(new Coffer.Api.Db.Entities.ScheduledJobRow
            {
                LedgerId = ledger.LedgerId,
                JobType = "quote-refresh",
                Enabled = false,
                HourLocal = 19,
                MinuteLocal = 0,
                ConfiguredByUserId = ledger.UserId,
                ConsecutiveFailures = Coffer.Api.Scheduling.SchedulerRunner
                    .DisableAfterConsecutiveFailures,
                LastError = "the database rejected the operation",
                LastFailureAt = DateTime.UtcNow.AddHours(-1),
                DisabledReason = Coffer.Api.Scheduling.ScheduleDisableReasons.ConsecutiveFailures,
            });
            await seed.SaveChangesAsync();
        }

        var before = await client.GetFromJsonAsync<ScheduleDto>(
            $"/api/ledgers/{ledger.LedgerId}/schedules/quote-refresh");
        Assert.NotNull(before);
        Assert.False(before!.Enabled);
        Assert.Equal(
            Coffer.Api.Scheduling.SchedulerRunner.DisableAfterConsecutiveFailures,
            before.ConsecutiveFailures);
        Assert.Equal("the database rejected the operation", before.LastError);
        Assert.Equal(
            Coffer.Api.Scheduling.ScheduleDisableReasons.ConsecutiveFailures,
            before.DisabledReason);

        // The operator turns it back on.
        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/schedules/quote-refresh",
            new ScheduleDto(Enabled: true, HourLocal: 19, MinuteLocal: 0, Timezone: "UTC"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await client.GetFromJsonAsync<ScheduleDto>(
            $"/api/ledgers/{ledger.LedgerId}/schedules/quote-refresh");
        Assert.NotNull(after);
        Assert.True(after!.Enabled);
        Assert.Equal(0, after.ConsecutiveFailures);
        Assert.Null(after.LastError);
        Assert.Null(after.DisabledReason);
    }

    /// <summary>
    /// Editing an already-enabled job does NOT launder a failure streak.
    /// </summary>
    /// <remarks>
    /// The reset is scoped to the disabled -> enabled transition. Clearing on every
    /// upsert would mean a user nudging the run time by a minute erased the evidence
    /// that the job has been failing for four days — and the counter is what the
    /// scheduler compares against its threshold, so it would also silently extend the
    /// budget every time the form was saved.
    /// </remarks>
    [Fact]
    public async Task Changing_the_time_on_an_enabled_failing_job_keeps_its_streak()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await using (var seed = _fixture.NewDbContext())
        {
            seed.ScheduledJobs.Add(new Coffer.Api.Db.Entities.ScheduledJobRow
            {
                LedgerId = ledger.LedgerId,
                JobType = "quote-refresh",
                Enabled = true,
                HourLocal = 19,
                MinuteLocal = 0,
                ConfiguredByUserId = ledger.UserId,
                ConsecutiveFailures = 3,
                LastError = "a network request failed or timed out",
                LastFailureAt = DateTime.UtcNow.AddHours(-2),
            });
            await seed.SaveChangesAsync();
        }

        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/schedules/quote-refresh",
            new ScheduleDto(Enabled: true, HourLocal: 20, MinuteLocal: 30, Timezone: "UTC"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await client.GetFromJsonAsync<ScheduleDto>(
            $"/api/ledgers/{ledger.LedgerId}/schedules/quote-refresh");
        Assert.NotNull(after);
        Assert.Equal(20, after!.HourLocal);
        Assert.Equal(3, after.ConsecutiveFailures);
        Assert.Equal("a network request failed or timed out", after.LastError);
    }

    [Fact]
    public async Task Put_rejects_out_of_range_time()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/schedules/quote-refresh",
            new ScheduleDto(Enabled: true, HourLocal: 25, MinuteLocal: 0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Schedule_honors_the_provided_timezone()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = $"/api/ledgers/{ledger.LedgerId}/schedules/quote-refresh";

        // 05:00 in UTC → next_run_at lands at 05:00 UTC, independent of the test
        // host's local timezone. (Proves the schedule's tz is applied, not the
        // server's.) UTC has no DST, so this is deterministic.
        var put = await client.PutAsJsonAsync(path,
            new ScheduleDto(Enabled: true, HourLocal: 5, MinuteLocal: 0, Timezone: "UTC"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<ScheduleDto>())!;

        Assert.Equal("UTC", saved.Timezone);
        Assert.NotNull(saved.NextRunAt);
        var utc = saved.NextRunAt!.Value.ToUniversalTime();
        Assert.Equal(5, utc.Hour);
        Assert.Equal(0, utc.Minute);
    }

    [Fact]
    public async Task Unknown_job_type_is_rejected()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/schedules/not-a-job");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
