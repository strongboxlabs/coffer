// Ledger-level endpoints.

import type {
    LedgerConsistencyReport,
    ProjectionConsistency,
    BalanceHealthReport,
    LedgerSummary,
} from '../types/ledger';
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
 * GET /api/ledgers/{id}/balances/health — READ-ONLY check of every
 * txn_header_account_balances row against the pure walk (mig 206).
 * Reports which rows disagree and changes nothing.
 *
 * This was a POST that healed as a side effect of checking, because the
 * only implementation of the rules lived inside the recompute's
 * DELETE + INSERT. Asking rewrote the answer — on one ledger, 2,741 rows
 * silently. Checking and repairing are now two deliberate actions.
 */
export function checkBalanceHealth(
    ledgerId: string,
): Promise<BalanceHealthReport> {
    return request<BalanceHealthReport>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/health`,
    );
}

/**
 * POST /api/ledgers/{id}/balances/repair — rebuild every stored running
 * balance from the legs. The user chooses this explicitly, after a check
 * has reported drift; it is the remedy for a writer that mutated legs
 * without invoking the recompute, not something to run speculatively.
 */
export function repairBalances(
    ledgerId: string,
): Promise<BalanceHealthReport> {
    return request<BalanceHealthReport>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/repair`,
        { method: 'POST' },
    );
}

/**
 * GET /api/ledgers/{id}/balances/consistency — READ-ONLY check of every derived
 * projection (balances, holdings, realized gains, posting counts). Writes nothing.
 */
export function checkLedgerConsistency(
    ledgerId: string,
): Promise<LedgerConsistencyReport> {
    return request<LedgerConsistencyReport>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/consistency`,
    );
}

/**
 * POST /api/ledgers/{id}/balances/consistency/{projection}/repair — rebuild one
 * projection, touching only what the check reported. Every projection the report
 * names has a repair, so the UI never surfaces a problem with no way to fix it.
 */
export function repairProjection(
    ledgerId: string,
    projection: string,
): Promise<ProjectionConsistency> {
    return request<ProjectionConsistency>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/consistency/`
        + `${encodeURIComponent(projection)}/repair`,
        { method: 'POST' },
    );
}
