import type { Schedule } from '../types/schedule';
import { request } from './_request';

/** GET /schedules/{jobType} — the per-ledger daily schedule (defaulted). */
export function fetchSchedule(ledgerId: string, jobType: string): Promise<Schedule> {
    return request<Schedule>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/schedules/${encodeURIComponent(jobType)}`,
    );
}

/** PUT /schedules/{jobType} — set enabled + the daily time-of-day. */
export function saveSchedule(
    ledgerId: string,
    jobType: string,
    body: { enabled: boolean; hourLocal: number; minuteLocal: number; timezone: string },
): Promise<Schedule> {
    return request<Schedule>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/schedules/${encodeURIComponent(jobType)}`,
        { method: 'PUT', body },
    );
}
