using System.Net.Http.Headers;
using System.Net.Http.Json;

using Coffer.Api.Notifications;

namespace Coffer.Api.Notifications.Providers;

/// <summary>
/// A generic JSON POST: ntfy, Discord, Slack, or anything that accepts a webhook.
/// </summary>
/// <remarks>
/// One implementation covers every chat and push service worth naming, which is why
/// it ships alongside <see cref="HealthchecksSubscriber"/> rather than a second
/// service-specific provider.
/// <para>
/// <b>It cannot detect absence</b>, and that limitation is the point of
/// <see cref="SubscriberCapability"/>. This subscriber reaches a phone in seconds
/// when something goes wrong, and says nothing at all when the deployment is dead —
/// which is the failure that actually happened. Configure this and nothing else and
/// the install has no way to notice its own silence, so the settings surface warns
/// when no heartbeat subscriber is enabled.
/// </para>
/// </remarks>
public sealed class WebhookSubscriber : INotificationSubscriber
{
    private readonly HttpClient _http;

    public WebhookSubscriber(HttpClient http) => _http = http;

    public string SubscriberKey => "webhook";

    public string DisplayName => "Webhook (ntfy, Discord, Slack, custom)";

    public SubscriberCapability Capability => SubscriberCapability.Message;

    public async Task DeliverAsync(
        NotificationEvent notification,
        SubscriberConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(config);

        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
        {
            // A flat shape on purpose: every consumer here is either a human reading
            // a chat message or a template someone wrote by hand, and both cope
            // badly with nesting.
            Content = JsonContent.Create(new
            {
                severity = notification.Severity,
                topic = notification.Topic,
                @event = notification.EventKey,
                summary = notification.Summary,
                detail = notification.Detail,
            }),
        };

        if (!string.IsNullOrWhiteSpace(config.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);

        using var response = await _http.SendAsync(request, cancellationToken)
                                        .ConfigureAwait(false);

        // Throw on failure so the publisher records it: a webhook whose token was
        // rotated must not fail quietly.
        response.EnsureSuccessStatusCode();
    }
}
