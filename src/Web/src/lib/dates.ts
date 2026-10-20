// Centralized date / datetime formatting for ISO strings crossing
// the API ↔ SPA boundary.
//
// The server stores most "calendar" fields as TIMESTAMPTZ but their
// semantic is a CALENDAR DATE — `posted_at`, `transacted_at`,
// `as_of`, `acquired_at`, the security-split date — none have a
// meaningful time-of-day. The SPA sends them as
// `YYYY-MM-DDT00:00:00.000Z` (UTC midnight). Formatting those as
// local time silently shifts to the previous day for any user east
// of UTC (everywhere in the Americas, every evening on the West
// Coast, etc.).
//
// Other fields ARE wall-clock instants — `created_at`, `cleared_at`,
// `last_sync_at`. For those the local-timezone display is correct.
//
// The rule:
//
//   - CALENDAR DATE → `formatLedgerDate` (anchored to UTC; the
//     formatted value matches the day the user picked in the date
//     input regardless of their timezone).
//   - INSTANT → `formatLedgerDateTime` (or pick a fitting locale
//     formatter directly; local timezone is correct).
//
// All ad-hoc `new Intl.DateTimeFormat(undefined, …)` + `formatDate`
// helpers across the codebase should be replaced with these. If
// you find an exception, add a comment explaining why local-tz is
// correct for that field.

/**
 * Calendar-date formatter — interprets the ISO string as a wall-clock
 * date (UTC-anchored). Use for `posted_at`, `transacted_at`,
 * `as_of`, `acquired_at`, `split_at`, `price_as_of`, etc.
 *
 * `2026-05-18T00:00:00Z` always renders as "May 18, 2026" regardless
 * of the user's timezone.
 *
 * Locale follows the OS / browser default (passing `undefined` to
 * `Intl.DateTimeFormat`); only the time zone is pinned.
 */
const LEDGER_DATE_FORMAT = new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
});

/**
 * Compact variant — `May 18` (no year). Used in dense rows where the
 * year is implied by surrounding context (the scroll-track's year
 * labels, day-stamped split rows). Same UTC-anchored semantic.
 */
const LEDGER_DATE_COMPACT_FORMAT = new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: '2-digit',
    timeZone: 'UTC',
});

/**
 * Full date + month + year — `May 18, 2026`. Identical to
 * `LEDGER_DATE_FORMAT` today; kept as a named export so callers who
 * specifically want the full long form can express that intent.
 */
const LEDGER_DATE_LONG_FORMAT = new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: '2-digit',
    year: 'numeric',
    timeZone: 'UTC',
});

/**
 * Instant formatter — local timezone, full date + time. Use for
 * `created_at`, `cleared_at`, `last_sync_at`, anything that
 * represents a wall-clock event.
 */
const LEDGER_DATE_TIME_FORMAT = new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
});

/**
 * Format a calendar-date ISO string for display. Pass the raw API
 * value; the result matches the day the user picked / saved.
 *
 * Returns the input string unchanged if it can't be parsed (defensive
 * — the server should always send a valid ISO).
 */
export function formatLedgerDate(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return LEDGER_DATE_FORMAT.format(d);
}

/**
 * Compact `May 18` — no year. Same UTC-anchored semantic.
 */
export function formatLedgerDateCompact(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return LEDGER_DATE_COMPACT_FORMAT.format(d);
}

/**
 * Long form `May 18, 2026` — explicit name for callers who want the
 * full date + year.
 */
export function formatLedgerDateLong(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return LEDGER_DATE_LONG_FORMAT.format(d);
}

/**
 * Format a wall-clock instant ISO string in the user's local
 * timezone. Use for `created_at`, `cleared_at`, sync timestamps —
 * any field whose value is a moment in time, not a calendar date.
 */
export function formatLedgerDateTime(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return LEDGER_DATE_TIME_FORMAT.format(d);
}

/**
 * Calendar-date ISO → `YYYY-MM-DD` for `<input type="date">`. Reads
 * the UTC components so the value round-trips through a date input
 * without timezone shift. Mirror of `formatLedgerDate`'s
 * UTC-anchored semantic.
 */
export function toDateInputValue(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return `${d.getUTCFullYear().toString().padStart(4, '0')}-${
        (d.getUTCMonth() + 1).toString().padStart(2, '0')}-${
        d.getUTCDate().toString().padStart(2, '0')}`;
}

/**
 * Today's date as `YYYY-MM-DD` in the user's local timezone — what
 * they'd write down on a paper register. The input then round-trips
 * via `${value}T00:00:00.000Z` on save, and `formatLedgerDate`
 * recovers the same day for display.
 *
 * Why local (not UTC) for "today": a user in Los Angeles at 9pm on
 * May 17 should see "today = May 17", not May 18 (which it'd be in
 * UTC). The submitted ISO becomes `2026-05-17T00:00:00Z`, and the
 * UTC-anchored display recovers `May 17` for everyone.
 */
export function todayInputValue(): string {
    const d = new Date();
    return `${d.getFullYear().toString().padStart(4, '0')}-${
        (d.getMonth() + 1).toString().padStart(2, '0')}-${
        d.getDate().toString().padStart(2, '0')}`;
}

/**
 * Shift a `YYYY-MM-DD` value by `delta` days. Used by editor
 * keyboard nav (+/- to step a day). UTC-arithmetic to avoid DST
 * edge cases at the local-midnight boundary.
 */
export function shiftDateInputValue(current: string, delta: number): string {
    const base = current.length > 0 && !Number.isNaN(Date.parse(current))
        ? new Date(current)
        : new Date();
    base.setUTCDate(base.getUTCDate() + delta);
    return `${base.getUTCFullYear().toString().padStart(4, '0')}-${
        (base.getUTCMonth() + 1).toString().padStart(2, '0')}-${
        base.getUTCDate().toString().padStart(2, '0')}`;
}
