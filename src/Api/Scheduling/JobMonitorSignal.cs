using Coffer.Api.Notifications;

namespace Coffer.Api.Scheduling;

/// <summary>
/// Turns a per-ledger scheduled run into the ledger-scope event its monitor watches.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of <c>SchedulerRunner</c> so the mapping is testable without a database and
/// without a scheduler tick, and out of the handlers because a handler that forgot to
/// emit would produce exactly the silence the monitor exists to detect. The runner emits
/// once per run, including the run that threw, and no handler can opt out.
/// </para>
/// <para>
/// Degraded maps to FAILURE, not success. A healthchecks check has two states, and a run
/// that completed without doing its work must not hold the check green — that is the lie
/// this whole outcome channel was added to prevent. It is safe to be this strict because
/// Degraded is already scoped to a genuine provider outage rather than any error at all:
/// a delisted ticker does not reach here.
/// </para>
/// </remarks>
public static class JobMonitorSignal
{
    /// <summary>
    /// Cap on the reason text carried into a published summary.
    /// </summary>
    /// <remarks>
    /// Matches <c>SchedulerRunner.MaxErrorLength</c>, deliberately. The same message is
    /// written to <c>scheduled_jobs.last_error</c> through <c>Truncate(…, 500)</c>, whose
    /// comment says why: "A message, never a stack trace — the column is surfaced in the
    /// SPA." The published summary had no cap at all, which made the stricter of the two
    /// paths the one nobody looks at.
    ///
    /// This summary travels further than the column does. It is delivered to whatever URL
    /// a ledger holder configured, and <c>ledger_events_read</c> (mig 208) grants SELECT
    /// to every grant holder with no role filter — so a viewer sees it too. An unbounded
    /// provider or Npgsql message is the wrong thing to hand either of them.
    /// </remarks>
    private const int MaxReasonLength = 500;

    /// <summary>The event to publish for one finished run, or null if the job type has
    /// no monitor.</summary>
    public static NotificationEvent? For(string jobType, JobRunOutcome outcome, Exception? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

        // A job type with no monitor cannot be watched, which MonitorScopeTests forbids —
        // but the runner also dispatches job types it has no handler for, so answering
        // null is better than throwing inside a tick.
        if (!NotificationMonitors.IsKnown(NotificationScope.Ledger, jobType)) return null;

        var failed = outcome.Result != JobRunResult.Ok || failure is not null;

        // The exception's MESSAGE never reaches the summary — only its class does. This
        // text is delivered to whatever URL a ledger holder configured and is readable by
        // every grant holder including a viewer, and a raw Npgsql or HttpRequestException
        // message routinely names a host, a port, a database or a full URL. Capping the
        // length bounded how much of that escaped; it did not stop it escaping.
        //
        // The detail is not lost: the runner logs the exception in full, and the capped
        // message still goes to scheduled_jobs.last_error. Both stay on this side of the
        // wire, which is the distinction that matters.
        //
        // outcome.Message is handler-authored prose rather than exception text, so it is
        // passed through — still capped, because the feed handler's version grows with the
        // number of faulted connections.
        var reason = failure is not null
            ? Coffer.Api.Notifications.PublishedFailure.Describe(failure)
            : Truncate(outcome.Message, MaxReasonLength);

        return new NotificationEvent(
            Severity: failed ? NotificationSeverity.Warning : NotificationSeverity.Info,
            Topic: TopicFor(jobType),
            EventKey: jobType + (failed ? ".failed" : ".succeeded"),
            Summary: failed
                ? $"Scheduled {jobType} did not complete: {reason ?? "no reason recorded"}"
                : $"Scheduled {jobType} completed.",
            // The exception's TYPE, which is a class name and carries no user or
            // infrastructure data, so a triager gets the useful half without the leak.
            Detail: failure is null
                ? null
                : Coffer.Api.Notifications.PublishedFailure.DetailFor(failure),
            Monitor: jobType,
            Signal: failed ? MonitorSignal.Failure : MonitorSignal.Success);
    }

    /// <summary>
    /// Trims to <paramref name="max"/>, marking that it was trimmed.
    /// </summary>
    /// <remarks>
    /// The ellipsis matters: a reader who cannot tell a complete message from a cut one
    /// will read the truncated tail as the whole story.
    /// </remarks>
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// The topic a job's events belong to, so a MESSAGE target can filter on it.
    /// </summary>
    /// <remarks>
    /// Both scopes' topic CHECK constraints already allow every value used here (mig 207
    /// and 208), so no topic migration is needed — worth stating because a topic outside
    /// the CHECK would make the ledger_events INSERT raise 23514 BEFORE delivery and take
    /// the whole scheduler tick down with it.
    /// </remarks>
    private static string TopicFor(string jobType) => jobType switch
    {
        NotificationMonitors.QuoteRefresh => NotificationTopics.Quotes,
        NotificationMonitors.Snapshot => NotificationTopics.Snapshot,
        NotificationMonitors.FeedSync => NotificationTopics.Sync,
        _ => NotificationTopics.Scheduler,
    };
}
