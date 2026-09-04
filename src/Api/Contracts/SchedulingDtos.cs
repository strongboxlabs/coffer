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
/// week still carries a fresh timestamp. Anything rendering it must pair it with the
/// failure fields, or it states the opposite of the truth for exactly the job worth
/// looking at. There is no last_success_at column.
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
    string? DisabledReason = null);
