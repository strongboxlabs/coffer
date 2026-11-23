using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;

namespace Coffer.Api.Notifications;

/// <summary>
/// The deployment-scope subjects still outstanding: what the System-settings gear and the System tabs
/// show (ADR-0102, amended for ADR-0110), by the same rule the ledger indicators use.
/// </summary>
/// <remarks>
/// A subject is outstanding when its latest warning-or-above event has no info event of the same
/// subject after it. Grouped in SQL, so the result is at most one row per subject per severity
/// whatever the log holds, and the latest event is the subject's current state: a critical since
/// downgraded does not pin the gear red. The ledger version is <see cref="OutstandingSubjects"/>;
/// there is no ledger id here, because a deployment event is a fact about the installation.
/// </remarks>
internal static class SystemOutstandingSubjects
{
    internal static async Task<List<SystemOutstandingSubject>> ListAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var problems = await db.SystemEvents.AsNoTracking()
            .Where(e => OutstandingSubjects.ProblemSeverities.Contains(e.Severity))
            .WithSubject()
            .GroupBy(e => new { e.Subject, e.Severity })
            .Select(g => new
            {
                g.Key.Subject,
                g.Key.Severity,
                OccurredAt = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (problems.Count == 0) return [];

        var clears = await db.SystemEvents.AsNoTracking()
            .Where(e => e.Severity == NotificationSeverity.Info)
            .WithSubject()
            .GroupBy(e => e.Subject)
            .Select(g => new { Subject = g.Key, OccurredAt = g.Max(e => e.OccurredAt) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var clearedAt = clears.ToDictionary(c => c.Subject, c => c.OccurredAt);

        return problems
            .GroupBy(p => p.Subject)
            .Select(g => g.OrderByDescending(p => p.OccurredAt).First())
            .Select(p => new SystemOutstandingSubject(
                p.Subject, p.Severity, p.OccurredAt,
                clearedAt.TryGetValue(p.Subject, out var cleared) ? cleared : null))
            .Where(s => s.LastClearAt is not { } cleared || cleared <= s.LatestAt)
            .ToList();
    }
}

/// <summary>One outstanding deployment-scope subject, as <see cref="OutstandingSubject"/> is for a ledger.</summary>
internal sealed record SystemOutstandingSubject(
    string Subject, string Severity, DateTime LatestAt, DateTime? LastClearAt);
