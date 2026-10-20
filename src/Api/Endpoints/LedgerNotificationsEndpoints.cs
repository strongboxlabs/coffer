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

        // Read for the GET, so a viewer sees where their ledger reports. Changing where it
        // reports is the owner's (FU-095): a target receives the ledger's events, and an
        // editor could point them at an address of their own.
        group.MapGet("/", GetAsync).AsLedgerRead();
        group.MapPost("/subscribers", CreateSubscriberAsync).AsLedgerOwner();
        group.MapDelete("/subscribers/{id:guid}", DeleteSubscriberAsync).AsLedgerOwner();

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

        // CROSS-ledger, so it hangs off /api/ledgers rather than the group above —
        // the question it answers is "which of my ledgers needs me", which no
        // per-ledger route can be asked. No RequireLedgerAccess for the same reason:
        // there is no ledger in the path to gate on, and the scoping is the grant
        // join below plus ledger_events' RLS behind it.
        //
        // One route rather than one fetch per ledger. The navigation renders on every
        // page, so N requests behind it would be N requests behind everything.
        //
        // "attention" cannot collide with /api/ledgers/{ledgerId:guid} — the guid
        // constraint refuses it — but it is a literal segment either way, which wins.
        routes.MapGet("/api/ledgers/attention", ListAttentionAsync)
              .RequireAuthorization();

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
        Coffer.Api.Http.OutboundPolicy policy,
        LedgerKeyService keys,
        AppDbContext db,
        TimeProvider clock,
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
        // Public https addresses only, unless an operator allowed the network (FU-082):
        // the server POSTs to this, and a target was a way to make it reach Postgres, the
        // Docker network or the LAN.
        if (policy.Refusal(request.Url.Trim()) is { } refused)
            return BusinessError.Problem(BusinessError.Codes.NotificationUrlRefused, refused);

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
        // Held until the sealed config is saved (FU-078).
        using var sealing = keys.EnterSealing();
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
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
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
            .Select(p => EventSubjects.Of(p.EventKey))
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
            .Where(c => subjects.Contains(EventSubjects.Of(c.EventKey), StringComparer.Ordinal))
            .GroupBy(c => EventSubjects.Of(c.EventKey), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.OccurredAt).ToList(),
                          StringComparer.Ordinal);

        var events = problems.Select(p =>
        {
            DateTime? resolvedAt = null;
            if (clearedBySubject.TryGetValue(EventSubjects.Of(p.EventKey), out var times))
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
    /// Which of the caller's ledgers has something outstanding, and how badly.
    /// </summary>
    /// <remarks>
    /// <para>The question behind every navigation indicator. Until this existed a
    /// problem was only visible if you were already looking at the screen that shows
    /// it: the consistency verdict on Settings → General, notifications on Settings →
    /// Notifications, both inside a ledger you had to have chosen first. So "which of
    /// my ledgers needs me" had no answer anywhere.</para>
    ///
    /// <para><b>Sourced from outstanding NOTIFICATIONS, not the consistency
    /// verdict.</b> An indicator that lit only for drift would lie by omission the
    /// moment a bank feed failed or a backup went stale, and the reader's question is
    /// "does this need me", not "is this one check failing".
    /// <c>ledger_consistency_state</c> (mig 242) keeps its own narrower job.</para>
    ///
    /// <para><b>Bounded by subjects, not by history.</b> The obvious shape — fetch the
    /// newest N problems and resolve them in memory, as the per-ledger panel does —
    /// does not survive going cross-ledger: any cap lets one noisy ledger crowd another
    /// out of its own indicator, silently, and the number that felt generous would be
    /// read off today's data. So the grouping happens in SQL and the result is at most
    /// one row per ledger per subject per severity — a handful, whatever the log
    /// holds.</para>
    ///
    /// <para>This matters more since the consistency monitor stopped throttling its
    /// announcements: a ledger drifting for six months now has hundreds of rows saying
    /// one thing. Collapsing to the LATEST event per subject is also what makes the
    /// severity right — a subject's current state is what the most recent event about
    /// it says, so a critical that has since been downgraded does not pin the ledger
    /// red forever.</para>
    ///
    /// <para>Read through the APP-role context, like <see cref="ListEventsAsync"/>, so
    /// <c>ledger_events_read</c> (mig 208) is the backstop. The grant join is the
    /// primary check and is not decoration: this is the one route here with no
    /// <c>ledgerId</c> in the path, so a <c>ServiceDbContextFactory</c> copied in from
    /// a neighbouring repository would hand every ledger's problems to every caller
    /// with no error and nothing to notice.</para>
    /// </remarks>
    private static async Task<IResult> ListAttentionAsync(
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleAsync(currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (visible.Count == 0) return Results.Ok(Array.Empty<LedgerAttentionDto>());

        var visibleIds = visible.Select(l => l.Id).ToArray();

        // Same floor as the panel: warning and above. Every enabled job records an
        // `info` success on every run, so anything laxer would report "attention
        // needed" for a ledger where nothing has gone wrong.
        var wanted = NotificationSeverity.Ascending
            .Where(sev => NotificationSeverity.MeetsFloor(sev, NotificationSeverity.Warning))
            .ToArray();

        // The newest problem per ledger / subject / severity. Severity is in the key
        // rather than aggregated because the two cannot be picked apart afterwards:
        // MAX(occurred_at) tells you WHEN, and it is the row AT that instant whose
        // severity is the subject's current state.
        var problems = await db.LedgerEvents.AsNoTracking()
            .Where(e => visibleIds.Contains(e.LedgerId) && wanted.Contains(e.Severity))
            // The same subject rule as EventSubjects.Of, written so the DATABASE can
            // evaluate it: substring(event_key, 1, strpos(event_key, '.')). Spelled out
            // here rather than called, because EF translates an expression tree and not
            // a method body — a helper would either fail to translate or quietly
            // evaluate on the client over every row in the table.
            //
            // EventSubjectTests runs both spellings over the same keys against a real
            // Postgres and fails if they disagree. A comment asking the next reader to
            // keep them in step would not.
            .GroupBy(e => new
            {
                e.LedgerId,
                Subject = e.EventKey.Substring(0, e.EventKey.IndexOf(".") + 1),
                e.Severity,
            })
            .Select(g => new
            {
                g.Key.LedgerId,
                g.Key.Subject,
                g.Key.Severity,
                OccurredAt = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (problems.Count == 0) return Results.Ok(Array.Empty<LedgerAttentionDto>());

        // The newest all-clear per ledger / subject. Only the newest is needed: a
        // subject is fine iff SOMETHING later than its last problem said so.
        var clears = await db.LedgerEvents.AsNoTracking()
            .Where(e => visibleIds.Contains(e.LedgerId)
                        && e.Severity == NotificationSeverity.Info)
            .GroupBy(e => new
            {
                e.LedgerId,
                Subject = e.EventKey.Substring(0, e.EventKey.IndexOf(".") + 1),
            })
            .Select(g => new
            {
                g.Key.LedgerId,
                g.Key.Subject,
                OccurredAt = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var clearedAt = clears.ToDictionary(
            c => (c.LedgerId, c.Subject), c => c.OccurredAt);

        var outstanding = problems
            .GroupBy(p => (p.LedgerId, p.Subject))
            // Within a subject, the latest event is its current state.
            .Select(g => g.OrderByDescending(p => p.OccurredAt).First())
            .Where(p => !clearedAt.TryGetValue((p.LedgerId, p.Subject), out var cleared)
                        || cleared <= p.OccurredAt)
            .ToList();

        var result = outstanding
            .GroupBy(p => p.LedgerId)
            .Select(g => new LedgerAttentionDto(
                g.Key,
                // Highest wins, by the declared order rather than by string compare:
                // "warning" sorts after "critical" alphabetically, which would invert
                // the whole point of the indicator.
                g.OrderByDescending(p => Array.IndexOf(NotificationSeverity.Ascending, p.Severity))
                 .First().Severity,
                g.Count()))
            .OrderBy(a => a.LedgerId)
            .ToList();

        return Results.Ok(result);
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
