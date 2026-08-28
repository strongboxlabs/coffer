using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The bus records every event and routes it only to the targets that asked.
/// </summary>
/// <remarks>
/// The properties worth pinning are the ones whose failure is SILENT: an event that
/// is recorded but delivered nowhere, or a delivery that fails and leaves no trace.
/// Both would rebuild, inside the notification subsystem, the exact defect it exists
/// to remove.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NotificationPublisherTests
{
    private readonly PostgresFixture _fixture;

    public NotificationPublisherTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>A subscriber that records what it was handed, or throws on command.</summary>
    private sealed class SpySubscriber : INotificationSubscriber
    {
        private readonly bool _throws;
        public SpySubscriber(string key, SubscriberCapability capability, bool throws = false)
        {
            SubscriberKey = key;
            Capability = capability;
            _throws = throws;
        }

        public string SubscriberKey { get; }
        public string DisplayName => SubscriberKey;
        public SubscriberCapability Capability { get; }
        public List<NotificationEvent> Delivered { get; } = [];

        public Task DeliverAsync(
            NotificationEvent notification, SubscriberConfig config, CancellationToken ct)
        {
            if (_throws) throw new InvalidOperationException("target refused");
            Delivered.Add(notification);
            return Task.CompletedTask;
        }
    }

    private NotificationPublisher PublisherFor(params INotificationSubscriber[] subscribers) =>
        new(_fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            subscribers,
            NullLogger<NotificationPublisher>.Instance);

    /// <summary>
    /// Clear the deployment-scope tables before seeding.
    /// </summary>
    /// <remarks>
    /// These tables are NOT ledger-scoped, so the per-test synthetic ledger that
    /// isolates every other suite does nothing here — a subscriber another test
    /// added is still enabled and still matches. That is the same exception
    /// engineering-standards §5.2 already makes for genuinely global tables like
    /// <c>bootstrap_tokens</c>, and it is a real property of deployment-scope
    /// features rather than a wart in these tests: a first version of this file
    /// skipped it and the topic-filter test failed because an earlier test's
    /// unfiltered subscriber received the event.
    /// </remarks>
    private async Task ResetAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE notification_subscribers; TRUNCATE system_events;");
    }

    /// <summary>Seed a configured target with its config sealed, as production does.</summary>
    private async Task<Guid> AddSubscriberAsync(
        string key, string minSeverity, string[]? topics = null, string? monitors = null)
    {
        var keys = _fixture.NewLedgerKeyService();
        var sealedConfig = keys.SealWithMasterKey(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new SubscriberConfig("https://example.invalid/ping"))));

        await using var db = _fixture.NewDbContext();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO notification_subscribers
                (id, subscriber_key, display_name, min_severity, topics, monitors, config_ciphertext)
            VALUES ({id}, {key}, {key}, {minSeverity}, {topics}, {monitors}, {sealedConfig});");
        return id;
    }

    private async Task<(int Failures, string? Error, DateTime? Success)> HealthAsync(Guid id)
    {
        await using var db = _fixture.NewDbContext();
        var row = await db.NotificationSubscribers.AsNoTracking().SingleAsync(r => r.Id == id);
        return (row.ConsecutiveFailures, row.LastError, row.LastSuccessAt);
    }

    [Fact]
    public async Task An_event_is_recorded_and_delivered_to_a_matching_target()
    {
        await ResetAsync();
        var id = await AddSubscriberAsync("spy", NotificationSeverity.Warning);
        // MESSAGE capability: this is about generic delivery, which is the message
        // class's job. It used to say Heartbeat and pass only because any Critical
        // event reached a heartbeat target — the conflation that let an unrelated
        // critical mark a backup check down.
        var spy = new SpySubscriber("spy", SubscriberCapability.Message);

        await PublisherFor(spy).PublishAsync(new NotificationEvent(
            NotificationSeverity.Critical, NotificationTopics.Backup,
            "backup.stale", "No backup in 60 hours."));

        // Recorded ...
        await using var db = _fixture.NewDbContext();
        Assert.Contains(await db.SystemEvents.AsNoTracking().ToListAsync(),
            e => e.EventKey == "backup.stale" && e.Severity == NotificationSeverity.Critical);

        // ... and delivered.
        Assert.Contains(spy.Delivered, e => e.EventKey == "backup.stale");

        var health = await HealthAsync(id);
        Assert.Equal(0, health.Failures);
        Assert.NotNull(health.Success);
    }

    [Fact]
    public async Task An_event_below_a_targets_floor_is_recorded_but_not_delivered()
    {
        await ResetAsync();
        await AddSubscriberAsync("spy", NotificationSeverity.Critical);
        var spy = new SpySubscriber("spy", SubscriberCapability.Message);

        await PublisherFor(spy).PublishAsync(new NotificationEvent(
            NotificationSeverity.Info, NotificationTopics.Quotes,
            "quotes.refreshed", "Quotes refreshed."));

        // Persisting everything while delivering a subset is ADR-0096 D4: the log
        // keeps the routine traffic, the bus carries only what warrants attention.
        // A channel that reports every quote refresh gets muted, and then it is
        // protecting nothing.
        await using var db = _fixture.NewDbContext();
        Assert.Contains(await db.SystemEvents.AsNoTracking().ToListAsync(),
            e => e.EventKey == "quotes.refreshed");
        Assert.Empty(spy.Delivered);
    }

    [Fact]
    public async Task A_topic_filter_excludes_other_topics()
    {
        await ResetAsync();
        await AddSubscriberAsync("spy", NotificationSeverity.Info, ["backup"]);
        // Topic filtering is a message-target concern. A heartbeat target is bound to
        // one monitor instead, which is a stronger statement than a topic filter.
        var spy = new SpySubscriber("spy", SubscriberCapability.Message);
        var publisher = PublisherFor(spy);

        await publisher.PublishAsync(new NotificationEvent(
            NotificationSeverity.Critical, NotificationTopics.Consistency,
            "consistency.drift", "Drift found."));
        Assert.Empty(spy.Delivered);

        await publisher.PublishAsync(new NotificationEvent(
            NotificationSeverity.Info, NotificationTopics.Backup,
            "backup.succeeded", "Backup created.",
            Monitor: NotificationMonitors.Backup, Signal: MonitorSignal.Success));
        Assert.Single(spy.Delivered);
    }

    [Fact]
    public async Task A_failing_target_is_recorded_and_does_not_break_the_publish()
    {
        await ResetAsync();
        var id = await AddSubscriberAsync("angry", NotificationSeverity.Info);
        var angry = new SpySubscriber("angry", SubscriberCapability.Message, throws: true);

        // Must NOT throw. A backup job failing because a chat webhook is down would
        // be worse than the silence this replaces.
        await PublisherFor(angry).PublishAsync(new NotificationEvent(
            NotificationSeverity.Critical, NotificationTopics.Backup,
            "backup.failed", "Backup failed."));

        var health = await HealthAsync(id);
        Assert.Equal(1, health.Failures);
        Assert.Contains("refused", health.Error);
    }

    [Fact]
    public async Task A_target_with_no_registered_provider_is_recorded_as_failing()
    {
        // A renamed key, or a provider dropped from the build. Without this, an
        // unroutable target looks identical to a working one on the settings page —
        // which is the silent failure, wearing a different hat.
        await ResetAsync();
        var id = await AddSubscriberAsync("ghost", NotificationSeverity.Info);

        await PublisherFor(new SpySubscriber("spy", SubscriberCapability.Message))
            .PublishAsync(new NotificationEvent(
                NotificationSeverity.Critical, NotificationTopics.Scheduler,
                "job.disabled", "A job was auto-disabled."));

        var health = await HealthAsync(id);
        Assert.Equal(1, health.Failures);
        Assert.Contains("No provider registered", health.Error);
    }

    [Fact]
    public async Task The_heartbeat_question_answers_honestly()
    {
        // What the settings surface asks before telling someone they are covered.
        await ResetAsync();
        var publisher = PublisherFor(
            new SpySubscriber("webhook", SubscriberCapability.Message),
            new SpySubscriber("healthchecks", SubscriberCapability.Heartbeat));

        await AddSubscriberAsync("webhook", NotificationSeverity.Info);
        var beforeBinding = await publisher.MonitorCoverageAsync();
        Assert.False(beforeBinding[NotificationMonitors.Backup],
            "a message-only target must not count as absence detection");

        // Bound to the monitor it actually watches. Coverage is per monitor now,
        // because one healthchecks URL is one check: a target watching backups says
        // nothing about any other job, and the old boolean could not express that.
        await AddSubscriberAsync("healthchecks", NotificationSeverity.Info,
            monitors: NotificationMonitors.Backup);
        Assert.True((await publisher.MonitorCoverageAsync())[NotificationMonitors.Backup]);
    }
}
