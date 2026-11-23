import { describe, expect, it } from 'vitest';

import type { AccountSummary, ReminderSummary, UpcomingOccurrence } from '@/lib/types';

import {
    dueWithin,
    isManual,
    overdueBySeries,
    problemDestination,
    reviewByAccount,
    reviewRemainders,
    willAutoPost,
} from './attention';

const TODAY = '2026-03-12';

function occ(over: Partial<UpcomingOccurrence>): UpcomingOccurrence {
    return {
        date: TODAY,
        kind: 'reminder',
        reminderId: 'series-a',
        headerId: null,
        payee: 'Payee A',
        memo: null,
        amount: -10,
        seriesNextDue: null,
        sourceAccountId: 'acct-a',
        sourceAccountName: 'Account A',
        currencyCode: 'USD',
        autoPost: null,
        ...over,
    };
}

function series(over: Partial<ReminderSummary>): ReminderSummary {
    return {
        id: 'series-a',
        payee: 'Payee A',
        memo: null,
        amount: -10,
        rrule: 'FREQ=MONTHLY',
        startDate: '2025-01-01',
        endDate: null,
        nextDueDate: null,
        autoCommitDaysBefore: null,
        isActive: true,
        isLoanReminder: false,
        origin: 'manual',
        ...over,
    };
}

describe('willAutoPost', () => {
    const posts = { autoPost: { excludedReason: null } };

    it('is true only for a series the job posts, dated today or later, with the job on', () => {
        expect(willAutoPost(occ({ ...posts, date: TODAY }), TODAY, true)).toBe(true);
        expect(willAutoPost(occ({ ...posts, date: '2026-03-20' }), TODAY, true)).toBe(true);
    });

    it('is false for a past occurrence, a series the job leaves, no auto-post, or the job off', () => {
        expect(willAutoPost(occ({ ...posts, date: '2026-03-11' }), TODAY, true)).toBe(false);
        expect(willAutoPost(occ({ autoPost: { excludedReason: 'inactive-account' } }), TODAY, true)).toBe(false);
        expect(willAutoPost(occ({ autoPost: null }), TODAY, true)).toBe(false);
        expect(willAutoPost(occ(posts), TODAY, false)).toBe(false);
    });

    it('leaves a managed loan payment across currencies to a person, as the job sets it aside (ADR-0114 D13)', () => {
        const aside = occ({ date: '2026-03-15', reminderId: 'loan', autoPost: { excludedReason: 'loan-across-currencies' } });

        expect(willAutoPost(aside, TODAY, true)).toBe(false);
        expect(isManual(aside, TODAY, true)).toBe(true);
        // Counted where a person's occurrences are, beside one the job posts, which is not.
        const due = dueWithin([aside, occ({ date: '2026-03-15', reminderId: 'job', ...posts })], TODAY, 7, true);
        expect(due.map((o) => o.reminderId)).toEqual(['loan']);
    });
});

describe('overdueBySeries', () => {
    it('counts a series once, from its earliest overdue occurrence', () => {
        const rows = overdueBySeries([
            occ({ date: '2026-02-12' }),
            occ({ date: '2026-01-12' }),
            occ({ reminderId: 'series-b', payee: 'Payee B', date: '2026-03-01' }),
            // Not overdue: due today, already posted, or skipped.
            occ({ date: TODAY }),
            occ({ reminderId: 'series-c', date: '2026-03-01', kind: 'scheduled' }),
            occ({ reminderId: 'series-d', date: '2026-03-01', kind: 'skipped' }),
        ], [], TODAY);

        expect(rows.map((r) => [r.reminderId, r.count, r.earliest?.date])).toEqual([
            ['series-a', 2, '2026-01-12'],
            ['series-b', 1, '2026-03-01'],
        ]);
    });

    it('counts a past occurrence of an auto-post series as overdue: the job will not post it', () => {
        const rows = overdueBySeries([occ({ date: '2026-03-10', autoPost: { excludedReason: null } })], [], TODAY);
        expect(rows).toHaveLength(1);
    });

    it('keeps a series whose next due date is older than the look-back', () => {
        const rows = overdueBySeries([], [
            series({ id: 'old', payee: 'Payee O', nextDueDate: '2025-09-01' }),
            series({ id: 'off', nextDueDate: '2025-09-01', isActive: false }),
            series({ id: 'recent', nextDueDate: '2026-03-01' }),
        ], TODAY);

        expect(rows.map((r) => [r.reminderId, r.overdueSince, r.count])).toEqual([['old', '2025-09-01', 0]]);
    });
});

describe('dueWithin', () => {
    it('lists a person\'s occurrences from today through the horizon, the job\'s left out', () => {
        const due = dueWithin([
            occ({ date: TODAY, reminderId: 'today' }),
            occ({ date: '2026-03-19', reminderId: 'edge' }),
            occ({ date: '2026-03-20', reminderId: 'beyond' }),
            occ({ date: '2026-03-11', reminderId: 'past' }),
            occ({ date: '2026-03-15', reminderId: 'job', autoPost: { excludedReason: null } }),
            occ({ date: '2026-03-15', reminderId: 'left', autoPost: { excludedReason: 'no-lines' } }),
        ], TODAY, 7, true);

        expect(due.map((o) => o.reminderId)).toEqual(['today', 'left', 'edge']);
    });
});

describe('reviewByAccount', () => {
    it('counts real accounts only, most first', () => {
        const account = (id: string, accountType: string, needsReviewCount: number) =>
            ({ id, name: id, accountType, needsReviewCount }) as AccountSummary;
        const review = reviewByAccount([
            account('card', 'credit_card', 4),
            account('uncategorized', 'category', 6),
            account('checking', 'bank', 2),
            account('savings', 'bank', 0),
        ]);
        expect(review.total).toBe(6);
        expect(review.accounts.map((a) => a.id)).toEqual(['card', 'checking']);
    });
});

describe('reviewRemainders', () => {
    const counts = [
        { id: 'card', name: 'Card A', count: 4 },
        { id: 'checking', name: 'Account A', count: 2 },
        { id: 'savings', name: 'Account B', count: 1 },
    ];
    const txn = (accountId: string) => ({ accountId, entry: { kind: 'txn' as const } });
    /** A split the list shows: one entry holding its lines. */
    const split = (accountId: string) => ({ accountId, entry: { kind: 'group' as const, legs: [{}, {}, {}] } });

    it('leaves each account its count less the rows shown on it, in the counts\' order', () => {
        const left = reviewRemainders(counts, [txn('card'), txn('checking'), txn('card')]);
        expect(left.accounts).toEqual([
            { id: 'card', name: 'Card A', count: 2 },
            { id: 'checking', name: 'Account A', count: 1 },
            { id: 'savings', name: 'Account B', count: 1 },
        ]);
        expect(left.total).toBe(4);
    });

    it('counts a split the list shows once, however many lines it has', () => {
        const left = reviewRemainders(counts, [split('card')]);
        expect(left.accounts[0]).toEqual({ id: 'card', name: 'Card A', count: 3 });
        expect(left.total).toBe(6);
    });

    it('floors an account at zero and omits it, when the list shows more than its count', () => {
        const left = reviewRemainders(counts, [txn('savings'), txn('savings'), txn('checking'), txn('checking')]);
        expect(left.accounts).toEqual([{ id: 'card', name: 'Card A', count: 4 }]);
        expect(left.total).toBe(4);
    });
});

describe('problemDestination', () => {
    it('sends each problem to the page that holds its fix', () => {
        expect(problemDestination('consistency.', 'consistency.drift')).toEqual({ kind: 'settings', tab: 'general', check: true });
        expect(problemDestination('feed-sync.', 'feed-sync.failed')).toEqual({ kind: 'settings', tab: 'feeds' });
        expect(problemDestination('reminder-auto-post.', 'reminder-auto-post.disabled')).toEqual({ kind: 'reminders' });
        expect(problemDestination('quote-refresh.', 'quote-refresh.disabled')).toEqual({ kind: 'settings', tab: 'quotes' });
        expect(problemDestination('snapshot.', 'snapshot.failed')).toEqual({ kind: 'settings', tab: 'snapshots' });
        expect(problemDestination('backup.', 'backup.none')).toEqual({ kind: 'system-backups' });
        expect(problemDestination('drive.', 'drive.stale')).toEqual({ kind: 'system-backups' });
        expect(problemDestination('reminder-backlog.', 'reminder-backlog.deferred')).toEqual({ kind: 'reminders' });
        expect(problemDestination('', 'dotless')).toEqual({ kind: 'settings', tab: 'notifications' });
    });
});
