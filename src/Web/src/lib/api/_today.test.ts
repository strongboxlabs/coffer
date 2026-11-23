import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

import { todayInputValue } from '../dates';
import { fetchAccountBalances } from './account';
import { fetchHoldings } from './holding';
import { checkLedgerConsistency, repairProjection } from './ledger';
import { fetchLedgerOverview } from './overview';
import {
    fetchReminderDetail,
    fetchReminders,
    fetchUpcomingReminders,
    fireReminderBank,
} from './reminder';
import { fetchSecurities, fetchSecurity } from './security';

let urls: string[];

beforeEach(() => {
    urls = [];
    vi.stubGlobal('fetch', vi.fn((url: string) => {
        urls.push(url);
        return Promise.resolve(new Response('{}', {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
        }));
    }));
});

afterEach(() => {
    vi.unstubAllGlobals();
});

function sentParams(): URLSearchParams {
    expect(urls).toHaveLength(1);
    return new URL(urls[0], 'http://coffer.test').searchParams;
}

/**
 * Every read of a current value sends the user's LOCAL date, the one the register
 * sends, so the server cuts the balance exactly where the register's Scheduled rows
 * begin. Asserted on what crosses the wire, because a client that forgot the date
 * would still render a plausible figure: the server's UTC fallback, wrong for part of
 * every day west or east of UTC.
 */
describe('current-value reads send the local date', () => {
    const calls: Array<[string, () => Promise<unknown>]> = [
        ['sidebar balances', () => fetchAccountBalances('l1')],
        ['dashboard overview', () => fetchLedgerOverview('l1')],
        ['portfolio card', () => fetchHoldings('l1', 'a1')],
        ['securities catalog', () => fetchSecurities('l1')],
        ['securities catalog, searched', () => fetchSecurities('l1', 'fund')],
        ['security detail', () => fetchSecurity('l1', 's1')],
        ['consistency report', () => checkLedgerConsistency('l1')],
        // The repair answers every projection checked again, the advisory's value included.
        ['consistency repair', () => repairProjection('l1', 'trade_prices')],
    ];

    it.each(calls)('%s', async (_name, call) => {
        await call();
        expect(sentParams().get('today')).toBe(todayInputValue());
    });

    it('keeps the other parameters beside it', async () => {
        await fetchSecurities('l1', ' fund ');
        expect(sentParams().get('q')).toBe('fund');
    });
});

/**
 * A managed loan's split is priced on the loan's balance as of the payment's own date,
 * never the caller's (ADR-0078, 2026-10-21), and a reminder's full payment reads no
 * balance at all, so no reminder call sends the user's date: one here would suggest a
 * dependency the server does not have.
 */
describe('reminder calls send no date of their own', () => {
    const calls: Array<[string, () => Promise<unknown>]> = [
        ['reminders list', () => fetchReminders('l1')],
        ['reminders agenda', () => fetchUpcomingReminders('l1', '2026-01-01', '2026-01-31')],
        ['reminder detail', () => fetchReminderDetail('l1', 'r1')],
        ['reminder detail, for an occurrence', () => fetchReminderDetail('l1', 'r1', '2026-01-10')],
        ['reminder fire, bank', () => fireReminderBank('l1', 'r1', {
            occurrenceDate: '2026-01-10', sourceAccountId: 'a1', postings: [],
            notes: { side: 'a1' },
        })],
    ];

    it.each(calls)('%s', async (_name, call) => {
        await call();
        expect(sentParams().has('today')).toBe(false);
    });

    it('the detail names the occurrence its split is priced for, when given one', async () => {
        await fetchReminderDetail('l1', 'r1', '2026-01-10');
        expect(sentParams().get('occurrenceDate')).toBe('2026-01-10');

        urls = [];
        await fetchReminderDetail('l1', 'r1');
        expect(sentParams().has('occurrenceDate')).toBe(false);
    });

    it('the agenda keeps its window', async () => {
        await fetchUpcomingReminders('l1', '2026-01-01', '2026-01-31');
        const params = sentParams();
        expect(params.get('from')).toBe('2026-01-01');
        expect(params.get('to')).toBe('2026-01-31');
    });
});
