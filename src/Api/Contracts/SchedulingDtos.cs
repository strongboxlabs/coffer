namespace Coffer.Api.Contracts;

/// <summary>
/// A per-ledger daily schedule (mig 136/137). Wire shape for
/// <c>GET/PUT /api/ledgers/{id}/schedules/{jobType}</c>. Hour/minute are the
/// time-of-day in <see cref="Timezone"/> (an IANA id the SPA sends from the
/// user's browser; null → server-local). <see cref="LastRunAt"/>/
/// <see cref="NextRunAt"/> are read-only (worker bookkeeping, ignored on PUT).
/// </summary>
public sealed record ScheduleDto(
    bool Enabled,
    int HourLocal,
    int MinuteLocal,
    string? Timezone = null,
    DateTime? LastRunAt = null,
    DateTime? NextRunAt = null);
