import { describe, expect, it } from 'vitest';

import {
    ceilingFromRows,
    crossingDay,
    cumulative,
    hasScheduled,
    markIsBudgeted,
    overspentRows,
    pastMark,
    projectedDays,
    projectedPath,
    projectTo,
    rootRows,
    scheduledByDay,
    type ScheduledDays,
    targetConflict,
    usualByDay,
    valuedCells,
    type ValuedCell,
} from './cumulative';
import type { BudgetCategoryRow, BudgetDailyCell, RateUnavailable } from '@/lib/types';

const cell = (day: number, amount: number, categoryId = 'a'): ValuedCell =>
    ({ categoryId, day, amount });

const MISSING: RateUnavailable = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-11-08', reason: 'rate_unavailable' };

// `mark` defaults to whatever `typical` ends up as, because with no target set
// the two ARE the same number — so a test that only speaks of `typical` keeps
// saying exactly what it said before targets existed. A test about targets sets
// both explicitly.
const row = (over: Partial<BudgetCategoryRow> = {}): BudgetCategoryRow => {
    const base = {
        categoryId: 'a', name: 'Groceries', parentId: null,
        actual: 100, typical: 100 as number | null, target: null, targetCurrencyCode: null,
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
        // In this month's own cells, a 31st in a 30-day month means the server and
        // the client disagree about the calendar; dropping it is safer than writing
        // past the array. The window's cells are another matter: see usualByDay.
        expect(cumulative([cell(31, 99), cell(0, 99), cell(2, 5)], 3)).toEqual([0, 5, 5]);
    });
});

/** A baseline with no shape: the same amount usually spent on every day of the month. */
const steady = (perDay: number, days = 30) =>
    cumulative(Array.from({ length: days }, (_, i) => cell(i + 1, perDay)), days);

/** No history: zero on every day. */
const zeros = (days = 30) => new Array<number>(days).fill(0);

/** Nothing scheduled after today. */
const nothing = (days = 30): ScheduledDays => ({ debits: zeros(days), credits: zeros(days) });

describe('usualByDay:the window\'s days past this month\'s end count on its last day', () => {
    // The window usually pays FIRST on the 1st and LATE on a day this month does
    // not have; this month has paid FIRST so far. Each cell is already the
    // window's mean, as the API sends it.
    const TODAY = 10;
    const FIRST = 100;
    const LATE = 60;
    const WINDOW_MONTH = FIRST + LATE;
    const spentSoFar = (days: number) => cumulative([cell(1, FIRST)], days);

    it('in a 28-day month: the baseline ends at the window\'s mean month, and the projection holds the 30th', () => {
        const DAYS = 28;
        const normal = usualByDay([cell(1, FIRST), cell(30, LATE)], DAYS);
        expect(normal).toHaveLength(DAYS);
        expect(normal.at(-1)).toBe(WINDOW_MONTH);
        expect(normal[DAYS - 2]).toBe(FIRST);
        expect(projectTo(spentSoFar(DAYS), TODAY, DAYS, nothing(DAYS), normal)).toBe(WINDOW_MONTH);
    });

    it('in a 30-day month: the window\'s 31st counts on the 30th', () => {
        const DAYS = 30;
        const normal = usualByDay([cell(1, FIRST), cell(31, LATE)], DAYS);
        expect(normal.at(-1)).toBe(WINDOW_MONTH);
        expect(projectTo(spentSoFar(DAYS), TODAY, DAYS, nothing(DAYS), normal)).toBe(WINDOW_MONTH);
    });

    it('crosses on the last day when only the window\'s later days take the path past the ceiling', () => {
        const DAYS = 28;
        const normal = usualByDay([cell(1, FIRST), cell(29, LATE / 2), cell(31, LATE / 2)], DAYS);
        expect(crossingDay(spentSoFar(DAYS), TODAY, DAYS, WINDOW_MONTH - 0.01, 'USD', nothing(DAYS), normal)).toBe(DAYS);
    });

    it('still leaves out a day before the month\'s first', () => {
        expect(usualByDay([cell(0, LATE), cell(1, FIRST)], 3)).toEqual([FIRST, FIRST, FIRST]);
    });
});

describe('projectTo', () => {
    it('is null before the month has started', () => {
        // A projection from zero elapsed days is a line through no data.
        expect(projectTo(cumulative([], 30), 0, 30, nothing(), steady(10))).toBeNull();
    });

    it('is zero, not null, when the month has started with no spend and no history', () => {
        // Nothing spent is a real answer and should draw a flat line at zero;
        // only "no days yet" is unanswerable.
        expect(projectTo(cumulative([], 30), 5, 30, nothing(), zeros())).toBe(0);
    });
});

describe('the projection: spent so far, plus the larger of what is scheduled and what the usual month adds', () => {
    // A month that pays one large amount on its 1st and spends a little on every
    // day after it, in the trailing window and in this month alike. Today is early
    // in the month, after the payment has left.
    const DAYS = 30;
    const TODAY = 4;
    const MONTHLY = 1_200;
    const DAILY = 20;
    /** The monthly payment on day 1, then `daily` on each day from the 2nd through `through`. */
    const pattern = (through: number, daily = DAILY): ValuedCell[] => [
        cell(1, MONTHLY),
        ...Array.from({ length: through - 1 }, (_, i) => cell(i + 2, daily)),
    ];
    // The baseline the band draws: the window's mean day-of-month profile,
    // accumulated. Every month of this window had the same shape, so its mean is
    // that shape.
    const normal = cumulative(pattern(DAYS), DAYS);
    const USUAL_MONTH = MONTHLY + DAILY * (DAYS - 1);
    const thisMonth = pattern(TODAY);
    const series = cumulative(thisMonth, DAYS);
    const SPENT = MONTHLY + DAILY * (TODAY - 1);
    const none = nothing(DAYS);

    // A one-off this month that the history does not hold, worth this many usual days.
    const ONE_OFF_DAYS = 10;
    const ONE_OFF = DAILY * ONE_OFF_DAYS;
    const withOneOff = cumulative([...thisMonth, cell(2, ONE_OFF)], DAYS);

    // A month running hotter than usual since the payment.
    const HOT_DAILY = DAILY * 2;
    const hot = cumulative(pattern(TODAY, HOT_DAILY), DAYS);
    const HOT_SPENT = MONTHLY + HOT_DAILY * (TODAY - 1);
    /** What the hot days spent above usual, counted in usual days. */
    const HOT_EXTRA_DAYS = ((HOT_DAILY - DAILY) * (TODAY - 1)) / DAILY;

    // A bill scheduled later in the month, larger than everything the usual month
    // adds from tomorrow. It is in the month's cells, as a Scheduled row is.
    const BILL_DAY = 25;
    const BIG = USUAL_MONTH - SPENT + 7 * DAILY;
    const withBill = [...thisMonth, cell(BILL_DAY, BIG)];
    const billSeries = cumulative(withBill, DAYS);
    const billScheduled = scheduledByDay(withBill, DAYS, TODAY);

    const noHistory = zeros(DAYS);

    it('counts a payment that leaves early in the month once, where the rate so far multiplied it', () => {
        expect(projectTo(series, TODAY, DAYS, none, normal)).toBe(USUAL_MONTH);
    });

    it('starts at what is spent and adds what the usual month adds from tomorrow, day by day', () => {
        const path = projectedPath(series, TODAY, DAYS, none, normal)!;
        expect(path).toHaveLength(DAYS - TODAY + 1);
        expect(path[0]).toBe(SPENT);
        expect(path[1]).toBe(SPENT + DAILY);
        expect(path.at(-1)).toBe(USUAL_MONTH);
    });

    it('counts a one-off the history does not hold once', () => {
        expect(projectTo(withOneOff, TODAY, DAYS, none, normal)).toBe(USUAL_MONTH + ONE_OFF);
    });

    it('projects a month running hotter than usual higher than the usual month', () => {
        const projected = projectTo(hot, TODAY, DAYS, none, normal)!;
        expect(projected).toBe(HOT_SPENT + DAILY * (DAYS - TODAY));
        expect(projected).toBeGreaterThan(USUAL_MONTH);
    });

    it('never falls below spent plus what is scheduled through each day: a bill larger than the usual remainder carries the path from its date', () => {
        const path = projectedPath(billSeries, TODAY, DAYS, billScheduled, normal)!;
        // The day before the bill, the usual days carry the path; from its date, the bill.
        expect(path[BILL_DAY - 1 - TODAY]).toBe(SPENT + DAILY * (BILL_DAY - 1 - TODAY));
        expect(path[BILL_DAY - TODAY]).toBe(SPENT + BIG);
        expect(path.at(-1)).toBe(SPENT + BIG);
        path.forEach((value, i) => {
            expect(value).toBeGreaterThanOrEqual(SPENT + (TODAY + i >= BILL_DAY ? BIG : 0));
        });
    });

    it('counts a scheduled bill the usual month already holds once', () => {
        // The window paid the same bill on the same day every month; this month it
        // is scheduled. Spent so far plus the usual remainder already holds it.
        const RECURRING = 15 * DAILY;
        const usual = cumulative([...pattern(DAYS), cell(BILL_DAY, RECURRING)], DAYS);
        const cells = [...thisMonth, cell(BILL_DAY, RECURRING)];
        const projected = projectTo(cumulative(cells, DAYS), TODAY, DAYS, scheduledByDay(cells, DAYS, TODAY), usual);
        expect(projected).toBe(USUAL_MONTH + RECURRING);
    });

    it('with no history, is spent plus scheduled', () => {
        expect(projectTo(series, TODAY, DAYS, none, noHistory)).toBe(SPENT);
        expect(projectTo(billSeries, TODAY, DAYS, billScheduled, noHistory)).toBe(SPENT + BIG);
    });

    describe('and the day it crosses the ceiling', () => {
        it('for the early payment: none at the usual month, and late in the month for a ceiling below it', () => {
            // At the ceiling is not past it.
            expect(crossingDay(series, TODAY, DAYS, USUAL_MONTH, 'USD', none, normal)).toBeNull();
            const DAYS_SHORT = 3;
            const ceiling = USUAL_MONTH - DAILY * DAYS_SHORT;
            expect(crossingDay(series, TODAY, DAYS, ceiling, 'USD', none, normal)).toBe(DAYS - DAYS_SHORT + 1);
        });

        it('for a one-off: the day the usual days carry the month past its usual total', () => {
            expect(crossingDay(withOneOff, TODAY, DAYS, USUAL_MONTH, 'USD', none, normal)).toBe(DAYS - ONE_OFF_DAYS + 1);
        });

        it('for a month running hotter than usual: past the usual total before the month ends', () => {
            expect(crossingDay(hot, TODAY, DAYS, USUAL_MONTH, 'USD', none, normal)).toBe(DAYS - HOT_EXTRA_DAYS + 1);
        });

        it('for a bill larger than the usual remainder: on the bill\'s own date, and none at the month\'s end figure', () => {
            expect(crossingDay(billSeries, TODAY, DAYS, SPENT + BIG - 0.01, 'USD', billScheduled, normal)).toBe(BILL_DAY);
            expect(crossingDay(billSeries, TODAY, DAYS, SPENT + BIG, 'USD', billScheduled, normal)).toBeNull();
        });

        it('with no history: on a scheduled bill\'s date, and never with nothing scheduled', () => {
            expect(crossingDay(billSeries, TODAY, DAYS, SPENT + BIG - 0.01, 'USD', billScheduled, noHistory)).toBe(BILL_DAY);
            expect(crossingDay(series, TODAY, DAYS, SPENT, 'USD', none, noHistory)).toBeNull();
        });
    });
});

describe('scheduled rows (ADR-0109 D10)', () => {
    // Day 10 of 30 in a ledger with no history: some spend so far, a small bill
    // dated the 20th and a large one dated the 28th, both already in the month's cells.
    const cells = [cell(2, 100), cell(20, 120), cell(28, 1450)];
    const series = cumulative(cells, 30);
    const scheduled = scheduledByDay(cells, 30, 10);

    it('takes only the rows dated after today, each on its own day', () => {
        expect(scheduled.debits[19]).toBe(120);
        expect(scheduled.debits[27]).toBe(1450);
        expect(scheduled.debits[1]).toBe(0);
        expect(scheduled.debits.reduce((a, b) => a + b, 0)).toBe(1570);
        expect(scheduled.credits.every((v) => v === 0)).toBe(true);
    });

    it('adds each bill on its own date, never spread across the month', () => {
        const path = projectedPath(series, 10, 30, scheduled, zeros())!;
        expect(path[0]).toBe(100);
        expect(path[19 - 10]).toBe(100);
        expect(path[20 - 10]).toBe(100 + 120);
        expect(projectTo(series, 10, 30, scheduled, zeros())).toBe(100 + 1570);
    });

    it('can cross the mark on a bill\'s own date', () => {
        // Under the mark until the 28th, when the large bill lands.
        expect(crossingDay(series, 10, 30, 1000, 'USD', scheduled, zeros())).toBe(28);
        // Without the bills nothing gets there.
        expect(crossingDay(series, 10, 30, 1000, 'USD', nothing(),zeros())).toBeNull();
    });
});

describe('a credit scheduled after today counts in full', () => {
    // Spend so far on the 2nd, and a refund in another category dated later in the
    // month, already in the month's cells as a Scheduled row is.
    const DAYS = 30;
    const TODAY = 10;
    const SPENT = 400;
    const REFUND = 150;
    const REFUND_DAY = 20;
    const DAILY = 20;
    const cells = [cell(2, SPENT), cell(REFUND_DAY, -REFUND, 'b')];
    const series = cumulative(cells, DAYS);
    const scheduled = scheduledByDay(cells, DAYS, TODAY);
    /** The month's Spent: every row, the scheduled refund included. */
    const MONTH_SPENT = series.at(-1)!;

    it('is split off from the spend scheduled, and is a row the curve marks', () => {
        expect(scheduled.credits[REFUND_DAY - 1]).toBe(-REFUND);
        expect(scheduled.debits.every((v) => v === 0)).toBe(true);
        expect(hasScheduled(scheduled)).toBe(true);
        expect(hasScheduled(nothing(DAYS))).toBe(false);
    });

    it('with no history, ends the month at its Spent', () => {
        expect(MONTH_SPENT).toBe(SPENT - REFUND);
        expect(projectTo(series, TODAY, DAYS, scheduled, zeros(DAYS))).toBe(MONTH_SPENT);
    });

    it('comes off a usual remainder larger than the bills, on its own date', () => {
        const path = projectedPath(series, TODAY, DAYS, scheduled, steady(DAILY, DAYS))!;
        expect(path[REFUND_DAY - 1 - TODAY]).toBe(SPENT + DAILY * (REFUND_DAY - 1 - TODAY));
        expect(path[REFUND_DAY - TODAY]).toBe(SPENT + DAILY * (REFUND_DAY - TODAY) - REFUND);
        expect(path.at(-1)).toBe(SPENT + DAILY * (DAYS - TODAY) - REFUND);
    });

    it('comes off beside a bill on the same day, whichever of the bill and the usual days is larger', () => {
        const BILL = 90;
        const both = [...cells, cell(REFUND_DAY, BILL)];
        const bothScheduled = scheduledByDay(both, DAYS, TODAY);
        const bothSeries = cumulative(both, DAYS);
        // No history: the bill carries the path, and the refund comes off it.
        expect(projectTo(bothSeries, TODAY, DAYS, bothScheduled, zeros(DAYS))).toBe(SPENT + BILL - REFUND);
        // The usual days add more than the bill: they carry it, and the refund still comes off.
        expect(DAILY * (DAYS - TODAY)).toBeGreaterThan(BILL);
        expect(projectTo(bothSeries, TODAY, DAYS, bothScheduled, steady(DAILY, DAYS)))
            .toBe(SPENT + DAILY * (DAYS - TODAY) - REFUND);
    });

    it('steps the path down by all of it on its date', () => {
        const day = projectedDays(series, TODAY, DAYS, scheduled, steady(DAILY, DAYS))!
            .find((p) => p.day === REFUND_DAY)!;
        expect(day.credit).toBe(-REFUND);
        expect(day.bill).toBe(0);
        expect(day.beforeBill - day.value).toBe(REFUND);
    });
});

describe('crossingDay — the actionable fact', () => {
    it('names the future day the projection passes the ceiling', () => {
        // The usual days add to what is spent; the crossing is the first day on
        // which that total is past the ceiling.
        const series = cumulative([cell(1, 100)], 30);
        expect(crossingDay(series, 5, 30, 290, 'USD', nothing(),steady(20))).toBe(15);
    });

    it('names the PAST day when the ceiling is already breached', () => {
        // Not a future date: it already happened, and saying "the 3rd" is more
        // use than projecting a crossing that is behind you.
        const series = cumulative([cell(1, 100), cell(3, 300)], 30);
        expect(crossingDay(series, 10, 30, 250, 'USD', nothing(),steady(20))).toBe(3);
    });

    it('is null when the projection never reaches the ceiling', () => {
        const series = cumulative([cell(1, 100)], 30);
        expect(crossingDay(series, 10, 30, 5000, 'USD', nothing(),steady(20))).toBeNull();
    });

    it('never reports a crossing earlier than today for a future breach', () => {
        // The projection starts at today, so a day that has already gone by
        // without a breach is never named as the day of one.
        const series = cumulative([cell(1, 900)], 30);
        const day = crossingDay(series, 20, 30, 1000, 'USD', nothing(),steady(20));
        expect(day).not.toBeNull();
        expect(day!).toBeGreaterThan(20);
    });

    it('names the day spending goes past a ceiling of zero for good, and none with nothing spent', () => {
        // Over is over, a ceiling of zero included: any spending is past it.
        const SPEND_DAY = 3;
        const SPEND = 5;
        expect(crossingDay(cumulative([cell(SPEND_DAY, SPEND)], 30), 10, 30, 0, 'USD', nothing(), steady(20)))
            .toBe(SPEND_DAY);
        // A total exactly at the ceiling is at it, not past it.
        expect(crossingDay(cumulative([], 30), 10, 30, 0, 'USD', nothing(), zeros())).toBeNull();
    });
});

describe('crossingDay — the day the month goes past the ceiling for good', () => {
    // No history, spent so far on the 2nd, and rows scheduled after today: a bill
    // that takes the path past the ceiling, then a refund that brings it back under.
    const DAYS = 30;
    const TODAY = 10;
    const SPENT = 400;
    const BILL = 300;
    const BILL_DAY = 15;
    const REFUND = 200;
    const REFUND_DAY = 20;
    const LATE_DAY = 25;
    const PAST_DAY = 3;
    /** Between the level the bill takes the path to and the level the refund brings it back to. */
    const CEILING = SPENT + BILL - REFUND / 2;
    const args = (cells: ValuedCell[]) =>
        [cumulative(cells, DAYS), TODAY, DAYS, scheduledByDay(cells, DAYS, TODAY), zeros(DAYS)] as const;
    const crossingOf = (cells: ValuedCell[]) => {
        const [series, today, days, scheduled, normal] = args(cells);
        return crossingDay(series, today, days, CEILING, 'USD', scheduled, normal);
    };
    const monthEndOf = (cells: ValuedCell[]) => projectTo(...args(cells))!;

    it('is none for a path that passes the ceiling and comes back to end under it', () => {
        const cells = [cell(2, SPENT), cell(BILL_DAY, BILL), cell(REFUND_DAY, -REFUND, 'b')];
        const path = projectedPath(...args(cells))!;
        expect(path[BILL_DAY - TODAY]).toBeGreaterThan(CEILING);
        expect(monthEndOf(cells)).toBeLessThan(CEILING);
        expect(crossingOf(cells)).toBeNull();
    });

    it('is the day it goes past for good, for a path that passes, comes back under, and ends past', () => {
        const cells = [cell(2, SPENT), cell(BILL_DAY, BILL), cell(REFUND_DAY, -REFUND, 'b'), cell(LATE_DAY, REFUND)];
        expect(monthEndOf(cells)).toBeGreaterThan(CEILING);
        expect(crossingOf(cells)).toBe(LATE_DAY);
    });

    it('is the day already gone for a breach that stays past through the month end', () => {
        const cells = [cell(2, SPENT), cell(PAST_DAY, CEILING), cell(REFUND_DAY, -REFUND, 'b')];
        expect(monthEndOf(cells)).toBeGreaterThan(CEILING);
        expect(crossingOf(cells)).toBe(PAST_DAY);
    });

    it('is none for a breach already gone that a scheduled refund undoes, the month ending under', () => {
        const OVER = REFUND / 4;
        const cells = [cell(2, SPENT), cell(PAST_DAY, CEILING - SPENT + OVER), cell(REFUND_DAY, -REFUND, 'b')];
        expect(cumulative(cells, DAYS)[TODAY - 1]).toBeGreaterThan(CEILING);
        expect(monthEndOf(cells)).toBeLessThan(CEILING);
        expect(crossingOf(cells)).toBeNull();
    });
});

describe('crossingDay — over is over, at the precision the figures print', () => {
    // A steady usual month from day 5: the projection lands exactly on AT_15TH on
    // the 15th and is well past it on the 16th.
    const series = cumulative([cell(1, 100)], 30);
    const usual = steady(20);
    const AT_15TH = 300;

    it('does not count a projection landing exactly on the ceiling as past it', () => {
        expect(crossingDay(series, 5, 30, AT_15TH, 'USD', nothing(),usual)).toBe(16);
    });

    it('does not count a projection past the ceiling by less than the last printed digit', () => {
        // The ceiling prints as the same figure as the projection.
        expect(crossingDay(series, 5, 30, AT_15TH - 0.004, 'USD', nothing(),usual)).toBe(16);
    });

    it('counts a projection one minor unit past the ceiling as past it', () => {
        expect(crossingDay(series, 5, 30, AT_15TH - 0.01, 'USD', nothing(),usual)).toBe(15);
    });

    it('judges the days already gone the same way', () => {
        // Spent to date from the 3rd is SPENT plus a residue below the last printed
        // digit: at a ceiling of SPENT on the 3rd and 4th, not past it, and the
        // usual days carry it past on the 5th.
        const SPENT = 250;
        const spent = cumulative([cell(1, 100), cell(3, SPENT - 100 + 0.004)], 30);
        expect(crossingDay(spent, 4, 30, SPENT, 'USD', nothing(),usual)).toBe(5);
        expect(crossingDay(spent, 4, 30, SPENT - 0.01, 'USD', nothing(),usual)).toBe(3);
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

    it('counts a stored target that did not convert, up and down (ADR-0114 D9)', () => {
        // Target null for a missing rate, and its own currency to say it is stored.
        const unconverted = { target: null, targetCurrencyCode: 'EUR', mark: null };
        expect(targetConflict([{ ...parent, ...unconverted }, child], 'fed')).toBe('Taxes');
        expect(targetConflict([parent, { ...child, ...unconverted }], 'tax')).toBe('Federal');
    });
});

describe('the mark is what the screen judges against', () => {
    it('ceilingFromRows sums the MARK, so a typed target moves the ceiling', () => {
        // Summing `typical` here would draw a ceiling that quietly ignored every
        // target on the screen — the chart and the table would then disagree about
        // the same month, and a ceiling can be drawn nearly twice too high.
        const derived = row({ categoryId: 'a', typical: 100, target: null, mark: 100 });
        const decided = row({ categoryId: 'b', typical: 100, target: 250, mark: 250 });
        expect(ceilingFromRows([derived, decided])).toBe(350);
    });

    it('overspentRows stops flagging a row once its target is raised above spend', () => {
        // The point of setting a target: a category you deliberately budgeted more
        // for is not "over" any more, even though its history says otherwise.
        const before = row({ actual: 150, typical: 100, target: null, mark: 100 });
        expect(overspentRows([before], 'USD')).toHaveLength(1);

        const after = row({ actual: 150, typical: 100, target: 200, mark: 200 });
        expect(overspentRows([after], 'USD')).toEqual([]);
    });

    it('a row with no mark is neither over nor part of the ceiling', () => {
        const unknown = row({ typical: null, target: null, mark: null, actual: 500 });
        expect(ceilingFromRows([unknown])).toBeNull();
        expect(overspentRows([unknown], 'USD')).toEqual([]);
    });
});

describe('overspentRows', () => {
    it('ranks worst overspend first', () => {
        const rows = [
            row({ categoryId: 'a', actual: 110, typical: 100 }),
            row({ categoryId: 'b', actual: 300, typical: 100 }),
            row({ categoryId: 'c', actual: 50, typical: 100 }),
        ];
        expect(overspentRows(rows, 'USD').map((r) => r.categoryId)).toEqual(['b', 'a']);
    });

    it('counts a category over by less than a whole unit: over is over', () => {
        expect(overspentRows([row({ actual: 100.4, typical: 100 })], 'USD')).toHaveLength(1);
        expect(overspentRows([row({ actual: 100.01, typical: 100 })], 'USD')).toHaveLength(1);
    });

    it('never counts a category exactly at its mark', () => {
        expect(overspentRows([row({ actual: 100, typical: 100 })], 'USD')).toEqual([]);
    });

    it('never flags a category with no history', () => {
        expect(overspentRows([row({ actual: 500, typical: null })], 'USD')).toEqual([]);
    });
});

describe('markIsBudgeted — what the basis line says', () => {
    const parent = row({ categoryId: 'tax', name: 'Taxes', parentId: null });
    const child = row({ categoryId: 'fed', name: 'Federal', parentId: 'tax' });
    const sibling = row({ categoryId: 'gas', name: 'Gas', parentId: null });

    it('for the month, is any target at all', () => {
        expect(markIsBudgeted([parent, child, sibling])).toBe(false);
        expect(markIsBudgeted([parent, { ...child, target: 60, mark: 60 }, sibling])).toBe(true);
    });

    it('for a category, is its own target or one typed beneath it, never a sibling\'s', () => {
        expect(markIsBudgeted([{ ...parent, target: 100, mark: 100 }, child], 'tax')).toBe(true);
        const typedBelow = [parent, { ...child, target: 60, mark: 60 }, sibling];
        expect(markIsBudgeted(typedBelow, 'tax')).toBe(true);
        expect(markIsBudgeted(typedBelow, 'gas')).toBe(false);
        expect(markIsBudgeted([parent, child, { ...sibling, target: 30, mark: 30 }], 'tax')).toBe(false);
    });

    it('counts a stored target that did not convert, which no target does not (ADR-0114 D9)', () => {
        const unconverted = { target: null, targetCurrencyCode: 'EUR', mark: null };
        expect(markIsBudgeted([parent, { ...child, ...unconverted }, sibling])).toBe(true);
        expect(markIsBudgeted([parent, { ...child, ...unconverted }], 'tax')).toBe(true);
        // The same null target with nothing stored is no target.
        expect(markIsBudgeted([parent, { ...child, target: null, targetCurrencyCode: null, mark: null }])).toBe(false);
    });

    it('reaches through a generation the month reports nothing for, given the whole tree', () => {
        // The middle category is absent from the rows; only the tree knows it is there.
        const grand = row({ categoryId: 'inc', name: 'Income tax', parentId: 'fed', target: 30, mark: 30 });
        const tree = new Map<string, string | null>([['tax', null], ['fed', 'tax'], ['inc', 'fed']]);
        expect(markIsBudgeted([parent, grand], 'tax', (id) => tree.get(id) ?? null)).toBe(true);
        expect(markIsBudgeted([parent, grand], 'tax')).toBe(false);
    });
});

describe('pastMark', () => {
    it('is how far the spend is past the mark: over, at, under', () => {
        expect(pastMark(row({ actual: 100.4, mark: 100 }), 'USD')).toBe(0.4);
        expect(pastMark(row({ actual: 100, mark: 100 }), 'USD')).toBe(0);
        expect(pastMark(row({ actual: 99.6, mark: 100 }), 'USD')).toBe(-0.4);
    });

    it('compares the two figures as they print, so a mean finer than a printed figure is not an overspend of nothing', () => {
        // A mean over three months, against spend that prints the same figure.
        const mean = row({ actual: 33.33, typical: 100 / 3, mark: 100 / 3 });
        expect(pastMark(mean, 'USD')).toBe(0);
        expect(overspentRows([mean], 'USD')).toEqual([]);
        // One printed unit past it is over.
        expect(pastMark(row({ actual: 33.34, mark: 100 / 3 }), 'USD')).toBe(0.01);
    });

    it('is null with no mark to judge by', () => {
        expect(pastMark(row({ actual: 50, typical: null, mark: null }), 'USD')).toBeNull();
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
        // Anchor: the parent's own mark is the whole tree's mark.
        expect(ceilingFromRows([parent])).toBe(1000);
        // Adding the child it already contains must not move the ceiling.
        // Summing every row gave parent plus child here, counting the child's
        // mark twice.
        expect(ceilingFromRows([parent, child])).toBe(1000);
    });

    it('overspent count does not report a parent and its child as two', () => {
        expect(overspentRows([parent], 'USD').map((r) => r.categoryId)).toEqual(['tax']);
        expect(overspentRows([parent, child], 'USD').map((r) => r.categoryId)).toEqual(['tax']);
    });

    it('still counts an orphaned child, which is real spending', () => {
        expect(ceilingFromRows([child])).toBe(600);
        expect(overspentRows([child], 'USD').map((r) => r.categoryId)).toEqual(['fed']);
    });
});

describe('a month with a missing rate (ADR-0114 D9)', () => {
    it('has no valued cells when one cell needs a missing rate, so no curve is drawn', () => {
        const unconverted: BudgetDailyCell = { categoryId: 'a', day: 2, amount: null, rateUnavailable: [MISSING] };
        expect(valuedCells([cell(1, 10), unconverted])).toBeNull();
        expect(valuedCells([cell(1, 10), cell(2, 5)])).toEqual([cell(1, 10), cell(2, 5)]);
    });

    it('gives no ceiling when a root mark needs a missing rate, never the sum of the others', () => {
        const rows = [row({ categoryId: 'a', mark: 100 }), row({ categoryId: 'b', mark: null, typical: null, rateUnavailable: [MISSING] })];
        expect(ceilingFromRows(rows)).toBeNull();
        // A row with no history is still left out, as before.
        expect(ceilingFromRows([row({ categoryId: 'a', mark: 100 }), row({ categoryId: 'b', mark: null, typical: null })])).toBe(100);
    });

    it('judges no row whose spend needs a missing rate', () => {
        expect(pastMark({ actual: null, mark: 50 }, 'USD')).toBeNull();
    });
});
