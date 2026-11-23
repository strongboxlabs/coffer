// The home screen's reads, each in one place so the header sentence and the widget
// that lists the same thing share one query and one cache entry. Keys shared with
// other pages use those pages' keys; the rest sit under ['home', ledgerId, …], which
// a write anywhere in the ledger invalidates (registerInvalidation).

import { useQuery } from '@tanstack/react-query';

import {
    fetchAccounts,
    fetchBudgetProgress,
    fetchConsistencyState,
    fetchFeedConnections,
    fetchLedgerOverview,
    fetchOutstandingProblems,
    fetchReminders,
    fetchSchedules,
    fetchTransactionEntries,
    fetchUpcomingReminders,
    type EntriesDirection,
} from '@/lib/api';

import { agendaWindow } from './attention';

/** The one agenda window Bills and the header share (fixed look-back and reach). */
export function useAgenda(ledgerId: string, today: string) {
    const { from, to } = agendaWindow(today);
    return useQuery({
        // Under the Reminders page's prefix, so its fire and skip invalidate this too.
        queryKey: ['reminders', 'upcoming', ledgerId, from, to],
        queryFn: () => fetchUpcomingReminders(ledgerId, from, to),
    });
}

/** The series list, for next-due dates older than the agenda window. */
export function useReminderList(ledgerId: string) {
    return useQuery({ queryKey: ['reminders', ledgerId], queryFn: () => fetchReminders(ledgerId) });
}

/** Every job's schedule: whether auto-post is on, and when each job last ran. */
export function useSchedules(ledgerId: string) {
    return useQuery({ queryKey: ['schedules', ledgerId], queryFn: () => fetchSchedules(ledgerId) });
}

/** The rail's own accounts read (inactive included), so To review costs nothing extra. */
export function useRailAccounts(ledgerId: string) {
    return useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
    });
}

/** The stored consistency verdict. */
export function useConsistencyState(ledgerId: string) {
    return useQuery({
        queryKey: ['home', ledgerId, 'consistency-state'],
        queryFn: () => fetchConsistencyState(ledgerId),
    });
}

/** The outstanding problems, fetched only when the rail's count says there are some. */
export function useOutstanding(ledgerId: string, count: number | undefined) {
    return useQuery({
        queryKey: ['home', ledgerId, 'outstanding', count],
        queryFn: () => fetchOutstandingProblems(ledgerId),
        enabled: (count ?? 0) > 0,
    });
}

/** The rail's feed-connections read. */
export function useFeedConnections(ledgerId: string) {
    return useQuery({
        queryKey: ['feed-connections', ledgerId],
        queryFn: () => fetchFeedConnections(ledgerId),
    });
}

/**
 * The newest register entries across the ledger, up to the user's today: what a
 * register shows as Scheduled is not recent. Every entry, reviewed or not; the
 * rows awaiting review are marked where they are drawn.
 */
export function useRecentEntries(ledgerId: string, today: string, rows: number, direction: EntriesDirection) {
    return useQuery({
        queryKey: ['home', ledgerId, 'recent', { rows, direction, today }],
        queryFn: () => fetchTransactionEntries(ledgerId, { direction, today, limit: rows }),
    });
}

/**
 * The newest entries awaiting review across the ledger, `rows` of them: the rows the
 * review counts count. Read only while there is something to review.
 */
export function useReviewEntries(ledgerId: string, rows: number, enabled: boolean) {
    return useQuery({
        queryKey: ['home', ledgerId, 'review-lines', { rows }],
        queryFn: () => fetchTransactionEntries(ledgerId, { needsReview: true, limit: rows }),
        enabled,
    });
}

/**
 * The user's current month against its marks over `windowMonths`, under the Budget
 * page's key. This month and the Watchlist both read it at This month's window, so
 * the two share one request and cannot disagree about a category.
 */
export function useBudgetProgress(ledgerId: string, today: string, windowMonths: number) {
    const month = today.slice(0, 7);
    return useQuery({
        queryKey: ['budget-progress', ledgerId, month, windowMonths, today],
        queryFn: () => fetchBudgetProgress(ledgerId, month, windowMonths),
    });
}

/** The overview, with net worth at the end of `since` when it is given. */
export function useOverview(ledgerId: string, today: string, since?: string) {
    return useQuery({
        queryKey: ['overview', ledgerId, { today, since: since ?? null }],
        queryFn: () => fetchLedgerOverview(ledgerId, since),
    });
}

/** The month-end before `today`: what "change since" compares with. */
export function previousMonthEnd(today: string): string {
    const [y, m] = today.split('-').map(Number);
    const end = new Date(Date.UTC(y, m - 1, 0));
    return end.toISOString().slice(0, 10);
}
