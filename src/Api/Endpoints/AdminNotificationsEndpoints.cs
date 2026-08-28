using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Errors;
using Coffer.Api.Notifications;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Managing where deployment-scope notifications are delivered (ADR-0096 D7/D8).
/// </summary>
/// <remarks>
/// Admin-gated, and deployment-scope by nature: these targets belong to the
/// installation, not to a ledger, which is why they are here rather than under
/// <c>/api/ledgers/{id}/</c>.
/// </remarks>
public static class AdminNotificationsEndpoints
{
    public static IEndpointRouteBuilder MapAdminNotificationsEndpoints(
        this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/notifications")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        group.MapGet("/providers", ListProviders);
        group.MapGet("/subscribers", ListSubscribersAsync);
        group.MapPost("/subscribers", CreateSubscriberAsync);
        group.MapDelete("/subscribers/{id:guid}", DeleteSubscriberAsync);
        group.MapGet("/events", ListEventsAsync);

        return routes;
    }

    /// <summary>What this build can deliver to, and what each can promise.</summary>
    private static IResult ListProviders(IEnumerable<INotificationSubscriber> providers) =>
        Results.Ok(providers
            .OrderBy(p => p.SubscriberKey, StringComparer.Ordinal)
            .Select(p => new NotificationProviderDto(
                p.SubscriberKey,
                p.DisplayName,
                p.Capability == SubscriberCapability.Heartbeat,
                p.Capability == SubscriberCapability.Heartbeat
                    ? NotificationMonitors.Deployment
                    : []))
            .ToList());

    /// <summary>
    /// Configured targets, plus whether absence detection is covered at all.
    /// </summary>
    /// <remarks>
    /// <c>hasHeartbeatSubscriber</c> is the honest part. Message-only providers
    /// reach a phone in seconds when something breaks and say nothing when the
    /// deployment is dead — the failure that actually cost 68 hours. Surfacing the
    /// answer means configuring only a chat webhook is a visible trade rather than
    /// a silent one.
    /// </remarks>
    private static async Task<IResult> ListSubscribersAsync(
        AppDbContext db,
        NotificationPublisher publisher,
        CancellationToken cancellationToken)
    {
        var rows = await db.NotificationSubscribers.AsNoTracking()
            .OrderBy(r => r.SubscriberKey)
            .Select(r => new NotificationSubscriberDto(
                r.Id,
                r.SubscriberKey,
                r.DisplayName,
                r.IsEnabled,
                r.MinSeverity,
                r.Topics,
                r.Monitors,
                r.LastSuccessAt,
                r.LastFailureAt,
                r.LastError,
                r.ConsecutiveFailures))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var coverage = await publisher.MonitorCoverageAsync(cancellationToken)
                                      .ConfigureAwait(false);

        return Results.Ok(new NotificationSubscribersResponse(rows, coverage));
    }

    private static async Task<IResult> CreateSubscriberAsync(
        CreateNotificationSubscriberRequest request,
        AppDbContext db,
        LedgerKeyService keys,
        IEnumerable<INotificationSubscriber> providers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!providers.Any(p => p.SubscriberKey == request.SubscriberKey))
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "No provider '" + request.SubscriberKey + "' in this build.");

        if (!NotificationSeverity.Ascending.Contains(request.MinSeverity))
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "min_severity must be one of: "
                + string.Join(", ", NotificationSeverity.Ascending) + ".");

        if (string.IsNullOrWhiteSpace(request.Url))
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "A delivery URL is required.");

        // The monitor binding is required for a dead-man's switch and meaningless for a
        // message target, and the capability — not the request — decides which. A
        // healthchecks URL identifies ONE check, so an unbound heartbeat target would be
        // configured, look healthy in the list, and receive nothing at all; that silent
        // shape is what this rejects. The database cannot express this rule, because
        // capability lives in the provider rather than the row.
        var provider = providers.First(p => p.SubscriberKey == request.SubscriberKey);
        var wantsMonitor = provider.Capability == SubscriberCapability.Heartbeat;
        var monitor = string.IsNullOrWhiteSpace(request.Monitors)
            ? null
            : request.Monitors.Trim();

        if (wantsMonitor && monitor is null)
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "'" + request.SubscriberKey + "' detects absence, so it must name the "
                + "monitor its URL watches: "
                + string.Join(", ", NotificationMonitors.Deployment) + ".");

        if (!wantsMonitor && monitor is not null)
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "'" + request.SubscriberKey + "' delivers messages and is not bound to a "
                + "monitor; leave it unset.");

        if (monitor is not null
            && !NotificationMonitors.IsKnown(NotificationScope.Deployment, monitor))
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "Unknown monitor '" + monitor + "'. This build monitors: "
                + string.Join(", ", NotificationMonitors.Deployment) + ".");

        // Sealed on the way in and never read back out (ADR-0096 D8): a webhook URL
        // routinely carries its own token, so the list endpoint above deliberately
        // has no field for it.
        var sealedConfig = keys.SealWithMasterKey(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new SubscriberConfig(request.Url.Trim(), request.Token))));

        var row = new Db.Entities.NotificationSubscriberRow
        {
            SubscriberKey = request.SubscriberKey,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? request.SubscriberKey
                : request.DisplayName.Trim(),
            MinSeverity = request.MinSeverity,
            Topics = request.Topics is { Length: > 0 } ? request.Topics : null,
            Monitors = monitor,
            ConfigCiphertext = sealedConfig,
            UpdatedAt = DateTime.UtcNow,
        };
        db.NotificationSubscribers.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new NotificationSubscriberDto(
            row.Id, row.SubscriberKey, row.DisplayName, row.IsEnabled,
            row.MinSeverity, row.Topics, row.Monitors, null, null, null, 0));
    }

    private static async Task<IResult> DeleteSubscriberAsync(
        Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var removed = await db.NotificationSubscribers
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>Recent deployment-scope events, newest first.</summary>
    private static async Task<IResult> ListEventsAsync(
        AppDbContext db, CancellationToken cancellationToken, int limit = 50) =>
        Results.Ok(await db.SystemEvents.AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(e => new SystemEventDto(
                e.Id, e.OccurredAt, e.Severity, e.Topic, e.EventKey, e.Summary))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));
}
