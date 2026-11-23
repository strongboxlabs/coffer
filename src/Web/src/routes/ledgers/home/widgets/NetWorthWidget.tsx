import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { ChartFrame, VIEWBOX } from '@/components/chart/ChartFrame';
import { ExchangeRatesLink, MissingRateNote } from '@/components/ConversionNote';
import { RatesFetchedThrough } from '@/components/RatesFetchedThrough';
import { PointSelect } from '@/components/chart/PointSelect';
import { Bar } from '@/components/chart/Series';
import { band, extent, vertical } from '@/components/chart/scale';
import { Skeleton } from '@/components/ui/Skeleton';
import { fetchNetWorthHistory } from '@/lib/api';
import { cn } from '@/lib/cn';
import { NO_RATE_LABEL, rateUnavailableTitle } from '@/lib/conversion';
import { formatLedgerDateCompact } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
import type { NetWorthHistory, NetWorthHistoryPoint, RateUnavailable } from '@/lib/types';

import { useHome, useWidgetFrame } from '../homeContext';
import { Money, NoRate } from '../Money';
import { previousMonthEnd, useOverview } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * Net worth today, its change since the last month-end, and its month-ends as bars
 * with today's hatched beside them. The change is the server's: net worth at the end
 * of that month is the history's bar for it. Bars take focus and a selection, so the
 * figure behind any bar is readable without a pointer.
 *
 * Every figure is in the reporting currency, converted (ADR-0114 D7). One that needs
 * a rate the ledger does not hold reads "no rate" (ADR-0114 D9), and one line under the
 * headline names the missing pair and day and how far rates are fetched. A point of
 * the history with no rate draws no bar; its slot says "no rate" and the rest draw.
 */
export function NetWorthWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const months = Number(frame.settings.months ?? 12);
    const showSplit = frame.settings.showSplit !== false;
    const since = previousMonthEnd(home.today);
    const overview = useOverview(home.ledgerId, home.today, since);

    const link = (
        <Link to="/ledgers/$ledgerId/accounts" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Accounts →
        </Link>
    );

    if (overview.isError) {
        return <WidgetPanel title="Net worth" link={link}><WidgetError what="net worth" error={overview.error} onRetry={() => void overview.refetch()} /></WidgetPanel>;
    }
    if (!overview.data) {
        return (
            <WidgetPanel title="Net worth" link={link}>
                <div aria-busy="true" aria-label="Loading net worth" className="space-y-2 p-4">
                    <Skeleton className="h-control-28px w-fixed-160px" />
                    <Skeleton className="h-fixed-128px" />
                </div>
            </WidgetPanel>
        );
    }

    const o = overview.data;
    const c = o.currencyCode;
    const comparison = o.comparison;
    // Every rate a figure here needed and the ledger does not hold (ADR-0114 D9): each
    // such figure reads "no rate", and one line under the headline says which.
    const missing = [...o.rateUnavailable, ...(comparison?.rateUnavailable ?? [])];
    return (
        <WidgetPanel title="Net worth" link={link}>
            <div className="space-y-2 p-4">
                <Money amount={o.netWorth} currency={c} tone="fixed" rateUnavailable={o.rateUnavailable} className="block text-xl" />
                <MissingRateNote missing={missing} ratesLink={<ExchangeRatesLink ledgerId={home.ledgerId} />}>
                    <RatesFetchedThrough ledgerId={home.ledgerId} missing={missing} />
                </MissingRateNote>
                {comparison ? (
                    <p className="flex flex-wrap items-baseline justify-between gap-x-2 text-sm">
                        <span className="text-text-muted">Change since {formatLedgerDateCompact(comparison.since)}</span>
                        {comparison.noHistory ? (
                            <span className="text-text-muted">— <span className="text-xs">no history at {formatLedgerDateCompact(comparison.since)}</span></span>
                        ) : (
                            <Money amount={comparison.netWorth.change} currency={c} tone="news" rateUnavailable={[...o.rateUnavailable, ...comparison.rateUnavailable]} />
                        )}
                    </p>
                ) : null}
                {showSplit ? (
                    <dl className="space-y-0.5 text-sm">
                        <div className="flex justify-between gap-2">
                            <dt className="text-text-muted">Assets</dt>
                            <dd><Money amount={o.totalAssets} currency={c} tone="fixed" rateUnavailable={o.rateUnavailable} className="font-normal" /></dd>
                        </div>
                        <div className="flex justify-between gap-2">
                            <dt className="text-text-muted">Liabilities</dt>
                            <dd><Money amount={o.totalLiabilities} currency={c} tone="fixed" rateUnavailable={o.rateUnavailable} className="font-normal" /></dd>
                        </div>
                    </dl>
                ) : null}
                <History months={months} since={comparison?.since ?? since} explained={missing} />
            </div>
        </WidgetPanel>
    );
}

/** One missing rate as a key: a pair and a day. */
const rateKey = (m: RateUnavailable) => `${m.fromCurrency}\u0000${m.toCurrency}\u0000${m.date}`;

function History({ months, since, explained }: {
    months: number;
    since: string;
    /** The missing rates the line under the headline already names. */
    explained: readonly RateUnavailable[];
}) {
    const home = useHome();
    const [y, m] = home.today.split('-').map(Number);
    const from = new Date(Date.UTC(y!, m! - 1 - months, 1)).toISOString();
    const to = new Date(Date.UTC(y!, m!, 1)).toISOString();
    const history = useQuery({
        queryKey: ['home', home.ledgerId, 'net-worth-history', months, home.today],
        queryFn: () => fetchNetWorthHistory(home.ledgerId, from, to),
    });

    if (history.isError) {
        return <WidgetError what="net worth history" error={history.error} onRetry={() => void history.refetch()} />;
    }
    if (!history.data) return <Skeleton className="h-fixed-128px" />;
    // The month-ends and today the history has, each a slot whether or not it has a
    // figure: a point with no rate is not a net worth (ADR-0114 D9), so its slot draws
    // no bar and says "no rate", and every other point still draws.
    const slots = history.data.points.filter((p) => !p.noHistory);
    const monthEnds = history.data.points.slice(0, -1).filter((p) => !p.noHistory).length;
    if (monthEnds < 3) {
        return (
            <p className="text-xs text-text-muted">
                Net worth history appears after three month-ends: {monthEnds} so far.
            </p>
        );
    }
    // What the headline's line does not already name gets a line of its own.
    const named = new Set(explained.map(rateKey));
    const unexplained = slots
        .flatMap((p) => (p.netWorth === null ? p.rateUnavailable : []))
        .filter((m) => !named.has(rateKey(m)));
    const note = (
        <MissingRateNote missing={unexplained} ratesLink={<ExchangeRatesLink ledgerId={home.ledgerId} />} className="mt-1">
            <RatesFetchedThrough ledgerId={home.ledgerId} missing={unexplained} />
        </MissingRateNote>
    );
    if (!slots.some((p) => p.netWorth !== null)) return note;
    return (
        <>
            <Bars history={history.data} slots={slots} since={since} />
            {note}
        </>
    );
}

function Bars({ history, slots, since }: {
    history: NetWorthHistory;
    /** Every point with a history, in order; the last is today. */
    slots: readonly NetWorthHistoryPoint[];
    since: string;
}) {
    const initial = slots.findIndex((p) => p.asOf.slice(0, 10) === since);
    const [selected, setSelected] = useState<number | null>(initial >= 0 ? initial : null);
    const c = history.currencyCode;
    const drawn = slots
        .map((p, i) => ({ p, i, netWorth: p.netWorth }))
        .filter((d): d is { p: NetWorthHistoryPoint; i: number; netWorth: number } => d.netWorth !== null);
    const values = drawn.map((d) => d.netWorth);
    const [lo, hi] = extent(values);
    const y = vertical([lo, hi], VIEWBOX.height);
    const b = band(slots.length, VIEWBOX.width);
    const barWidth = b.width * 0.6;
    const isToday = (i: number) => i === slots.length - 1;
    const name = (i: number) => (isToday(i) ? 'Today' : formatLedgerDateCompact(slots[i]!.asOf));
    const unpriced = drawn.filter(({ p }) => p.unpricedSecurityCount > 0);
    const noRate = slots
        .map((p, i) => ({ p, i }))
        .filter(({ p }) => p.netWorth === null);
    const first = slots[0]!;
    const firstLabel = `${MONTHS[Number(first.asOf.slice(5, 7)) - 1]} '${first.asOf.slice(2, 4)}`;
    const falls = values.at(-1)! < values[0]!;
    const figureText = (p: NetWorthHistoryPoint) => (p.netWorth === null ? NO_RATE_LABEL : formatCurrency(p.netWorth, c));
    const ariaLabel = noRate.length === 0
        ? `Net worth at ${slots.length - 1} month-ends and today, ${falls ? 'lower' : 'higher'} today than at the first`
        : `Net worth at ${slots.length - 1} month-ends and today; no bar for ${noRate.map(({ i }) => name(i)).join(', ')}. `
            + rateUnavailableTitle(noRate.flatMap(({ p }) => p.rateUnavailable));
    const selectedPoint = selected === null ? undefined : slots[selected];

    return (
        <div>
            {selectedPoint !== undefined && selected !== null ? (
                <p className="text-xs text-text-muted" aria-live="polite">
                    Selected {name(selected)}:{' '}
                    {selectedPoint.netWorth === null
                        ? <NoRate missing={selectedPoint.rateUnavailable} />
                        : <span className="font-mono tabular-nums text-text">{formatCurrency(selectedPoint.netWorth, c)}</span>}
                </p>
            ) : null}
            <ChartFrame
                heightClass="h-fixed-128px"
                ariaLabel={ariaLabel}
                overlay={
                    <>
                        <div aria-hidden className="pointer-events-none absolute inset-x-0 border-t border-text-subtle" style={{ top: `${(y(0) / VIEWBOX.height) * 100}%` }} />
                        {noRate.map(({ p, i }) => (
                            // The empty slot says why it is empty; its column's button
                            // and the chart's label say it to a screen reader, and its
                            // selection names the missing rate.
                            <span
                                key={p.asOf}
                                aria-hidden
                                data-no-rate={p.asOf.slice(0, 10)}
                                className={cn(
                                    'pointer-events-none absolute bottom-1 whitespace-nowrap text-[0.625rem] text-text-subtle',
                                    isToday(i) ? 'right-0' : '-translate-x-1/2',
                                )}
                                style={isToday(i) ? undefined : { left: `${(b.center(i) / VIEWBOX.width) * 100}%` }}
                            >
                                {NO_RATE_LABEL}
                            </span>
                        ))}
                        {unpriced.map(({ p, i, netWorth }) => (
                            <span
                                key={p.asOf}
                                aria-hidden
                                data-unpriced={p.asOf.slice(0, 10)}
                                className="pointer-events-none absolute size-fixed-6px -translate-x-1/2 rounded-full border-[1.5px] border-text-subtle bg-surface"
                                style={{
                                    left: `${(b.center(i) / VIEWBOX.width) * 100}%`,
                                    top: `calc(${(Math.min(y(netWorth), y(0)) / VIEWBOX.height) * 100}% - 10px)`,
                                }}
                            />
                        ))}
                        <PointSelect
                            label="Net worth month-ends"
                            labels={slots.map((p, i) => `${name(i)}: ${figureText(p)}`)}
                            selected={selected}
                            onSelect={setSelected}
                        />
                    </>
                }
            >
                {drawn.map(({ p, i, netWorth }) => (
                    <Bar
                        key={p.asOf}
                        x={b.center(i) - barWidth / 2}
                        width={barWidth}
                        baselineY={y(0)}
                        valueY={y(netWorth)}
                        className={cn(netWorth < 0 ? 'fill-mark-danger' : 'fill-chip-neutral-text/60')}
                        hatched={isToday(i)}
                    />
                ))}
            </ChartFrame>
            <div aria-hidden className="mt-1 flex justify-between text-[0.625rem] text-text-subtle">
                <span>{firstLabel}</span>
                <span>Today</span>
            </div>
            {unpriced.length > 0 ? (
                <p className="mt-1 text-xs text-text-muted">
                    {unpriced.map(({ p, i }) => `○ ${name(i)}: ${p.unpricedSecurityCount} holding${p.unpricedSecurityCount === 1 ? '' : 's'} unpriced`).join('; ')}; valued at cost.
                </p>
            ) : null}
        </div>
    );
}
