// Ledger-level endpoints.

import type { BalanceHealthReport, LedgerSummary } from '../types/ledger';
import { request } from './_request';

/**
 * GET /api/ledgers — every ledger the authenticated user has any
 * grant on. Backed by the `user_visible_ledgers` view server-side;
 * RLS filters by `app.user_id` so the response only ever contains
 * ledgers the caller can actually access.
 */
export function fetchVisibleLedgers(): Promise<LedgerSummary[]> {
    return request<LedgerSummary[]>('/api/ledgers');
}

/**
 * POST /api/ledgers — create a ledger; the caller becomes its owner.
 * `seedDefaultCategories` (ADR-0071 D5, default true) seeds a starter category
 * tree so the ledger is usable immediately; pass false to start blank.
 */
export function createLedger(
    name: string,
    seedDefaultCategories = true,
): Promise<LedgerSummary> {
    return request<LedgerSummary>('/api/ledgers', {
        method: 'POST',
        body: { name, seedDefaultCategories },
    });
}

/** PATCH /api/ledgers/{id} — rename (owner-only server-side). */
export function renameLedger(id: string, name: string): Promise<void> {
    return request<void>(`/api/ledgers/${encodeURIComponent(id)}`, {
        method: 'PATCH',
        body: { name },
    });
}

/**
 * DELETE /api/ledgers/{id} — permanently delete a ledger and its entire
 * footprint (owner-only server-side). Irreversible.
 */
export function deleteLedger(id: string): Promise<void> {
    return request<void>(`/api/ledgers/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

/**
 * POST /api/ledgers/{id}/balances/health — verify-and-heal sweep
 * over every txn_header_account_balances row in the ledger. Returns
 * which rows were drifted (and have now been healed by the
 * side-effecting recompute). UI surface: the "Verify balances"
 * button on the feed-connections page.
 */
export function verifyBalanceHealth(
    ledgerId: string,
): Promise<BalanceHealthReport> {
    return request<BalanceHealthReport>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/health`,
        { method: 'POST' },
    );
}
