import { describe, expect, it } from 'vitest';

import { addMonths, monthGridRange, monthLabel, monthMatrix } from './calendar';

describe('monthMatrix', () => {
    it('builds a Sun-first grid padded to full weeks', () => {
        // June 2026: the 1st is a Monday, 30 days.
        const grid = monthMatrix(2026, 6);
        expect(grid[0]).toHaveLength(7);
        expect(grid[0][0].date).toBe('2026-05-31');      // Sunday before the 1st
        expect(grid[0][0].inMonth).toBe(false);
        expect(grid[0][1].date).toBe('2026-06-01');      // Monday the 1st
        expect(grid[0][1].inMonth).toBe(true);
        const flat = grid.flat();
        expect(flat.filter((c) => c.inMonth)).toHaveLength(30);
        expect(flat.every((_, i) => i === 0 || flat[i].date > flat[i - 1].date)).toBe(true); // strictly ascending, no gaps
    });

    it('clamps to leap February (29 days) and never overflows', () => {
        const feb = monthMatrix(2024, 2);                 // 2024 leap year
        expect(feb.flat().filter((c) => c.inMonth)).toHaveLength(29);
        expect(feb.flat().some((c) => c.date === '2024-02-29')).toBe(true);
    });

    it('handles a 31-day month starting on Saturday (6 rows)', () => {
        // Aug 2025: the 1st is a Friday, 31 days -> needs 6 weeks.
        const aug = monthMatrix(2025, 8);
        expect(aug.length).toBe(6);
        expect(aug.flat().filter((c) => c.inMonth)).toHaveLength(31);
    });
});

describe('monthGridRange', () => {
    it('spans the grid first cell to last cell', () => {
        const { from, to } = monthGridRange(2026, 6);
        expect(from).toBe('2026-05-31');
        expect(to).toBe('2026-07-04');                    // last Saturday of the grid
    });
});

describe('addMonths', () => {
    it('steps and wraps the year', () => {
        expect(addMonths(2026, 6, 1)).toEqual({ year: 2026, month: 7 });
        expect(addMonths(2026, 12, 1)).toEqual({ year: 2027, month: 1 });
        expect(addMonths(2026, 1, -1)).toEqual({ year: 2025, month: 12 });
        expect(addMonths(2026, 6, -8)).toEqual({ year: 2025, month: 10 });
    });
});

describe('monthLabel', () => {
    it('formats month + year', () => {
        expect(monthLabel(2026, 6)).toBe('June 2026');
        expect(monthLabel(2026, 1)).toBe('January 2026');
    });
});
