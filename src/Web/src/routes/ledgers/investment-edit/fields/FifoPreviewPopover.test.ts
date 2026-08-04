import { describe, expect, it } from 'vitest';
import type { InvestmentLotDto } from '@/lib/types';
import { computeFifoPlan } from './FifoPreviewPopover';

const mkLot = (
    n: number,
    qty: number,
    unitCost: number,
    acquiredAt = `2025-0${n}-01T00:00:00Z`,
): InvestmentLotDto => ({
    lotId: `lot-${n}`,
    acquiredAt,
    quantity: qty,
    unitCost,
});

describe('computeFifoPlan', () => {
    it('returns empty plan when sharesToDispose is 0', () => {
        const plan = computeFifoPlan([mkLot(1, 100, 10)], 0);
        expect(plan.consumed).toEqual([]);
        expect(plan.totalBasis).toBe(0);
        expect(plan.shortfall).toBe(0);
    });

    it('consumes a single lot partially', () => {
        const plan = computeFifoPlan([mkLot(1, 100, 10)], 30);
        expect(plan.consumed).toHaveLength(1);
        expect(plan.consumed[0]!.qtyConsumed).toBe(30);
        expect(plan.consumed[0]!.basisClosed).toBe(300);
        expect(plan.totalBasis).toBe(300);
        expect(plan.shortfall).toBe(0);
    });

    it('walks lots FIFO across multiple', () => {
        // First lot 50 @ $10, second 50 @ $20. Sell 75.
        // → close all of lot 1 ($500) + 25 of lot 2 ($500).
        const plan = computeFifoPlan(
            [mkLot(1, 50, 10), mkLot(2, 50, 20)],
            75,
        );
        expect(plan.consumed).toHaveLength(2);
        expect(plan.consumed[0]!.qtyConsumed).toBe(50);
        expect(plan.consumed[0]!.basisClosed).toBe(500);
        expect(plan.consumed[1]!.qtyConsumed).toBe(25);
        expect(plan.consumed[1]!.basisClosed).toBe(500);
        expect(plan.totalBasis).toBe(1000);
        expect(plan.shortfall).toBe(0);
    });

    it('reports shortfall when shares exceed total open quantity', () => {
        const plan = computeFifoPlan(
            [mkLot(1, 50, 10), mkLot(2, 50, 20)],
            120,
        );
        // All 100 shares consumed; 20 unmet.
        expect(plan.consumed).toHaveLength(2);
        expect(plan.consumed.reduce((s, c) => s + c.qtyConsumed, 0)).toBe(100);
        expect(plan.totalBasis).toBe(50 * 10 + 50 * 20);
        expect(plan.shortfall).toBe(20);
    });

    it('handles an empty lots list as full shortfall', () => {
        const plan = computeFifoPlan([], 10);
        expect(plan.consumed).toEqual([]);
        expect(plan.totalBasis).toBe(0);
        expect(plan.shortfall).toBe(10);
    });

    it('stops walking once shortfall is zero (extra lots ignored)', () => {
        const plan = computeFifoPlan(
            [mkLot(1, 100, 10), mkLot(2, 100, 20), mkLot(3, 100, 30)],
            50,
        );
        // Only first lot touched.
        expect(plan.consumed).toHaveLength(1);
        expect(plan.consumed[0]!.lotId).toBe('lot-1');
    });
});
