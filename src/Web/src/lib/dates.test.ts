import { afterEach, describe, it, expect } from 'vitest';

import * as dates from './dates';
import {
    formatLedgerDate,
    formatLedgerDateCompact,
    formatLedgerDateLong,
    shiftDateInputValue,
    toDateInputValue,
} from './dates';

// All assertions hold regardless of the machine's local timezone:
// the UTC-anchored formatters never shift to the previous/next day
// when the user is east/west of UTC. We don't override the test
// runtime's timezone — these tests pass under any TZ, which is the
// whole point.

describe('formatLedgerDate', () => {
    it('renders a UTC-midnight ISO as the same calendar day in any timezone', () => {
        // 2026-05-18T00:00:00Z is "midnight UTC on May 18" — every
        // user should see "May 18" regardless of local offset. Prior
        // to lib/dates this rendered as "May 17" west of UTC.
        const formatted = formatLedgerDate('2026-05-18T00:00:00.000Z');
        expect(formatted).toMatch(/\bMay\b/);
        expect(formatted).toMatch(/\b18\b/);
        expect(formatted).toMatch(/\b2026\b/);
    });

    it('also works for late-UTC instants — 23:00Z stays on the same calendar day', () => {
        // A row saved late-day UTC must still render as that day,
        // not jump to the next morning for users east of UTC.
        const formatted = formatLedgerDate('2026-05-18T23:00:00.000Z');
        expect(formatted).toMatch(/\bMay\b/);
        expect(formatted).toMatch(/\b18\b/);
    });

    it('returns the input unchanged when the ISO is unparseable', () => {
        expect(formatLedgerDate('not-a-date')).toBe('not-a-date');
    });
});

describe('formatLedgerDateCompact / formatLedgerDateLong', () => {
    it('compact omits the year', () => {
        const compact = formatLedgerDateCompact('2026-05-18T00:00:00.000Z');
        expect(compact).toMatch(/\bMay\b/);
        expect(compact).toMatch(/\b18\b/);
        expect(compact).not.toMatch(/\b2026\b/);
    });

    it('long includes the year', () => {
        const long = formatLedgerDateLong('2026-05-18T00:00:00.000Z');
        expect(long).toMatch(/\bMay\b/);
        expect(long).toMatch(/\b18\b/);
        expect(long).toMatch(/\b2026\b/);
    });

    // The investment register prints the long form and the bank register the
    // default one, so the two registers print a date alike only while the two
    // formats agree. A day below 10 is where they would part: a padded day.
    it.each([
        ['a day below 10', '2026-11-03T00:00:00.000Z'],
        ['the first of a month', '2026-05-01T12:00:00.000Z'],
        ['a two-digit day', '2026-05-18T00:00:00.000Z'],
    ])('long prints %s as the default format does', (_label, iso) => {
        expect(formatLedgerDateLong(iso)).toBe(formatLedgerDate(iso));
    });
});

// Every form lib/dates prints a date in writes the day as the default form does, with no
// leading zero, so no two screens print one date two ways. Every export of lib/dates is
// listed here, as a printed form or as a date input's value, so one added later is held
// to the rule or fails the list.
describe('the day of every printed date', () => {
    // Each printed form is given the 5th of a month where it reads the day: a calendar
    // date at UTC midnight, an instant at 1:10 pm local time, whose hour and minute
    // cannot read as the day.
    const FIFTH = {
        calendar: '2026-11-05T00:00:00.000Z',
        instant: new Date(2026, 10, 5, 13, 10).toISOString(),
    };
    const PRINTED: Record<string, { print: (iso: string) => string; reads: keyof typeof FIFTH }> = {
        formatLedgerDate: { print: dates.formatLedgerDate, reads: 'calendar' },
        formatLedgerDateCompact: { print: dates.formatLedgerDateCompact, reads: 'calendar' },
        formatLedgerDateLong: { print: dates.formatLedgerDateLong, reads: 'calendar' },
        formatLedgerDateTime: { print: dates.formatLedgerDateTime, reads: 'instant' },
        formatInstantDateCompact: { print: dates.formatInstantDateCompact, reads: 'instant' },
    };
    // A date input's value is YYYY-MM-DD, which pads the day by its format.
    const INPUT_VALUES = ['shiftDateInputValue', 'toDateInputValue', 'todayInputValue'];

    it('lists every export of lib/dates', () => {
        expect(Object.keys(dates).sort()).toEqual([...Object.keys(PRINTED), ...INPUT_VALUES].sort());
    });

    it.each(Object.entries(PRINTED))('%s prints the 5th as the default form does, never 05', (_name, { print, reads }) => {
        expect(print(FIFTH[reads]).match(/\b0?5\b/g)).toEqual(['5']);
    });
});

describe('formatInstantDateCompact', () => {
    const zone = process.env.TZ;
    afterEach(() => { process.env.TZ = zone; });

    it('prints an instant\'s day in the reader\'s zone, where the calendar form prints the UTC day', () => {
        process.env.TZ = 'America/Los_Angeles';
        // Evening of the 10th on the coast, the 11th in UTC.
        const evening = '2026-03-11T03:28:00Z';
        expect(dates.formatInstantDateCompact(evening)).toBe(new Date(2026, 2, 10).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }));
        expect(dates.formatInstantDateCompact(evening)).not.toBe(dates.formatLedgerDateCompact(evening));
    });
});

describe('toDateInputValue', () => {
    it('round-trips a UTC-midnight ISO without timezone shift', () => {
        expect(toDateInputValue('2026-05-18T00:00:00.000Z')).toBe('2026-05-18');
    });

    it('reads the UTC calendar date even for late-UTC instants', () => {
        // West-of-UTC formatters would have returned "2026-05-18"
        // for 23:00Z on the 18th (correct) but might return
        // "2026-05-19" for 04:00Z on the 19th depending on offset.
        // We always use UTC components so the returned value matches
        // the calendar date the row was saved as.
        expect(toDateInputValue('2026-05-19T04:00:00.000Z')).toBe('2026-05-19');
    });

    it('returns empty string for unparseable input', () => {
        expect(toDateInputValue('not-a-date')).toBe('');
    });
});

describe('shiftDateInputValue', () => {
    it('advances by one day', () => {
        expect(shiftDateInputValue('2026-05-18', 1)).toBe('2026-05-19');
    });

    it('retreats by one day', () => {
        expect(shiftDateInputValue('2026-05-18', -1)).toBe('2026-05-17');
    });

    it('crosses month boundaries cleanly', () => {
        expect(shiftDateInputValue('2026-05-31', 1)).toBe('2026-06-01');
        expect(shiftDateInputValue('2026-06-01', -1)).toBe('2026-05-31');
    });

    it('crosses year boundaries cleanly', () => {
        expect(shiftDateInputValue('2026-12-31', 1)).toBe('2027-01-01');
        expect(shiftDateInputValue('2027-01-01', -1)).toBe('2026-12-31');
    });

    it('handles DST-adjacent dates without shifting an extra day', () => {
        // March 8 2026 = DST spring-forward in US/Pacific. UTC
        // arithmetic dodges the local-midnight gap entirely; both
        // directions should land on the calendar-adjacent day.
        expect(shiftDateInputValue('2026-03-08', 1)).toBe('2026-03-09');
        expect(shiftDateInputValue('2026-03-08', -1)).toBe('2026-03-07');
    });
});
