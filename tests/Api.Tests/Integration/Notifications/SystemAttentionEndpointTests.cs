using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// <c>GET /api/admin/notifications/attention</c>: what the INSTALLATION needs, behind the
/// System-settings gear and the System tabs (ADR-0102, amended for ADR-0110).
/// </summary>
/// <remarks>
/// Assertions name the subjects each test seeds rather than comparing the whole list: the scheduler
/// ticks once at start-up, and a monitor's own boot-time events are not this test's business.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SystemAttentionEndpointTests : DatabaseTest
{
    private const string Route = "/api/admin/notifications/attention";

    public SystemAttentionEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private async Task<HttpClient> ClientAsync(ApiFactory factory, bool isAdmin)
    {
        var userId = Guid.NewGuid();
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();

        await using (var db = Db.NewServiceDbContext())
        {
            db.Users.Add(new UserRow
            {
                Id = userId,
                Username = "user-" + Guid.NewGuid().ToString("N")[..8],
                DisplayName = isAdmin ? "admin" : "member",
                IsAdmin = isAdmin,
            });
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash, ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }

    /// <summary>One deployment event at a chosen instant, bypassing the publisher: the read rule is
    /// what is under test, over arrangements no publisher produces today.</summary>
    private async Task AddEventAsync(string severity, string topic, string eventKey, DateTime at)
    {
        var summary = $"{eventKey} @ {at:O}";
        await using var db = Db.NewServiceDbContext();
        db.SystemEvents.Add(new SystemEventRow
        {
            Severity = severity, Topic = topic, EventKey = eventKey, Summary = summary, DetailJson = "{}",
        });
        await db.SaveChangesAsync();
        await db.SystemEvents.Where(e => e.Summary == summary)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurredAt, at));
    }

    private Task AnnounceAsync(string version) =>
        new SystemSettingsRepository(Db.NewServiceFactory()).SetValueJsonAsync(
            SystemSettingsRepository.UpgradeAnnouncedKey, $"\"{version}\"", null, DateTime.UtcNow);

    private static DateTime At(int day) => new(2026, 6, day, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<SystemAttentionDto> GetAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SystemAttentionDto>(Route))!;

    [Fact]
    public async Task An_admin_gets_the_outstanding_subjects_worst_first_with_the_announced_version()
    {
        await AddEventAsync(NotificationSeverity.Warning, NotificationTopics.Upgrade, "upgrade.available", At(2));
        await AddEventAsync(NotificationSeverity.Critical, NotificationTopics.Scheduler, "probe.failed", At(3));
        await AnnounceAsync("99.0.0");     // far ahead of the build, so nothing clears it
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, isAdmin: true);

        var attention = await GetAsync(client);

        Assert.Equal("critical", attention.Worst);
        var names = attention.Subjects.Select(s => s.Subject).ToList();
        Assert.Contains("upgrade", names);
        Assert.Contains("probe", names);
        // The more severe one first, whatever order they happened in.
        Assert.True(names.IndexOf("probe") < names.IndexOf("upgrade"));
        Assert.Equal("warning", attention.Subjects.Single(s => s.Subject == "upgrade").Severity);
        Assert.Equal("99.0.0", attention.UpgradeVersion);
    }

    [Fact]
    public async Task A_later_all_clear_takes_a_subject_off_the_list()
    {
        await AddEventAsync(NotificationSeverity.Warning, NotificationTopics.Scheduler, "probe.failed", At(2));
        await AddEventAsync(NotificationSeverity.Info, NotificationTopics.Scheduler, "probe.succeeded", At(3));
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, isAdmin: true);

        Assert.DoesNotContain((await GetAsync(client)).Subjects, s => s.Subject == "probe");
    }

    [Fact]
    public async Task A_subject_reports_the_severity_of_its_latest_event()
    {
        await AddEventAsync(NotificationSeverity.Critical, NotificationTopics.Scheduler, "probe.failed", At(2));
        await AddEventAsync(NotificationSeverity.Warning, NotificationTopics.Scheduler, "probe.degraded", At(3));
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, isAdmin: true);

        // One bad night does not pin the gear red for good (ADR-0102 D3).
        Assert.Equal("warning", (await GetAsync(client)).Subjects.Single(s => s.Subject == "probe").Severity);
    }

    [Fact]
    public async Task A_release_the_install_has_reached_is_cleared_by_the_read_with_no_check()
    {
        // The automatic check is off in this database, and nothing goes to the network. An install
        // that upgraded must not keep a dot for a release it already runs.
        await AddEventAsync(NotificationSeverity.Warning, NotificationTopics.Upgrade, "upgrade.available", At(2));
        await AnnounceAsync("0.0.1");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, isAdmin: true);

        var attention = await GetAsync(client);

        Assert.DoesNotContain(attention.Subjects, s => s.Subject == "upgrade");
        Assert.Null(attention.UpgradeVersion);
        await using var db = Db.NewServiceDbContext();
        Assert.True(await db.SystemEvents.AnyAsync(e => e.EventKey == "upgrade.current"));
    }

    [Fact]
    public async Task A_non_admin_is_refused()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, isAdmin: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
    }
}
