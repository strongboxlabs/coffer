import type { LedgerOverview } from '../types/overview';
import { request } from './_request';

/**
 * Ledger overview aggregate (ADR-0056 slice 1) — net worth, per-account
 * balances grouped by type, and the investment roll-up, in one call.
 */
export function fetchLedgerOverview(ledgerId: string): Promise<LedgerOverview> {
    return request<LedgerOverview>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/overview`,
    );
}
