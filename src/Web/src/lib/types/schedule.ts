// Per-ledger daily schedule (mig 136) — one shape for every job type
// (quote-refresh, snapshot). Mirror of the API ScheduleDto.

export interface Schedule {
    enabled: boolean;
    hourLocal: number;
    minuteLocal: number;
    /** IANA tz the time is interpreted in (the user's browser tz at save). */
    timezone: string | null;
    lastRunAt: string | null;
    nextRunAt: string | null;
}
