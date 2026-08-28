using Coffer.Api.Notifications;

namespace Coffer.Api.Notifications.Providers;

/// <summary>
/// A dead-man's-switch monitor: healthchecks.io, or a self-hosted Healthchecks.
/// </summary>
/// <remarks>
/// The reason this exists rather than a webhook to a chat app: it is the only
/// subscriber class that can report what did NOT happen. Snapshots and backups once
/// died for 68 and 47 hours and nothing said anything, because the thing that should
/// have complained was the thing that had stopped running. A monitor outside the
/// deployment counts the pings it expected and alerts on their absence — no code
/// inside a dead container can do that (ADR-0096 D5).
/// <para>
/// Protocol: <c>POST {url}</c> is a success ping, <c>POST {url}/fail</c> marks the
/// check failed. Both are plain HTTP with no body required, which is also why a
/// self-hosted Healthchecks works identically — the URL is the whole configuration.
/// </para>
/// </remarks>
public sealed class HealthchecksSubscriber : INotificationSubscriber
{
    private readonly HttpClient _http;
    private readonly ILogger<HealthchecksSubscriber> _logger;

    public HealthchecksSubscriber(HttpClient http, ILogger<HealthchecksSubscriber> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string SubscriberKey => "healthchecks";

    public string DisplayName => "Healthchecks (healthchecks.io or self-hosted)";

    public SubscriberCapability Capability => SubscriberCapability.Heartbeat;

    public async Task DeliverAsync(
        NotificationEvent notification,
        SubscriberConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(config);

        // The SIGNAL decides, not the severity. This provider can express exactly two
        // things about the one check its URL identifies: alive, or down. Severity is a
        // human-facing scale it has no representation for — the docs are explicit that
        // the request body is stored for diagnostics and does not affect alerting.
        //
        // It used to key off `Severity == Critical`, which meant any critical event
        // reaching a heartbeat target marked THAT check down regardless of what the
        // event was about. Routing now guarantees only this monitor's own signals
        // arrive (NotificationPublisher.Wants), so the mapping here is total: Success
        // pings, Failure reports down, and nothing else can get this far.
        var isFailure = notification.Signal == MonitorSignal.Failure;
        var url = isFailure
            ? config.Url.TrimEnd('/') + "/fail"
            : config.Url;

        if (notification.Signal == MonitorSignal.None)
        {
            // Unreachable through the publisher, kept as a guard rather than an
            // assumption: a direct caller handing over a non-signal event would
            // otherwise silently ping the check and mark a dead job alive.
            _logger.LogDebug(
                "healthchecks: {EventKey} carries no monitor signal; not pinging.",
                notification.EventKey);
            return;
        }

        // The summary rides along as the body so the monitor's log shows WHY, which
        // is what turns "check failed" into something actionable.
        using var content = new StringContent(notification.Summary);
        using var response = await _http.PostAsync(url, content, cancellationToken)
                                        .ConfigureAwait(false);

        // Throw on failure — the publisher records it against the subscriber row so
        // a monitor that stopped accepting pings is visible, not silent.
        response.EnsureSuccessStatusCode();
    }
}
