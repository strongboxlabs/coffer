using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// Configuring where notifications go, and being told honestly what is covered.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminNotificationsEndpointTests
{
    private readonly PostgresFixture _fixture;

    public AdminNotificationsEndpointTests(PostgresFixture fixture) => _fixture = fixture;


    /// <summary>
    /// A client authenticated as a GENUINE admin — a users row with is_admin set, and a
    /// real cookie session for it.
    /// </summary>
    /// <remarks>
    /// Not the dev-auth client. DevAuthHandler stamps is_admin=true unconditionally for
    /// the seeded system user, whose row says otherwise (mig 138 excluded it from the
    /// admin backfill deliberately: SetupEndpoints derives "first user is admin" from
    /// whether any admin exists, so making that user an admin would mean the setup
    /// ceremony never grants it again).
    ///
    /// That fabricated claim was harmless until mig 213 gave the deployment-scope tables
    /// an admin-only RLS policy. Then a write through the app role got a 500: the
    /// endpoint filter allowed it on the claim and the policy refused it on the row. The
    /// database is the single source of truth for admin-ness, and production agrees with
    /// it — CookieAuthHandler stamps the claim only `if (validated.IsAdmin)`. So these
    /// tests authenticate the way a real admin does instead of asking the harness to
    /// assert something the schema contradicts.
    /// </remarks>
    private async Task<HttpClient> AdminClientAsync(ApiFactory factory)
    {
        var userId = Guid.NewGuid();
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();

        await using (var db = _fixture.NewServiceDbContext())
        {
            db.Users.Add(new Coffer.Api.Db.Entities.UserRow
            {
                Id = userId,
                Username = "admin-" + Guid.NewGuid().ToString("N")[..8],
                DisplayName = "admin",
                IsAdmin = true,
            });
            db.AuthSessions.Add(new Coffer.Api.Db.Entities.SessionRow
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SessionHash = hash,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={plaintext}");
        return client;
    }

    private async Task ResetAsync()
    {
        // Deployment-scope tables: a per-test ledger isolates nothing here.
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE notification_subscribers;");
    }

    [Fact]
    public async Task Providers_declare_whether_they_can_detect_absence()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AdminClientAsync(factory);

        var providers = await client.GetFromJsonAsync<List<NotificationProviderDto>>(
            "/api/admin/notifications/providers");

        Assert.NotNull(providers);
        var healthchecks = Assert.Single(providers!, p => p.SubscriberKey == "healthchecks");
        var webhook = Assert.Single(providers!, p => p.SubscriberKey == "webhook");

        // The distinction the settings page has to show. Getting it backwards would
        // tell someone a chat webhook covers absence detection, which is worse than
        // telling them nothing at all.
        Assert.True(healthchecks.DetectsAbsence);
        Assert.False(webhook.DetectsAbsence);
    }

    [Fact]
    public async Task Adding_only_a_message_provider_reports_no_absence_detection()
    {
        await ResetAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AdminClientAsync(factory);

        var created = await client.PostAsJsonAsync("/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "webhook",
                Url = "https://example.invalid/hook",
                MinSeverity = "warning",
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var listed = await client.GetFromJsonAsync<NotificationSubscribersResponse>(
            "/api/admin/notifications/subscribers");
        Assert.NotNull(listed);
        Assert.Single(listed!.Subscribers);

        // The honest answer: a webhook is configured and the install still cannot
        // notice its own silence.
        // Per MONITOR, because one healthchecks URL is one check. The old boolean
        // claimed "absence detection covered" from the mere existence of a heartbeat
        // target, which said nothing about whether any given job was watched.
        Assert.False(listed.MonitorCoverage[NotificationMonitors.Backup]);

        // Adding a heartbeat target changes the answer.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/abc",
                // A dead-man's switch must name what it watches; unbound it would
                // receive nothing, so the endpoint rejects it.
                Monitors = NotificationMonitors.Backup,
            })).StatusCode);

        var after = await client.GetFromJsonAsync<NotificationSubscribersResponse>(
            "/api/admin/notifications/subscribers");
        Assert.True(after!.MonitorCoverage[NotificationMonitors.Backup]);
    }

    [Fact]
    public async Task The_listing_never_returns_the_delivery_url()
    {
        await ResetAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AdminClientAsync(factory);

        const string secretUrl = "https://hc.invalid/super-secret-token-in-the-path";
        await client.PostAsJsonAsync("/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks", Url = secretUrl,
            });

        // A webhook URL routinely embeds its own credential, so the config is sealed
        // and the read path has no field for it. Asserting on the raw body rather
        // than the DTO, because the leak that matters is whatever actually goes over
        // the wire.
        var body = await (await client.GetAsync("/api/admin/notifications/subscribers"))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_provider_is_rejected()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsJsonAsync("/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "carrier-pigeon", Url = "https://example.invalid",
            });

        // Loud, not silent: a target whose provider does not exist would never
        // deliver, and would look configured on the settings page.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_dead_mans_switch_must_name_what_it_watches_and_a_message_target_must_not()
    {
        // A healthchecks URL identifies ONE check. An unbound heartbeat target would be
        // accepted, appear in the list looking configured, and receive nothing at all —
        // the silent shape this whole subsystem exists to remove. So the binding is
        // required for that capability, and rejected for a message target, which is
        // bound to nothing.
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AdminClientAsync(factory);

        var unbound = await client.PostAsJsonAsync(
            "/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/unbound",
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unbound.StatusCode);

        var boundMessage = await client.PostAsJsonAsync(
            "/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "webhook",
                Url = "https://example.invalid/hook",
                Monitors = NotificationMonitors.Backup,
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, boundMessage.StatusCode);

        var unknown = await client.PostAsJsonAsync(
            "/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/typo",
                Monitors = "backpu",
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        // And the valid combination lands.
        var ok = await client.PostAsJsonAsync(
            "/api/admin/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/good",
                Monitors = NotificationMonitors.Backup,
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
