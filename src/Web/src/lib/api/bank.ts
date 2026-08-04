// Bank-domain transaction endpoints — manual create + PATCH on
// the `/transactions` endpoint, plus the bank-feed-only editor
// recall panels (similar-payees, merge-candidates). Universal
// recon-status + delete live in [./register.ts]; investment
// endpoints in [./investment.ts] (lands with A4.c.3).

import type {
    CreateTransactionRequest,
    MergeCandidateDto,
    PatchTransactionRequest,
    SimilarPayeeDto,
} from '../types/bank';
import type { RegisterEntry } from '../types/register';
import { request } from './_request';

/**
 * POST /api/ledgers/{ledgerId}/transactions — create a manual
 * bank-shape transaction with one or more postings (ADR-0025).
 * Returns `{ headerId }`.
 *
 * 422 codes:
 *   * `transaction-postings-empty`             — body had no postings.
 *   * `transaction-account-required`           — sourceAccountId missing.
 *   * `transaction-posted-at-required`         — postedAt missing.
 *   * `transaction-posting-self`               — a posting's counterparty == source.
 *   * `transaction-posting-counterparty-required` — a posting has no counterparty.
 *   * `ledger-not-visible`                     — caller has no grant on this ledger.
 *   * `account-not-in-ledger`                  — a supplied account belongs to another ledger.
 */
export function createTransaction(
    ledgerId: string,
    body: CreateTransactionRequest,
): Promise<{ headerId: string }> {
    return request<{ headerId: string }>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions`,
        { method: 'POST', body },
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/transactions/{headerId} (ADR-0025).
 * Apply header overrides and/or a postings reshape in one atomic
 * Postgres transaction. `null` (or omitted) on a header field
 * means "leave the override column alone"; `postings` omitted
 * means "don't touch the postings list."
 *
 * When `accountId` is supplied, the response is the freshly-resolved
 * `RegisterEntry` for the saved header on that account — used by the
 * SPA to patch the register in place (via `mutateEntries`) without
 * a full window refresh. When omitted, the endpoint replies 204.
 *
 * 422 codes:
 *   * `transaction-patch-empty`                — body has neither header fields nor postings.
 *   * `transaction-postings-empty`             — postings supplied with 0 items.
 *   * `transaction-posting-self` / `-counterparty-required`
 *                                              — per-posting validation, mirrors POST.
 *   * `ledger-not-visible`                     — caller has no grant.
 *   * `transaction-not-in-ledger`              — supplied headerId is in another ledger.
 *   * `transaction-posting-leg-not-in-header`  — a postings.items[].legId doesn't match.
 *   * `transaction-source-account-mismatch`    — supplied sourceAccountId has no legs on this header.
 *   * `account-not-in-ledger`                  — a supplied counterparty is in another ledger.
 */
export function patchTransaction(
    ledgerId: string,
    headerId: string,
    body: PatchTransactionRequest,
    accountId?: string,
): Promise<RegisterEntry | null> {
    const url = accountId
        ? `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}?account_id=${encodeURIComponent(accountId)}`
        : `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}`;
    return request<RegisterEntry | null>(url, { method: 'PATCH', body });
}

/**
 * GET /api/ledgers/{ledgerId}/transactions/{headerId}/similar-payees
 * — slice 2c.6c Tier 1 recall. Bank-feed editor concern: server
 * reads the row's raw bank payee and returns up to 5 prior
 * approved `(payee, category)` pairs the user previously chose
 * for the same online payee. Empty array on non-bank-feed rows,
 * missing payees, or no matches — the editor hides the panel in
 * those cases. Cross-ledger or unknown header ids also return
 * empty (intentionally indistinguishable from "no suggestions"
 * for probe safety).
 */
export function fetchSimilarPayees(
    ledgerId: string,
    headerId: string,
): Promise<SimilarPayeeDto[]> {
    return request<SimilarPayeeDto[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}/similar-payees`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/transactions/{headerId}/merge-candidates
 * — slice 2c.6d. Bank-feed editor concern: returns up to 5 manual
 * rows whose aggregated source-account amount matches this
 * header's, within ±7 days. The editor's "Possible matches" panel
 * renders them as chips; clicking pre-fills the editor and arms
 * `mergeFromHeaderId` on the next PATCH. Empty array for
 * non-existent / cross-ledger targets, or when no manual rows
 * match the (account, amount, date) filter.
 */
export function fetchMergeCandidates(
    ledgerId: string,
    headerId: string,
): Promise<MergeCandidateDto[]> {
    return request<MergeCandidateDto[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}/merge-candidates`,
    );
}
