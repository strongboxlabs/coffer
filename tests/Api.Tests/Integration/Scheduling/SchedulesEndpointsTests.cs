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
