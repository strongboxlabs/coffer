using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;

namespace Coffer.Api.Notifications;

/// <summary>
/// The subjects still outstanding on a set of ledgers: the one query behind the
/// cross-ledger attention summary and a ledger's list of outstanding problems.
/// </summary>
/// <remarks>
/// <para>A subject is outstanding when its latest warning-or-above event has no info
/// event of the same subject after it (<see cref="EventSubjects"/>). Grouped in SQL, so
/// the result is at most one row per ledger per subject per severity whatever the log
/// holds: any cap on fetched history would let a noisy subject crowd a quiet one out.
/// The latest event per subject is its current state, so a critical since downgraded
/// does not pin a ledger red.</para>
/// <para>One query for both readers, so the count on the navigation indicator and the
/// rows a ledger lists can never disagree.</para>
/// </remarks>
internal static class OutstandingSubjects
{
    /// <summary>Warning and above: every enabled job records an info success on every
    /// run, so anything laxer is a ledger where nothing went wrong.</summary>
    internal static readonly string[] ProblemSeverities = NotificationSeverity.Ascending
        .Where(sev => NotificationSeverity.MeetsFloor(sev, NotificationSeverity.Warning))
        .ToArray();

    /// <summary>
    /// Each outstanding subject on <paramref name="ledgerIds"/>: its latest problem's
    /// severity and time, and the last all-clear before it, if there was one.
    /// </summary>
    internal static async Task<List<OutstandingSubject>> ListAsync(
        AppDbContext db, Guid[] ledgerIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // The newest problem per ledger / subject / severity. Severity is in the key
        // rather than aggregated because the two cannot be picked apart afterwards:
        // MAX(occurred_at) tells you WHEN, and it is the row AT that instant whose
        // severity is the subject's current state.
        var problems = await db.LedgerEvents.AsNoTracking()
            .Where(e => ledgerIds.Contains(e.LedgerId) && ProblemSeverities.Contains(e.Severity))
            .WithSubject()
            .GroupBy(e => new { e.LedgerId, e.Subject, e.Severity })
            .Select(g => new
            {
                g.Key.LedgerId,
                g.Key.Subject,
                g.Key.Severity,
                OccurredAt = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (problems.Count == 0) return [];

        // The newest all-clear per ledger / subject. Only the newest is needed: a
        // subject is fine iff SOMETHING later than its last problem said so.
        var clears = await db.LedgerEvents.AsNoTracking()
            .Where(e => ledgerIds.Contains(e.LedgerId) && e.Severity == NotificationSeverity.Info)
            .WithSubject()
            .GroupBy(e => new { e.LedgerId, e.Subject })
            .Select(g => new
            {
                g.Key.LedgerId,
                g.Key.Subject,
                OccurredAt = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var clearedAt = clears.ToDictionary(c => (c.LedgerId, c.Subject), c => c.OccurredAt);

        return problems
            .GroupBy(p => (p.LedgerId, p.Subject))
            // Within a subject, the latest event is its current state.
            .Select(g => g.OrderByDescending(p => p.OccurredAt).First())
            .Select(p => new OutstandingSubject(
                p.LedgerId, p.Subject, p.Severity, p.OccurredAt,
                clearedAt.TryGetValue((p.LedgerId, p.Subject), out var cleared) ? cleared : null))
            .Where(s => s.LastClearAt is not { } cleared || cleared <= s.LatestAt)
            .ToList();
    }

    /// <summary>A severity's rank by the declared order, for ordering: "warning" sorts
    /// after "critical" alphabetically, which would invert every comparison.</summary>
    internal static int Rank(string severity) => Array.IndexOf(NotificationSeverity.Ascending, severity);
}

/// <summary>
/// One outstanding subject: <see cref="Severity"/> and <see cref="LatestAt"/> are its
/// latest problem's; <see cref="LastClearAt"/> is the last info event of the subject,
/// earlier than that problem, or null when it never had one.
/// </summary>
internal sealed record OutstandingSubject(
    Guid LedgerId, string Subject, string Severity, DateTime LatestAt, DateTime? LastClearAt);
