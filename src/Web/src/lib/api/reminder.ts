// Reminders read + manage + authoring client (ADR-0047 / ADR-0049 / ADR-0051).
// Mirror of the routes on RemindersEndpoints.cs (list / upcoming / detail /
// active / skip / fire, plus create + edit for bank and investment series).

import type {
    ReminderSummary,
    UpcomingOccurrence,
    ReminderDetail,
    SetReminderActiveRequest,
    SkipReminderRequest,
    SkipReminderResponse,
    FireReminderRequest,
    FireReminderResponse,
    FireBankReminderRequest,
    FireInvestmentReminderRequest,
    CreateReminderRequest,
    CreateInvestmentReminderRequest,
    EditReminderRequest,
    EditInvestmentReminderRequest,
} from '../types/reminder';
import { request } from './_request';

const base = (ledgerId: string) =>
    `/api/ledgers/${encodeURIComponent(ledgerId)}/reminders`;

/** GET /reminders — the management list (one row per series). */
export function fetchReminders(ledgerId: string): Promise<ReminderSummary[]> {
    return request<ReminderSummary[]>(base(ledgerId));
}

/** GET /reminders/upcoming?from&to — the agenda/calendar window (dates are
 * 'YYYY-MM-DD'). The server caps the window at ~2 years. */
export function fetchUpcomingReminders(
    ledgerId: string, from: string, to: string,
): Promise<UpcomingOccurrence[]> {
    const qs = new URLSearchParams({ from, to }).toString();
    return request<UpcomingOccurrence[]>(`${base(ledgerId)}/upcoming?${qs}`);
}

/** GET /reminders/{id} — series detail + template legs (editor/detail load). */
export function fetchReminderDetail(
    ledgerId: string, reminderId: string,
): Promise<ReminderDetail> {
    return request<ReminderDetail>(`${base(ledgerId)}/${encodeURIComponent(reminderId)}`);
}

/** PATCH /reminders/{id}/active — soft disable/enable (204). */
export function setReminderActive(
    ledgerId: string, reminderId: string, body: SetReminderActiveRequest,
): Promise<void> {
    return request<void>(`${base(ledgerId)}/${encodeURIComponent(reminderId)}/active`, {
        method: 'PATCH', body,
    });
}

/** POST /reminders/{id}/skip — suppress one occurrence. */
export function skipReminder(
    ledgerId: string, reminderId: string, body: SkipReminderRequest,
): Promise<SkipReminderResponse> {
    return request<SkipReminderResponse>(
        `${base(ledgerId)}/${encodeURIComponent(reminderId)}/skip`, { method: 'POST', body });
}

/** POST /reminders/{id}/fire — materialize one occurrence into a committed
 * transaction (Moneydance "Record next occurrence"). */
export function fireReminder(
    ledgerId: string, reminderId: string, body: FireReminderRequest,
): Promise<FireReminderResponse> {
    return request<FireReminderResponse>(
        `${base(ledgerId)}/${encodeURIComponent(reminderId)}/fire`, { method: 'POST', body });
}

/** POST /reminders/{id}/fire/bank — adjust-at-post for a BANK series: commit
 * the EDITED transaction (incl. splits) as the occurrence (ADR-0049). */
export function fireReminderBank(
    ledgerId: string, reminderId: string, body: FireBankReminderRequest,
): Promise<FireReminderResponse> {
    return request<FireReminderResponse>(
        `${base(ledgerId)}/${encodeURIComponent(reminderId)}/fire/bank`, { method: 'POST', body });
}

/** POST /reminders/{id}/fire/investment — adjust-at-post for an INVESTMENT
 * series: commit the EDITED transaction as the occurrence (ADR-0049). */
export function fireReminderInvestment(
    ledgerId: string, reminderId: string, body: FireInvestmentReminderRequest,
): Promise<FireReminderResponse> {
    return request<FireReminderResponse>(
        `${base(ledgerId)}/${encodeURIComponent(reminderId)}/fire/investment`, { method: 'POST', body });
}

// ----- authoring: create / edit a series (ADR-0051 slice B) -----------------

/** POST /reminders — create a BANK-shape reminder series (201 -> ReminderDetail). */
export function createReminderBank(
    ledgerId: string, body: CreateReminderRequest,
): Promise<ReminderDetail> {
    return request<ReminderDetail>(base(ledgerId), { method: 'POST', body });
}

/** POST /reminders/investment — create an INVESTMENT-shape series. */
export function createReminderInvestment(
    ledgerId: string, body: CreateInvestmentReminderRequest,
): Promise<ReminderDetail> {
    return request<ReminderDetail>(`${base(ledgerId)}/investment`, { method: 'POST', body });
}

/** PATCH /reminders/{id} — edit a BANK series (200 -> ReminderDetail). */
export function updateReminderBank(
    ledgerId: string, reminderId: string, body: EditReminderRequest,
): Promise<ReminderDetail> {
    return request<ReminderDetail>(
        `${base(ledgerId)}/${encodeURIComponent(reminderId)}`, { method: 'PATCH', body });
}

/** PATCH /reminders/{id}/investment — edit an INVESTMENT series. */
export function updateReminderInvestment(
    ledgerId: string, reminderId: string, body: EditInvestmentReminderRequest,
): Promise<ReminderDetail> {
    return request<ReminderDetail>(
        `${base(ledgerId)}/${encodeURIComponent(reminderId)}/investment`, { method: 'PATCH', body });
}
