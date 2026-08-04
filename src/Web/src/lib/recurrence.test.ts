import { describe, expect, it } from 'vitest';

import { buildRrule, humanizeRrule, parseRrule, type RecurrenceState } from './recurrence';

describe('humanizeRrule', () => {
    it('humanizes daily rules with and without interval', () => {
        expect(humanizeRrule('FREQ=DAILY')).toBe('Every day');
        expect(humanizeRrule('FREQ=DAILY;INTERVAL=3')).toBe('Every 3 days');
        // a biweekly paycheck modeled as daily-14 reads clearly (vs MD's "daily (14 days)")
        expect(humanizeRrule('FREQ=DAILY;INTERVAL=14')).toBe('Every 14 days');
    });

    it('humanizes weekly rules with day-of-week, ordered Sun..Sat', () => {
        expect(humanizeRrule('FREQ=WEEKLY')).toBe('Every week');
        expect(humanizeRrule('FREQ=WEEKLY;BYDAY=MO')).toBe('Every week on Mon');
        expect(humanizeRrule('FREQ=WEEKLY;INTERVAL=2;BYDAY=MO')).toBe('Every 2 weeks on Mon');
        // unordered input -> stable Sun..Sat reading order
        expect(humanizeRrule('FREQ=WEEKLY;BYDAY=FR,MO,WE')).toBe('Every week on Mon, Wed, Fri');
    });

    it('humanizes monthly rules incl. last-day and interval ("every third")', () => {
        expect(humanizeRrule('FREQ=MONTHLY;BYMONTHDAY=1')).toBe('Monthly on the 1st');
        expect(humanizeRrule('FREQ=MONTHLY;BYMONTHDAY=22')).toBe('Monthly on the 22nd');
        expect(humanizeRrule('FREQ=MONTHLY;BYMONTHDAY=-1')).toBe('Monthly on the last day');
        // clearer than MD's "monthly (every third)"
        expect(humanizeRrule('FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=15')).toBe('Every 3 months on the 15th');
    });

    it('humanizes yearly rules', () => {
        expect(humanizeRrule('FREQ=YEARLY')).toBe('Every year');
        expect(humanizeRrule('FREQ=YEARLY;INTERVAL=2')).toBe('Every 2 years');
    });

    it('tolerates an RRULE: prefix and lowercase', () => {
        expect(humanizeRrule('RRULE:FREQ=MONTHLY;BYMONTHDAY=1')).toBe('Monthly on the 1st');
        expect(humanizeRrule('freq=daily')).toBe('Every day');
    });

    it('returns Custom for blank/irregular and Custom schedule for unknown FREQ', () => {
        expect(humanizeRrule(null)).toBe('Custom');
        expect(humanizeRrule(undefined)).toBe('Custom');
        expect(humanizeRrule('')).toBe('Custom');
        expect(humanizeRrule('   ')).toBe('Custom');
        expect(humanizeRrule('FREQ=HOURLY')).toBe('Custom schedule');
        expect(humanizeRrule('not an rrule')).toBe('Custom schedule');
    });

    it('does not invent a day-of-month it was not given (ignores unsupported sub-parts, never a wrong phrase)', () => {
        expect(humanizeRrule('FREQ=MONTHLY')).toBe('Monthly');
        expect(humanizeRrule('FREQ=MONTHLY;INTERVAL=2')).toBe('Every 2 months');
        // an nth-weekday rule we don't model reads as plain "Monthly" - true,
        // if incomplete - not a wrong day-of-month.
        expect(humanizeRrule('FREQ=MONTHLY;BYSETPOS=2;BYDAY=MO')).toBe('Monthly');
    });
});

function state(overrides: Partial<RecurrenceState>): RecurrenceState {
    return { freq: 'daily', interval: 1, weekdays: [], monthDay: 1, ...overrides };
}

describe('buildRrule', () => {
    it('builds a rule for each freq', () => {
        expect(buildRrule(state({ freq: 'daily' }))).toBe('FREQ=DAILY');
        expect(buildRrule(state({ freq: 'weekly' }))).toBe('FREQ=WEEKLY');
        expect(buildRrule(state({ freq: 'monthly', monthDay: 1 }))).toBe('FREQ=MONTHLY;BYMONTHDAY=1');
        expect(buildRrule(state({ freq: 'yearly' }))).toBe('FREQ=YEARLY');
    });

    it('omits INTERVAL at 1 and emits it when > 1', () => {
        expect(buildRrule(state({ freq: 'daily', interval: 1 }))).toBe('FREQ=DAILY');
        expect(buildRrule(state({ freq: 'daily', interval: 3 }))).toBe('FREQ=DAILY;INTERVAL=3');
        expect(buildRrule(state({ freq: 'weekly', interval: 2, weekdays: ['MO'] })))
            .toBe('FREQ=WEEKLY;INTERVAL=2;BYDAY=MO');
    });

    it('weekly: appends BYDAY in canonical Sun..Sat order regardless of input order', () => {
        expect(buildRrule(state({ freq: 'weekly', weekdays: ['FR', 'MO', 'WE'] })))
            .toBe('FREQ=WEEKLY;BYDAY=MO,WE,FR');
        // empty weekdays -> no BYDAY part
        expect(buildRrule(state({ freq: 'weekly', weekdays: [] }))).toBe('FREQ=WEEKLY');
    });

    it('monthly: numeric BYMONTHDAY and last-day (-1)', () => {
        expect(buildRrule(state({ freq: 'monthly', monthDay: 10 }))).toBe('FREQ=MONTHLY;BYMONTHDAY=10');
        expect(buildRrule(state({ freq: 'monthly', monthDay: 'last' }))).toBe('FREQ=MONTHLY;BYMONTHDAY=-1');
        expect(buildRrule(state({ freq: 'monthly', interval: 3, monthDay: 15 })))
            .toBe('FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=15');
    });

    it('daily/yearly: never emit BY* parts even if weekdays/monthDay are set', () => {
        expect(buildRrule(state({ freq: 'daily', weekdays: ['MO'], monthDay: 5 }))).toBe('FREQ=DAILY');
        expect(buildRrule(state({ freq: 'yearly', weekdays: ['MO'], monthDay: 5 }))).toBe('FREQ=YEARLY');
    });
});

describe('parseRrule', () => {
    it('parses each supported freq with interval defaulting to 1', () => {
        expect(parseRrule('FREQ=DAILY')).toEqual(state({ freq: 'daily' }));
        expect(parseRrule('FREQ=DAILY;INTERVAL=3')).toEqual(state({ freq: 'daily', interval: 3 }));
        expect(parseRrule('FREQ=WEEKLY')).toEqual(state({ freq: 'weekly' }));
        expect(parseRrule('FREQ=YEARLY;INTERVAL=2')).toEqual(state({ freq: 'yearly', interval: 2 }));
    });

    it('parses weekly BYDAY into canonical order; [] when absent', () => {
        expect(parseRrule('FREQ=WEEKLY;BYDAY=FR,MO,WE'))
            .toEqual(state({ freq: 'weekly', weekdays: ['MO', 'WE', 'FR'] }));
        expect(parseRrule('FREQ=WEEKLY')).toEqual(state({ freq: 'weekly', weekdays: [] }));
    });

    it('parses monthly numeric, last-day, and default day-of-month', () => {
        expect(parseRrule('FREQ=MONTHLY;BYMONTHDAY=10')).toEqual(state({ freq: 'monthly', monthDay: 10 }));
        expect(parseRrule('FREQ=MONTHLY;BYMONTHDAY=-1')).toEqual(state({ freq: 'monthly', monthDay: 'last' }));
        // MONTHLY with no BYMONTHDAY defaults to day 1
        expect(parseRrule('FREQ=MONTHLY')).toEqual(state({ freq: 'monthly', monthDay: 1 }));
    });

    it('tolerates an RRULE: prefix and lowercase keys', () => {
        expect(parseRrule('RRULE:FREQ=MONTHLY;BYMONTHDAY=10'))
            .toEqual(state({ freq: 'monthly', monthDay: 10 }));
        expect(parseRrule('freq=daily;interval=2')).toEqual(state({ freq: 'daily', interval: 2 }));
    });

    it('returns null for blank/null/unsupported rules (never guesses)', () => {
        expect(parseRrule(null)).toBeNull();
        expect(parseRrule(undefined)).toBeNull();
        expect(parseRrule('')).toBeNull();
        expect(parseRrule('   ')).toBeNull();
        expect(parseRrule('FREQ=HOURLY')).toBeNull();
        expect(parseRrule('not an rrule')).toBeNull();
        expect(parseRrule('FREQ=MONTHLY;BYSETPOS=1;BYDAY=MO')).toBeNull();
        // ordinal BYDAY (nth-weekday) is out of the supported set
        expect(parseRrule('FREQ=WEEKLY;BYDAY=2MO')).toBeNull();
        // BY* parts on freqs that don't take them
        expect(parseRrule('FREQ=DAILY;BYDAY=MO')).toBeNull();
        expect(parseRrule('FREQ=YEARLY;BYMONTHDAY=5')).toBeNull();
        expect(parseRrule('FREQ=WEEKLY;BYMONTHDAY=5')).toBeNull();
        expect(parseRrule('FREQ=MONTHLY;BYDAY=MO')).toBeNull();
        // out-of-range day-of-month
        expect(parseRrule('FREQ=MONTHLY;BYMONTHDAY=40')).toBeNull();
    });
});

describe('buildRrule <-> parseRrule round-trip', () => {
    const samples: RecurrenceState[] = [
        state({ freq: 'daily' }),
        state({ freq: 'daily', interval: 14 }),
        state({ freq: 'weekly', interval: 2, weekdays: ['MO', 'WE'] }),
        state({ freq: 'monthly', monthDay: 10 }),
        state({ freq: 'monthly', interval: 3, monthDay: 'last' }),
        state({ freq: 'yearly' }),
    ];

    it('round-trips representative states', () => {
        for (const s of samples) {
            expect(parseRrule(buildRrule(s))).toEqual(s);
        }
    });

    it('every buildRrule output is also humanizable (shared pattern set)', () => {
        for (const s of samples) {
            expect(humanizeRrule(buildRrule(s))).not.toBe('Custom schedule');
        }
    });
});
