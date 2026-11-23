import { type MouseEvent, useId, useMemo, useState } from 'react';
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { ApiError, addFxRate, fetchFxRatePage, fetchQuotesPrefs } from '@/lib/api';
import { NO_RATE_LABEL } from '@/lib/conversion';
import { formatLedgerDateCompact, formatLedgerDateLong, shiftDateInputValue } from '@/lib/dates';
import { errorMessage } from '@/lib/errorMessage';
import { useFxRatesView } from '@/lib/marketSeriesQueries';
import { formatFxRate } from '@/lib/money';
import { useMutation } from '@/lib/mutation';
import { invalidateConvertedReads } from '@/lib/registerInvalidation';
import type { FxMissingSpan, FxPair, FxPairSource, FxRate } from '@/lib/types';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { LedgerHubSection } from '@/components/LedgerHubSection';
import { ActionFooter } from '@/components/ui/ActionFooter';
import { Chip } from '@/components/ui/Chip';
import { Modal } from '@/components/ui/Modal';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { pendingTextProps, usePendingButtonProps } from '@/components/ui/pending';

/** How many of a pair's stored rates one page lists. */
const PAGE_SIZE = 50;

const CAPTION = 'text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted';
// A pending field (aria-disabled, pending.ts) looks as a disabled one does.
const FIELD = 'w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent aria-disabled:cursor-not-allowed aria-disabled:opacity-50';

/** The server's day (UTC), as `yyyy-MM-dd`: no rate can be stored for a later day
 *  (ADR-0114 D9), so the date field stops there. */
function utcToday(): string {
    return new Date().toISOString().slice(0, 10);
}

const pairKey = (p: { fromCurrency: string; toCurrency: string }) => `${p.fromCurrency}-${p.toCurrency}`;
const pairName = (p: { fromCurrency: string; toCurrency: string }) => `${p.fromCurrency} → ${p.toCurrency}`;

/** A security's or a benchmark's name with its symbol, once when they are the same. */
const withSymbol = (name: string, symbol: string | null) =>
    symbol && symbol !== name ? `${name} (${symbol})` : name;

const LINK = 'text-accent hover:underline';

/** Frankfurter's key: the exchange-rate provider (ADR-0114 D4, D8). */
const FRANKFURTER = 'frankfurter';

/**
 * One thing that needs a pair, named and linked (RATES-1b): an account with its
 * currency; a security with the currency it is priced in and the account holding it;
 * a benchmark with the returns it is drawn against. The row opens the pair's rates on
 * a click, so a link here keeps its click to itself.
 */
function NeededBy({ ledgerId, pair, source }: { ledgerId: string; pair: FxPair; source: FxPairSource }) {
    const keep = (e: MouseEvent) => e.stopPropagation();
    switch (source.neededFor) {
        case 'position':
            return (
                <span className="block">
                    <Link
                        to="/ledgers/$ledgerId/securities/$securityId"
                        params={{ ledgerId, securityId: source.id }}
                        onClick={keep}
                        className={LINK}
                    >
                        {withSymbol(source.name, source.symbol)}
                    </Link>
                    <span className="block text-[0.6875rem] text-text-subtle">
                        priced in {pair.fromCurrency}
                        {source.accountName !== null ? `, held in ${source.accountName} (${pair.toCurrency})` : ''}
                    </span>
                </span>
            );
        case 'benchmark':
            return (
                <span className="block">
                    <Link
                        to="/ledgers/$ledgerId/settings"
                        params={{ ledgerId }}
                        search={{ tab: 'quotes' }}
                        onClick={keep}
                        className={LINK}
                    >
                        {withSymbol(source.name, source.symbol)}
                    </Link>
                    <span className="block text-[0.6875rem] text-text-subtle">
                        benchmark, drawn against {pair.toCurrency} returns
                    </span>
                </span>
            );
        default:
            return (
                <span className="block">
                    <Link
                        to="/ledgers/$ledgerId/accounts/$accountId"
                        params={{ ledgerId, accountId: source.id }}
                        onClick={keep}
                        className={LINK}
                    >
                        {source.name} ({pair.fromCurrency})
                    </Link>
                </span>
            );
    }
}

/** How many days a span holds, both ends included. */
function spanDays(span: FxMissingSpan): number {
    return Math.round((Date.parse(span.throughDate) - Date.parse(span.fromDate)) / 86_400_000) + 1;
}

/** A span in words: `Nov 6, 2026`, `Oct 31 to Nov 10, 2026`, or both years when it
 *  crosses one. */
function spanText(span: FxMissingSpan): string {
    if (span.fromDate === span.throughDate) return formatLedgerDateLong(span.fromDate);
    const from = span.fromDate.slice(0, 4) === span.throughDate.slice(0, 4)
        ? formatLedgerDateCompact(span.fromDate)
        : formatLedgerDateLong(span.fromDate);
    return `${from} to ${formatLedgerDateLong(span.throughDate)}`;
}

/** The Missing days cell (ADR-0114 D9): none; every needed day, when no rate is stored;
 *  or how many days and which. */
function MissingDays({ pair }: { pair: FxPair }) {
    const spans = pair.missingDays;
    if (spans.length === 0) return <span className="text-text-muted">None</span>;
    if (pair.firstRateDate === null) {
        return (
            <>
                <span className="font-medium">{pair.neededFrom === null ? 'Today' : 'Every day'}</span>
                <span className="block text-[0.6875rem]">
                    {pair.neededFrom === null
                        ? 'only the latest rate is needed'
                        : `from ${formatLedgerDateLong(spans[0]!.fromDate)}`}
                </span>
            </>
        );
    }
    const days = spans.reduce((n, s) => n + spanDays(s), 0);
    return (
        <>
            <span className="font-medium">{days === 1 ? '1 day' : `${days} days`}</span>
            {spans.map((s) => (
                <span key={s.fromDate} className="block text-[0.6875rem]">{spanText(s)}</span>
            ))}
        </>
    );
}

/** A stored rate's source, as the rates table names it. */
function SourceCell({ rate }: { rate: FxRate }) {
    return rate.source === 'manual'
        ? <Chip>Manual</Chip>
        : <span title={rate.upstream ? `Frankfurter names ${rate.upstream} as its source.` : undefined}>Frankfurter</span>;
}

/**
 * Settings → Exchange rates (ADR-0114 D9, D10): the currency pairs the ledger needs to
 * combine its accounts into its reporting currency, how far each is covered, and each
 * pair's stored rates, newest first. A day with no stored rate can take one typed by
 * an owner or an editor; a viewer sees the same view without the add control. A stored
 * rate is never changed, so no row has an edit control.
 */
export function ExchangeRatesPanel({ ledgerId }: { ledgerId: string }) {
    const { canEdit } = useLedgerRole(ledgerId);
    const viewQuery = useFxRatesView(ledgerId);
    // The reader's own Frankfurter switch (ADR-0114 D8), which a fetch of rates needs;
    // the Quotes tab's read, so a change there shows here.
    const prefsQuery = useQuery({
        queryKey: ['quotes-prefs', ledgerId],
        queryFn: () => fetchQuotesPrefs(ledgerId),
    });
    const fetching = prefsQuery.data ? prefsQuery.data.enabledProviders.includes(FRANKFURTER) : undefined;
    // The pairs whose stored rates are open below the table, by key.
    const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
    const [adding, setAdding] = useState<FxPair | null>(null);

    const toggle = (p: FxPair) =>
        setOpen((was) => {
            const next = new Set(was);
            if (next.has(pairKey(p))) next.delete(pairKey(p));
            else next.add(pairKey(p));
            return next;
        });

    const view = viewQuery.data;

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Exchange rates</h2>
                <p className="text-sm text-text-muted">
                    The currency pairs this ledger needs to combine its accounts into its
                    reporting currency
                    {view ? <>, <span className="font-mono font-medium text-text">{view.reportingCurrency}</span></> : null}
                    , and how far each is covered. Rates are fetched from Frankfurter while it
                    is on in{' '}
                    <Link
                        to="/ledgers/$ledgerId/settings"
                        params={{ ledgerId }}
                        search={{ tab: 'quotes' }}
                        className="font-medium text-accent hover:underline"
                    >
                        Quotes
                    </Link>
                    ; a day with no stored rate can take one entered by hand. Every
                    account&apos;s own figures stay in its own currency.
                </p>
            </header>

            {viewQuery.isPending ? (
                <p className="text-sm text-text-subtle">Loading…</p>
            ) : viewQuery.isError ? (
                <p role="alert" className="text-sm text-state-danger">
                    {errorMessage(viewQuery.error, 'Could not load the exchange rates.')}
                </p>
            ) : view!.pairs.length === 0 ? (
                <Panel>
                    <PanelBody>
                        <p className="text-sm text-text-muted">
                            Every account and price is in {view!.reportingCurrency}, so nothing
                            converts and this ledger needs no exchange rate.
                        </p>
                    </PanelBody>
                </Panel>
            ) : (
                <>
                    <MissingSummary pairs={view!.pairs} canEdit={canEdit} fetching={fetching} />
                    <Panel>
                        <table className="w-full text-sm">
                            <thead className="border-b border-border text-[0.6875rem] uppercase tracking-wider text-text-muted">
                                <tr>
                                    <th className="px-4 py-2 text-left font-semibold">Pair</th>
                                    <th className="px-4 py-2 text-left font-semibold">Needed for</th>
                                    <th className="px-4 py-2 text-left font-semibold">Source</th>
                                    <th className="px-4 py-2 text-left font-semibold">Covered through</th>
                                    <th className="px-4 py-2 text-right font-semibold">Latest rate</th>
                                    <th className="px-4 py-2 text-left font-semibold">Missing days</th>
                                    <th className="px-4 py-2 text-right font-semibold" aria-hidden></th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-border/60">
                                {view!.pairs.map((p) => (
                                    <PairRow
                                        key={pairKey(p)}
                                        ledgerId={ledgerId}
                                        pair={p}
                                        expanded={open.has(pairKey(p))}
                                        onToggle={() => toggle(p)}
                                    />
                                ))}
                            </tbody>
                        </table>
                        <div className="border-t border-border/60 px-4 py-2">
                            <p className="text-xs text-text-subtle">
                                A day with no rate of its own uses the pair&apos;s last rate
                                before it. A day before a pair&apos;s first rate, or after the
                                date it is covered through, has none. A fetched pair is covered
                                through the last day it was fetched for; a manual one through
                                its latest entry.
                            </p>
                        </div>
                    </Panel>

                    {view!.pairs.filter((p) => open.has(pairKey(p))).map((p) => (
                        <PairRates
                            key={pairKey(p)}
                            ledgerId={ledgerId}
                            pair={p}
                            onAdd={canEdit ? () => setAdding(p) : undefined}
                        />
                    ))}
                </>
            )}

            {adding !== null && canEdit ? (
                <AddRateDialog ledgerId={ledgerId} pair={adding} onClose={() => setAdding(null)} />
            ) : null}
        </div>
    );
}

/** The line over the pairs when any has days with no rate (RATES-1b): how many, what
 *  those days read (the mark every screen prints, ADR-0114 D9), and what can fill them:
 *  a rate typed by an owner or an editor (ADR-0114 D10), and a fetch only while the
 *  reader's Frankfurter switch is on (ADR-0114 D8). Nothing when every pair is covered. */
function MissingSummary({ pairs, canEdit, fetching }: {
    pairs: readonly FxPair[];
    canEdit: boolean;
    /** Whether Frankfurter is on for the reader; undefined until that is read. */
    fetching: boolean | undefined;
}) {
    const short = pairs.filter((p) => p.missingDays.length > 0).length;
    if (short === 0) return null;
    const fill = canEdit
        ? (fetching ? 'Add a rate for a missing day, or wait for the next fetch.' : 'Add a rate for a missing day.')
        : (fetching
            ? 'Wait for the next fetch, or ask an owner or editor to add a rate for a missing day.'
            : 'An owner or editor can add a rate for a missing day.');
    return (
        <div role="status" className="rounded border border-state-warning/40 bg-state-warning-soft p-3 text-sm text-state-warning">
            <p className="font-medium">
                {pairs.length === 1
                    ? 'The pair has days with no rate.'
                    : `${short} of ${pairs.length} pairs ${short === 1 ? 'has' : 'have'} days with no rate.`}
            </p>
            <p className="mt-1 text-xs">
                On those days a total that combines the pair&apos;s currencies reads &quot;{NO_RATE_LABEL}&quot;
                instead of a figure. {fill}
                {fetching === false ? ' Frankfurter is off for you on this ledger, so nothing fetches them.' : null}
            </p>
        </div>
    );
}

/** One needed pair in the table: what needs it, where its rates come from, how far it
 *  is covered, its newest stored rate, and the days it has none for. */
function PairRow({ ledgerId, pair, expanded, onToggle }: {
    ledgerId: string;
    pair: FxPair;
    expanded: boolean;
    onToggle: () => void;
}) {
    const fetched = pair.fetchedThroughDate !== null;
    const manual = pair.latestManualDate !== null;
    // The newest stored rate, one row of the pair's first page.
    const latestQuery = useQuery({
        queryKey: ['fx-rates', ledgerId, pair.fromCurrency, pair.toCurrency, 'latest'],
        queryFn: () => fetchFxRatePage(ledgerId, pair.fromCurrency, pair.toCurrency, { limit: 1 }),
    });
    const latest = latestQuery.data?.rates[0] ?? null;

    return (
        <tr className="cursor-pointer hover:bg-surface-hover" onClick={onToggle}>
            <td className="whitespace-nowrap px-4 py-2 font-mono font-medium">{pairName(pair)}</td>
            <td className="px-4 py-2">
                {pair.neededBy.map((source) => (
                    <NeededBy
                        key={`${source.neededFor}-${source.id}-${source.accountId ?? ''}`}
                        ledgerId={ledgerId}
                        pair={pair}
                        source={source}
                    />
                ))}
            </td>
            <td className="px-4 py-2 text-left text-[0.6875rem] text-text-muted">
                {fetched ? (
                    <>
                        Frankfurter{' '}
                        {manual ? <Chip title="Some days were entered by hand.">Manual</Chip> : null}
                    </>
                ) : manual ? (
                    <Chip>Manual</Chip>
                ) : (
                    'Not fetched yet'
                )}
            </td>
            <td className="whitespace-nowrap px-4 py-2">
                {pair.coveredThroughDate !== null ? (
                    <>
                        <span className="font-mono text-[0.75rem] tabular-nums">
                            {formatLedgerDateLong(pair.coveredThroughDate)}
                        </span>
                        <span className="block text-[0.6875rem] text-text-subtle">
                            {pair.coveredThroughDate === pair.fetchedThroughDate ? 'fetched through' : 'latest entry'}
                        </span>
                    </>
                ) : (
                    <span className="text-text-subtle">—</span>
                )}
            </td>
            <td className="whitespace-nowrap px-4 py-2 text-right">
                {latestQuery.isPending ? (
                    <span className="text-text-subtle">Loading…</span>
                ) : latestQuery.isError ? (
                    <span className="text-state-danger">Could not load</span>
                ) : latest === null ? (
                    <span className="text-text-subtle">No rate stored</span>
                ) : (
                    <>
                        <span className="font-mono tabular-nums">
                            {formatFxRate(latest.rate, pair.fromCurrency, pair.toCurrency)}
                        </span>
                        <span className="block text-[0.6875rem] text-text-subtle">
                            {formatLedgerDateLong(latest.date)}
                        </span>
                    </>
                )}
            </td>
            <td className={`px-4 py-2 ${pair.missingDays.length > 0 ? 'text-state-warning' : ''}`}>
                <MissingDays pair={pair} />
            </td>
            <td className="px-4 py-2 text-right text-[0.6875rem] text-text-subtle">
                {/* A real button, so the keyboard opens the rates as the pointer does. */}
                <button
                    type="button"
                    aria-expanded={expanded}
                    aria-label={`${expanded ? 'Hide' : 'Show'} ${pair.fromCurrency} to ${pair.toCurrency} rates`}
                    onClick={(e) => {
                        e.stopPropagation();
                        onToggle();
                    }}
                    className="rounded hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                    rates →
                </button>
            </td>
        </tr>
    );
}

/** How far a pair is covered, in a sentence, from the fields the rates view gives. */
function coverageSentence(pair: FxPair): string {
    const parts: string[] = [];
    if (pair.fetchedFromDate !== null && pair.fetchedThroughDate !== null) {
        parts.push(`Fetched from Frankfurter for ${formatLedgerDateLong(pair.fetchedFromDate)} through ${formatLedgerDateLong(pair.fetchedThroughDate)}.`);
    } else {
        parts.push('Not fetched from Frankfurter yet.');
    }
    if (pair.latestManualDate !== null) {
        parts.push(`Latest rate entered by hand: ${formatLedgerDateLong(pair.latestManualDate)}.`);
    }
    parts.push(pair.neededFrom !== null
        ? `Needed from ${formatLedgerDateLong(pair.neededFrom)}.`
        : 'Only the latest rate is needed.');
    return parts.join(' ');
}

/** One pair's stored rates, newest first, a page at a time (RATES-2). */
function PairRates({ ledgerId, pair, onAdd }: {
    ledgerId: string;
    pair: FxPair;
    /** Absent for a viewer: no rate can be added. */
    onAdd?: () => void;
}) {
    const pendingProps = usePendingButtonProps();
    const ratesQuery = useInfiniteQuery({
        queryKey: ['fx-rates', ledgerId, pair.fromCurrency, pair.toCurrency, 'pages'],
        queryFn: ({ pageParam }) =>
            fetchFxRatePage(ledgerId, pair.fromCurrency, pair.toCurrency, {
                before: pageParam ?? undefined,
                limit: PAGE_SIZE,
            }),
        initialPageParam: null as string | null,
        getNextPageParam: (last) => last.nextBefore,
    });
    const rates = useMemo(() => ratesQuery.data?.pages.flatMap((p) => p.rates) ?? [], [ratesQuery.data]);

    return (
        <LedgerHubSection
            sectionKey={`fx-${pairKey(pair)}`}
            ledgerId={ledgerId}
            title={pairName(pair)}
            count={ratesQuery.data ? rates.length : undefined}
            totalCount={pair.rateCount}
            headerAction={onAdd ? (
                <button
                    type="button"
                    onClick={onAdd}
                    aria-label={`Add a ${pair.fromCurrency} to ${pair.toCurrency} rate`}
                    className="font-medium text-accent hover:underline"
                >
                    + Add rate
                </button>
            ) : undefined}
        >
            <div className="border-b border-border/60 px-4 py-2">
                <p className="text-xs text-text-muted">{coverageSentence(pair)}</p>
            </div>
            {ratesQuery.isPending ? (
                <PanelBody>
                    <p className="text-sm text-text-subtle">Loading…</p>
                </PanelBody>
            ) : ratesQuery.isError ? (
                <PanelBody>
                    <p role="alert" className="text-sm text-state-danger">
                        {errorMessage(ratesQuery.error, 'Could not load the rates.')}
                    </p>
                </PanelBody>
            ) : rates.length === 0 ? (
                <PanelBody>
                    <p className="text-sm text-text-muted">No rate is stored for this pair yet.</p>
                </PanelBody>
            ) : (
                <>
                    <table className="w-full text-sm">
                        <thead className="border-b border-border text-[0.6875rem] uppercase tracking-wider text-text-muted">
                            <tr>
                                <th className="px-4 py-2 text-left font-semibold">As of</th>
                                <th className="px-4 py-2 text-right font-semibold">Rate</th>
                                <th className="px-4 py-2 text-left font-semibold">Source</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-border/60">
                            {rates.map((r) => (
                                <tr key={r.date}>
                                    <td className="px-4 py-2 font-mono text-[0.75rem] tabular-nums">
                                        {formatLedgerDateLong(r.date)}
                                    </td>
                                    <td className="px-4 py-2 text-right font-mono tabular-nums">
                                        {formatFxRate(r.rate, pair.fromCurrency, pair.toCurrency)}
                                    </td>
                                    <td className="px-4 py-2 text-left text-[0.6875rem] text-text-muted">
                                        <SourceCell rate={r} />
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                    {ratesQuery.hasNextPage ? (
                        <div className="border-t border-border/60 px-4 py-2 text-center">
                            <button
                                type="button"
                                {...pendingProps(ratesQuery.isFetchingNextPage, () => void ratesQuery.fetchNextPage())}
                                className="text-xs font-medium text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                            >
                                <PendingLabel pending={ratesQuery.isFetchingNextPage} label="Load more" pendingLabel="Loading…" />
                            </button>
                        </div>
                    ) : null}
                </>
            )}
            <div className="border-t border-border/60 px-4 py-2">
                <p className="text-xs text-text-subtle">
                    A stored rate is never changed, whatever its source. A fetch skips a day
                    that already has a rate.
                </p>
            </div>
        </LedgerHubSection>
    );
}

/** A refused rate in plain words (ADR-0114 D9, D10). */
function addRateRefusal(error: unknown, pair: FxPair, date: string): string {
    const code = error instanceof ApiError ? error.code : undefined;
    const day = date.length > 0 ? formatLedgerDateLong(date) : 'That day';
    switch (code) {
        case 'fx-rate-day-taken':
            return `${day} already has a ${pair.fromCurrency} to ${pair.toCurrency} rate. A stored rate is never changed or replaced, so pick a day with no rate.`;
        case 'fx-rate-date-in-future':
            return `${day} has not begun yet in UTC, the server's day. A rate can be entered only for today or an earlier day.`;
        case 'fx-rate-date-invalid':
            return 'Enter the day the rate is for as a date.';
        case 'fx-rate-invalid':
            return `A rate is a positive number: how much one ${pair.fromCurrency} is worth in ${pair.toCurrency}. This one is zero, negative or too large to store.`;
        case 'fx-pair-not-needed':
            return `This ledger no longer converts ${pair.fromCurrency} into ${pair.toCurrency}, so it takes no rate for it. Close this and the list shows the pairs it needs.`;
        case 'fx-rate-currency-invalid':
            return 'A rate converts one 3-letter currency into a different one.';
        default:
            return errorMessage(error, 'Could not save the rate.');
    }
}

/**
 * Type a rate for a day with none (RATES-3, RATES-4). The pair is fixed; the day must
 * have begun on the server (UTC) and hold no stored rate, which the dialog looks up as
 * the day is picked, naming the rate already stored. A saved rate is marked Manual and
 * never changed. The server refuses a taken day too, as the backstop.
 */
function AddRateDialog({ ledgerId, pair, onClose }: {
    ledgerId: string;
    pair: FxPair;
    onClose: () => void;
}) {
    const queryClient = useQueryClient();
    const dateId = useId();
    const rateId = useId();
    const hintId = useId();
    const [date, setDate] = useState('');
    const [rateText, setRateText] = useState('');
    const today = utcToday();
    const dateValid = /^\d{4}-\d{2}-\d{2}$/.test(date);

    // What is stored on the day picked: the newest rate before the day after it.
    const dayQuery = useQuery({
        queryKey: ['fx-rates', ledgerId, pair.fromCurrency, pair.toCurrency, 'day', date],
        queryFn: () => fetchFxRatePage(ledgerId, pair.fromCurrency, pair.toCurrency, {
            before: shiftDateInputValue(date, 1),
            limit: 1,
        }),
        enabled: dateValid,
    });
    const stored = dayQuery.data?.rates[0];
    const taken = stored !== undefined && stored.date === date ? stored : null;
    const inFuture = dateValid && date > today;

    const addMutation = useMutation({
        mutationFn: (entry: { date: string; rate: number }) => addFxRate(ledgerId, {
            fromCurrency: pair.fromCurrency,
            toCurrency: pair.toCurrency,
            date: entry.date,
            rate: entry.rate,
        }),
        onSuccess: () => {
            // A new rate can turn a "no rate" figure into a number anywhere the
            // ledger converts, and fills the pair's coverage.
            invalidateConvertedReads(queryClient, ledgerId);
            onClose();
        },
    });

    const rate = Number(rateText);
    const rateValid = rateText.trim().length > 0 && Number.isFinite(rate) && rate > 0;
    const unavailable = !dateValid || !rateValid || taken !== null || inFuture;

    return (
        <Modal open onClose={onClose} titleId="add-rate-title" className="max-w-md">
            <form
                onSubmit={(e) => {
                    e.preventDefault();
                    if (!unavailable) addMutation.mutate({ date, rate });
                }}
            >
                <header className="border-b border-border px-4 py-3">
                    <h2 id="add-rate-title" className="text-base font-semibold">Add rate</h2>
                </header>
                <div className="space-y-3 p-4">
                    <div className="flex flex-col gap-1">
                        <span className={CAPTION}>Pair</span>
                        <p className="font-mono text-sm font-medium">{pairName(pair)}</p>
                    </div>
                    <div className="flex flex-col gap-1">
                        <label htmlFor={dateId} className={CAPTION}>Date *</label>
                        <input
                            id={dateId}
                            type="date"
                            required
                            autoFocus
                            max={today}
                            value={date}
                            aria-invalid={taken !== null || inFuture ? true : undefined}
                            aria-describedby={hintId}
                            onChange={(e) => setDate(e.target.value)}
                            className={`${FIELD} aria-invalid:border-state-danger`}
                            // The day and rate being saved hold until the save answers
                            // (ADR-0023 §L), so the hint keeps describing that day.
                            {...pendingTextProps<HTMLInputElement>(addMutation.isPending)}
                        />
                        {taken !== null ? (
                            <p id={hintId} role="alert" className="text-xs text-state-danger">
                                {formatLedgerDateLong(date)} already has a rate:{' '}
                                {formatFxRate(taken.rate, pair.fromCurrency, pair.toCurrency)}, from{' '}
                                {taken.source === 'manual' ? 'an entry by hand' : 'Frankfurter'}. A stored
                                rate is never changed or replaced, so pick a day with no rate.
                            </p>
                        ) : inFuture ? (
                            <p id={hintId} role="alert" className="text-xs text-state-danger">
                                {formatLedgerDateLong(date)} has not begun yet in UTC, the server&apos;s
                                day. A rate can be entered only for today or an earlier day.
                            </p>
                        ) : dateValid && dayQuery.isSuccess ? (
                            <p id={hintId} className="text-xs text-text-subtle">
                                No rate is stored for {formatLedgerDateLong(date)}.
                            </p>
                        ) : (
                            <p id={hintId} className="text-xs text-text-subtle">
                                A day with no stored rate, up to today (UTC).
                            </p>
                        )}
                    </div>
                    <div className="flex flex-col gap-1">
                        <label htmlFor={rateId} className={CAPTION}>Rate *</label>
                        <span className="flex items-center gap-2">
                            <span className="whitespace-nowrap font-mono text-sm text-text-muted">1 {pair.fromCurrency} =</span>
                            <input
                                id={rateId}
                                type="number"
                                step="any"
                                min="0"
                                required
                                value={rateText}
                                onChange={(e) => setRateText(e.target.value)}
                                className={FIELD}
                                {...pendingTextProps<HTMLInputElement>(addMutation.isPending)}
                            />
                            <span className="font-mono text-sm text-text-muted">{pair.toCurrency}</span>
                        </span>
                    </div>
                    <p className="text-xs text-text-subtle">
                        Saved as Manual. Once saved, a rate is never changed, and a later fetch
                        leaves this day alone, so check the figure first.
                    </p>
                    {addMutation.isError ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {addRateRefusal(addMutation.error, pair, addMutation.variables?.date ?? date)}
                        </p>
                    ) : null}
                </div>
                <ActionFooter
                    className="border-t border-border bg-surface-muted/30 px-4 py-2"
                    cancel={{ label: 'Cancel', onClick: onClose, pending: addMutation.isPending }}
                    confirm={{
                        label: 'Save',
                        type: 'submit',
                        disabled: unavailable,
                        pending: addMutation.isPending,
                        pendingLabel: 'Saving…',
                    }}
                />
            </form>
        </Modal>
    );
}
