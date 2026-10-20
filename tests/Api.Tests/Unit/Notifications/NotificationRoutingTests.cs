using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Notifications;
using Coffer.Api.Notifications.Providers;

namespace Coffer.Api.Tests.Unit.Notifications;

/// <summary>
/// The routing rules a subscriber is filtered by, and the capability split.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no HTTP. What they guard is the logic that decides
/// whether a notification reaches anyone — the part where an off-by-one in a severity
/// comparison silently drops exactly the events that mattered.
/// </remarks>
public sealed class NotificationRoutingTests
{
    // ---- severity floor (ADR-0096 D3) --------------------------------------

    [Theory]
    // A floor of info takes everything.
    [InlineData(NotificationSeverity.Info, NotificationSeverity.Info, true)]
    [InlineData(NotificationSeverity.Warning, NotificationSeverity.Info, true)]
    [InlineData(NotificationSeverity.Critical, NotificationSeverity.Info, true)]
    // A floor of warning drops routine noise and keeps the rest.
    [InlineData(NotificationSeverity.Info, NotificationSeverity.Warning, false)]
    [InlineData(NotificationSeverity.Warning, NotificationSeverity.Warning, true)]
    [InlineData(NotificationSeverity.Critical, NotificationSeverity.Warning, true)]
    // A floor of critical keeps only what needs a human.
    [InlineData(NotificationSeverity.Info, NotificationSeverity.Critical, false)]
    [InlineData(NotificationSeverity.Warning, NotificationSeverity.Critical, false)]
    [InlineData(NotificationSeverity.Critical, NotificationSeverity.Critical, true)]
    public void Severity_floor_is_inclusive(string severity, string floor, bool expected) =>
        Assert.Equal(expected, NotificationSeverity.MeetsFloor(severity, floor));

    [Fact]
    public void An_unknown_severity_is_delivered_rather_than_dropped()
    {
        // Fail LOUD, not silent. Dropping a notification because someone typo'd its
        // level would reproduce the exact defect this subsystem exists to remove —
        // an event that happened and nobody heard about it. Better a spurious
        // delivery than a swallowed one.
        Assert.True(NotificationSeverity.MeetsFloor("catastrophic", NotificationSeverity.Critical));
        Assert.True(NotificationSeverity.MeetsFloor(NotificationSeverity.Info, "nonsense"));
    }

    // ---- capability split (ADR-0096 D7) ------------------------------------

    [Fact]
    public void Healthchecks_is_heartbeat_capable_and_webhook_is_not()
    {
        // The distinction the settings surface warns on. Only a heartbeat provider
        // can report what did NOT happen, because noticing silence takes something
        // outside the deployment. Get this backwards and an install that configured
        // only a chat webhook would be told it has absence detection when it does
        // not — which is worse than telling it nothing.
        var healthchecks = new HealthchecksSubscriber(
            new HttpClient(), NullLogger<HealthchecksSubscriber>.Instance);
        var webhook = new WebhookSubscriber(new HttpClient());

        Assert.Equal(SubscriberCapability.Heartbeat, healthchecks.Capability);
        Assert.Equal(SubscriberCapability.Message, webhook.Capability);
    }

    [Fact]
    public void Provider_keys_are_stable_and_distinct()
    {
        // These strings are persisted in notification_subscribers.subscriber_key, so
        // renaming one orphans every configured target of that type — the publisher
        // would find no provider and record an unroutable subscriber.
        var healthchecks = new HealthchecksSubscriber(
            new HttpClient(), NullLogger<HealthchecksSubscriber>.Instance);
        var webhook = new WebhookSubscriber(new HttpClient());

        Assert.Equal("healthchecks", healthchecks.SubscriberKey);
        Assert.Equal("webhook", webhook.SubscriberKey);
    }

    // ---- topic vocabulary --------------------------------------------------

    [Fact]
    public void Every_topic_is_accepted_by_the_database_check_constraint()
    {
        // Migration 207 constrains system_events.topic. The constraint and this list
        // have to agree or a publish throws at the INSERT — which, for a
        // notification, means the event is lost at exactly the moment it mattered.
        Assert.Equal(
            new[] { "backup", "snapshot", "sync", "quotes", "consistency", "scheduler" },
            NotificationTopics.All);
    }
}
