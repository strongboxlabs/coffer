// Bulk selection endpoints (ADR-0024).

import type {
    BulkDeleteResponse,
    BulkMoveAccountResponse,
    BulkReconStatusResponse,
    BulkUnhideResponse,
    SelectionRequest,
    SelectionSummary,
} from '../types/selection';
import type { ReconStatus } from '../types/register';
import { request } from './_request';

/**
 * POST /api/ledgers/{ledgerId}/transactions/selection-summary — count
 * and account-scoped sum for a selection. Drives the bulk-action
 * footer's "N selected · Σ $X.XX" readout. Single source of truth so
 * the SPA stays correct even when selected rows are evicted from the
 * windowed register.
 *
 * Surfaces the API's 422 codes verbatim via `ApiError`:
 *   * `selection-kind-invalid`           — kind ∉ { explicit, all }.
 *   * `selection-empty`                  — explicit mode with no ids.
 *   * `selection-status-filter-invalid`  — statusFilter ∉ valid set.
 *   * `selection-exclude-too-large`      — excludeIds/headerIds > 10000.
 *   * `ledger-not-visible`               — caller has no grant.
 */
export function fetchSelectionSummary(
    ledgerId: string,
    selection: SelectionRequest,
    signal?: AbortSignal,
): Promise<SelectionSummary> {
    return request<SelectionSummary>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/selection-summary`,
        { method: 'POST', body: selection, signal },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/transactions/bulk-recon-status — apply
 * a single status to the selection's legs on `accountId` in one atomic
 * upsert. Reconciliation is per-account (ADR-0082), so the register passes
 * the account it's showing. Returns the number of rows affected.
 */
export function bulkSetReconStatus(
    ledgerId: string,
    selection: SelectionRequest,
    accountId: string,
    status: ReconStatus,
): Promise<BulkReconStatusResponse> {
    return request<BulkReconStatusResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/bulk-recon-status`,
        { method: 'POST', body: { selection, status, accountId } },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/transactions/bulk-delete — apply the
 * per-row hard-delete vs soft-hide policy across every header in
 * the selection inside one transaction. Caller surfaces a typed-
 * confirmation dialog for large counts (ADR-0024 threshold).
 */
export function bulkDeleteTransactions(
    ledgerId: string,
    selection: SelectionRequest,
): Promise<BulkDeleteResponse> {
    return request<BulkDeleteResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/bulk-delete`,
        { method: 'POST', body: { selection } },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/transactions/bulk-unhide (ADR-0072 D2) —
 * un-hide every (hidden) header in the selection in one transaction,
 * recomputing balances + holdings. The selection carries
 * `statusFilter: 'hidden'` (the Hidden view).
 */
export function bulkUnhideTransactions(
    ledgerId: string,
    selection: SelectionRequest,
): Promise<BulkUnhideResponse> {
    return request<BulkUnhideResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/bulk-unhide`,
        { method: 'POST', body: { selection } },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/transactions/bulk-move-account
 * (ADR-0072 D3) — move the whole (account-scoped) selection to another
 * real account, all-or-nothing. Surfaces the 422 guard codes verbatim:
 *   * `transaction-header-is-investment`         — a selected row is an investment txn.
 *   * `transaction-move-target-invalid`         — target is a category / other ledger.
 *   * `transaction-move-source-required`        — selection isn't account-scoped.
 *   * `transaction-move-target-same-as-source`  — target === source.
 *   * `transaction-move-split-to-investment`    — a split headed to an investment account.
 *   * `transaction-move-collision`              — target already on a selected row.
 */
export function bulkMoveToAccount(
    ledgerId: string,
    selection: SelectionRequest,
    targetAccountId: string,
): Promise<BulkMoveAccountResponse> {
    return request<BulkMoveAccountResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/bulk-move-account`,
        { method: 'POST', body: { selection, targetAccountId } },
    );
}
