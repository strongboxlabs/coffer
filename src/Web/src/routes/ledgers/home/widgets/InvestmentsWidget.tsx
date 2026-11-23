import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { Skeleton } from '@/components/ui/Skeleton';
import { fetchAllocation, fetchReturns, fetchSecurities } from '@/lib/api';
import { formatLedgerDateCompact } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
import type { AllocationDimension, ReturnsResult } from '@/lib/types';

import { useHome, useWidgetFrame } from '../homeContext';
import { Money, NoRate } from '../Money';
import { useOverview, useRailAccounts } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

const WINDOW_LABEL: Record<string, string> = { ytd: 'YTD', '1y': '1 year', inception: 'since inception' };
const DIMENSION_LABEL: Record<string, string> = {
    asset_class: 'By asset class', region: 'By region', vehicle: 'By vehicle', account: 'By account',
};

/**
 * Holdings, brokerage cash and their total, labelled so they reconcile; the
 * unrealized gain; the return over a window, its gain and its two rates; and the
 * allocation as ranked bars. Rendered only in a ledger with an investment account.
 * The return and allocation value the same instant as the overview: the end of the
 * user's today.
 *
 * Every figure is in the ledger's reporting currency, converted (ADR-0114 D7); the
 * returns and allocation reports name it too. A figure that needs a missing rate
 * reads "no rate", and so does every figure read off it, rates and shares included
 * (ADR-0114 D9).
 */
export function InvestmentsWidget() {
    const home = useHome();
    const accounts = useRailAccounts(home.ledgerId);
    const hasInvestment = accounts.data?.some((a) => a.accountType === 'investment') ?? false;

    if (accounts.isPending) return null;
    if (!hasInvestment) {
        // Kept in place while the layout is edited, so its slot survives a ledger
        // that has no brokerage yet; absent otherwise.
        return home.editing ? (
            <WidgetPanel title="Investments">
                <p className="p-4 text-sm text-text-muted">Investments — appears when this ledger has an investment account.</p>
            </WidgetPanel>
        ) : null;
    }
    return <InvestmentsBody />;
}

function InvestmentsBody() {
    const home = useHome();
    const frame = useWidgetFrame();
    const allocation = String(frame.settings.allocation ?? 'asset_class');
    const returnWindow = String(frame.settings.returnWindow ?? 'ytd');
    const overview = useOverview(home.ledgerId, home.today);

    const link = (
        <Link to="/ledgers/$ledgerId/securities" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Securities →
        </Link>
    );
    if (overview.isError) {
        return <WidgetPanel title="Investments" link={link}><WidgetError what="investments" error={overview.error} onRetry={() => void overview.refetch()} /></WidgetPanel>;
    }
    if (!overview.data) {
        return (
            <WidgetPanel title="Investments" link={link}>
                <div aria-busy="true" aria-label="Loading investments" className="space-y-2 p-4">
                    {Array.from({ length: 5 }, (_, i) => <Skeleton key={i} className="h-control-20px" />)}
                </div>
            </WidgetPanel>
        );
    }

    const o = overview.data;
    const p = o.portfolio;
    const c = p.currencyCode;
    const missing = p.rateUnavailable;
    return (
        <WidgetPanel title="Investments" link={link}>
            <div className="space-y-3 p-4 text-sm">
                <dl className="space-y-0.5">
                    <Figure label="Holdings"><Money amount={p.value} currency={c} tone="fixed" rateUnavailable={missing} /></Figure>
                    <Figure label="Brokerage cash"><Money amount={p.cashBalance} currency={c} tone="fixed" rateUnavailable={missing} /></Figure>
                    <Figure label="Invested total"><Money amount={o.investmentsValue} currency={o.currencyCode} tone="fixed" rateUnavailable={o.rateUnavailable} /></Figure>
                    <Figure label="Unrealized gain">
                        <Money amount={p.unrealizedGain} currency={c} tone="news" rateUnavailable={missing} />
                        {p.percentChange === null ? null : <span className="ml-1 font-mono text-xs tabular-nums text-text-muted">({percentAsPrinted(p.percentChange).toFixed(2)}%)</span>}
                    </Figure>
                </dl>
                <Returns window={returnWindow} />
                {allocation === 'off' ? null : <Allocation dimension={allocation as AllocationDimension} />}
            </div>
        </WidgetPanel>
    );
}

/** A percentage as it prints, to two places, rounded half away from zero as the
 *  rates are: a change that prints as zero has no sign. */
function percentAsPrinted(percent: number): number {
    const shown = (Math.sign(percent) * Math.round(Math.abs(percent) * 100)) / 100;
    return shown === 0 ? 0 : shown;
}

function Figure({ label, children }: { label: string; children: ReactNode }) {
    return (
        <div className="flex flex-wrap items-baseline justify-between gap-x-2">
            <dt className="text-text-muted">{label}</dt>
            <dd>{children}</dd>
        </div>
    );
}

/**
 * The window's start as the API wants it: a local calendar date at 00:00Z. A year
 * back is the same day a calendar year earlier, or that month's last day when the
 * year before has no such day (Feb 29 gives Feb 28).
 */
function windowStart(window: string, today: string): string | undefined {
    if (window === 'ytd') return `${today.slice(0, 4)}-01-01T00:00:00Z`;
    if (window === '1y') {
        const [y, m, d] = today.split('-').map(Number);
        const year = y! - 1;
        const last = new Date(Date.UTC(year, m!, 0)).getUTCDate();
        const day = Math.min(d!, last);
        return `${String(year).padStart(4, '0')}-${String(m).padStart(2, '0')}-${String(day).padStart(2, '0')}T00:00:00Z`;
    }
    return undefined;
}

function Returns({ window }: { window: string }) {
    const home = useHome();
    const returns = useQuery({
        queryKey: ['home', home.ledgerId, 'returns', window, home.today],
        queryFn: () => fetchReturns(home.ledgerId, windowStart(window, home.today)),
    });
    if (returns.isError) return <WidgetError what="returns" error={returns.error} onRetry={() => void returns.refetch()} />;
    if (!returns.data) return <Skeleton className="h-control-36px" />;
    const r = returns.data;
    return (
        <dl className="space-y-0.5 border-t border-border/60 pt-2">
            <div className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">
                Return {WINDOW_LABEL[window]}
            </div>
            {/* The window's change in value that money in and out does not explain:
                price movement, dividends and interest, less fees. Not annualized, so
                the rows that are say so themselves. */}
            <Figure label="Gain incl. income"><Money amount={r.gain} currency={r.currencyCode} tone="news" rateUnavailable={r.rateUnavailable} /></Figure>
            <RateRow label="Time-weighted, annualized" rate={r.timeWeightedReturn} reason={r.timeWeightedUnavailableReason} result={r} covered={covered(r)} />
            <RateRow label="Money-weighted, annualized" rate={r.moneyWeightedReturn} reason={r.moneyWeightedUnavailableReason} result={r} />
        </dl>
    );
}

/** The time-weighted rate covers only the time the portfolio was invested, and is
 *  shown with that span: annualizing a short stretch magnifies it. */
function covered(r: ReturnsResult): string | null {
    if (r.timeWeightedCoveredDays === null) return null;
    return r.timeWeightedCoveredDays < 365 ? `over ${r.timeWeightedCoveredDays} days` : `over ${r.timeWeightedCoveredYears?.toFixed(1)} years`;
}

function RateRow({ label, rate, reason, result, covered }: {
    label: string;
    rate: number | null;
    reason: string | null;
    result: ReturnsResult;
    covered?: string | null;
}) {
    // As printed, to two places: a rate that prints as zero is not a red one.
    const shown = rate === null ? null : (Math.sign(rate) * Math.round(Math.abs(rate) * 10_000)) / 100;
    return (
        <div className="flex flex-wrap items-baseline justify-between gap-x-2">
            <dt className="text-text-muted">{label}</dt>
            <dd className="text-right">
                {shown === null && result.rateUnavailable.length > 0 ? (
                    // A rate over a value that needs a missing rate is not a rate. Decided by
                    // the structured list, not the reason, which is prose: no figure over a
                    // partial sum is trusted, so a rate missing for any reason while a rate
                    // in the window is missing reads as 'no rate', its title naming each pair.
                    <NoRate missing={result.rateUnavailable} className="font-semibold" />
                ) : shown === null ? (
                    <span className="text-text-muted" title={reason ?? undefined}>— <span className="text-xs">{reason}</span></span>
                ) : (
                    <>
                        <span className={`font-mono font-semibold tabular-nums ${shown < 0 ? 'text-state-danger' : 'text-text'}`}>
                            {(shown === 0 ? 0 : shown).toFixed(2)}%
                        </span>
                        {covered ? <span className="ml-1 text-xs text-text-muted">{covered}</span> : null}
                    </>
                )}
            </dd>
        </div>
    );
}

function Allocation({ dimension }: { dimension: AllocationDimension }) {
    const home = useHome();
    const allocation = useQuery({
        queryKey: ['home', home.ledgerId, 'allocation', dimension, home.today],
        queryFn: () => fetchAllocation(home.ledgerId, dimension),
    });
    const securities = useQuery({ queryKey: ['securities', home.ledgerId], queryFn: () => fetchSecurities(home.ledgerId) });
    if (allocation.isError) return <WidgetError what="allocation" error={allocation.error} onRetry={() => void allocation.refetch()} />;
    if (!allocation.data) return <Skeleton className="h-fixed-80px" />;

    const a = allocation.data;
    // A share of a total that needs a missing rate means nothing, so neither does its
    // bar; the buckets are still named, so what is held stays readable.
    const held = (securities.data ?? []).filter((s) => s.totalQuantity !== 0 && s.latestPriceAsOf !== null);
    const oldest = held.length === 0 ? null : held.map((s) => s.latestPriceAsOf!).sort()[0]!;
    // Valued buckets by value, then the ones with no value.
    const buckets = [...a.buckets].sort((x, y) =>
        x.marketValue === null ? (y.marketValue === null ? 0 : 1)
            : y.marketValue === null ? -1
                : y.marketValue - x.marketValue);
    const top = buckets[0]?.percent ?? 0;

    return (
        <div className="space-y-1 border-t border-border/60 pt-2">
            <div className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">
                {DIMENSION_LABEL[dimension] ?? dimension}
                {oldest ? <span className="font-normal normal-case tracking-normal"> · oldest price used {formatLedgerDateCompact(oldest)}</span> : null}
            </div>
            <ul className="space-y-1">
                {buckets.map((bk) => (
                    <li key={bk.bucket} className="flex items-center gap-2 text-xs" title={bk.marketValue === null ? undefined : formatCurrency(bk.marketValue, bk.currencyCode)}>
                        <span className="w-fixed-96px shrink-0 truncate">{humanize(bk.bucket)}</span>
                        <span aria-hidden className="relative h-fixed-8px min-w-0 flex-1 rounded-sm bg-surface-hover">
                            {bk.percent === null ? null : (
                                <span className="absolute inset-y-0 left-0 rounded-sm bg-accent" style={{ width: `${top !== null && top > 0 ? (bk.percent / top) * 100 : 0}%` }} />
                            )}
                        </span>
                        <span className="w-fixed-48px shrink-0 text-right font-mono tabular-nums">
                            {bk.percent === null ? <NoRate missing={[...bk.rateUnavailable, ...a.rateUnavailable]} /> : `${bk.percent.toFixed(1)}%`}
                        </span>
                    </li>
                ))}
            </ul>
            {a.undecomposedMultiAssets.length > 0 ? (
                <p className="text-xs text-text-muted">
                    {a.undecomposedMultiAssets.map((u) => (u.percentOfTotal === null ? (u.ticker ?? u.name) : `${u.ticker ?? u.name} (${u.percentOfTotal.toFixed(1)}%)`)).join(', ')}{' '}
                    isn&apos;t split into asset classes, so the other rows read low.
                </p>
            ) : null}
            {a.excludedBrokerageCash !== 0 ? (
                <p className="text-xs text-text-muted">
                    Excludes {a.excludedBrokerageCash === null ? <NoRate missing={a.rateUnavailable} /> : formatCurrency(a.excludedBrokerageCash, a.currencyCode)} brokerage cash.
                </p>
            ) : null}
        </div>
    );
}

/** `multi_asset` → `Multi asset`. */
function humanize(bucket: string): string {
    const s = bucket.replace(/_/g, ' ');
    return s.charAt(0).toUpperCase() + s.slice(1);
}
