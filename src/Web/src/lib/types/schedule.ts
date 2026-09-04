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
    /** Failure streak; zero after any run that did not throw. */
    consecutiveFailures: number;
    /** Newest failure OR degraded message. Present does not mean failing — read
     *  consecutiveFailures for that. */
    lastError: string | null;
    lastFailureAt: string | null;
    /** Why it is off when a person did not switch it off (mig 216); null for enabled
     *  or user-disabled. */
    disabledReason: string | null;
}
