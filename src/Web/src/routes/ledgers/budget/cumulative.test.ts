import { describe, expect, it } from 'vitest';

import {
    ceilingFromRows,
    crossingDay,
    cumulative,
    overspentRows,
    projectTo,
    rootRows,
    targetConflict,
} from './cumulative';
import type { BudgetCategoryRow, BudgetDailyCell } from '@/lib/types';

const cell = (day: number, amount: number, categoryId = 'a'): BudgetDailyCell =>
    ({ categoryId, day, amount });

// `mark` defaults to whatever `typical` ends up as, because with no target set
// the two ARE the same number — so a test that only speaks of `typical` keeps
// saying exactly what it said before targets existed. A test about targets sets
// both explicitly.
const row = (over: Partial<BudgetCategoryRow> = {}): BudgetCategoryRow => {
    const base = {
        categoryId: 'a', name: 'Groceries', parentId: null,
        actual: 100, typical: 100 as number | null, target: null,
        ...over,
    };
    return { ...base, mark: over.mark !== undefined ? over.mark : base.typical };
};

describe('cumulative', () => {
    it('is dense even though the cells are sparse', () => {
        // The trap. Spend on day 1 and day 5 gives TWO cells; a series built
        // only from those would put day 5 second on the x-axis and turn a quiet
        // four days into a vertical climb.
        const series = cumulative([cell(1, 10), cell(5, 40)], 6);
        expect(series).toEqual([10, 10, 10, 10, 50, 50]);
    });

    it('sums several cells landing on the same day', () => {
        expect(cumulative([cell(2, 10), cell(2, 15)], 3)).toEqual([0, 25, 25]);
    });

    it('filters to one category when focusing a row', () => {
        const cells = [cell(1, 10, 'a'), cell(1, 90, 'b'), cell(3, 5, 'a')];
        expect(cumulative(cells, 3)).toEqual([100, 100, 105]);
        expect(cumulative(cells, 3, 'a')).toEqual([10, 10, 15]);
    });

    it('ignores a day outside the month rather than trusting it', () => {
        // A 31st in a 30-day month means the server and the client disagree
        // about the calendar; dropping it is safer than writing past the array.
        expect(cumulative([cell(31, 99), cell(0, 99), cell(2, 5)], 3)).toEqual([0, 5, 5]);
    });
});

describe('projectTo', () => {
    it('extrapolates the rate observed so far', () => {
        // £160 over 16 days of a 30-day month → £300.
        const series = cumulative([cell(1, 160)], 30);
        expect(projectTo(series, 16, 30)).toBe(300);
    });

    it('is null before the month has started', () => {
        // A projection from zero elapsed days is a line through no data.
        expect(projectTo(cumulative([], 30), 0, 30)).toBeNull();
    });

    it('is zero, not null, when the month has started with no spend', () => {
        // Nothing spent is a real answer and should draw a flat line at zero;
        // only "no days yet" is unanswerable.
        expect(projectTo(cumulative([], 30), 5, 30)).toBe(0);
    });
});

describe('crossingDay — the actionable fact', () => {
    it('names the future day the projection passes the ceiling', () => {
        // £20/day against a £300 ceiling → day 15.
        const series = cumulative([cell(1, 100)], 30);
        expect(crossingDay(series, 5, 30, 300)).toBe(15);
    });

    it('names the PAST day when the ceiling is already breached', () => {
        // Not a future date: it already happened, and saying "the 3rd" is more
        // use than projecting a crossing that is behind you.
        const series = cumulative([cell(1, 100), cell(3, 300)], 30);
        expect(crossingDay(series, 10, 30, 250)).toBe(3);
    });

    it('is null when the projection never reaches the ceiling', () => {
        const series = cumulative([cell(1, 100)], 30);
        expect(crossingDay(series, 10, 30, 5000)).toBeNull();
    });

    it('never reports a crossing earlier than today for a future breach', () => {
        // Arithmetic can put the crossing in the past when spending was
        // front-loaded and the rate has since dropped; reporting a day that has
        // already gone by without a breach would be a lie.
        const series = cumulative([cell(1, 900)], 30);
        const day = crossingDay(series, 20, 30, 1000);
        if (day !== null) expect(day).toBeGreaterThan(20);
    });

    it('is null for a ceiling of zero rather than crossing instantly', () => {
        expect(crossingDay(cumulative([cell(1, 5)], 30), 10, 30, 0)).toBeNull();
    });
});

describe('ceilingFromRows', () => {
    it('sums the marks the table shows, so the two can never disagree', () => {
        expect(ceilingFromRows([row({ typical: 100 }), row({ typical: 250 })])).toBe(350);
    });

    it('skips a row with no history instead of counting it as zero', () => {
        // An unknown is not a commitment to spend nothing. Counting it zero
        // would tighten the ceiling every time a new category appeared.
        expect(ceilingFromRows([row({ typical: 100 }), row({ typical: null })])).toBe(100);
    });

    it('is null when nothing has history at all', () => {
        expect(ceilingFromRows([row({ typical: null })])).toBeNull();
        expect(ceilingFromRows([])).toBeNull();
    });
});

describe('targetConflict — what the cell disables on', () => {
    const parent = row({ categoryId: 'tax', name: 'Taxes', parentId: null });
    const child = row({ categoryId: 'fed', name: 'Federal', parentId: 'tax' });
    const grand = row({ categoryId: 'inc', name: 'Income tax', parentId: 'fed' });

    it('names the targeted ancestor', () => {
        const rows = [{ ...parent, target: 1000, mark: 1000 }, child];
        expect(targetConflict(rows, 'fed')).toBe('Taxes');
    });

    it('names the targeted descendant', () => {
        const rows = [parent, { ...child, target: 600, mark: 600 }];
        expect(targetConflict(rows, 'tax')).toBe('Federal');
    });

    it('reaches through an untargeted generation', () => {
        const rows = [parent, child, { ...grand, target: 300, mark: 300 }];
        expect(targetConflict(rows, 'tax')).toBe('Income tax');
    });

    it('is null for a sibling, and for the row that holds the target itself', () => {
        const a = row({ categoryId: 'gas', name: 'Gas', parentId: 'tax', target: 60, mark: 60 });
        const b = row({ categoryId: 'water', name: 'Water', parentId: 'tax' });
        expect(targetConflict([parent, a, b], 'water')).toBeNull();
        // Replacing your own number is an edit, not a conflict.
        expect(targetConflict([parent, a, b], 'gas')).toBeNull();
    });
});

describe('the mark is what the screen judges against', () => {
    it('ceilingFromRows sums the MARK, so a typed target moves the ceiling', () => {
        // Summing `typical` here would draw a ceiling that quietly ignored every
        // target on the screen — the chart and the table would then disagree about
        // the same month, which is the class of bug that drew one 1.87x too high.
        const derived = row({ categoryId: 'a', typical: 100, target: null, mark: 100 });
        const decided = row({ categoryId: 'b', typical: 100, target: 250, mark: 250 });
        expect(ceilingFromRows([derived, decided])).toBe(350);
    });

    it('overspentRows stops flagging a row once its target is raised above spend', () => {
        // The point of setting a target: a category you deliberately budgeted more
        // for is not "over" any more, even though its history says otherwise.
        const before = row({ actual: 150, typical: 100, target: null, mark: 100 });
        expect(overspentRows([before])).toHaveLength(1);

        const after = row({ actual: 150, typical: 100, target: 200, mark: 200 });
        expect(overspentRows([after])).toEqual([]);
    });

    it('a row with no mark is neither over nor part of the ceiling', () => {
        const unknown = row({ typical: null, target: null, mark: null, actual: 500 });
        expect(ceilingFromRows([unknown])).toBeNull();
        expect(overspentRows([unknown])).toEqual([]);
    });
});

describe('overspentRows', () => {
    it('ranks worst overspend first', () => {
        const rows = [
            row({ categoryId: 'a', actual: 110, typical: 100 }),
            row({ categoryId: 'b', actual: 300, typical: 100 }),
            row({ categoryId: 'c', actual: 50, typical: 100 }),
        ];
        expect(overspentRows(rows).map((r) => r.categoryId)).toEqual(['b', 'a']);
    });

    it('keeps quiet about a category a pound over', () => {
        // The floor is what stops the list crying wolf every month.
        expect(overspentRows([row({ actual: 100.4, typical: 100 })])).toEqual([]);
    });

    it('never flags a category with no history', () => {
        expect(overspentRows([row({ actual: 500, typical: null })])).toEqual([]);
    });
});

describe('rootRows — the rollup guard', () => {
    // The API rolls up, so a parent row ALREADY contains its children. Anything
    // that sums or counts rows has to reduce to roots first.
    const parent = row({ categoryId: 'tax', name: 'Taxes', typical: 1000, actual: 1200 });
    const child = row({ categoryId: 'fed', name: 'Federal', parentId: 'tax', typical: 600, actual: 700 });

    it('drops a child whose parent is present', () => {
        expect(rootRows([parent, child]).map((r) => r.categoryId)).toEqual(['tax']);
    });

    it('KEEPS a child whose parent is absent from the result', () => {
        // The parent had no spending this month, so the API never returned it.
        // Treating this row as a child would drop it from the table entirely.
        expect(rootRows([child]).map((r) => r.categoryId)).toEqual(['fed']);
    });

    it('is the identity on a flat result', () => {
        const flat = [row({ categoryId: 'a' }), row({ categoryId: 'b' })];
        expect(rootRows(flat)).toHaveLength(2);
    });
});

describe('hero and table cannot disagree', () => {
    const parent = row({ categoryId: 'tax', name: 'Taxes', typical: 1000, actual: 1200 });
    const child = row({ categoryId: 'fed', name: 'Federal', parentId: 'tax', typical: 600, actual: 700 });

    it('ceiling counts a rolled-up parent once, not once per generation', () => {
        // Anchor: the parent alone is 1000, and that is the whole tree's mark.
        expect(ceilingFromRows([parent])).toBe(1000);
        // Adding the child it already contains must not move the ceiling.
        // Summing every row gave 1600 here, and 1.87x on the real March data.
        expect(ceilingFromRows([parent, child])).toBe(1000);
    });

    it('overspent count does not report a parent and its child as two', () => {
        expect(overspentRows([parent]).map((r) => r.categoryId)).toEqual(['tax']);
        expect(overspentRows([parent, child]).map((r) => r.categoryId)).toEqual(['tax']);
    });

    it('still counts an orphaned child, which is real spending', () => {
        expect(ceilingFromRows([child])).toBe(600);
        expect(overspentRows([child]).map((r) => r.categoryId)).toEqual(['fed']);
    });
});
