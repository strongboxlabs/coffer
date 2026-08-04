// Month-grid helpers for the reminders calendar (ADR-0049). Pure UTC date
// math; no calendar dependency (the grid is read-only - no date-selection
// widget, no popovers, no localization edge cases beyond the month label).

export interface CalendarCell {
    /** 'YYYY-MM-DD', UTC-anchored to match the API's calendar-date semantics. */
    date: string;
    /** True when this day falls in the requested month (vs the leading/trailing
     * days that pad the grid to full weeks). */
    inMonth: boolean;
}

function isoUtc(d: Date): string {
    return `${d.getUTCFullYear().toString().padStart(4, '0')}-${
        (d.getUTCMonth() + 1).toString().padStart(2, '0')}-${
        d.getUTCDate().toString().padStart(2, '0')}`;
}

/**
 * A Sun-first month grid padded to full weeks. `month` is 1-based (1 = Jan).
 * Each row is a 7-cell week; the grid is 4-6 weeks depending on the month.
 */
export function monthMatrix(year: number, month: number): CalendarCell[][] {
    const first = new Date(Date.UTC(year, month - 1, 1));
    const firstDow = first.getUTCDay();                       // 0 = Sun .. 6 = Sat
    const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate();
    const weeks = Math.ceil((firstDow + daysInMonth) / 7);

    const cursor = new Date(first);
    cursor.setUTCDate(cursor.getUTCDate() - firstDow);        // back up to that week's Sunday

    const grid: CalendarCell[][] = [];
    for (let w = 0; w < weeks; w += 1) {
        const row: CalendarCell[] = [];
        for (let d = 0; d < 7; d += 1) {
            row.push({ date: isoUtc(cursor), inMonth: cursor.getUTCMonth() === month - 1 });
            cursor.setUTCDate(cursor.getUTCDate() + 1);
        }
        grid.push(row);
    }
    return grid;
}

const MONTH_LABEL_FORMAT = new Intl.DateTimeFormat(undefined, {
    month: 'long', year: 'numeric', timeZone: 'UTC',
});

/** "June 2026" for a 1-based month. */
export function monthLabel(year: number, month: number): string {
    return MONTH_LABEL_FORMAT.format(new Date(Date.UTC(year, month - 1, 1)));
}

/** Step a {year, month} (1-based) by `delta` months, wrapping the year. */
export function addMonths(
    year: number, month: number, delta: number,
): { year: number; month: number } {
    const index = year * 12 + (month - 1) + delta;
    return { year: Math.floor(index / 12), month: (index % 12) + 1 };
}

/** The grid's visible window as inclusive [from, to] 'YYYY-MM-DD' (first and
 * last cells), for the `/reminders/upcoming?from&to` query. */
export function monthGridRange(year: number, month: number): { from: string; to: string } {
    const grid = monthMatrix(year, month);
    return { from: grid[0][0].date, to: grid[grid.length - 1][6].date };
}

/** Today as {year, month (1-based), date 'YYYY-MM-DD'} in local time - the
 * calendar's initial month + "today" highlight anchor. */
export function todayParts(): { year: number; month: number; date: string } {
    const now = new Date();
    return {
        year: now.getFullYear(),
        month: now.getMonth() + 1,
        date: `${now.getFullYear().toString().padStart(4, '0')}-${
            (now.getMonth() + 1).toString().padStart(2, '0')}-${
            now.getDate().toString().padStart(2, '0')}`,
    };
}
