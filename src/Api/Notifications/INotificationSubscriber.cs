namespace Coffer.Api.Notifications;

/// <summary>
/// What a delivery target can actually promise — ADR-0096 D7.
/// </summary>
/// <remarks>
/// This is the load-bearing distinction in the whole subsystem, and it is not
/// cosmetic. Only a <see cref="Heartbeat"/> provider can tell you that something
/// FAILED TO HAPPEN, because noticing silence requires something outside the
/// deployment to be counting. A dead container sends no Discord message and writes
/// no database row; a dead-man's-switch monitor notices the missing ping.
/// <para>
/// Configure only <see cref="Message"/> providers and you have kept the exact
/// failure mode that cost 68 hours — which is why the configuration surface warns
/// when no heartbeat-capable subscriber is enabled.
/// </para>
/// </remarks>
public enum SubscriberCapability
{
    /// <summary>
    /// Delivers what it is given and nothing more: ntfy, Discord, Slack, a generic
    /// webhook. Cannot detect absence.
    /// </summary>
    Message,

    /// <summary>
    /// Pinged on success, alerts when the pings stop: healthchecks.io (SaaS or
    /// self-hosted), Uptime Kuma push monitors. Detects absence.
    /// </summary>
    Heartbeat,
}

/// <summary>
/// A configured delivery target for deployment-scope notifications.
/// </summary>
/// <remarks>
/// Mirrors the provider shape already proven for quotes (<c>IQuotePullProvider</c>,
/// ADR-0033): a stable key, a display name, DI registration, and an orchestrator
/// that owns routing and persistence. Providers deliver; they do not decide what to
/// deliver or record what they did.
/// </remarks>
public interface INotificationSubscriber
{
    /// <summary>
    /// Stable identifier, matching <c>notification_subscribers.subscriber_key</c>.
    /// Lowercase, hyphenated.
    /// </summary>
    string SubscriberKey { get; }

    /// <summary>Human-readable name for the settings surface.</summary>
    string DisplayName { get; }

    /// <summary>
    /// What this provider can promise. See <see cref="SubscriberCapability"/> — the
    /// difference decides whether an install can detect its own silence.
    /// </summary>
    SubscriberCapability Capability { get; }

    /// <summary>
    /// Deliver one event to one configured target.
    /// </summary>
    /// <param name="notification">The event being delivered.</param>
    /// <param name="config">
    /// The target's opened configuration — the URL or token, already unsealed by the
    /// publisher. A provider never touches the ciphertext or the master key.
    /// </param>
    /// <remarks>
    /// Throw to signal failure. The publisher records it against the subscriber row
    /// (<c>last_error</c>, <c>consecutive_failures</c>) so a target that quietly
    /// stops working is visible rather than silent — otherwise the subsystem
    /// rebuilds the problem it exists to solve, one layer up.
    /// </remarks>
    Task DeliverAsync(
        NotificationEvent notification,
        SubscriberConfig config,
        CancellationToken cancellationToken);
}

/// <summary>
/// One target's opened configuration.
/// </summary>
/// <param name="Url">
/// Where to send. For a heartbeat provider this is the monitor's base ping URL;
/// the provider appends its own success/fail path.
/// </param>
/// <param name="Token">Optional bearer/auth token, for providers that take one.</param>
public sealed record SubscriberConfig(string Url, string? Token = null);
