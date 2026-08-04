import { describe, it, expect } from 'vitest';

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
