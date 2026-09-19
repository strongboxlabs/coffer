import type { BudgetCategoryRow, BudgetDailyCell } from '@/lib/types';

/**
 * The pure arithmetic behind the budget hero, kept out of the component so it
 * can be tested without rendering an SVG — and because every one of these rules
 * is a decision someone will judge their spending by, not a drawing detail.
 */

/**
 * Running total per day, 1..days. Index 0 is day 1.
 *
 * Cells are SPARSE: a day with no spend has no cell, and a cumulative series
 * that skipped those days would compress the x-axis and make a quiet fortnight
 * look like a steep climb. So the series is dense by construction and a day
 * with nothing simply repeats the previous total.
 */
export function cumulative(
    cells: readonly BudgetDailyCell[],
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
 * Where the month ends up if the rest of it runs at the rate seen so far.
 *
 * Deliberately NOT "typical total scaled by how hot the month is". Extrapolating
 * the OBSERVED rate answers "if I carry on like this", which is the question
 * someone looking at a projection is asking. Returns null before anything has
 * happened — a projection from zero days is a straight line through no data.
 */
export function projectTo(
    series: readonly number[],
    elapsedDays: number,
    days: number,
): number | null {
    if (elapsedDays < 1 || series.length === 0) return null;
    const spent = series[Math.min(elapsedDays, series.length) - 1] ?? 0;
    if (spent === 0) return 0;
    return (spent / elapsedDays) * days;
}

/**
 * The first day the projection crosses the ceiling, or null if it never does.
 *
 * This is the actionable fact on the whole screen — not "you are 12% over" but
 * "you run out on the 24th". Days already past count too: if the ceiling is
 * already breached, the answer is the day it happened, not a future date.
 */
export function crossingDay(
    series: readonly number[],
    elapsedDays: number,
    days: number,
    ceiling: number,
): number | null {
    if (ceiling <= 0) return null;
    for (let d = 1; d <= Math.min(elapsedDays, series.length); d++) {
        if ((series[d - 1] ?? 0) > ceiling) return d;
    }
    const projected = projectTo(series, elapsedDays, days);
    if (projected === null || projected <= ceiling) return null;
    const spent = series[Math.min(elapsedDays, series.length) - 1] ?? 0;
    const rate = spent / elapsedDays;
    if (rate <= 0) return null;
    const day = Math.ceil(ceiling / rate);
    return day > days ? null : Math.max(day, elapsedDays + 1);
}

/**
 * The rows that carry the whole tree exactly once.
 *
 * THE API ROLLS UP, so its rows contain parents AND their children, and a
 * parent's amount already includes every descendant's. Anything that SUMS or
 * COUNTS rows has to reduce to roots first or it double-counts — the hero drew
 * a ceiling 1.87× too high this way, against a table that had already been
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
 * rather than zero — an unknown is not a commitment to spend nothing.
 */
export function ceilingFromRows(rows: readonly BudgetCategoryRow[]): number | null {
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
 * Rows a reader should look at first: furthest past their own mark, worst first.
 *
 * Roots only, for the same reason the ceiling is: counting a parent and its
 * children separately reported nineteen categories past normal where ten were.
 */
export function overspentRows(
    rows: readonly BudgetCategoryRow[],
    floor = 1,
): BudgetCategoryRow[] {
    // Against the MARK, so a category the user deliberately budgeted more for
    // stops being reported as over the moment they raise it.
    return rootRows(rows)
        .filter((r) => r.mark !== null && r.actual - r.mark > floor)
        .sort((a, b) => (b.actual - (b.mark ?? 0)) - (a.actual - (a.mark ?? 0)));
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
        if (node.target !== null) return node.name;
        cur = node.parentId;
    }

    // Downwards, by walking each TARGETED row up to see whether it passes
    // through me. Cheaper than materialising the subtree, because the targeted
    // set is one month's typed numbers while a top-level category's descendants
    // can be most of the chart of accounts.
    for (const other of rows) {
        if (other.target === null || other.categoryId === categoryId) continue;
        let up = other.parentId;
        for (let hops = 0; up !== null && hops < MAX_DEPTH; hops++) {
            if (up === categoryId) return other.name;
            up = byId.get(up)?.parentId ?? null;
        }
    }

    return null;
}
