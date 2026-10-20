namespace Coffer.Api.Contracts;

/// <summary>
/// A per-ledger daily schedule (mig 136/137). Wire shape for
/// <c>GET/PUT /api/ledgers/{id}/schedules/{jobType}</c>. Hour/minute are the
/// time-of-day in <see cref="Timezone"/> (an IANA id the SPA sends from the
/// user's browser; null → server-local). <see cref="LastRunAt"/>/
/// <see cref="NextRunAt"/> are read-only (worker bookkeeping, ignored on PUT).
/// </summary>
/// <remarks>
/// <para>
/// <b>LastRunAt means ATTEMPTED, not succeeded.</b> The scheduler claims its slot and
/// commits before dispatching the handler, so a job that has failed every night for a
/// week still carries a fresh timestamp. <see cref="LastSuccessAt"/> (mig 252, FU-070) is
/// the last run that did its work, fully or in part; show that.
/// </para>
/// <para>
/// <b>NothingDoneSince</b> is set when the last run completed without failing and did NONE
/// of its work (FU-071): an auto-snapshot skipped for a full pool, every quote provider down,
/// every feed connection awaiting reconnection. It is the last success, or when the schedule
/// was set up if there never was one. A day past it the schedule is in danger, whatever the
/// failure count says, because a did-nothing run does not count as a failure.
/// </para>
/// <para>
/// <b>LastError is not proof of failure.</b> A degraded run — the handler completed but
/// achieved less than it should — populates it with ConsecutiveFailures at zero. Read
/// the count for health and the message for the reason; a failure badge keyed off the
/// message alone mislabels every degraded run.
/// </para>
/// <para>
/// DisabledReason is a ScheduleDisableReasons value, or null when the job is enabled or
/// a person switched it off. It is current state and clears on re-enable (mig 216).
/// </para>
/// </remarks>
public sealed record ScheduleDto(
    bool Enabled,
    int HourLocal,
    int MinuteLocal,
    string? Timezone = null,
    DateTime? LastRunAt = null,
    DateTime? NextRunAt = null,
    int ConsecutiveFailures = 0,
    string? LastError = null,
    DateTime? LastFailureAt = null,
    string? DisabledReason = null,
    DateTime? LastSuccessAt = null,
    DateTime? NothingDoneSince = null);

/// <summary>The schedule-health rules both schedule shapes share.</summary>
public static class ScheduleHealthRules
{
    /// <summary>
    /// Since when a job has done nothing, when its last run completed without failing and did
    /// none of its work; null otherwise. Such a run leaves a message (<paramref name="lastError"/>)
    /// with no failure counted, and does not move the last success, so its last success is older
    /// than its last attempt. A run that did its work in part moves both to the same instant.
    /// </summary>
    public static DateTime? NothingDoneSince(
        string? lastError, int consecutiveFailures, DateTime? lastRunAt, DateTime? lastSuccessAt, DateTime createdAt) =>
        lastError is not null && consecutiveFailures == 0 && lastRunAt is { } ran
        && (lastSuccessAt is null || lastSuccessAt < ran)
            ? lastSuccessAt ?? createdAt
            : null;
}
