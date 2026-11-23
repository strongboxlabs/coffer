import type { AllocationDimension, AllocationResult, ReturnsResult } from '../types/portfolio';
import { request } from './_request';
import { withToday } from './_today';

/**
 * GET /reports/returns — the ledger's returns over `[from, today]`, ending at the end
 * of the user's today as the register counts it, the instant the overview values
 * investments at. `from` is a local calendar date at 00:00Z, or absent for
 * since-inception.
 */
export function fetchReturns(ledgerId: string, from?: string): Promise<ReturnsResult> {
    const params = new URLSearchParams();
    if (from) params.set('from', from);
    withToday(params);
    return request<ReturnsResult>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/returns?${params.toString()}`,
    );
}

/** GET /reports/allocation — the portfolio by `dimension`, valued at the same instant
 *  as {@link fetchReturns}. */
export function fetchAllocation(
    ledgerId: string,
    dimension: AllocationDimension,
): Promise<AllocationResult> {
    const params = withToday(new URLSearchParams({ dimension }));
    return request<AllocationResult>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/allocation?${params.toString()}`,
    );
}
