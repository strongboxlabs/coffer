// Ledger-level endpoints.

import type {
    LedgerConsistencyReport,
    LedgerConsistencyState,
    ProjectionRepairResult,
    LedgerSummary,
    UpdateLedgerRequest,
} from '../types/ledger';
import { request } from './_request';
import { todayQuery } from './_today';

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
 * `currencyCode` is the reporting currency (ADR-0114 D7); omitted or blank, the
 * server takes USD. Anything not a 3-letter code is 422 `ledger-currency-invalid`.
 */
export function createLedger(
    name: string,
    seedDefaultCategories = true,
    currencyCode?: string,
): Promise<LedgerSummary> {
    return request<LedgerSummary>('/api/ledgers', {
        method: 'POST',
        body: currencyCode === undefined
            ? { name, seedDefaultCategories }
            : { name, seedDefaultCategories, currencyCode },
    });
}

/** PATCH /api/ledgers/{id} — rename (owner-only server-side). */
export function renameLedger(id: string, name: string): Promise<void> {
    return updateLedger(id, { name });
}

/**
 * PATCH /api/ledgers/{id} — rename, change the reporting currency, or both. Owner
 * only. 204. A currency change starts a background backfill of the rates it needs
 * (ADR-0114 D11).
 *
 * 422 codes: `ledger-name-required` (neither field, or a blank name),
 * `ledger-currency-invalid`, `ledger-not-visible`, `ledger-not-owner`,
 * `ledger-write-busy` (the currency is applied first, so a busy refusal changes
 * nothing, the name included).
 */
export function updateLedger(id: string, body: UpdateLedgerRequest): Promise<void> {
    return request<void>(`/api/ledgers/${encodeURIComponent(id)}`, {
        method: 'PATCH',
        body,
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
 * POST /api/ledgers/{id}/balances/consistency — check every derived projection
 * (balances, holdings, realized gains, posting counts, prices from trades, stored
 * memo-and-tags notes, holdings currency, the currency of fetched quotes). It changes no figure and heals nothing; it
 * records its verdict as the scheduled check does, which is what the Overview's
 * Problems widget and its "checked" text read. The unbacked-price advisory's holding
 * value is a current value, so the user's local date is sent as the register sends it.
 */
export function checkLedgerConsistency(
    ledgerId: string,
): Promise<LedgerConsistencyReport> {
    return request<LedgerConsistencyReport>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/consistency${todayQuery()}`,
        { method: 'POST' },
    );
}

/**
 * GET /api/ledgers/{id}/balances/consistency/state — the last check's stored verdict,
 * without running a check. A ledger never checked reads as no verdict.
 */
export function fetchConsistencyState(ledgerId: string): Promise<LedgerConsistencyState> {
    return request<LedgerConsistencyState>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/consistency/state`,
    );
}

/**
 * POST /api/ledgers/{id}/balances/consistency/{projection}/repair — rebuild one
 * projection, touching only what the check reported. Every projection the report
 * names has a repair, so the UI never surfaces a problem with no way to fix it.
 * Answers what it rewrote and every projection checked again after it, so the
 * user's local date goes with it, as with the check.
 *
 * The `quote_currency` repair asks Yahoo, so it is refused with 422
 * `consistency-repair-needs-yahoo` while the repairing user's Yahoo opt-in is off,
 * and nothing changes. What it could not repair comes back in `notRepaired`.
 */
export function repairProjection(
    ledgerId: string,
    projection: string,
): Promise<ProjectionRepairResult> {
    return request<ProjectionRepairResult>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/balances/consistency/`
        + `${encodeURIComponent(projection)}/repair${todayQuery()}`,
        { method: 'POST' },
    );
}
