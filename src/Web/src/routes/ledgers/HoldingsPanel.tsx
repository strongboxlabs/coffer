import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { ApiError, fetchHoldings } from '@/lib/api';
import type { HoldingsViewDto, PositionDto } from '@/lib/types';
import { EmptyState } from '@/components/ui/EmptyState';
import { cn } from '@/lib/cn';
import { atDisplayPrecision, formatCurrency, formatPrice, formatShares } from '@/lib/money';

import { NoRate } from './home/Money';

// Portfolio View for one investment account (slice A1.b), split across two
// pieces so the register page can show portfolio info without the per-
// security table eating vertical space:
//
//   • PortfolioBar — a single-line summary (Total · Portfolio · Cash ·
//     Unrealized ±%) plus an Activity / Holdings view switch. Always on
//     top of the investment register.
//   • HoldingsTable — the full per-security positions table, shown only
//     when the user selects the Holdings view.
//
// Both read the same `['holdings', ledgerId, accountId]` query, so React
// Query serves one fetch to both. The endpoint resolves the brokerage's
// Holdings sibling server-side (ADR-0019); callers pass the user-visible
// brokerage id.

const NDASH = '–'; // en dash; used for null current-value cells

const HOLDINGS_STALE_MS = 60_000;

/**
 * Two decimals, `-` on a loss and no sign on a gain (ADR-0023 §H). A change that prints
 * as a zero is a plain zero, as the gain beside it is, never a negative one.
 */
function fmtPercent(value: number): string {
    const shown = value.toFixed(2);
    return `${/^-0\.0+$/.test(shown) ? shown.slice(1) : shown}%`;
}

/** A percent change at the two places `fmtPercent` prints it; a printed zero is never below zero. */
function shownPercentChange(value: number): number {
    return Number(value.toFixed(2));
}

/** A loss is news, so it is red; a gain is default text, never green (ADR-0023 §H). */
function gainTextTone(gain: number): string {
    return gain < 0 ? 'text-state-danger' : 'text-text';
}

interface HoldingsPanelProps {
    ledgerId: string;
    accountId: string;
}

export type AccountView = 'activity' | 'holdings';

interface PortfolioBarProps extends HoldingsPanelProps {
    view: AccountView;
    onViewChange: (view: AccountView) => void;
}

/**
 * Compact portfolio summary + Activity / Holdings view switch. Renders a
 * single line above the investment register in both views, so switching
 * back and forth keeps the same header in place.
 */
export function PortfolioBar({
    ledgerId,
    accountId,
    view,
    onViewChange,
}: PortfolioBarProps) {
    const query = useQuery<HoldingsViewDto>({
        queryKey: ['holdings', ledgerId, accountId],
        queryFn: () => fetchHoldings(ledgerId, accountId),
        staleTime: HOLDINGS_STALE_MS,
    });

    const data = query.data;
    const positionsCount = data?.positions.length;

    let summary: ReactNode;
    if (query.isLoading) {
        summary = <span className="text-text-muted">Loading portfolio…</span>;
    } else if (query.isError) {
        const detail =
            query.error instanceof ApiError
                ? query.error.detail
                : 'Failed to load portfolio.';
        summary = (
            <span role="alert" className="text-state-danger">
                {detail}
            </span>
        );
    } else if (data !== undefined) {
        const s = data.summary;
        const c = data.currencyCode;
        // A figure that needs a missing rate reads "no rate" (ADR-0114 D9); the server
        // leaves one null for no other reason, so a null with nothing named is a dash.
        const missing = s.rateUnavailable ?? [];
        const figure = (v: number | null) => (
            v !== null ? formatCurrency(v, c) : missing.length > 0 ? <NoRate missing={missing} /> : NDASH);
        summary = (
            <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
                <Stat label="Total" value={figure(s.total)} strong />
                <Stat label="Portfolio" value={figure(s.portfolioValue)} />
                <Stat label="Cash" value={figure(s.cashBalance)} />
                <Stat
                    label="Unrealized"
                    value={
                        s.unrealizedGain === null || s.percentChange === null ? figure(null) : (
                            // One span prints both, so it is red when either prints as a loss.
                            <span className={gainTextTone(Math.min(
                                atDisplayPrecision(s.unrealizedGain, c), shownPercentChange(s.percentChange)))}>
                                {formatCurrency(atDisplayPrecision(s.unrealizedGain, c), c)} (
                                {fmtPercent(s.percentChange)})
                            </span>
                        )
                    }
                />
            </div>
        );
    } else {
        summary = null;
    }

    return (
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border bg-surface-muted px-4 py-2 text-xs">
            {summary}
            {view === 'activity' ? (
                <button
                    type="button"
                    onClick={() => onViewChange('holdings')}
                    className="flex shrink-0 items-center gap-0.5 font-medium text-text-muted transition-colors hover:text-text"
                >
                    Holdings
                    {positionsCount !== undefined ? ` (${positionsCount})` : ''}
                    <ChevronRight className="size-icon-sm" aria-hidden />
                </button>
            ) : (
                <button
                    type="button"
                    onClick={() => onViewChange('activity')}
                    className="flex shrink-0 items-center gap-0.5 font-medium text-text-muted transition-colors hover:text-text"
                >
                    <ChevronLeft className="size-icon-sm" aria-hidden />
                    Activity
                </button>
            )}
        </div>
    );
}

function Stat({
    label,
    value,
    strong,
}: {
    label: string;
    value: ReactNode;
    strong?: boolean;
}) {
    return (
        <span className="flex items-baseline gap-1">
            <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                {label}
            </span>
            <span
                className={cn(
                    'font-mono tabular-nums',
                    strong ? 'font-semibold text-text' : 'text-text',
                )}
            >
                {value}
            </span>
        </span>
    );
}

/**
 * The full per-security positions table for the Holdings view. Fills the
 * register area and scrolls on its own when an account holds more
 * securities than fit — so a long holdings list never pushes anything
 * off-screen.
 */
export function HoldingsTable({ ledgerId, accountId }: HoldingsPanelProps) {
    const query = useQuery<HoldingsViewDto>({
        queryKey: ['holdings', ledgerId, accountId],
        queryFn: () => fetchHoldings(ledgerId, accountId),
        staleTime: HOLDINGS_STALE_MS,
    });

    if (query.isLoading) {
        return (
            <div className="flex-1 bg-surface px-4 py-6 text-xs text-text-muted">
                Loading portfolio…
            </div>
        );
    }
    if (query.isError) {
        const detail =
            query.error instanceof ApiError
                ? query.error.detail
                : 'Failed to load portfolio.';
        return (
            <div
                role="alert"
                className="flex-1 bg-surface px-4 py-6 text-xs text-state-danger"
            >
                {detail}
            </div>
        );
    }
    const data = query.data;
    if (data === undefined) return null;

    return (
        <div className="flex min-h-0 flex-1 flex-col bg-surface">
            <div className="min-h-0 flex-1 overflow-auto">
                {data.positions.length === 0 ? (
                    <EmptyState
                        className="m-4"
                        message="No holdings yet."
                        hint="Add an investment transaction to populate this view."
                    />
                ) : (
                    <PositionsTable
                        positions={data.positions}
                        currency={data.currencyCode}
                    />
                )}
            </div>
        </div>
    );
}

interface PositionsTableProps {
    positions: readonly PositionDto[];
    currency: string;
}

function PositionsTable({ positions, currency }: PositionsTableProps) {
    return (
        <div className="overflow-x-auto">
            <table className="w-full text-xs">
                <thead>
                    <tr className="border-b border-border bg-surface-header text-[0.625rem] uppercase tracking-wider text-text-muted">
                        <th className="px-3 py-2 text-left">Security</th>
                        <th className="px-3 py-2 text-right">Shares</th>
                        <th className="px-3 py-2 text-right">Price</th>
                        <th className="px-3 py-2 text-right">Cost / Share</th>
                        <th className="px-3 py-2 text-right">Cost Basis</th>
                        <th className="px-3 py-2 text-right">Current Value</th>
                        <th className="px-3 py-2 text-right">Unrealized</th>
                        <th className="px-3 py-2 text-right">% Change</th>
                    </tr>
                </thead>
                <tbody>
                    {positions.map((p) => (
                        <PositionRow
                            key={p.securityId}
                            position={p}
                            currency={currency}
                        />
                    ))}
                </tbody>
            </table>
        </div>
    );
}

interface PositionRowProps {
    position: PositionDto;
    currency: string;
}

function PositionRow({ position: p, currency }: PositionRowProps) {
    // Un-priced positions render their current* columns as a dash —
    // better signal than a zero amount / 0% which would imply "actually zero."
    const hasPrice = p.currentPrice !== null;
    // The price is in its own currency: the account's unless named (ADR-0114 D5).
    const priceCurrency = p.priceCurrencyCode ?? currency;
    // A position priced in another currency is valued in the account's currency at the
    // latest rate, `accountValue`, which is what the account adds up and what the gain
    // compares with the cost (ADR-0114 D7). Its value in the price's currency shows
    // beneath, so the conversion can be read off the row.
    const foreign = p.priceCurrencyCode !== undefined && p.priceCurrencyCode !== currency;
    const missing = p.rateUnavailable ?? [];
    // A figure null for want of a rate says so; one null for want of a price is a dash.
    const nullFigure = missing.length > 0 ? <NoRate missing={missing} /> : NDASH;
    // The prices (price, cost per share) print through formatPrice, and the money through
    // formatCurrency at the currency's places (ADR-0114 D15). The gain takes its sign and
    // its red from the figure as it prints, so a residue below one minor unit is a zero.
    // The percent takes its own, from its two printed places: a gain that prints as a
    // zero beside a percent that prints as a loss leaves the percent red.
    const shownGain = p.unrealizedGain === null ? null : atDisplayPrecision(p.unrealizedGain, currency);
    const gainTone = shownGain === null ? 'text-text-subtle' : gainTextTone(shownGain);
    const shownPercent = p.percentChange === null ? null : shownPercentChange(p.percentChange);
    const percentTone = shownPercent === null ? 'text-text-subtle' : gainTextTone(shownPercent);

    return (
        <tr className="border-b border-border/30 bg-surface hover:bg-surface-hover">
            <td className="px-3 py-2 text-left">
                <div className="flex flex-col">
                    <span className="font-medium text-text">
                        {p.ticker ?? p.name}
                    </span>
                    {p.ticker !== null ? (
                        <span className="text-[0.625rem] text-text-muted">
                            {p.name}
                        </span>
                    ) : null}
                </div>
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {formatShares(p.quantity)}
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {hasPrice ? formatPrice(p.currentPrice!, priceCurrency) : NDASH}
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {formatPrice(p.costPerShare, currency)}
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {formatCurrency(p.costBasis, currency)}
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {!foreign ? (
                    p.currentValue !== null ? formatCurrency(p.currentValue, currency) : NDASH
                ) : (
                    <div className="flex flex-col items-end">
                        <span data-position-value="account">
                            {p.accountValue != null ? formatCurrency(p.accountValue, currency) : nullFigure}
                        </span>
                        {p.currentValue !== null ? (
                            <span data-position-value="price" className="text-[0.625rem] text-text-muted">
                                {formatCurrency(p.currentValue, priceCurrency)}
                            </span>
                        ) : null}
                    </div>
                )}
            </td>
            <td className={`px-3 py-2 text-right font-mono tabular-nums ${gainTone}`}>
                {shownGain !== null ? formatCurrency(shownGain, currency) : nullFigure}
            </td>
            <td className={`px-3 py-2 text-right font-mono tabular-nums ${percentTone}`}>
                {p.percentChange !== null ? fmtPercent(p.percentChange) : nullFigure}
            </td>
        </tr>
    );
}
