import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { ApiError, fetchHoldings } from '@/lib/api';
import type { HoldingsViewDto, PositionDto } from '@/lib/types';
import { EmptyState } from '@/components/ui/EmptyState';
import { cn } from '@/lib/cn';
import { formatCurrency, formatShares } from '@/lib/money';

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

/** Pretty signed-percent — e.g. `+11.81%` / `-3.22%`. */
function fmtPercent(value: number): string {
    const sign = value > 0 ? '+' : '';
    return `${sign}${value.toFixed(2)}%`;
}

function gainTextTone(gain: number): string {
    if (gain > 0) return 'text-state-success';
    if (gain < 0) return 'text-state-danger';
    return 'text-text';
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
        summary = (
            <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
                <Stat label="Total" value={formatCurrency(s.total, c)} strong />
                <Stat label="Portfolio" value={formatCurrency(s.portfolioValue, c)} />
                <Stat label="Cash" value={formatCurrency(s.cashBalance, c)} />
                <Stat
                    label="Unrealized"
                    value={
                        <span className={gainTextTone(s.unrealizedGain)}>
                            {formatCurrency(s.unrealizedGain, c)} (
                            {fmtPercent(s.percentChange)})
                        </span>
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
                    <ChevronRight className="h-3.5 w-3.5" aria-hidden />
                </button>
            ) : (
                <button
                    type="button"
                    onClick={() => onViewChange('activity')}
                    className="flex shrink-0 items-center gap-0.5 font-medium text-text-muted transition-colors hover:text-text"
                >
                    <ChevronLeft className="h-3.5 w-3.5" aria-hidden />
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
            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">
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
    // better signal than $0 / 0% which would imply "actually zero."
    const hasPrice = p.currentPrice !== null;
    const gainTone =
        p.unrealizedGain === null ? 'text-text-subtle' : gainTextTone(p.unrealizedGain);

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
                {hasPrice ? formatCurrency(p.currentPrice!, currency) : NDASH}
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {formatCurrency(p.costPerShare, currency)}
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {formatCurrency(p.costBasis, currency)}
            </td>
            <td className="px-3 py-2 text-right font-mono tabular-nums">
                {p.currentValue !== null
                    ? formatCurrency(p.currentValue, currency)
                    : NDASH}
            </td>
            <td className={`px-3 py-2 text-right font-mono tabular-nums ${gainTone}`}>
                {p.unrealizedGain !== null
                    ? formatCurrency(p.unrealizedGain, currency)
                    : NDASH}
            </td>
            <td className={`px-3 py-2 text-right font-mono tabular-nums ${gainTone}`}>
                {p.percentChange !== null ? fmtPercent(p.percentChange) : NDASH}
            </td>
        </tr>
    );
}
