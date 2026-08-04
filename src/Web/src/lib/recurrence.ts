// RRULE -> human-readable text for the reminders agenda/list (ADR-0049).
//
// The server (Ical.Net) owns RRULE validation + occurrence expansion; the SPA
// never expands a rule (it reads materialized slots from /reminders/upcoming).
// Its only RRULE job here is to render a friendly summary, so this is a small
// hand-rolled humanizer over the closed pattern set the editor produces
// (daily / weekly-by-day / monthly-by-day / monthly-last-day / yearly +
// interval) — no `rrule` dependency (pinned-deps posture, ADR-0049 D4). The
// editor's buildRrule / parseRrule (ADR-0051) over the SAME closed set live
// alongside this humanizer below.
//
// Phrasing is intentionally clearer than Moneydance's ("Every 2 weeks on Mon"
// vs MD's "daily (14 days)"; "Every 3 months" vs "monthly (every third)").

const DAY_NAMES: Record<string, string> = {
    SU: 'Sun', MO: 'Mon', TU: 'Tue', WE: 'Wed', TH: 'Thu', FR: 'Fri', SA: 'Sat',
};
const DAY_ORDER = ['SU', 'MO', 'TU', 'WE', 'TH', 'FR', 'SA'];

function ruleParts(rrule: string): Map<string, string> {
    const map = new Map<string, string>();
    // Tolerate an optional `RRULE:` prefix; split on `;` into KEY=VALUE pairs.
    for (const segment of rrule.replace(/^RRULE:/i, '').split(';')) {
        const eq = segment.indexOf('=');
        if (eq <= 0) continue;
        map.set(segment.slice(0, eq).trim().toUpperCase(), segment.slice(eq + 1).trim());
    }
    return map;
}

function ordinal(n: number): string {
    const v = n % 100;
    if (v >= 11 && v <= 13) return `${n}th`;
    switch (n % 10) {
        case 1: return `${n}st`;
        case 2: return `${n}nd`;
        case 3: return `${n}rd`;
        default: return `${n}th`;
    }
}

/** "Every day" / "Every 3 days" etc. */
function everyN(n: number, unit: string): string {
    return n === 1 ? `Every ${unit}` : `Every ${n} ${unit}s`;
}

/**
 * Human summary of an RFC-5545 RRULE for the agenda/list.
 *
 * - blank / irregular (no rule) → `"Custom"` (a manual-fire reminder).
 * - a rule outside the supported pattern set → `"Custom schedule"`
 *   (never a wrong phrase — we don't guess at BYSETPOS etc.).
 */
export function humanizeRrule(rrule: string | null | undefined): string {
    if (rrule === null || rrule === undefined || rrule.trim().length === 0) {
        return 'Custom';
    }
    const parts = ruleParts(rrule);
    const interval = Number(parts.get('INTERVAL') ?? '1') || 1;

    switch (parts.get('FREQ')?.toUpperCase()) {
        case 'DAILY':
            return everyN(interval, 'day');

        case 'WEEKLY': {
            const base = everyN(interval, 'week');
            const byday = parts.get('BYDAY');
            if (byday === undefined) return base;
            const codes = byday.split(',')
                .map((d) => d.trim().toUpperCase())
                .filter((c) => c in DAY_NAMES);
            codes.sort((a, b) => DAY_ORDER.indexOf(a) - DAY_ORDER.indexOf(b));
            return codes.length > 0
                ? `${base} on ${codes.map((c) => DAY_NAMES[c]).join(', ')}`
                : base;
        }

        case 'MONTHLY': {
            const base = interval === 1 ? 'Monthly' : `Every ${interval} months`;
            const monthDay = parts.get('BYMONTHDAY');
            if (monthDay === '-1') return `${base} on the last day`;
            const day = Number(monthDay);
            return monthDay !== undefined && !Number.isNaN(day)
                ? `${base} on the ${ordinal(day)}`
                : base;
        }

        case 'YEARLY':
            return everyN(interval, 'year');

        default:
            return 'Custom schedule';
    }
}

// ---------------------------------------------------------------------------
// RRULE build/parse for the reminder-editor recurrence builder (ADR-0051 R2).
//
// Same closed pattern set as humanizeRrule above (daily / weekly-by-day /
// monthly-by-day / monthly-last-day / yearly + interval), and the same
// no-`rrule`-dependency posture. buildRrule produces exactly the strings
// humanizeRrule parses; parseRrule is its inverse for edit-mode prefill and
// returns null for anything outside the supported set (never guesses).
// ---------------------------------------------------------------------------

/** Editor-facing recurrence model for the closed supported pattern set. */
export interface RecurrenceState {
    freq: 'daily' | 'weekly' | 'monthly' | 'yearly';
    interval: number;            // >= 1
    weekdays: string[];          // WEEKLY only: subset of DAY_ORDER (SU..SA) in that canonical order; [] otherwise
    monthDay: number | 'last';   // MONTHLY only: 1..31, or 'last' (=> BYMONTHDAY=-1); ignored for other freqs
}

/**
 * Build an RFC-5545 RRULE string from editor state.
 *
 * - Always emits `FREQ=<UPPER>`; emits `INTERVAL=<n>` only when n > 1
 *   (humanizeRrule treats a missing INTERVAL as 1).
 * - WEEKLY: appends `BYDAY=` (canonical SU..SA order) when weekdays non-empty.
 * - MONTHLY: appends `BYMONTHDAY=<n>` (or `-1` for 'last').
 * - DAILY / YEARLY: no BY* parts (start-date anchors the month+day).
 */
export function buildRrule(state: RecurrenceState): string {
    const segments = [`FREQ=${state.freq.toUpperCase()}`];
    if (state.interval > 1) {
        segments.push(`INTERVAL=${state.interval}`);
    }
    if (state.freq === 'weekly' && state.weekdays.length > 0) {
        const codes = [...state.weekdays]
            .sort((a, b) => DAY_ORDER.indexOf(a) - DAY_ORDER.indexOf(b));
        segments.push(`BYDAY=${codes.join(',')}`);
    }
    if (state.freq === 'monthly') {
        segments.push(`BYMONTHDAY=${state.monthDay === 'last' ? -1 : state.monthDay}`);
    }
    return segments.join(';');
}

/**
 * Parse an RFC-5545 RRULE back into editor state for edit-mode prefill.
 *
 * Returns null for blank/null input OR any rule outside the supported set
 * (unknown FREQ, BYSETPOS, ordinal BYDAY like `2MO`, etc.) — never guesses.
 */
export function parseRrule(rrule: string | null | undefined): RecurrenceState | null {
    if (rrule === null || rrule === undefined || rrule.trim().length === 0) {
        return null;
    }
    const parts = ruleParts(rrule);
    const freqRaw = parts.get('FREQ')?.toUpperCase();
    const interval = Number(parts.get('INTERVAL') ?? '1') || 1;

    switch (freqRaw) {
        case 'DAILY':
        case 'YEARLY': {
            // No BY* parts allowed for these freqs.
            if (parts.has('BYDAY') || parts.has('BYMONTHDAY')) return null;
            return {
                freq: freqRaw === 'DAILY' ? 'daily' : 'yearly',
                interval,
                weekdays: [],
                monthDay: 1,
            };
        }

        case 'WEEKLY': {
            if (parts.has('BYMONTHDAY')) return null;
            const byday = parts.get('BYDAY');
            let weekdays: string[] = [];
            if (byday !== undefined) {
                const codes = byday.split(',').map((d) => d.trim().toUpperCase());
                // Reject ordinal prefixes (e.g. `2MO`) and unknown codes — out of set.
                if (!codes.every((c) => DAY_ORDER.includes(c))) return null;
                weekdays = [...codes].sort((a, b) => DAY_ORDER.indexOf(a) - DAY_ORDER.indexOf(b));
            }
            return { freq: 'weekly', interval, weekdays, monthDay: 1 };
        }

        case 'MONTHLY': {
            if (parts.has('BYDAY')) return null;
            const raw = parts.get('BYMONTHDAY');
            let monthDay: number | 'last' = 1;
            if (raw !== undefined) {
                if (raw === '-1') {
                    monthDay = 'last';
                } else {
                    const day = Number(raw);
                    if (!Number.isInteger(day) || day < 1 || day > 31) return null;
                    monthDay = day;
                }
            }
            return { freq: 'monthly', interval, weekdays: [], monthDay };
        }

        default:
            return null;
    }
}
