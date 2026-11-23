import { atDisplayPrecision } from '@/lib/money';
import type { BudgetCategoryRow, BudgetDailyCell, BudgetProgress, RateUnavailable } from '@/lib/types';

/**
 * The pure arithmetic behind the budget hero, kept out of the component so it
 * can be tested without rendering an SVG — and because every one of these rules
 * is a decision someone will judge their spending by, not a drawing detail.
 */

/** A day's cell with a figure: one whose rates were all there (ADR-0114 D9). */
export type ValuedCell = BudgetDailyCell & { amount: number };

/**
 * The cells, when every one has a figure; null when a rate one of them needs is
 * missing. Nothing is drawn, projected or dated from a month with such a cell: a
 * curve that skipped it would be a partial sum.
 */
export function valuedCells(cells: readonly BudgetDailyCell[]): readonly ValuedCell[] | null {
    return cells.every((c): c is ValuedCell => c.amount !== null) ? cells : null;
}

/**
 * Every missing rate a figure of the month needed (ADR-0114 D9): its totals', its
 * rows' and its days'. Empty when every figure converted.
 */
export function budgetRateUnavailable(data: BudgetProgress): RateUnavailable[] {
    return [
        ...(data.rateUnavailable ?? []),
        ...data.rows.flatMap((r) => r.rateUnavailable ?? []),
        ...data.dailyActual.flatMap((c) => c.rateUnavailable ?? []),
        ...data.dailyNormal.flatMap((c) => c.rateUnavailable ?? []),
    ];
}

/**
 * Running total per day, 1..days. Index 0 is day 1.
 *
 * Cells are SPARSE: a day with no spend has no cell, and a cumulative series
 * that skipped those days would compress the x-axis and make a quiet fortnight
 * look like a steep climb. So the series is dense by construction and a day
 * with nothing simply repeats the previous total.
 */
export function cumulative(
    cells: readonly ValuedCell[],
    days: number,
    categoryId?: string,
): number[] {
    const perDay = new Array<number>(days + 1).fill(0);
    for (const c of cells) {
        if (categoryId !== undefined && c.categoryId !== categoryId) continue;
        if (c.day < 1 || c.day > days) continue;
        perDay[c.day] += c.amount;
    }
    const out: number[] = [];
    let running = 0;
    for (let d = 1; d <= days; d++) {
        running += perDay[d]!;
        out.push(running);
    }
    return out;
}

/**
 * The baseline the shaded band is drawn around and the projection reads:
 * `dailyNormal` accumulated, per day 1..days. Index 0 is day 1.
 *
 * The window's cells are keyed by each window month's own day of the month, so
 * they run to that month's last day, which can be past this month's. That spend
 * is part of the usual month all the same, so it counts on this month's last day.
 * Dropped, as {@link cumulative} drops a day past the month, it would leave the
 * band's end and the usual rest of the month short of the window's mean month by
 * whatever the window spent on the days this month does not have. Only this
 * month's own cells treat such a day as a calendar disagreement.
 */
export function usualByDay(cells: readonly ValuedCell[], days: number): number[] {
    return cumulative(cells.map((c) => (c.day > days ? { ...c, day: days } : c)), days);
}

/**
 * The rows dated after today, per day (index 0 is day 1), split by sign: the rows
 * the register shows as Scheduled. Zero on every day up to and including today.
 *
 * Split because the projection reads the two differently ({@link projectedDays}):
 * scheduled spend may be a bill the usual month holds too, while a scheduled refund
 * or other credit is money coming back whatever the usual month holds. A cell is one
 * category's day, the finest the API sends.
 */
export interface ScheduledDays {
    /** Spend: the cells that add to the month. */
    debits: readonly number[];
    /** Refunds and other credits: the cells that take from it, as negative amounts. */
    credits: readonly number[];
}

/** The rows dated after `elapsedDays`, per day and split by sign: see {@link ScheduledDays}. */
export function scheduledByDay(
    cells: readonly ValuedCell[],
    days: number,
    elapsedDays: number,
    categoryId?: string,
): ScheduledDays {
    const debits = new Array<number>(days).fill(0);
    const credits = new Array<number>(days).fill(0);
    for (const c of cells) {
        if (categoryId !== undefined && c.categoryId !== categoryId) continue;
        if (c.day <= elapsedDays || c.day > days) continue;
        if (c.amount < 0) credits[c.day - 1]! += c.amount;
        else debits[c.day - 1]! += c.amount;
    }
    return { debits, credits };
}

/** Whether any row is scheduled after today: what the curve marks and its legend explains. */
export function hasScheduled(scheduled: ScheduledDays): boolean {
    return scheduled.debits.some((v) => v > 0) || scheduled.credits.some((v) => v < 0);
}

/** One day of {@link projectedPath}, with what the curve needs to draw a scheduled row's step. */
export interface ProjectedDay {
    /** Day of the month, 1-based; the first is today. */
    day: number;
    /** The projection on this day. */
    value: number;
    /** Spend scheduled on this day: zero on today. */
    bill: number;
    /** Credits scheduled on this day, as a negative amount: zero on today. */
    credit: number;
    /** The projection on this day without its own scheduled rows, bills and credits
     *  alike: where their step starts. */
    beforeBill: number;
}

/**
 * Where the month heads from today, day by day (the first is today, `elapsedDays`):
 * what is spent so far, plus the LARGER of the spend still scheduled by that day and
 * what is usually spent from tomorrow to that day, plus every credit scheduled by
 * that day.
 *
 * NOT THE RATE SO FAR. Spending is not a daily rate. A payment that leaves once a
 * month, early in it, carried forward as spend ÷ days elapsed is multiplied by the
 * days in the month ÷ the days elapsed; a one-off is multiplied the same way.
 * The usual part comes from `normal`, the baseline the shaded band is drawn
 * around: the trailing window's MEAN spend per day of the month (the window's
 * total for that day ÷ the window's months in which the ledger already existed on
 * that day, which is the window's length unless the ledger is younger, as the API
 * sends it), accumulated.
 * It knows when money usually leaves, so a payment already gone counts once, in
 * what is spent, and each day after today adds what that day usually adds.
 *
 * THE LARGER, NOT THE SUM, FOR SPEND. A scheduled bill is often one the usual
 * month holds too, and adding both would count it twice. Taking the larger keeps
 * a bill bigger than the usual remainder landing on its own date (ADR-0109 D10).
 *
 * A CREDIT IN FULL. A refund or other credit scheduled is money coming back, and
 * it comes back whatever the usual month holds, so it is added outside the larger,
 * on its own date. Inside it, it would be lost whenever the usual remainder is the
 * larger, and the path would end above the month's Spent, which counts it.
 *
 * So on each day the path is never below what is spent plus what is scheduled
 * through that day, bills and credits alike, and the month-end figure is never
 * below the month's Spent.
 *
 * A month running hotter than usual still projects higher, because what is
 * spent is real. With no usable history the baseline is zero on every day and
 * the projection is what is spent plus what is scheduled, so it ends at Spent.
 *
 * Null before anything has happened: a projection from zero days has nothing to
 * start from.
 */
export function projectedDays(
    series: readonly number[],
    elapsedDays: number,
    days: number,
    scheduled: ScheduledDays,
    normal: readonly number[],
): ProjectedDay[] | null {
    if (elapsedDays < 1 || series.length === 0) return null;
    const spent = series[Math.min(elapsedDays, series.length) - 1] ?? 0;
    // The band's own indexing: day d is normal[d - 1].
    const usualBy = (day: number) =>
        normal.length === 0 ? 0 : (normal[Math.min(day, normal.length) - 1] ?? 0);
    const usualToday = usualBy(elapsedDays);
    const out: ProjectedDay[] = [];
    let billsBy = 0;
    let creditsBy = 0;
    for (let d = elapsedDays; d <= days; d++) {
        const bill = d > elapsedDays ? (scheduled.debits[d - 1] ?? 0) : 0;
        const credit = d > elapsedDays ? (scheduled.credits[d - 1] ?? 0) : 0;
        billsBy += bill;
        creditsBy += credit;
        const usualFromTomorrow = usualBy(d) - usualToday;
        out.push({
            day: d,
            value: spent + Math.max(billsBy, usualFromTomorrow) + creditsBy,
            bill,
            credit,
            beforeBill: spent + Math.max(billsBy - bill, usualFromTomorrow) + (creditsBy - credit),
        });
    }
    return out;
}

/**
 * The line under a curve with scheduled rows, saying what the circles are and
 * what the dashed line adds up. Beside the rule it describes, so the two change
 * together; the hero and the home screen's This month both show it.
 */
export const PROJECTION_LEGEND =
    '○ a scheduled bill or refund, on its date. The dashed line adds to what you\'ve spent whichever is larger: '
    + 'the bills still scheduled, or what you usually spend in the rest of the month. A scheduled refund comes off in full.';

/** The projection's values, day by day from today: see {@link projectedDays}. */
export function projectedPath(
    series: readonly number[],
    elapsedDays: number,
    days: number,
    scheduled: ScheduledDays,
    normal: readonly number[],
): number[] | null {
    return projectedDays(series, elapsedDays, days, scheduled, normal)?.map((p) => p.value) ?? null;
}

/** Where the month ends up: the last point of {@link projectedPath}. */
export function projectTo(
    series: readonly number[],
    elapsedDays: number,
    days: number,
    scheduled: ScheduledDays,
    normal: readonly number[],
): number | null {
    const path = projectedPath(series, elapsedDays, days, scheduled, normal);
    return path === null ? null : (path.at(-1) ?? 0);
}

/**
 * The day the month goes past the ceiling for good, or null when it ends inside it.
 *
 * This is the actionable fact on the whole screen — not "you are 12% over" but
 * "you run out on the 24th". The month's values are its own line on the days up to
 * today and {@link projectedPath} after; the crossing is the first day from which
 * every value through the month's last day is past the ceiling. A breach already
 * behind today that stays is dated the day it happened, and a future one can fall
 * on a scheduled bill's own date.
 *
 * FOR GOOD, NOT FIRST. The path can come back under the ceiling (a usual day that
 * is net a refund, a scheduled credit), so the first day past it is not where the
 * month ends up. Dated by the first, a month ending inside the ceiling would still
 * be told it crosses it, in red, beside a month-end figure that is not. Dated for
 * good, in a month that has started there is a crossing exactly when the month-end
 * figure, the last of these values, is past the ceiling, so the date and the figure
 * always agree. A month that has not started has no values to judge and no
 * month-end figure, so it has no crossing either.
 *
 * Past is {@link pastBy}'s: any amount past the ceiling as the two figures print,
 * on the days gone and the days projected alike. A total landing exactly on the
 * ceiling is at it, not past it, and so is one finer than a printed figure. A ceiling
 * of zero is judged the same way: any spending past it is past it, dated like any
 * other, so a month with every mark at zero is not told it finishes inside it beside
 * a month-end figure in red.
 */
export function crossingDay(
    series: readonly number[],
    elapsedDays: number,
    days: number,
    ceiling: number,
    currency: string,
    scheduled: ScheduledDays,
    normal: readonly number[],
): number | null {
    const lastActual = Math.min(elapsedDays, days);
    const path = lastActual < days ? projectedPath(series, elapsedDays, days, scheduled, normal) : [];
    // A month that has not started has no values to judge.
    if (path === null) return null;
    const valueOn = (day: number) =>
        day <= lastActual ? (series[day - 1] ?? 0) : (path[day - elapsedDays] ?? 0);
    let crossing: number | null = null;
    for (let d = days; d >= 1 && pastBy(valueOn(d), ceiling, currency) > 0; d--) {
        crossing = d;
    }
    return crossing;
}

/**
 * The rows that carry the whole tree exactly once.
 *
 * THE API ROLLS UP, so its rows contain parents AND their children, and a
 * parent's amount already includes every descendant's. Anything that SUMS or
 * COUNTS rows has to reduce to roots first or it double-counts — the hero drew
 * a ceiling far too high this way, against a table that had already been
 * fixed, while a comment two lines up promised the two could never disagree.
 * Sharing one definition is what makes that promise structural.
 *
 * "Root" means no parent IN THIS RESULT, not `parentId === null`: a category
 * whose parent had no spending is absent from the rows, and treating it as a
 * child would drop it entirely.
 */
export function rootRows(rows: readonly BudgetCategoryRow[]): BudgetCategoryRow[] {
    const present = new Set(rows.map((r) => r.categoryId));
    return rows.filter((r) => r.parentId === null || !present.has(r.parentId));
}

/**
 * The ceiling the hero draws: the sum of the marks the TABLE shows.
 *
 * Load-bearing that this derives from the same rows the table renders. If the
 * hero computed its own limit, the two could disagree on screen, and the user
 * would be right to trust neither. A row with no history contributes nothing
 * rather than zero — an unknown is not a commitment to spend nothing. A row whose
 * mark needs a missing rate is not unknown but unconverted, so the ceiling is null
 * with it rather than a partial sum (ADR-0114 D9).
 */
export function ceilingFromRows(rows: readonly BudgetCategoryRow[]): number | null {
    if (rootRows(rows).some((r) => r.mark === null && (r.rateUnavailable?.length ?? 0) > 0)) return null;
    // MARK, not typical. The mark is the row's typed target where it has one,
    // and otherwise its derived normal already adjusted for anything typed
    // beneath it (ADR-0099 D1a) — so summing marks over roots is what keeps the
    // ceiling equal to the sum of the ticks the table actually draws. Summing
    // `typical` here would quietly ignore every target on the screen.
    const known = rootRows(rows).filter((r) => r.mark !== null);
    if (known.length === 0) return null;
    return known.reduce((sum, r) => sum + (r.mark ?? 0), 0);
}

/**
 * Whether a target is STORED for this row's category and month (ADR-0099 D1).
 * `target` alone cannot say: a target typed in another currency whose rate is
 * missing arrives as `target` null, the very shape of no target (ADR-0114 D9), and
 * only its `targetCurrencyCode` tells the two apart. A stored target counts whether
 * or not it converted: it still blocks its ancestors and descendants (ADR-0099 D1a),
 * the month still has it, and its mark still rests on it.
 */
export function hasStoredTarget(row: Pick<BudgetCategoryRow, 'target' | 'targetCurrencyCode'>): boolean {
    return row.target !== null || (row.targetCurrencyCode ?? null) !== null;
}

/**
 * Whether a mark rests on a typed target, which is what its basis line says:
 * "budgeted" when it does, the typical over the window when it does not. With no
 * category named it is the month's whole mark, the sum of the roots'; with one,
 * that category's. Under ADR-0099 D1a an untargeted category's mark carries every
 * target typed beneath it, so a target anywhere in its subtree counts, not only
 * its own: one rule, the month's any-target rule, scoped to the subtree.
 *
 * `parentOf` walks up the tree. The rows' own parents serve when none is given,
 * but a category the month reports nothing for is absent from the rows, so a
 * caller holding the whole tree passes it.
 */
export function markIsBudgeted(
    rows: readonly BudgetCategoryRow[],
    categoryId?: string,
    parentOf?: (id: string) => string | null,
): boolean {
    const targeted = rows.filter(hasStoredTarget);
    if (categoryId === undefined) return targeted.length > 0;
    const rowParents = new Map(rows.map((r) => [r.categoryId, r.parentId]));
    const up = parentOf ?? ((id: string) => rowParents.get(id) ?? null);
    const MAX_DEPTH = 64;
    return targeted.some((r) => {
        let cur: string | null = r.categoryId;
        for (let hops = 0; cur !== null && hops <= MAX_DEPTH; hops++) {
            if (cur === categoryId) return true;
            cur = up(cur);
        }
        return false;
    });
}

/**
 * How far a row's spend is past its mark, as the two figures print: positive is
 * over by that much, zero is at the mark, negative is under, and null is no mark
 * to judge by. Over is over: any amount past the mark counts, with no floor. The
 * figures are compared as printed because the mark is often a mean, finer than a
 * printed figure, and a difference the two figures cannot show would read as an
 * overspend of nothing beside two equal numbers.
 */
export function pastMark(row: Pick<BudgetCategoryRow, 'actual' | 'mark'>, currency: string): number | null {
    // A spend or a mark that needs a missing rate has nothing to judge by.
    if (row.mark === null || row.actual === null) return null;
    return pastBy(row.actual, row.mark, currency);
}

/**
 * How far an amount is past a mark as the two print, the comparison
 * {@link pastMark} makes for a row: positive is past it by that much, zero is at
 * it, negative is short of it. The projection and the date it crosses on read
 * the same rule, so a total the figures print as equal is never past.
 */
export function pastBy(amount: number, mark: number, currency: string): number {
    return atDisplayPrecision(atDisplayPrecision(amount, currency) - atDisplayPrecision(mark, currency), currency);
}

/**
 * Rows a reader should look at first: past their own mark by any amount, worst
 * first.
 *
 * Roots only, for the same reason the ceiling is: counting a parent and its
 * children separately counts one overspend once per level, inflating the number
 * of categories past normal.
 */
export function overspentRows(
    rows: readonly BudgetCategoryRow[],
    currency: string,
): BudgetCategoryRow[] {
    // Against the MARK, so a category the user deliberately budgeted more for
    // stops being reported as over the moment they raise it.
    const over = (r: BudgetCategoryRow) => pastMark(r, currency) ?? 0;
    return rootRows(rows)
        .filter((r) => over(r) > 0)
        .sort((a, b) => over(b) - over(a));
}

/**
 * The category already holding a target that blocks one here, or null.
 *
 * ADR-0099 D1a: a target may sit on a node OR on its descendants, never both.
 * The server enforces it and refuses with the conflicting name; this is the
 * same question asked locally so the cell can render DISABLED with that name on
 * hover, rather than accepting a number and then rejecting it. Letting someone
 * type into a field that cannot accept the value is the worse failure.
 *
 * Computed from the rows on screen, which is a deliberate approximation: the
 * API returns categories with activity or a target, so an ancestor with neither
 * is absent and a conflict through it is invisible here. That direction is
 * safe — the write still goes to the server, which walks the whole tree and
 * refuses. This exists to make the common case legible, not to be the gate.
 */
export function targetConflict(
    rows: readonly BudgetCategoryRow[],
    categoryId: string,
): string | null {
    const byId = new Map(rows.map((r) => [r.categoryId, r]));
    const MAX_DEPTH = 64;

    // Upwards: anything above me already holding one.
    let cur = byId.get(categoryId)?.parentId ?? null;
    for (let hops = 0; cur !== null && hops < MAX_DEPTH; hops++) {
        const node = byId.get(cur);
        if (node === undefined) break;
        if (hasStoredTarget(node)) return node.name;
        cur = node.parentId;
    }

    // Downwards, by walking each TARGETED row up to see whether it passes
    // through me. Cheaper than materialising the subtree, because the targeted
    // set is one month's typed numbers while a top-level category's descendants
    // can be most of the chart of accounts.
    for (const other of rows) {
        if (!hasStoredTarget(other) || other.categoryId === categoryId) continue;
        let up = other.parentId;
        for (let hops = 0; up !== null && hops < MAX_DEPTH; hops++) {
            if (up === categoryId) return other.name;
            up = byId.get(up)?.parentId ?? null;
        }
    }

    return null;
}
