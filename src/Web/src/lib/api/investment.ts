// Investment-domain transaction endpoints (ADR-0029). The
// `/investment-transactions` surface is distinct from the bank
// `/transactions` surface so each domain owns its own validation
// vocabulary + side-effects (FIFO lot consumption, holdings
// recompute, fee handling). Universal recon-status + delete live
// in [./register.ts]; bank create/PATCH in [./bank.ts].

import type {
    DeleteTransactionResponse,
    RegisterEntry,
} from '../types/register';
import type {
    CreateInvestmentTransactionRequest,
    CreateInvestmentTransactionResponse,
    InvestmentLotDto,
    InvestmentMergeCandidate,
    PatchInvestmentTransactionRequest,
} from '../types/investment';
import { request } from './_request';

/**
 * POST /api/ledgers/{ledgerId}/investment-transactions — create
 * a new investment txn per the action × field matrix in ADR-0029.
 *
 * Server validates the request against the action's required-field
 * set and returns 422 with a structured code on rejection
 * (e.g. `investment-txn-shares-required`,
 * `investment-txn-account-not-investment`). The editor surfaces
 * the code to map to a per-field error message.
 */
export function createInvestmentTransaction(
    ledgerId: string,
    body: CreateInvestmentTransactionRequest,
): Promise<CreateInvestmentTransactionResponse> {
    return request<CreateInvestmentTransactionResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/investment-transactions`,
        { method: 'POST', body },
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/investment-transactions/{headerId} —
 * full postings reshape per ADR-0025. The supplied body IS the new
 * state of the world; null on a field means null in the saved state.
 *
 * When <c>accountId</c> is supplied, the response is the freshly-
 * resolved <c>RegisterEntry</c> for the saved header on that account
 * — used by the SPA to patch the register window in place (via
 * <c>mutateEntries</c>) without losing scroll position or row order.
 * When omitted, the endpoint replies 204.
 *
 * Same 422 vocabulary as POST plus `transaction-not-in-ledger`
 * (header doesn't exist or wrong ledger) and
 * `investment-txn-header-not-investment` (header is a bank-shape
 * row — go to /transactions instead).
 */
export function patchInvestmentTransaction(
    ledgerId: string,
    headerId: string,
    body: PatchInvestmentTransactionRequest,
    accountId?: string,
): Promise<RegisterEntry | null> {
    const url = accountId
        ? `/api/ledgers/${encodeURIComponent(ledgerId)}/investment-transactions/${encodeURIComponent(headerId)}?account_id=${encodeURIComponent(accountId)}`
        : `/api/ledgers/${encodeURIComponent(ledgerId)}/investment-transactions/${encodeURIComponent(headerId)}`;
    return request<RegisterEntry | null>(url, { method: 'PATCH', body });
}

/**
 * DELETE /api/ledgers/{ledgerId}/investment-transactions/{headerId} —
 * hard-delete manual rows / soft-hide imported rows (mirrors the
 * bank-side policy; load-bearing for the queued SimpleFIN
 * brokerage feed per ADR-0029).
 *
 * Response carries `{ kind: 'hard-deleted' | 'soft-hidden' }` so
 * the editor can show the right confirmation toast.
 */
export function deleteInvestmentTransaction(
    ledgerId: string,
    headerId: string,
): Promise<DeleteTransactionResponse> {
    return request<DeleteTransactionResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/investment-transactions/${encodeURIComponent(headerId)}`,
        { method: 'DELETE' },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/investment-transactions/{headerId}/merge-candidates —
 * settled investment rows the edited (fresh, needs-review) row could fold
 * into: same brokerage + security, matching principal (or quantity), within
 * ±7 effective days. Drives the editor's "possible matches" panel. Empty
 * array when the row isn't merge-eligible (mirrors the bank client).
 */
export function fetchInvestmentMergeCandidates(
    ledgerId: string,
    headerId: string,
): Promise<InvestmentMergeCandidate[]> {
    return request<InvestmentMergeCandidate[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/investment-transactions/${encodeURIComponent(headerId)}/merge-candidates`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/accounts/{accountId}/securities/{securityId}/lots —
 * open lots on a (brokerage, security) ordered ASC by `acquired_at`
 * (FIFO consumption order). Drives the editor's Sell / SellX
 * preview popover (A4.c.4).
 *
 * Listed here for shape completeness; the popover is its own
 * follow-up slice and isn't consumed by the editor today.
 */
export function fetchOpenLots(
    ledgerId: string,
    accountId: string,
    securityId: string,
): Promise<InvestmentLotDto[]> {
    return request<InvestmentLotDto[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/securities/${encodeURIComponent(securityId)}/lots`,
    );
}
