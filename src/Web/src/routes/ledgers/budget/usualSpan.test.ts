import { describe, expect, it } from 'vitest';

import { formatLedgerDate } from '@/lib/dates';

import { bandLabel, fillLabel, typicalBasis, unsetLabel, usualSpan } from './usualSpan';

// The reported month and its three-month window: December, January and February.
const MONTH = '2026-03';
const MONTH_NAME = 'March 2026';
const WINDOW = 3;
const span = (historyFrom: string | null) => usualSpan({ month: MONTH, windowMonths: WINDOW, historyFrom });

describe('usualSpan: what the usual figures average over', () => {
    it('is the whole window when the ledger is at least as old as it', () => {
        expect(span(null)).toEqual({ kind: 'months', months: WINDOW });
    });

    it('is the whole months since the ledger began on a month\'s first day', () => {
        const MONTHS_SINCE = 2;
        expect(span('2026-01-01')).toEqual({ kind: 'months', months: MONTHS_SINCE });
        expect(span('2026-02-01')).toEqual({ kind: 'months', months: 1 });
    });

    it('is the months since a day when the ledger began mid-month, since no one count is true of every day', () => {
        expect(span('2026-01-15')).toEqual({ kind: 'since', from: '2026-01-15' });
    });

    it('is nothing when the ledger began on or after the reported month\'s first day', () => {
        expect(span('2026-03-01')).toEqual({ kind: 'none' });
        expect(span('2026-03-09')).toEqual({ kind: 'none' });
    });
});

describe('the labels that name it', () => {
    it('are unchanged with the whole window', () => {
        const whole = span(null);
        expect(bandLabel(whole)).toBe(`shaded band = your last ${WINDOW} months, same days`);
        expect(unsetLabel(whole, MONTH_NAME)).toBe(`unset = average of the ${WINDOW} months before ${MONTH_NAME}`);
        expect(fillLabel(whole)).toBe(`Fill from ${WINDOW}-month average`);
        expect(typicalBasis(whole)).toBe(`typical, ${WINDOW} months`);
    });

    it('say the true count when fewer whole months were averaged, one month in the singular', () => {
        const two = span('2026-01-01');
        expect(bandLabel(two)).toBe('shaded band = your last 2 months, same days');
        expect(unsetLabel(two, MONTH_NAME)).toBe(`unset = average of the 2 months before ${MONTH_NAME}`);
        expect(fillLabel(two)).toBe('Fill from 2-month average');
        expect(typicalBasis(two)).toBe('typical, 2 months');

        const one = span('2026-02-01');
        expect(bandLabel(one)).toBe('shaded band = your last month, same days');
        expect(unsetLabel(one, MONTH_NAME)).toBe(`unset = average of the month before ${MONTH_NAME}`);
        expect(fillLabel(one)).toBe('Fill from 1-month average');
        expect(typicalBasis(one)).toBe('typical, 1 month');
    });

    it('name the day the history starts when the count differs across the month\'s days', () => {
        const FROM = '2026-01-15';
        const since = span(FROM);
        const day = formatLedgerDate(FROM);
        expect(bandLabel(since)).toBe(`shaded band = your months since ${day}, same days`);
        expect(unsetLabel(since, MONTH_NAME)).toBe(`unset = average of your months since ${day}, before ${MONTH_NAME}`);
        expect(fillLabel(since)).toBe(`Fill from average since ${day}`);
        expect(typicalBasis(since)).toBe(`typical, since ${day}`);
    });

    it('say there is no history when nothing before the month was averaged, and draw no band caption', () => {
        const none = span('2026-03-09');
        expect(bandLabel(none)).toBeNull();
        expect(unsetLabel(none, MONTH_NAME)).toBe(`unset = no history before ${MONTH_NAME}`);
        expect(fillLabel(none)).toBe('Fill from average');
        expect(typicalBasis(none)).toBe('typical, no history yet');
    });
});
