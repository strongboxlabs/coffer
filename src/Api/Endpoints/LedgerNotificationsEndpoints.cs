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
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Notifications;

namespace Coffer.Api.Endpoints;

/// <summary>
/// A ledger's notification settings: inherit the deployment's targets, or name its
/// own (ADR-0096).
/// </summary>
/// <remarks>
/// Ledger-scoped and grant-gated, unlike the deployment surface under
/// <c>/api/admin/notifications</c> — which is the whole point of the scope split.
/// A ledger holder configures where THEIR ledger's events go without being an admin,
/// and without seeing the deployment's targets.
/// </remarks>
public static class LedgerNotificationsEndpoints
{
    public static IEndpointRouteBuilder MapLedgerNotificationsEndpoints(
        this IEndpointRouteBuilder routes)
    {
        // RequireLedgerAccess installs the ADR-0083 D2 gate. Without it the
        // .AsLedgerRead() below is inert decoration — it only attaches metadata that
        // GateAsync reads, and GateAsync is what RequireLedgerAccess adds — so every
        // route here fell through to GetVisibleByIdAsync, which succeeds for ANY
        // grant. A viewer could rewrite where a ledger's alerts go.
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/notifications")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        // Read for the GET; the default (write) stands for the three mutations, so a
        // viewer can see where their ledger reports and only owner/editor may change it.
        group.MapGet("/", GetAsync).AsLedgerRead();
        group.MapPost("/subscribers", CreateSubscriberAsync);
        group.MapDelete("/subscribers/{id:guid}", DeleteSubscriberAsync);

        // Which providers this build can deliver to. Deliberately mirrored onto the
        // ledger scope: the admin route is RequireAdmin, so a non-admin holder
        // configuring their own ledger got a 403 and an empty provider dropdown.
        // The list is build metadata (keys, display names, capability) — no targets,
        // no URLs, nothing ledger-specific.
        group.MapGet("/providers", ListProviders).AsLedgerRead();

        // What this ledger has been TOLD, as opposed to who it tells. Read-level, so a
        // viewer sees it: a viewer who cannot see that the nightly sync has been failing
        // for a week is being kept from the one fact the ledger most needs them to know.
        group.MapGet("/events", ListEventsAsync).AsLedgerRead();

        return routes;
    }

    /// <summary>
    /// What this build can deliver to at ledger scope, and what each can promise.
    /// </summary>
    /// <remarks>
    /// Heartbeat providers WERE excluded here, on the reasoning that every monitor is a
    /// deployment job so a ledger switch could never fire. That was true of the code and
    /// wrong about the design: quote-refresh and snapshot are per-ledger scheduled jobs,
    /// they are exactly the things that go quiet without anyone noticing, and the
    /// scheduler now emits a signal per run for each of them. So the exclusion is gone
    /// and the monitors offered are the LEDGER ones — never the deployment's backup,
    /// which no ledger runs.
    /// </remarks>
    private static IResult ListProviders(IEnumerable<INotificationSubscriber> providers) =>
        Results.Ok(providers
            .OrderBy(p => p.SubscriberKey, StringComparer.Ordinal)
            .Select(p => new NotificationProviderDto(
                p.SubscriberKey,
                p.DisplayName,
                p.Capability == SubscriberCapability.Heartbeat,
                p.Capability == SubscriberCapability.Heartbeat
                    ? NotificationMonitors.For(NotificationScope.Ledger)
                    : []))
            .ToList());

    private static async Task<IResult> GetAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AppDbContext db,
        NotificationPublisher publisher,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var targets = await db.LedgerNotificationSubscribers.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .OrderBy(t => t.SubscriberKey)
            .Select(t => new NotificationSubscriberDto(
                t.Id, t.SubscriberKey, t.DisplayName, t.IsEnabled, t.MinSeverity,
                t.Topics,
                t.Monitors, t.LastSuccessAt, t.LastFailureAt, t.LastError,
                t.ConsecutiveFailures))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var monitors = await publisher.LedgerMonitorStatusAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new LedgerNotificationSettingsDto(
            targets, monitors.Coverage, monitors.WatchingNothing));
    }

    private static async Task<IResult> CreateSubscriberAsync(
        Guid ledgerId,
        CreateNotificationSubscriberRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        IEnumerable<INotificationSubscriber> providers,
        LedgerKeyService keys,
        AppDbContext db,
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

        // The same binding rule as the deployment surface, over a DIFFERENT monitor set:
        // a dead-man's switch names the one per-ledger job its URL watches, a message
        // target names none. The set is scope-checked, so a ledger cannot bind to the
        // deployment's backup job — no ledger runs one, so that switch could never fire.
        var provider = providers.First(p => p.SubscriberKey == request.SubscriberKey);
        var wantsMonitor = provider.Capability == SubscriberCapability.Heartbeat;
        var monitor = string.IsNullOrWhiteSpace(request.Monitors)
            ? null
            : request.Monitors.Trim();

        if (wantsMonitor && monitor is null)
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "'" + request.SubscriberKey + "' detects absence, so it must name the job "
                + "its URL watches: "
                + string.Join(", ", NotificationMonitors.For(NotificationScope.Ledger)) + ".");

        if (!wantsMonitor && monitor is not null)
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "'" + request.SubscriberKey + "' delivers messages and is not bound to a "
                + "job; leave it unset.");

        if (monitor is not null
            && !NotificationMonitors.IsKnown(NotificationScope.Ledger, monitor))
            return BusinessError.Problem(BusinessError.Codes.NotificationProviderUnknown,
                "'" + monitor + "' is not a job this ledger runs. A ledger can watch: "
                + string.Join(", ", NotificationMonitors.For(NotificationScope.Ledger)) + ".");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Sealed on the way in and never read back — same as the deployment surface.
        var sealedConfig = keys.SealWithMasterKey(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new SubscriberConfig(request.Url.Trim(), request.Token))));

        var row = new Db.Entities.LedgerNotificationSubscriberRow
        {
            LedgerId = ledgerId,
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
        db.LedgerNotificationSubscribers.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new NotificationSubscriberDto(
            row.Id, row.SubscriberKey, row.DisplayName, row.IsEnabled,
            row.MinSeverity, row.Topics, row.Monitors, null, null, null, 0));
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/notifications/events</c> — what has gone wrong on
    /// this ledger recently. The in-app surface ADR-0096 D5 promised and never built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WARNING AND ABOVE, not everything, and this is the whole design of the thing.
    /// Every scheduled run of every enabled job publishes an <c>info</c> success, so a
    /// newest-20 over all severities is twenty "quote-refresh completed" lines with the
    /// one failure pushed off the end. A surface that buries the thing you needed to see
    /// under proof that everything else is fine is worse than no surface: it looks
    /// answered.
    /// </para>
    /// <para>
    /// The info rows are not waste — they are the throttle's record
    /// (<c>PublishLedgerThrottledAsync</c> dedupes on them) and the evidence a job ran at
    /// all. They are simply not what this endpoint is for.
    /// </para>
    /// <para>
    /// Reads through the APP-role context on purpose. That makes
    /// <c>ledger_events_read</c> (mig 208) the backstop, and this the first caller that
    /// policy has ever had. The temptation is real: fourteen repositories take
    /// <c>ServiceDbContextFactory</c> and two of them (McpAuditRepository,
    /// AdminAuditRepository) are shaped almost exactly like this, so the copy-paste path
    /// leads straight to a BYPASSRLS context that would serve every ledger's events to
    /// every caller — with no error and nothing to notice. The explicit ledger_id filter
    /// below is the primary check; the policy is what catches the day someone forgets it.
    /// </para>
    /// <para>
    /// Fetches exactly what it renders. The deployment panel asks for the server's default
    /// 50 and then renders <c>.slice(0, 20)</c>, throwing away 30 rows while offering no
    /// way to reach row 51; copying that shape would import the bug.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ListEventsAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AppDbContext db,
        CancellationToken cancellationToken,
        int limit = 20)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Not MeetsFloor: that is a per-subscriber routing decision made in memory. Here
        // the filter has to reach the database so the LIMIT applies AFTER it — filtering
        // in C# would take the newest 20 rows and then discard the successes, which is
        // the bug this endpoint exists to avoid.
        var wanted = NotificationSeverity.Ascending
            .Where(sev => NotificationSeverity.MeetsFloor(sev, NotificationSeverity.Warning))
            .ToArray();

        var take = Math.Clamp(limit, 1, 100);

        var problems = await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId && wanted.Contains(e.Severity))
            .OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .Select(e => new { e.Id, e.OccurredAt, e.Severity, e.Topic, e.EventKey, e.Summary })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (problems.Count == 0) return Results.Ok(Array.Empty<SystemEventDto>());

        // Which of these are still true?
        //
        // Event keys are "<subject>.<outcome>" — quote-refresh.failed,
        // consistency.drift, snapshot.succeeded — so a later INFO event with the same
        // SUBJECT means that subject is fine again. That rule needs no new column and no
        // correlation id, and it makes every scheduled-job warning self-resolving for
        // free, because the success signal each job already publishes on its next good
        // run IS the all-clear.
        //
        // Worth stating why this is derived on read rather than stored: the event log is
        // append-only, and "is this still true?" is a fact about the rows AROUND a row,
        // not a property of it. Writing resolved_at back onto a log entry would mean
        // mutating history to answer a question history can already answer.
        var oldest = problems[^1].OccurredAt;
        var subjects = problems
            .Select(p => Subject(p.EventKey))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // One query for the all-clears, bounded to the window the problems span.
        var clears = await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId
                        && e.Severity == NotificationSeverity.Info
                        && e.OccurredAt > oldest)
            .Select(e => new { e.EventKey, e.OccurredAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var clearedBySubject = clears
            .Where(c => subjects.Contains(Subject(c.EventKey), StringComparer.Ordinal))
            .GroupBy(c => Subject(c.EventKey), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.OccurredAt).ToList(),
                          StringComparer.Ordinal);

        var events = problems.Select(p =>
        {
            DateTime? resolvedAt = null;
            if (clearedBySubject.TryGetValue(Subject(p.EventKey), out var times))
            {
                // The FIRST all-clear after this problem, not the latest: that is when it
                // stopped being true. The latest would re-date an old fix to today.
                var after = times.Where(t => t > p.OccurredAt).ToList();
                if (after.Count > 0) resolvedAt = after.Min();
            }
            return new SystemEventDto(
                p.Id, p.OccurredAt, p.Severity, p.Topic, p.EventKey, p.Summary, resolvedAt);
        }).ToList();

        return Results.Ok(events);
    }

    /// <summary>
    /// The thing an event is ABOUT — the part of the key before the first dot.
    /// </summary>
    /// <remarks>
    /// "quote-refresh.failed" and "quote-refresh.succeeded" are two things said about one
    /// subject. Keeping that convention in a single function means the read side and the
    /// publishers cannot drift on what counts as "the same thing".
    /// </remarks>
    private static string Subject(string eventKey)
    {
        var dot = eventKey.IndexOf('.', StringComparison.Ordinal);
        return dot <= 0 ? eventKey : eventKey[..dot];
    }

    private static async Task<IResult> DeleteSubscriberAsync(
        Guid ledgerId,
        Guid id,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Scoped by ledger as well as id: an id from another ledger must not delete,
        // even though RLS would already refuse it. Defence in depth, and it makes the
        // intent readable without knowing the policy.
        var removed = await db.LedgerNotificationSubscribers
            .Where(t => t.Id == id && t.LedgerId == ledgerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }
}
