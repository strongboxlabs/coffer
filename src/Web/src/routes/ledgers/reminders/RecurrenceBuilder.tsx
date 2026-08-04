import { type RecurrenceState, buildRrule, humanizeRrule } from '@/lib/recurrence';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { cn } from '@/lib/cn';

// Recurrence builder for the reminder editor (ADR-0051 slice B). The schedule
// section of the reminder dialog: a fully controlled view over the closed
// pattern set lib/recurrence.ts supports (daily / weekly-by-day /
// monthly-by-day / monthly-last-day / yearly + interval), plus the editor's
// own start/end/auto-commit fields.
//
// Controlled: the parent owns the canonical ScheduleValue and passes it in via
// `value`; every edit emits a new value through `onChange`. This component
// holds NO canonical state of its own — it's a pure projection, matching the
// AccountEditorDialog modern-web idioms (ADR-0023: sectioned, labels above
// inputs). The server (Ical.Net) owns RRULE expansion; buildRrule/humanizeRrule
// drive the live preview line only.

const inputClass =
    'mt-1 w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

const FREQ_OPTIONS: ReadonlyArray<{ value: RecurrenceState['freq']; label: string; unit: string }> = [
    { value: 'daily', label: 'Daily', unit: 'day' },
    { value: 'weekly', label: 'Weekly', unit: 'week' },
    { value: 'monthly', label: 'Monthly', unit: 'month' },
    { value: 'yearly', label: 'Yearly', unit: 'year' },
];

// Canonical SU..SA order (matches RecurrenceState.weekdays / lib/recurrence).
const WEEKDAYS: ReadonlyArray<{ code: string; label: string }> = [
    { code: 'SU', label: 'S' },
    { code: 'MO', label: 'M' },
    { code: 'TU', label: 'T' },
    { code: 'WE', label: 'W' },
    { code: 'TH', label: 'T' },
    { code: 'FR', label: 'F' },
    { code: 'SA', label: 'S' },
];

const WEEKDAY_ORDER = WEEKDAYS.map((d) => d.code);

export interface ScheduleValue {
    recurrence: RecurrenceState;          // { freq, interval, weekdays, monthDay }
    startDate: string;                    // 'YYYY-MM-DD'
    endDate: string | null;               // null = never
    autoCommitDaysBefore: number | null;  // null = manual approve; >=0 = auto-commit N days before due
}

/** Day-of-month (1..31) parsed from a 'YYYY-MM-DD' string; falls back to 1. */
function dayOfMonth(startDate: string): number {
    const day = Number.parseInt(startDate.slice(8, 10), 10);
    return Number.isInteger(day) && day >= 1 && day <= 31 ? day : 1;
}

/** Weekday code (SU..SA) of a 'YYYY-MM-DD' string, or null if unparseable. */
function weekdayOf(startDate: string): string | null {
    const ms = Date.parse(`${startDate}T00:00:00`);
    if (Number.isNaN(ms)) return null;
    return WEEKDAY_ORDER[new Date(ms).getDay()] ?? null;
}

/** Same month/day one calendar year later (Feb 29 -> Feb 28 in a non-leap
 *  target year). A sensible default end when the user switches to "ends On". */
function oneYearAfter(startDate: string): string {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(startDate);
    if (m === null) return startDate;
    const year = Number(m[1]) + 1;
    const isLeap = (y: number) => (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;
    const day = m[2] === '02' && m[3] === '29' && !isLeap(year) ? 28 : Number(m[3]);
    return `${year}-${m[2]}-${String(day).padStart(2, '0')}`;
}

/** A sensible default for create: Monthly on the start date's day-of-month, no end, manual. */
export function defaultSchedule(startDate: string): ScheduleValue {
    return {
        recurrence: { freq: 'monthly', interval: 1, weekdays: [], monthDay: dayOfMonth(startDate) },
        startDate,
        endDate: null,
        autoCommitDaysBefore: null,
    };
}

export function RecurrenceBuilder(props: {
    value: ScheduleValue;
    onChange: (next: ScheduleValue) => void;
    disabled?: boolean;
}): React.JSX.Element {
    const { value, onChange, disabled = false } = props;
    const { recurrence } = value;
    const freqMeta = FREQ_OPTIONS.find((f) => f.value === recurrence.freq) ?? FREQ_OPTIONS[0];

    const emitRecurrence = (patch: Partial<RecurrenceState>) =>
        onChange({ ...value, recurrence: { ...recurrence, ...patch } });

    const handleFreq = (freq: RecurrenceState['freq']) => {
        // Seed the by-parts when switching INTO a freq that needs them, so the
        // emitted recurrence is immediately valid (weekly => start's weekday;
        // monthly => start's day-of-month).
        if (freq === 'weekly' && recurrence.weekdays.length === 0) {
            const seed = weekdayOf(value.startDate);
            emitRecurrence({ freq, weekdays: seed ? [seed] : [] });
            return;
        }
        if (freq === 'monthly' && recurrence.monthDay === 'last') {
            emitRecurrence({ freq });
            return;
        }
        if (freq === 'monthly') {
            emitRecurrence({ freq, monthDay: dayOfMonth(value.startDate) });
            return;
        }
        emitRecurrence({ freq });
    };

    const toggleWeekday = (code: string) => {
        const has = recurrence.weekdays.includes(code);
        // Keep at least one weekday selected: an empty BYDAY makes buildRrule
        // drop the part, and the server then falls back to the start date's
        // weekday - so an empty UI would silently mean "the start weekday".
        if (has && recurrence.weekdays.length === 1) return;
        const next = has
            ? recurrence.weekdays.filter((c) => c !== code)
            : [...recurrence.weekdays, code];
        next.sort((a, b) => WEEKDAY_ORDER.indexOf(a) - WEEKDAY_ORDER.indexOf(b));
        emitRecurrence({ weekdays: next });
    };

    const monthDayIsLast = recurrence.monthDay === 'last';
    const monthDayNumber = typeof recurrence.monthDay === 'number' ? recurrence.monthDay : 1;

    const preview =
        humanizeRrule(buildRrule(recurrence)) +
        (value.startDate ? ` · from ${value.startDate}` : '') +
        (value.endDate ? `, until ${value.endDate}` : '');

    return (
        <div className="space-y-3">
            {/* Frequency + interval */}
            <div className="flex items-end gap-3">
                <div className="block flex-1">
                    <FieldLabel className="block">Frequency</FieldLabel>
                    <select
                        className={inputClass}
                        value={recurrence.freq}
                        disabled={disabled}
                        aria-label="Frequency"
                        onChange={(e) => handleFreq(e.target.value as RecurrenceState['freq'])}
                    >
                        {FREQ_OPTIONS.map((f) => (
                            <option key={f.value} value={f.value}>{f.label}</option>
                        ))}
                    </select>
                </div>
                <div className="block flex-1">
                    <FieldLabel className="block">Every</FieldLabel>
                    <div className="mt-1 flex items-center gap-2">
                        <input
                            type="number"
                            min={1}
                            className={cn(inputClass, 'mt-0 w-20')}
                            value={recurrence.interval}
                            disabled={disabled}
                            aria-label="Interval"
                            onChange={(e) => {
                                const n = Number.parseInt(e.target.value, 10);
                                emitRecurrence({ interval: Number.isInteger(n) && n >= 1 ? n : 1 });
                            }}
                        />
                        <span className="text-sm text-text-muted">
                            {recurrence.interval === 1 ? freqMeta.unit : `${freqMeta.unit}s`}
                        </span>
                    </div>
                </div>
            </div>

            {/* Weekly: weekday toggles */}
            {recurrence.freq === 'weekly' ? (
                <div>
                    <FieldLabel className="block">On days</FieldLabel>
                    <div className="mt-1 flex gap-1.5" role="group" aria-label="Weekdays">
                        {WEEKDAYS.map((d, i) => {
                            const on = recurrence.weekdays.includes(d.code);
                            return (
                                <button
                                    key={d.code}
                                    type="button"
                                    disabled={disabled}
                                    aria-pressed={on}
                                    aria-label={d.code}
                                    title={d.code}
                                    onClick={() => toggleWeekday(d.code)}
                                    className={cn(
                                        'h-8 w-8 rounded-full border text-xs font-medium transition-colors',
                                        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                                        'disabled:pointer-events-none disabled:opacity-50',
                                        on
                                            ? 'border-accent bg-accent text-text-inverse'
                                            : 'border-border bg-surface text-text hover:bg-surface-hover',
                                    )}
                                >
                                    {/* Distinguish the two S / two T columns for screen readers via aria-label. */}
                                    <span aria-hidden>{d.label}</span>
                                    <span className="sr-only">{` (${d.code}) column ${i + 1}`}</span>
                                </button>
                            );
                        })}
                    </div>
                </div>
            ) : null}

            {/* Monthly: day-of-month + last-day toggle */}
            {recurrence.freq === 'monthly' ? (
                <div>
                    <div className="flex items-end gap-3">
                        <div className="block">
                            <FieldLabel className="block">Day of month</FieldLabel>
                            <input
                                type="number"
                                min={1}
                                max={31}
                                className={cn(inputClass, 'mt-1 w-24')}
                                value={monthDayIsLast ? '' : monthDayNumber}
                                disabled={disabled || monthDayIsLast}
                                aria-label="Day of month"
                                onChange={(e) => {
                                    const n = Number.parseInt(e.target.value, 10);
                                    if (!Number.isInteger(n)) return;
                                    emitRecurrence({ monthDay: Math.min(31, Math.max(1, n)) });
                                }}
                            />
                        </div>
                        <label className="flex items-center gap-2 pb-2">
                            <input
                                type="checkbox"
                                checked={monthDayIsLast}
                                disabled={disabled}
                                onChange={(e) =>
                                    emitRecurrence({
                                        monthDay: e.target.checked ? 'last' : dayOfMonth(value.startDate),
                                    })
                                }
                            />
                            <span className="text-sm text-text">Last day</span>
                        </label>
                    </div>
                    {typeof recurrence.monthDay === 'number' && recurrence.monthDay >= 29 ? (
                        <p className="mt-1 text-xs text-text-muted">
                            Days 29-31 are skipped in shorter months. Use the Last day option for month-end.
                        </p>
                    ) : null}
                </div>
            ) : null}

            {/* Start date */}
            <div className="block">
                <FieldLabel className="block">Start date</FieldLabel>
                <input
                    type="date"
                    className={inputClass}
                    value={value.startDate}
                    disabled={disabled}
                    aria-label="Start date"
                    onChange={(e) => onChange({ ...value, startDate: e.target.value })}
                />
            </div>

            {/* End */}
            <div>
                <FieldLabel className="block">Ends</FieldLabel>
                <div className="mt-1 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm text-text">
                    <label className="flex items-center gap-1.5">
                        <input
                            type="radio"
                            name="recurrence-end-mode"
                            checked={value.endDate === null}
                            disabled={disabled}
                            onChange={() => onChange({ ...value, endDate: null })}
                        />
                        <span>Never</span>
                    </label>
                    <label className="flex items-center gap-1.5">
                        <input
                            type="radio"
                            name="recurrence-end-mode"
                            checked={value.endDate !== null}
                            disabled={disabled}
                            // Selecting "On" seeds a concrete end date (one year
                            // out) so the choice registers and the date field is
                            // populated and editable; "Never" clears it again.
                            onChange={() =>
                                onChange({
                                    ...value,
                                    endDate: value.endDate ?? oneYearAfter(value.startDate),
                                })
                            }
                        />
                        <span>On</span>
                    </label>
                    <input
                        type="date"
                        className={cn(inputClass, 'mt-0 w-44')}
                        value={value.endDate ?? ''}
                        disabled={disabled}
                        aria-label="End date"
                        onChange={(e) => onChange({ ...value, endDate: e.target.value || null })}
                    />
                </div>
            </div>

            {/* Auto-commit */}
            <div>
                <FieldLabel className="block">Posting</FieldLabel>
                <div className="mt-1 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm text-text">
                    <label className="flex items-center gap-1.5">
                        <input
                            type="radio"
                            name="recurrence-commit-mode"
                            checked={value.autoCommitDaysBefore === null}
                            disabled={disabled}
                            onChange={() => onChange({ ...value, autoCommitDaysBefore: null })}
                        />
                        <span>Manual approval</span>
                    </label>
                    <label className="flex items-center gap-1.5">
                        <input
                            type="radio"
                            name="recurrence-commit-mode"
                            checked={value.autoCommitDaysBefore !== null}
                            disabled={disabled}
                            onChange={() =>
                                onChange({
                                    ...value,
                                    autoCommitDaysBefore: value.autoCommitDaysBefore ?? 0,
                                })
                            }
                        />
                        <span>Auto-post</span>
                    </label>
                    {value.autoCommitDaysBefore !== null ? (
                        <div className="flex items-center gap-2">
                            <input
                                type="number"
                                min={0}
                                className={cn(inputClass, 'mt-0 w-20')}
                                value={value.autoCommitDaysBefore}
                                disabled={disabled}
                                aria-label="Days before due"
                                onChange={(e) => {
                                    const n = Number.parseInt(e.target.value, 10);
                                    onChange({
                                        ...value,
                                        autoCommitDaysBefore: Number.isInteger(n) && n >= 0 ? n : 0,
                                    });
                                }}
                            />
                            <span className="text-text-muted">days before</span>
                        </div>
                    ) : null}
                </div>
            </div>

            {/* Live preview */}
            <p className="text-xs text-text-muted">{preview}</p>
        </div>
    );
}
