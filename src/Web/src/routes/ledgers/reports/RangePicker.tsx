import { useEffect, useRef, useState } from 'react';

import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import type { ReportSpec } from '@/types/report';

/**
 * The date range, as a mode rather than a pair of dates (ADR-0104 D5).
 *
 * THE MODE IS A HEADING, NOT A TOGGLE. Nobody chooses "sliding" in the
 * abstract; they choose "last 12 months", and the stored spec records that it
 * slides because of the group they picked it from. The rejected alternative
 * was an explicit Sliding/Fixed switch plus count, unit and whole-periods
 * inputs — four controls to say one thing, three of them inert in Fixed mode,
 * and a one-to-one mapping onto the spec that makes the reader assemble a
 * range out of storage fields.
 *
 * Whole-period presets set `windowAlign`, so "last 12 months" draws twelve
 * WHOLE columns rather than thirteen with two partial ones.
 */

type Preset = {
    readonly key: string;
    readonly label: string;
    readonly apply: () => Partial<ReportSpec>;
};

const SLIDING: readonly Preset[] = [
    { key: 'd30', label: 'Last 30 days', apply: () => sliding(30, 'Day', false) },
    { key: 'm3', label: 'Last 3 months', apply: () => sliding(3, 'Month', true) },
    { key: 'm12', label: 'Last 12 months', apply: () => sliding(12, 'Month', true) },
    { key: 'ytd', label: 'Year to date', apply: () => sliding(1, 'Year', true) },
    { key: 'y3', label: 'Last 3 years', apply: () => sliding(3, 'Year', true) },
];

function sliding(count: number, unit: 'Day' | 'Month' | 'Year', align: boolean): Partial<ReportSpec> {
    return {
        dateMode: 'Sliding',
        windowCount: count,
        windowUnit: unit,
        windowAlign: align,
        from: undefined,
        to: undefined,
    };
}

/** A calendar year, as an exclusive upper bound — the convention the API uses. */
function calendarYear(year: number): Partial<ReportSpec> {
    return {
        dateMode: 'Static',
        from: `${year}-01-01T00:00:00Z`,
        to: `${year + 1}-01-01T00:00:00Z`,
        windowCount: undefined,
    };
}

/** What the control says when it is closed. */
export function describeRange(spec: ReportSpec): string {
    if (spec.dateMode === 'Sliding') {
        const hit = SLIDING.find((p) => {
            const a = p.apply();
            return a.windowCount === spec.windowCount
                && a.windowUnit === spec.windowUnit
                && (a.windowAlign ?? false) === (spec.windowAlign ?? false);
        });
        if (hit) return hit.label;
        // Not a preset. Say so rather than printing a label a preset also
        // uses: an UNALIGNED 3 months and the aligned "Last 3 months" differ
        // by where the window starts, and a control that renders both the
        // same way is a coin flip the reader cannot see.
        const unit = (spec.windowUnit ?? 'Month').toLowerCase();
        const plural = spec.windowCount === 1 ? '' : 's';
        return `Last ${spec.windowCount ?? '?'} ${unit}${plural} (to the day)`;
    }
    if (!spec.from && !spec.to) return 'All time';
    const from = spec.from?.slice(0, 10) ?? '…';
    const to = spec.to?.slice(0, 10) ?? '…';
    return `${from} → ${to}`;
}

export function RangePicker({
    spec,
    onChange,
    disabled,
}: {
    spec: ReportSpec;
    onChange: (patch: Partial<ReportSpec>) => void;
    disabled?: boolean;
}) {
    const [open, setOpen] = useState(false);
    const [custom, setCustom] = useState(false);
    const rootRef = useRef<HTMLDivElement>(null);

    useEffect(() => {
        if (!open) return;
        const onDown = (e: MouseEvent) => {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
        };
        document.addEventListener('mousedown', onDown);
        return () => document.removeEventListener('mousedown', onDown);
    }, [open]);

    const choose = (patch: Partial<ReportSpec>) => {
        onChange(patch);
        setOpen(false);
        setCustom(false);
    };

    const thisYear = new Date().getUTCFullYear();

    return (
        <div ref={rootRef} className="relative flex min-w-0 flex-col gap-1">
            <FieldLabel htmlFor="report-range">Range</FieldLabel>
            <button
                id="report-range"
                type="button"
                disabled={disabled}
                aria-expanded={open}
                // The value, not just the field name. A `FieldLabel htmlFor`
                // association otherwise becomes the whole accessible name, so
                // this announced "Range" and never what the range WAS.
                aria-label={`Range: ${describeRange(spec)}`}
                onClick={() => setOpen((v) => !v)}
                className="flex h-control-28px items-center gap-2 rounded border border-border bg-surface px-2 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-60"
            >
                <span className="truncate">{describeRange(spec)}</span>
                <span aria-hidden className="text-[0.625rem] text-text-subtle">▾</span>
            </button>

            {open ? (
                <div className="absolute top-full left-0 z-50 mt-1 w-fixed-256px overflow-hidden rounded border border-border-strong bg-surface shadow-lg">
                    <p className="px-2.5 pt-2 pb-1 text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                        Sliding — moves with today
                    </p>
                    <ul className="pb-1">
                        {SLIDING.map((preset) => {
                            const active = describeRange(spec) === preset.label;
                            return (
                                <li key={preset.key}>
                                    <button
                                        type="button"
                                        onClick={() => choose(preset.apply())}
                                        className={cn(
                                            'flex w-full items-center px-2.5 py-1 text-left text-xs hover:bg-surface-hover',
                                            active ? 'bg-accent-soft text-accent-soft-text' : 'text-text',
                                        )}
                                    >
                                        {preset.label}
                                    </button>
                                </li>
                            );
                        })}
                    </ul>

                    <div className="border-t border-border">
                        <p className="px-2.5 pt-2 pb-1 text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                            Fixed
                        </p>
                        <ul className="pb-1">
                            {[thisYear - 1, thisYear - 2, thisYear - 3].map((year) => (
                                <li key={year}>
                                    <button
                                        type="button"
                                        onClick={() => choose(calendarYear(year))}
                                        className="flex w-full px-2.5 py-1 text-left text-xs text-text hover:bg-surface-hover"
                                    >
                                        {year}
                                    </button>
                                </li>
                            ))}
                        </ul>

                        {custom ? (
                            // Inline rather than a second dialog: two dates do
                            // not earn a modal, and keeping them here means the
                            // mode they belong to is still on screen.
                            <div className="flex items-end gap-1.5 border-t border-border px-2.5 py-2">
                                <label className="flex min-w-0 flex-col gap-1">
                                    <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">From</span>
                                    <input
                                        type="date"
                                        aria-label="Range from"
                                        defaultValue={spec.from?.slice(0, 10) ?? ''}
                                        onChange={(e) => onChange({
                                            dateMode: 'Static',
                                            from: e.target.value ? `${e.target.value}T00:00:00Z` : undefined,
                                        })}
                                        className="h-control-28px w-full rounded border border-border bg-surface px-1 text-xs"
                                    />
                                </label>
                                <label className="flex min-w-0 flex-col gap-1">
                                    <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">To</span>
                                    <input
                                        type="date"
                                        aria-label="Range to"
                                        defaultValue={spec.to?.slice(0, 10) ?? ''}
                                        onChange={(e) => onChange({
                                            dateMode: 'Static',
                                            to: e.target.value ? `${e.target.value}T00:00:00Z` : undefined,
                                        })}
                                        className="h-control-28px w-full rounded border border-border bg-surface px-1 text-xs"
                                    />
                                </label>
                            </div>
                        ) : (
                            <button
                                type="button"
                                onClick={() => setCustom(true)}
                                className="flex w-full border-t border-border px-2.5 py-1.5 text-left text-xs text-accent hover:bg-surface-hover"
                            >
                                Custom dates…
                            </button>
                        )}
                    </div>
                </div>
            ) : null}
        </div>
    );
}
