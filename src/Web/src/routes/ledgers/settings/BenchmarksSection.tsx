import { useId, useMemo, useRef, useState, type ReactNode, type Ref } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { ApiError, addBenchmark, removeBenchmark } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { useBenchmarks } from '@/lib/marketSeriesQueries';
import { useMutation } from '@/lib/mutation';
import type { Benchmark } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { PendingScope } from '@/components/ui/pending';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';

/** How many benchmarks a ledger keeps (ADR-0114 D1); the server refuses a fourth. */
export const MAX_BENCHMARKS = 3;

/** The refusals that are about the symbol typed, which marks the field invalid. The
 *  others (Yahoo off, the cap, Yahoo unreachable) are about the moment, not the symbol. */
const SYMBOL_REFUSALS: ReadonlySet<string> = new Set([
    'benchmark-symbol-invalid',
    'benchmark-already-added',
    'benchmark-is-index',
    'benchmark-instrument-unsupported',
    'benchmark-no-adjusted-close',
    'currency-missing',
    'currency-unknown',
    'ticker-not-resolved',
]);

/** A benchmark as a confirm names it: the fund and its symbol, or the symbol alone when
 *  Yahoo gave no name. */
function benchmarkLabel(b: Benchmark): string {
    return b.name === b.symbol ? b.symbol : `${b.name} (${b.symbol})`;
}

/**
 * A refused add in plain words, naming the symbol (ADR-0114 D1, D2). Each code the
 * server answers has its own sentence; one it does not know falls back to its detail.
 */
function refusalText(error: unknown, symbol: string): string {
    const code = error instanceof ApiError ? error.code : undefined;
    switch (code) {
        case 'benchmark-symbol-invalid':
            return 'Enter a Yahoo Finance symbol for a fund or ETF, such as VT.';
        case 'benchmark-already-added':
            return `${symbol} is already one of this ledger's benchmarks.`;
        case 'benchmark-limit-reached':
            return `A ledger keeps up to three benchmarks. Remove one to add another.`;
        case 'benchmark-is-index':
            return `${symbol} is an index, not a fund. An index has no dividend-adjusted close, so it cannot show total return. Add a fund or ETF that tracks it.`;
        case 'benchmark-instrument-unsupported':
            return `Yahoo Finance does not list ${symbol} as a fund or ETF. A benchmark is a fund or ETF, so its total return can be measured.`;
        case 'benchmark-no-adjusted-close':
            return `Yahoo Finance returned no dividend-adjusted close for ${symbol}, so its total return cannot be measured. A benchmark needs a fund or ETF that has one.`;
        case 'currency-missing':
            return `Yahoo Finance names no currency for ${symbol}, so its line could not be converted. It cannot be a benchmark.`;
        case 'currency-unknown':
            return `Yahoo Finance quotes ${symbol} in a currency Coffer does not recognise, so it cannot be a benchmark.`;
        case 'ticker-not-resolved':
            return `Yahoo Finance found nothing for ${symbol}. Check the symbol as Yahoo Finance writes it, such as VT or a listing with its exchange suffix.`;
        case 'rate-limited':
            return `Yahoo Finance is limiting requests right now, so ${symbol} could not be checked. Try again in a few minutes.`;
        case 'fetch-failed':
            return `Coffer could not reach Yahoo Finance to check ${symbol}. Nothing was added. Try again later.`;
        case 'parse-failed':
            return `Yahoo Finance answered with something Coffer could not read, so ${symbol} was not checked. Nothing was added. Try again later.`;
        default:
            return errorMessage(error, 'Could not add the benchmark.');
    }
}

/**
 * The ledger's benchmarks, on Settings → Quotes under the Yahoo Finance switch
 * (ADR-0114 D1, D2): up to three funds or ETFs a Returns report can draw beside the
 * ledger's return. Owners and editors add and remove them; a viewer sees the list.
 *
 * Adding one asks Yahoo Finance (egress), under the reader's own Yahoo opt-in, so with
 * Yahoo off the field and Add are disabled before anything is typed, with the reason
 * and a way to the switch where the hint goes (`yahooOn`, `onFocusYahoo`). The server
 * refuses too, in the same words. Removing one asks first, because a saved Returns
 * report may carry it.
 */
export function BenchmarksSection({ ledgerId, canEdit, yahooOn, onFocusYahoo }: {
    ledgerId: string;
    canEdit: boolean;
    /** The reader's Yahoo opt-in; undefined while it is read. */
    yahooOn: boolean | undefined;
    /** Moves focus to the Yahoo Finance switch above. */
    onFocusYahoo: () => void;
}) {
    const queryClient = useQueryClient();
    const symbolId = useId();
    const hintId = useId();
    const errorId = useId();
    const [symbol, setSymbol] = useState('');
    const [removing, setRemoving] = useState<Benchmark | null>(null);
    // An add that lands clears the symbol, which disables Add with focus on it; focus
    // goes to the field it waits on (ADR-0023 §L).
    const symbolRef = useRef<HTMLInputElement>(null);

    const benchmarksQuery = useBenchmarks(ledgerId);
    const benchmarks = benchmarksQuery.data ?? [];

    const addMutation = useMutation({
        mutationFn: (s: string) => addBenchmark(ledgerId, s),
        onSuccess: () => {
            setSymbol('');
            void queryClient.invalidateQueries({ queryKey: ['benchmarks', ledgerId] });
        },
    });
    const removeMutation = useMutation({
        mutationFn: (id: string) => removeBenchmark(ledgerId, id),
        onSuccess: async () => {
            setRemoving(null);
            await queryClient.invalidateQueries({ queryKey: ['benchmarks', ledgerId] });
        },
    });

    // A removed benchmark takes its Remove with it: focus goes to the next row's
    // Remove, the previous row's, else the add control (ADR-0023 §L). The symbol field
    // is that control while it takes input; with Yahoo off it is disabled, and the way
    // to the switch the add waits on stands in for it; while the opt-in is still read
    // there is neither, and the section's own region takes focus, never the page.
    const yahooLinkRef = useRef<HTMLButtonElement>(null);
    const regionRef = useRef<HTMLDivElement>(null);
    const headingId = useId();
    const home = useMemo(() => ({
        get current(): HTMLElement | null {
            const field = symbolRef.current;
            if (field !== null && !field.disabled) return field;
            return yahooLinkRef.current ?? regionRef.current;
        },
    }), []);
    const removeControl = useRowRemovalFocus(benchmarks.map((b) => b.id), home);

    const typed = symbol.trim().toUpperCase();
    const atCap = benchmarks.length >= MAX_BENCHMARKS;
    // Unavailable, not busy: three already, Yahoo off, or the opt-in not yet read.
    const unavailable = atCap || yahooOn !== true;
    const busy = addMutation.isPending || removeMutation.isPending;
    const addRefusal = addMutation.error ?? null;
    const addCode = addRefusal instanceof ApiError ? addRefusal.code : undefined;
    const symbolInvalid = addCode !== undefined && SYMBOL_REFUSALS.has(addCode);
    const sentSymbol = addMutation.variables ?? typed;

    const yahooOff = (linkRef?: Ref<HTMLButtonElement>) => (
        <>
            Adding a benchmark checks the symbol with Yahoo Finance, which is off for this ledger.{' '}
            <button
                ref={linkRef}
                type="button"
                className="font-medium text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                onClick={onFocusYahoo}
            >
                Turn on Yahoo Finance above
            </button>
        </>
    );

    let hint: ReactNode;
    if (atCap) {
        hint = <p id={hintId} className="text-xs text-text-subtle">3 of 3 used. Remove one to add another.</p>;
    } else if (yahooOn === false) {
        hint = <p id={hintId} className="text-sm text-text-muted">{yahooOff(yahooLinkRef)}</p>;
    } else {
        hint = (
            <p id={hintId} className="text-xs text-text-subtle">
                Funds and ETFs only · {benchmarks.length} of {MAX_BENCHMARKS} used.
            </p>
        );
    }

    return (
        <div className="space-y-3">
            <header className="space-y-1">
                <h3 id={headingId} className="text-sm font-semibold">Benchmarks</h3>
                <p className="text-sm text-text-muted">
                    Funds or ETFs a Returns report can draw beside this ledger&apos;s return,
                    up to three. Each one&apos;s dividend-adjusted close comes from Yahoo
                    Finance, so the comparison is total return: price plus reinvested
                    dividends.
                </p>
            </header>
            <Panel
                ref={regionRef}
                role="group"
                aria-labelledby={headingId}
                tabIndex={-1}
                className="focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
                {/* Add and every Remove exclude each other: a remove that landed while
                    an add was checked would leave the count the add was judged by out of
                    date. One group, so a press of either holds the rest (pending.ts). */}
                <PendingScope>
                <PanelBody className="space-y-3">
                    {benchmarksQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : benchmarksQuery.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(benchmarksQuery.error, 'Could not load the benchmarks.')}
                        </p>
                    ) : benchmarks.length === 0 ? (
                        <p className="text-sm text-text-muted">
                            No benchmarks yet.{canEdit ? ' Add a fund or ETF below to compare a Returns report with it.' : ''}
                        </p>
                    ) : (
                        <ul aria-label="Benchmarks" className="divide-y divide-border rounded border border-border">
                            {benchmarks.map((b) => (
                                <li key={b.id} className="flex items-center gap-3 px-3 py-2 text-sm">
                                    <span className="flex-1 truncate font-medium text-text">
                                        {b.name}{' '}
                                        {b.name !== b.symbol ? (
                                            <span className="font-normal text-text-muted">{b.symbol}</span>
                                        ) : null}
                                    </span>
                                    <span className="text-[0.6875rem] text-text-muted">{b.currencyCode}</span>
                                    {canEdit ? (
                                        <Button
                                            ref={removeControl(b.id)}
                                            type="button"
                                            variant="secondary"
                                            size="sm"
                                            pending={busy}
                                            aria-label={`Remove ${b.symbol}`}
                                            onClick={() => setRemoving(b)}
                                        >
                                            Remove
                                        </Button>
                                    ) : null}
                                </li>
                            ))}
                        </ul>
                    )}

                    {removeMutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(removeMutation.error, 'Could not remove the benchmark.')}
                        </p>
                    ) : null}

                    {canEdit && benchmarksQuery.isSuccess ? (
                        <div className="space-y-2 border-t border-border pt-3">
                            <FieldLabel htmlFor={symbolId}>Add a benchmark</FieldLabel>
                            <form
                                className="flex max-w-md gap-2"
                                onSubmit={(e) => {
                                    e.preventDefault();
                                    if (typed.length > 0 && !unavailable) addMutation.mutate(typed);
                                }}
                            >
                                <Input
                                    ref={symbolRef}
                                    id={symbolId}
                                    type="text"
                                    placeholder="Fund or ETF symbol, e.g. VT"
                                    autoComplete="off"
                                    spellCheck={false}
                                    value={symbol}
                                    disabled={unavailable}
                                    pending={busy}
                                    aria-invalid={symbolInvalid ? true : undefined}
                                    aria-describedby={addRefusal ? `${errorId} ${hintId}` : hintId}
                                    onChange={(e) => setSymbol(e.target.value)}
                                    className="flex-1"
                                />
                                <Button
                                    type="submit"
                                    disabled={unavailable || typed.length === 0}
                                    pending={busy}
                                    title={atCap ? 'A ledger keeps up to three benchmarks.' : undefined}
                                    fallbackFocusRef={symbolRef}
                                    pendingLabel="Checking…"
                                >
                                    Add
                                </Button>
                            </form>
                            {addRefusal ? (
                                <p id={errorId} role="alert" className="text-sm text-state-danger">
                                    {addCode === 'benchmark-yahoo-off' ? yahooOff() : refusalText(addRefusal, sentSymbol)}
                                </p>
                            ) : null}
                            {hint}
                        </div>
                    ) : null}
                </PanelBody>
                </PendingScope>
            </Panel>

            <ConfirmDialog
                open={removing !== null}
                variant="danger"
                title={removing ? `Remove “${benchmarkLabel(removing)}”?` : ''}
                body={
                    removing ? (
                        <>
                            Saved Returns reports that compare with{' '}
                            <span className="font-medium text-text">{removing.symbol}</span> will
                            show no benchmark until {removing.symbol} is added again.
                        </>
                    ) : null
                }
                confirmLabel="Remove"
                isConfirming={removeMutation.isPending}
                onConfirm={() => {
                    if (removing) removeMutation.mutate(removing.id);
                }}
                onCancel={() => setRemoving(null)}
            />
        </div>
    );
}
