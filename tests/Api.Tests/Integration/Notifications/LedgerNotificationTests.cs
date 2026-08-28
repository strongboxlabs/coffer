using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// A ledger's events go to the deployment's targets, or to its own.
/// </summary>
/// <remarks>
/// The scope split from ADR-0096 D1, exercised. Ledger events are written to
/// <c>ledger_events</c> and never to <c>system_events</c> — writing them there would
/// have been the convenient thing and would have collapsed the distinction the whole
/// ADR rests on.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerNotificationTests
{
    private readonly PostgresFixture _fixture;

    public LedgerNotificationTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed class SpySubscriber : INotificationSubscriber
    {
        public SpySubscriber(string key) => SubscriberKey = key;
        public string SubscriberKey { get; }
        public string DisplayName => SubscriberKey;
        public SubscriberCapability Capability => SubscriberCapability.Message;
        public List<NotificationEvent> Delivered { get; } = [];

        public Task DeliverAsync(
            NotificationEvent notification, SubscriberConfig config, CancellationToken ct)
        {
            Delivered.Add(notification);
            return Task.CompletedTask;
        }
    }

    private NotificationPublisher PublisherFor(params INotificationSubscriber[] subs) =>
        new(_fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            subs,
            NullLogger<NotificationPublisher>.Instance);

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE notification_subscribers; TRUNCATE system_events;");
    }

    private byte[] SealedConfig() =>
        _fixture.NewLedgerKeyService().SealWithMasterKey(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new SubscriberConfig("https://example.invalid/x"))));

    private async Task AddSystemTargetAsync(string key)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO notification_subscribers
                (subscriber_key, display_name, min_severity, config_ciphertext)
            VALUES ({key}, {key}, 'info', {SealedConfig()});");
    }

    private async Task AddLedgerTargetAsync(Guid ledgerId, string key)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ledger_notification_subscribers
                (ledger_id, subscriber_key, display_name, min_severity, config_ciphertext)
            VALUES ({ledgerId}, {key}, {key}, 'info', {SealedConfig()});");
    }

    private static NotificationEvent Drift() => new(
        NotificationSeverity.Warning, NotificationTopics.Consistency,
        "consistency.drift", "Projections disagree.");

    [Fact]
    public async Task A_ledger_event_never_reaches_a_deployment_target()
    {
        await ResetAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddSystemTargetAsync("sys");

        var sys = new SpySubscriber("sys");
        await PublisherFor(sys).PublishLedgerAsync(ledger.LedgerId, Drift());

        // Inverts Inherit_delivers_a_ledger_event_to_the_deployments_targets, which
        // pinned the behaviour migration 214 retired. That test was correct about what
        // the code did and the code was wrong: a ledger's events went to every
        // deployment target with no ledger filter, by default, so on a shared install
        // one ledger's activity reached whoever watched the deployment channel. The
        // publisher's own sibling test called that a leak while this one called it a
        // feature.
        Assert.Empty(sys.Delivered);
    }

    [Fact]
    public async Task A_ledger_event_goes_to_that_ledgers_targets()
    {
        await ResetAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddSystemTargetAsync("sys");
        await AddLedgerTargetAsync(ledger.LedgerId, "mine");

        var sys = new SpySubscriber("sys");
        var mine = new SpySubscriber("mine");
        await PublisherFor(sys, mine).PublishLedgerAsync(ledger.LedgerId, Drift());

        // The deployment target must NOT see it. On a shared install that would leak
        // one ledger's activity to whoever watches the deployment's channel, which is
        // the reason "own" exists at all.
        Assert.Empty(sys.Delivered);
        Assert.Single(mine.Delivered);
    }

    [Fact]
    public async Task The_delivered_payload_names_the_ledger_it_is_about()
    {
        // The prerequisite for a single destination serving several ledgers. Each ledger
        // keeps its own targets now, so the intended way to get one channel is to point
        // several ledgers at the same URL — which is useless if the messages that arrive
        // are indistinguishable from each other.
        await ResetAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerTargetAsync(ledger.LedgerId, "mine");

        // Carries its own Detail, so this also proves the ledger name is ADDED rather
        // than substituted for whatever the event already said.
        var drift = new NotificationEvent(
            NotificationSeverity.Warning, NotificationTopics.Consistency,
            "consistency.drift", "Projections disagree.",
            Detail: new Dictionary<string, string> { ["projection"] = "3" });

        var mine = new SpySubscriber("mine");
        await PublisherFor(mine).PublishLedgerAsync(ledger.LedgerId, drift);

        var delivered = Assert.Single(mine.Delivered);
        Assert.NotNull(delivered.Detail);
        Assert.True(delivered.Detail!.ContainsKey("ledger"),
            "the delivered payload must say which ledger it is about");
        Assert.False(string.IsNullOrWhiteSpace(delivered.Detail["ledger"]));

        // And the event's own detail survives — the ledger name is added, not substituted.
        Assert.True(delivered.Detail.ContainsKey("projection"),
            "adding the ledger name must not drop the event's own detail");
    }

    [Fact]
    public async Task The_stored_event_does_not_duplicate_the_ledger_into_its_detail()
    {
        // ledger_events already has a ledger_id COLUMN. Writing the name into the JSON
        // too would duplicate it into every historical row and freeze a stale copy of a
        // renameable thing in the store as well as in the payload. The payload needs it
        // because a webhook body has no other way to say it; the row does not.
        await ResetAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerTargetAsync(ledger.LedgerId, "mine");

        await PublisherFor(new SpySubscriber("mine"))
            .PublishLedgerAsync(ledger.LedgerId, Drift());

        await using var read = _fixture.NewDbContext();
        var row = await read.LedgerEvents.AsNoTracking()
            .SingleAsync(e => e.LedgerId == ledger.LedgerId);
        Assert.DoesNotContain("\"ledger\"", row.DetailJson);
        Assert.Equal(ledger.LedgerId, row.LedgerId);
    }

    [Fact]
    public async Task No_targets_is_silence_rather_than_a_fallback()
    {
        await ResetAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddSystemTargetAsync("sys");

        var sys = new SpySubscriber("sys");
        await PublisherFor(sys).PublishLedgerAsync(ledger.LedgerId, Drift());

        // Deliberate: falling back to the deployment's targets would make "own with
        // nothing configured" mean the opposite of what it says, and it is the reason
        // there is no third "none" mode.
        Assert.Empty(sys.Delivered);
    }

    [Fact]
    public async Task A_ledger_event_never_lands_in_system_events()
    {
        await ResetAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await PublisherFor().PublishLedgerAsync(ledger.LedgerId, Drift());

        await using var db = _fixture.NewDbContext();
        Assert.Contains(await db.LedgerEvents.AsNoTracking()
                .Where(e => e.LedgerId == ledger.LedgerId).ToListAsync(),
            e => e.EventKey == "consistency.drift");
        // The scope split, enforced. A drift notice in system_events would outlive
        // its ledger and be readable by an admin with no grant on it.
        Assert.DoesNotContain(await db.SystemEvents.AsNoTracking().ToListAsync(),
            e => e.EventKey == "consistency.drift");
    }

    [Fact]
    public async Task The_throttle_is_per_ledger()
    {
        await ResetAsync();
        var first = await SyntheticLedger.CreateAsync(_fixture);
        var second = await SyntheticLedger.CreateAsync(_fixture);
        await AddSystemTargetAsync("sys");
        var publisher = PublisherFor(new SpySubscriber("sys"));

        Assert.True(await publisher.PublishLedgerThrottledAsync(
            first.LedgerId, Drift(), TimeSpan.FromHours(24)));
        // Same key, same window, same ledger → suppressed.
        Assert.False(await publisher.PublishLedgerThrottledAsync(
            first.LedgerId, Drift(), TimeSpan.FromHours(24)));

        // A DIFFERENT ledger's first report of the same kind must still get through:
        // a global throttle would silence it because another ledger happened to
        // drift first.
        Assert.True(await publisher.PublishLedgerThrottledAsync(
            second.LedgerId, Drift(), TimeSpan.FromHours(24)));
    }
}
