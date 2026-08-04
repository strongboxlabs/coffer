import { useQuery } from '@tanstack/react-query';
import { fetchOpenLots } from '@/lib/api';
import type { InvestmentLotDto, LedgerInvestmentAction } from '@/lib/types';
import { formatShares, formatCurrency } from '@/lib/money';

interface FifoPreviewPopoverProps {
    /** Action currently being edited. Popover renders only on
     *  sell / sellx / transfer_shares (all consume source lots FIFO). */
    action: LedgerInvestmentAction;
    /** The brokerage account id (NOT the Holdings sibling) — the
     *  API endpoint resolves the sibling internally. */
    brokerageAccountId: string;
    ledgerId: string;
    /** Security being disposed. Popover renders only when set. */
    securityId: string | null;
    /** Shares the user has entered. Disposals are signed negative on
     *  the wire but the user types the magnitude here; this is the
     *  raw input value (may be null / 0 / negative). The popover
     *  takes the absolute magnitude. */
    sharesInput: number | null;
}

/**
 * FIFO consumption preview for Sell / SellXfr (ADR-0029 §A4.c.4) and
 * Transfer-shares (ADR-0065). Renders a small popover anchored beside
 * the Shares field listing the open lots that will be consumed (sell)
 * or moved in-kind (transfer_shares), in FIFO order (oldest first),
 * and the resulting cost basis closed / carried.
 *
 * Server is the authority on lot consumption — this is advisory.
 * If the user enters more shares than the open position holds, the
 * popover warns; Save stays enabled and the server will reject if
 * its own rules say so.
 */
export function FifoPreviewPopover({
    action,
    brokerageAccountId,
    ledgerId,
    securityId,
    sharesInput,
}: FifoPreviewPopoverProps) {
    const isTransfer = action === 'transfer_shares';
    const visible = (action === 'sell' || action === 'sellx' || isTransfer)
        && securityId !== null
        && sharesInput !== null
        && Math.abs(sharesInput) > 0;

    const lotsQuery = useQuery({
        queryKey: ['open-lots', ledgerId, brokerageAccountId, securityId],
        queryFn: () => fetchOpenLots(ledgerId, brokerageAccountId, securityId!),
        enabled: visible,
        staleTime: 10_000,
    });

    if (!visible) return null;

    const lots: readonly InvestmentLotDto[] = lotsQuery.data ?? [];
    const shares = Math.abs(sharesInput);
    const plan = computeFifoPlan(lots, shares);
    const verb = isTransfer ? 'move' : 'close';

    return (
        <div
            className="mt-1 w-72 rounded border border-border bg-surface p-2 text-[0.6875rem] shadow-sm"
            role="tooltip"
            aria-label={isTransfer ? 'In-kind lot transfer preview' : 'FIFO lot consumption preview'}
        >
            <div className="mb-1 flex items-baseline justify-between">
                <span className="font-semibold text-text-default">
                    {plan.consumed.length === 0
                        ? 'No open lots'
                        : `Will ${verb} ${plan.consumed.length} lot${plan.consumed.length === 1 ? '' : 's'}`}
                </span>
                {lotsQuery.isLoading ? (
                    <span className="text-text-subtle">loading…</span>
                ) : null}
            </div>

            {plan.consumed.length > 0 ? (
                <ul className="space-y-0.5 font-mono tabular-nums">
                    {plan.consumed.map((c) => (
                        <li key={c.lotId} className="flex justify-between gap-2">
                            <span className="text-text-muted">
                                {c.acquiredAt.slice(0, 10)}
                            </span>
                            <span>
                                {fmt(c.qtyConsumed)}/{fmt(c.qtyAvailable)} sh @ {fmtUsd(c.unitCost)}
                            </span>
                            <span className="text-text-default">
                                {fmtUsd(c.basisClosed)}
                            </span>
                        </li>
                    ))}
                </ul>
            ) : null}

            <div className="mt-1 flex items-baseline justify-between border-t border-border pt-1 text-text-default">
                <span>{isTransfer ? 'Total basis carried' : 'Total basis closed'}</span>
                <span className="font-mono tabular-nums font-semibold">
                    {fmtUsd(plan.totalBasis)}
                </span>
            </div>

            {plan.shortfall > 0 ? (
                <div className="mt-1 rounded bg-state-danger-soft px-1.5 py-1 text-state-danger">
                    Exceeds open position by {fmt(plan.shortfall)} sh — server
                    may reject.
                </div>
            ) : null}
        </div>
    );
}

interface ConsumedLot {
    lotId: string;
    acquiredAt: string;
    qtyAvailable: number;
    qtyConsumed: number;
    unitCost: number;
    basisClosed: number;
}

interface FifoPlan {
    consumed: ConsumedLot[];
    totalBasis: number;
    /** Magnitude of the disposal not covered by open lots. */
    shortfall: number;
}

/**
 * Pure function — given the open lots (already FIFO-ordered by the
 * API) and the magnitude of shares to dispose, simulate the FIFO
 * walk and return the per-lot consumption + total basis. Exported
 * for testability.
 */
export function computeFifoPlan(
    lots: readonly InvestmentLotDto[],
    sharesToDispose: number,
): FifoPlan {
    const consumed: ConsumedLot[] = [];
    let remaining = sharesToDispose;
    let totalBasis = 0;

    for (const lot of lots) {
        if (remaining <= 0) break;
        const take = Math.min(remaining, lot.quantity);
        const basisClosed = take * lot.unitCost;
        consumed.push({
            lotId: lot.lotId,
            acquiredAt: lot.acquiredAt,
            qtyAvailable: lot.quantity,
            qtyConsumed: take,
            unitCost: lot.unitCost,
            basisClosed,
        });
        totalBasis += basisClosed;
        remaining -= take;
    }

    return { consumed, totalBasis, shortfall: Math.max(0, remaining) };
}

const fmt = formatShares;
const fmtUsd = formatCurrency;
