import { useEffect, useId, useRef, useState } from 'react';
import { Link } from '@tanstack/react-router';

import { cn } from '@/lib/cn';
import { RATES_LINK_CLASS } from '@/lib/conversion';
import { errorMessage } from '@/lib/errorMessage';
import { useBenchmarks } from '@/lib/marketSeriesQueries';
import type { Benchmark } from '@/lib/types';
import { FieldLabel } from '@/components/ui/FieldLabel';
import {
    dismissOnEscape,
    happenedInside,
    useDismissOnPageEscape,
    useReturnFocusOnClose,
} from '@/components/ui/popoverDismissal';

import { benchmarkLabel } from './returnsModel';

/**
 * The Returns report's Benchmark control (ADR-0114 D1): a popover in the Range
 * picker's shape, listing None and the ledger's benchmarks in their order.
 *
 * The pick is the spec's `benchmark`, so it saves with the report and rides the
 * URL. The list is the ledger's, managed in Settings → Quotes, which an empty list
 * points to. A pick that cannot be drawn is listed disabled with its reason, and
 * stays focusable (aria-disabled) so the reason is heard: a saved pick of a symbol
 * the ledger no longer holds stays the pick, and the list says why it draws
 * nothing. The server says what it knows stands in a benchmark's way (ADR-0114 D1): no
 * history stored yet, or no rate from its currency into the series' at all, so no
 * point of its line could be drawn. A benchmark with only some days missing stays
 * pickable; the chart says which points it has no value for.
 */

/** Why `b` cannot be drawn against a series in `seriesCurrency`, in words; null when it
 *  can, or when the list has not said. */
function benchmarkUnavailableText(b: Benchmark, seriesCurrency: string | undefined): string | null {
    const a = b.availability;
    if (!a) return null;
    if (a.unavailableReason === 'yahoo-off') {
        return 'No history is stored for it yet, and Yahoo Finance is off for you on this ledger, so nothing fetches it. Turn Yahoo on in Settings → Quotes.';
    }
    if (a.unavailableReason === 'no-history') {
        return 'No history is stored for it yet. It is fetched with the next refresh.';
    }
    if (seriesCurrency !== undefined && b.currencyCode !== seriesCurrency && a.noRatesInto.includes(seriesCurrency)) {
        return `Priced in ${b.currencyCode}, and these lines are in ${seriesCurrency}. No ${b.currencyCode} → ${seriesCurrency} rate is stored, so it cannot be converted.`;
    }
    return null;
}

const OPTION = 'flex w-full items-center px-2.5 py-1 text-left text-xs hover:bg-surface-hover';
const PICKED = 'bg-accent-soft text-accent-soft-text';

export function BenchmarkPicker({ ledgerId, value, onChange, canEdit, seriesCurrency }: {
    ledgerId: string;
    /** The picked symbol; undefined for None. */
    value: string | undefined;
    onChange: (symbol: string | undefined) => void;
    /** The currency of the line a benchmark is drawn against; undefined while unknown. */
    seriesCurrency: string | undefined;
    /** Whether the reader can manage the list: the empty list's pointer says who can. */
    canEdit: boolean;
}) {
    const [open, setOpen] = useState(false);
    const rootRef = useRef<HTMLDivElement>(null);
    const toggleRef = useRef<HTMLButtonElement>(null);
    const ids = useId();
    const benchmarks = useBenchmarks(ledgerId);

    useEffect(() => {
        if (!open) return;
        const onDown = (e: MouseEvent) => {
            if (rootRef.current && !happenedInside(e, rootRef.current)) setOpen(false);
        };
        document.addEventListener('mousedown', onDown);
        return () => document.removeEventListener('mousedown', onDown);
    }, [open]);
    useDismissOnPageEscape(open, () => setOpen(false));
    useReturnFocusOnClose(open, toggleRef);

    const list = benchmarks.data ?? [];
    const picked = value === undefined ? undefined : list.find((b) => b.symbol === value);
    const shown = value === undefined ? 'None' : picked ? benchmarkLabel(picked.name, picked.symbol) : value;
    // Known only once the list has answered: a symbol the list does not hold.
    const removed = value !== undefined && benchmarks.isSuccess && picked === undefined;

    const choose = (symbol: string | undefined) => {
        onChange(symbol);
        setOpen(false);
    };

    return (
        <div
            ref={rootRef}
            className="relative flex min-w-0 flex-col gap-1"
            onKeyDown={(e) => { if (open) dismissOnEscape(e, () => setOpen(false)); }}
        >
            <FieldLabel htmlFor={`${ids}-toggle`}>Benchmark</FieldLabel>
            <button
                ref={toggleRef}
                id={`${ids}-toggle`}
                type="button"
                aria-expanded={open}
                aria-haspopup="listbox"
                aria-controls={open ? `${ids}-list` : undefined}
                aria-label={`Benchmark: ${shown}`}
                onClick={() => setOpen((v) => !v)}
                className="flex h-control-28px items-center gap-2 rounded border border-border bg-surface px-2 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-60"
            >
                <span className="truncate">{shown}</span>
                <span aria-hidden className="text-[0.625rem] text-text-subtle">▾</span>
            </button>

            {open ? (
                <div className="absolute top-full left-0 z-50 mt-1 w-fixed-320px overflow-hidden rounded border border-border-strong bg-surface shadow-lg">
                    <p className="px-2.5 pt-2 pb-1 text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                        Compare with
                    </p>
                    <ul id={`${ids}-list`} role="listbox" aria-label="Benchmark" className="pb-1">
                        <li>
                            <button
                                type="button"
                                role="option"
                                aria-selected={value === undefined}
                                onClick={() => choose(undefined)}
                                className={cn(OPTION, value === undefined ? PICKED : 'text-text')}
                            >
                                None
                            </button>
                        </li>
                        {list.map((b) => {
                            const active = b.symbol === value;
                            const why = benchmarkUnavailableText(b, seriesCurrency);
                            if (why !== null) {
                                return (
                                    <li key={b.id}>
                                        <button
                                            type="button"
                                            role="option"
                                            aria-selected={active}
                                            aria-disabled
                                            aria-describedby={`${ids}-why-${b.id}`}
                                            // Disabled: kept focusable (aria-disabled) so the reason is heard.
                                            onClick={() => undefined}
                                            className={cn(
                                                'flex w-full cursor-not-allowed flex-col items-start gap-0.5 px-2.5 py-1 text-left text-xs text-text-subtle',
                                                active && PICKED,
                                            )}
                                        >
                                            <span className="flex w-full items-center justify-between gap-2">
                                                <span className="truncate">{benchmarkLabel(b.name, b.symbol)}</span>
                                                <span className="shrink-0 font-mono text-[0.625rem]">{b.currencyCode}</span>
                                            </span>
                                            <span id={`${ids}-why-${b.id}`} className="text-[0.6875rem] leading-[1.4] text-text-muted">
                                                {why}
                                            </span>
                                        </button>
                                    </li>
                                );
                            }
                            return (
                                <li key={b.id}>
                                    <button
                                        type="button"
                                        role="option"
                                        aria-selected={active}
                                        onClick={() => choose(b.symbol)}
                                        className={cn(OPTION, 'justify-between gap-2', active ? PICKED : 'text-text')}
                                    >
                                        <span className="truncate">{benchmarkLabel(b.name, b.symbol)}</span>
                                        <span className={cn('shrink-0 font-mono text-[0.625rem]', !active && 'text-text-subtle')}>
                                            {b.currencyCode}
                                        </span>
                                    </button>
                                </li>
                            );
                        })}
                        {removed ? (
                            <li>
                                <button
                                    type="button"
                                    role="option"
                                    aria-selected
                                    aria-disabled
                                    aria-describedby={`${ids}-removed`}
                                    // Disabled: nothing to pick, and it is the pick already.
                                    onClick={() => undefined}
                                    className="flex w-full cursor-not-allowed flex-col items-start gap-0.5 px-2.5 py-1 text-left text-xs text-text-subtle"
                                >
                                    <span className="truncate">{value}</span>
                                    <span id={`${ids}-removed`} className="text-[0.6875rem] leading-[1.4] text-text-muted">
                                        No longer one of this ledger’s benchmarks, so it draws nothing.
                                    </span>
                                </button>
                            </li>
                        ) : null}
                    </ul>
                    {benchmarks.isPending ? (
                        <p className="border-t border-border px-2.5 py-2 text-xs text-text-muted">Loading benchmarks…</p>
                    ) : benchmarks.isError ? (
                        <p role="alert" className="border-t border-border px-2.5 py-2 text-xs text-state-danger">
                            {errorMessage(benchmarks.error, 'The benchmarks could not be loaded.')}
                        </p>
                    ) : list.length === 0 ? (
                        <p className="border-t border-border px-2.5 py-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                            This ledger has no benchmarks yet.{' '}
                            {canEdit ? 'Add one in ' : 'An owner or editor adds them in '}
                            <Link
                                to="/ledgers/$ledgerId/settings"
                                params={{ ledgerId }}
                                search={{ tab: 'quotes' }}
                                className={RATES_LINK_CLASS}
                            >
                                Settings → Quotes
                            </Link>
                            .
                        </p>
                    ) : null}
                </div>
            ) : null}
        </div>
    );
}
