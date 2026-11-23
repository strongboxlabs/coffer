// What needs the reader, computed once for the header sentence and the widgets
// alike. A count in the sentence and the rows in a widget come from the same
// function over the same query, so the two can never disagree.

import { shiftDateInputValue } from '@/lib/dates';
import { formatRelative } from '@/lib/ledgerOperationDisplay';
import type { AccountSummary, LedgerConsistencyState, ReminderSummary, UpcomingOccurrence } from '@/lib/types';

import type { SettingsTab } from '../settings/settingsTabs';

/** The fixed look-back for overdue reminders. Fixed, so editing a widget never
 *  changes what the header counts. */
export const OVERDUE_LOOKBACK_DAYS = 90;

/** How far ahead the one agenda read reaches: the widest Bills horizon. */
export const AGENDA_AHEAD_DAYS = 30;

/** The header's "to post by" horizon, fixed for the same reason. */
export const HEADER_HORIZON_DAYS = 7;

/** The one agenda window the header and Bills share. */
export function agendaWindow(today: string): { from: string; to: string } {
    return {
        from: shiftDateInputValue(today, -OVERDUE_LOOKBACK_DAYS),
        to: shiftDateInputValue(today, AGENDA_AHEAD_DAYS),
    };
}

/**
 * Whether the auto-post job will post this occurrence: its series has auto-post days
 * and the job's own rule leaves nothing in its way, its date is today or later, and
 * the job is switched on. Anything else, including a past occurrence the job has not
 * posted, is a person's to post (ADR-0109 D4).
 */
export function willAutoPost(o: UpcomingOccurrence, today: string, autoPostEnabled: boolean): boolean {
    return autoPostEnabled
        && o.autoPost != null
        && o.autoPost.excludedReason === null
        && o.date >= today;
}

/** An occurrence waiting for a person: not yet posted or skipped, and not the job's. */
export function isManual(o: UpcomingOccurrence, today: string, autoPostEnabled: boolean): boolean {
    return o.kind === 'reminder' && !willAutoPost(o, today, autoPostEnabled);
}

/** One series with occurrences past due: counted once, however many it has. */
export interface OverdueSeries {
    reminderId: string;
    payee: string | null;
    /** The earliest overdue occurrence in the window, which acting on cascades
     *  nothing. Null for a series overdue only before the window. */
    earliest: UpcomingOccurrence | null;
    /** The latest overdue occurrence in the window: acting on it skips the earlier ones. */
    latest: UpcomingOccurrence | null;
    /** Overdue occurrences inside the window. */
    count: number;
    /** The series' next-due date when it is older than the look-back: the row says
     *  "overdue since" it instead of counting. */
    overdueSince: string | null;
}

/**
 * Overdue reminders, one per series, earliest first. Occurrences in the window
 * dated before today that nobody posted or skipped, plus any active series whose
 * next due date is older than the window, so a long-neglected series is not lost
 * past the look-back.
 */
export function overdueBySeries(
    occurrences: readonly UpcomingOccurrence[],
    reminders: readonly ReminderSummary[],
    today: string,
): OverdueSeries[] {
    const windowStart = shiftDateInputValue(today, -OVERDUE_LOOKBACK_DAYS);
    const bySeries = new Map<string, OverdueSeries>();
    for (const o of [...occurrences].sort((a, b) => a.date.localeCompare(b.date))) {
        if (o.kind !== 'reminder' || o.date >= today) continue;
        const row = bySeries.get(o.reminderId);
        if (row) {
            row.count++;
            row.latest = o;
        } else {
            bySeries.set(o.reminderId, {
                reminderId: o.reminderId, payee: o.payee, earliest: o, latest: o, count: 1, overdueSince: null,
            });
        }
    }
    for (const r of reminders) {
        if (!r.isActive || !r.nextDueDate || r.nextDueDate >= windowStart) continue;
        const row = bySeries.get(r.id);
        if (row) row.overdueSince = r.nextDueDate;
        else {
            bySeries.set(r.id, {
                reminderId: r.id, payee: r.payee, earliest: null, latest: null, count: 0, overdueSince: r.nextDueDate,
            });
        }
    }
    return [...bySeries.values()].sort((a, b) =>
        (a.overdueSince ?? a.earliest?.date ?? '').localeCompare(b.overdueSince ?? b.earliest?.date ?? ''));
}

/** Occurrences a person has to post from today through `days` ahead. */
export function dueWithin(
    occurrences: readonly UpcomingOccurrence[],
    today: string,
    days: number,
    autoPostEnabled: boolean,
): UpcomingOccurrence[] {
    const last = shiftDateInputValue(today, days);
    return occurrences
        .filter((o) => o.date >= today && o.date <= last && isManual(o, today, autoPostEnabled))
        .sort((a, b) => a.date.localeCompare(b.date));
}

/** One account with rows waiting for review. */
export interface ReviewAccount {
    id: string;
    name: string;
    count: number;
}

/**
 * Rows waiting for review, per account, most first. Real accounts only: a feed row's
 * Uncategorized leg is counted on the category too, which would count every row twice.
 */
export function reviewByAccount(accounts: readonly AccountSummary[]): { total: number; accounts: ReviewAccount[] } {
    const list = accounts
        .filter((a) => a.accountType !== 'category' && a.needsReviewCount > 0)
        .map((a) => ({ id: a.id, name: a.name, count: a.needsReviewCount }))
        .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name));
    return { total: list.reduce((sum, a) => sum + a.count, 0), accounts: list };
}

/**
 * What the review list leaves out, per account: the account's count less the rows
 * the list shows on it, in the order {@link reviewByAccount} gives. A count is of
 * entries, so a split the list shows is one row however many lines it has. Never
 * below zero (a count read before a row the list already shows), and an account
 * with nothing left is omitted.
 */
export function reviewRemainders(
    accounts: readonly ReviewAccount[],
    shown: readonly { accountId: string }[],
): { total: number; accounts: ReviewAccount[] } {
    const onAccount = new Map<string, number>();
    for (const { accountId } of shown) onAccount.set(accountId, (onAccount.get(accountId) ?? 0) + 1);
    const list = accounts
        .map((a) => ({ ...a, count: Math.max(0, a.count - (onAccount.get(a.id) ?? 0)) }))
        .filter((a) => a.count > 0);
    return { total: list.reduce((sum, a) => sum + a.count, 0), accounts: list };
}

/** When the consistency verdict was last taken, or that it never was: no verdict
 *  reads "not checked yet", never unhealthy. */
export function checkedPhrase(state: LedgerConsistencyState | undefined): string {
    return state?.lastCheckedAt ? `consistency checked ${formatRelative(state.lastCheckedAt)}` : 'consistency not checked yet';
}

/** Where a problem is fixed. */
export type ProblemDestination =
    | { kind: 'settings'; tab: SettingsTab; check?: true }
    | { kind: 'reminders' }
    | { kind: 'system-backups' };

/**
 * The page that holds the control that fixes a subject's problem. A job's disable
 * goes to the page holding that job's switch; anything this does not recognise goes
 * to the ledger's notifications, which list every notice.
 */
export function problemDestination(subjectKey: string, eventKey: string): ProblemDestination {
    const job = eventKey.endsWith('.disabled') ? subjectKey.slice(0, -1) : null;
    const subject = job ?? subjectKey.slice(0, -1);
    switch (subject) {
        case 'consistency':
            return { kind: 'settings', tab: 'general', check: true };
        case 'feed-sync':
            return { kind: 'settings', tab: 'feeds' };
        case 'quote-refresh':
            return { kind: 'settings', tab: 'quotes' };
        case 'snapshot':
            return { kind: 'settings', tab: 'snapshots' };
        case 'reminder-auto-post':
        case 'reminder-backlog':
            return { kind: 'reminders' };
        case 'backup':
        case 'drive':
            return { kind: 'system-backups' };
        default:
            return { kind: 'settings', tab: 'notifications' };
    }
}
