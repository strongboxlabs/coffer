// Account + account-group endpoints, plus the per-account PATCH
// surfaces that target an account resource (feed-mapping,
// sync-from-date, trade-commission). Feed-connection management
// lives in [./feed.ts].

import type {
    AccountSummary,
    AccountDetail,
    AccountGroupSummary,
    CreateAccountGroupRequest,
    PatchAccountGroupRequest,
    PatchAccountFeedMappingRequest,
    PatchAccountSyncFromDateRequest,
    FrequentCounterpartiesResponse,
    CreateAccountRequest,
    UpdateAccountRequest,
    LoanPaymentPreviewRequest,
    LoanPaymentPreviewResponse,
    SetupPaymentReminderRequest,
} from '../types/account';
import { request } from './_request';

/**
 * GET /api/ledgers/{id}/accounts — every active account in one
 * ledger, sorted by name server-side. With `includeInactive: true`,
 * inactive (is_active=false) accounts are also returned, marked via
 * the AccountSummary.isActive flag so the SPA can render them
 * differently (greyed / strikethrough). Default keeps existing call
 * sites (pickers, sidebar default) showing active-only.
 *
 * Returns 422 `ledger-not-visible` (via the API's app-layer gate,
 * also RLS-enforced) if the caller has no grant on the supplied
 * ledger; the call site surfaces that via ApiError.
 */
export function fetchAccounts(
    ledgerId: string,
    options?: { includeInactive?: boolean },
): Promise<AccountSummary[]> {
    const query = options?.includeInactive ? '?includeInactive=true' : '';
    return request<AccountSummary[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts${query}`,
    );
}

/**
 * POST /api/ledgers/{ledgerId}/accounts — create an account of any type
 * (ADR-0050). Returns the created {@link AccountSummary} (201). An
 * `investment` account also materializes its system Holdings sibling
 * server-side. 422 codes: `account-name-required`, `account-type-invalid`,
 * `account-category-kind-invalid`, `account-currency-invalid`,
 * `account-parent-invalid`.
 */
export function createAccount(
    ledgerId: string,
    body: CreateAccountRequest,
): Promise<AccountSummary> {
    return request<AccountSummary>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts`,
        { method: 'POST', body },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/accounts/{accountId} — the full editable shape
 * of one account (ADR-0050), incl. the metadata the list omits (account /
 * routing number, URL, notes). Used by the editor's edit mode to prefill.
 * 422 `account-not-in-ledger` if the id isn't in this ledger.
 */
export function fetchAccount(
    ledgerId: string,
    accountId: string,
): Promise<AccountDetail> {
    return request<AccountDetail>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}`,
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/accounts/{accountId} — edit an account's
 * general attributes (ADR-0050). Partial; `accountType` is immutable.
 * 204 on success. 422 codes: `account-not-in-ledger`, `account-is-system`,
 * `account-patch-empty`, `account-name-required`,
 * `account-category-kind-invalid`, `account-currency-invalid`.
 */
export function updateAccount(
    ledgerId: string,
    accountId: string,
    body: UpdateAccountRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}`,
        { method: 'PATCH', body },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/accounts/loan-payment-preview — ADR-0050
 * slice 3. Stateless amortization preview for the editor's Loan Terms block;
 * the C# `LoanAmortization` service is the single source of truth. Returns a
 * zero preview when the terms are incomplete.
 */
export function loanPaymentPreview(
    ledgerId: string,
    body: LoanPaymentPreviewRequest,
): Promise<LoanPaymentPreviewResponse> {
    return request<LoanPaymentPreviewResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/loan-payment-preview`,
        { method: 'POST', body },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/accounts/{accountId}/payment-reminder — set up
 * the managed payment reminder for a loan account (ADR-0050 ext). The split is
 * derived from the loan terms + balance; the cadence from payments-per-year.
 * 422 codes: `payment-reminder-exists`, `payment-reminder-terms-missing`,
 * `payment-reminder-source-invalid`.
 */
export function setupPaymentReminder(
    ledgerId: string,
    accountId: string,
    body: SetupPaymentReminderRequest,
): Promise<{ reminderId: string }> {
    return request<{ reminderId: string }>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/payment-reminder`,
        { method: 'POST', body },
    );
}

// ----------------------------------------------------------------------
// Account groups (sidebar tabs, migration 033)
// ----------------------------------------------------------------------

/**
 * GET /api/ledgers/{ledgerId}/account-groups — list the calling
 * user's sidebar tabs in this ledger, with each group's member
 * account ids inline. The implicit "All" tab is rendered client-
 * side and never appears in this list.
 */
export function fetchAccountGroups(
    ledgerId: string,
): Promise<AccountGroupSummary[]> {
    return request<AccountGroupSummary[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-groups`,
    );
}

/**
 * POST /api/ledgers/{ledgerId}/account-groups — create a new tab.
 *
 * 422 codes:
 *   * `account-group-name-required` — name blank.
 *   * `account-group-name-conflict` — duplicate name (case-insensitive).
 */
export function createAccountGroup(
    ledgerId: string,
    body: CreateAccountGroupRequest,
): Promise<{ id: string }> {
    return request<{ id: string }>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-groups`,
        { method: 'POST', body },
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/account-groups/{groupId} — rename
 * a tab.
 *
 * 422 codes: `account-group-name-required`, `account-group-name-conflict`,
 * `account-group-not-found`.
 */
export function patchAccountGroup(
    ledgerId: string,
    groupId: string,
    body: PatchAccountGroupRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-groups/${encodeURIComponent(groupId)}`,
        { method: 'PATCH', body },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/account-groups/{groupId} — drop
 * a tab. Membership rows cascade.
 */
export function deleteAccountGroup(
    ledgerId: string,
    groupId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-groups/${encodeURIComponent(groupId)}`,
        { method: 'DELETE' },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/account-groups/{groupId}/members/{accountId}
 * — add an account to a tab. Idempotent (re-adding an existing
 * membership succeeds with 204).
 */
export function addAccountGroupMember(
    ledgerId: string,
    groupId: string,
    accountId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-groups/${encodeURIComponent(groupId)}/members/${encodeURIComponent(accountId)}`,
        { method: 'POST' },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/account-groups/{groupId}/members/{accountId}
 * — remove an account from a tab. Idempotent.
 */
export function removeAccountGroupMember(
    ledgerId: string,
    groupId: string,
    accountId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-groups/${encodeURIComponent(groupId)}/members/${encodeURIComponent(accountId)}`,
        { method: 'DELETE' },
    );
}

// ----------------------------------------------------------------------
// Per-account PATCH surfaces (feed-mapping, sync-from-date, trade-commission)
// ----------------------------------------------------------------------

/** PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping —
 *  bind a Coffer account to one SimpleFIN account on a connection.
 *  Idempotent. Used by the mapping wizard after a Sync surfaces
 *  unmapped SimpleFIN accounts.
 *
 *  422 codes:
 *   * `feed-mapping-target-required` — body missing fields.
 *   * `account-not-in-ledger` — accountId is in a different ledger.
 *   * `feed-mapping-connection-mismatch` — connection is in a
 *     different ledger (cross-ledger bind attempt). */
export function mapAccountToFeed(
    ledgerId: string,
    accountId: string,
    body: PatchAccountFeedMappingRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/feed-mapping`,
        { method: 'PATCH', body },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping
 * — slice 2c.4. Clears the binding (NULLs `feed_connection_id` +
 * `external_id`) so the account drops out of sync-time mapping
 * lookups. Idempotent. 204 on success.
 */
export function unbindAccountFromFeed(
    ledgerId: string,
    accountId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/feed-mapping`,
        { method: 'DELETE' },
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/sync-from-date
 * — slice 2c.5. Set or clear the per-account SimpleFIN sync
 * watermark. Body `{ syncFromDate: "2026-02-20" }` makes the next
 * sync request transactions from that date forward (with the same
 * 7-day overlap the auto-watermark path applies). Body
 * `{ syncFromDate: null }` clears the watermark — next sync asks
 * for the full 90-day window.
 *
 * Possible 422s:
 *   * `account-not-in-ledger`
 *   * `account-not-bound-to-feed` — not mapped on the bank-feeds page yet
 *   * `sync-from-date-in-future` — supplied date is later than now
 */
export function setAccountSyncFromDate(
    ledgerId: string,
    accountId: string,
    body: PatchAccountSyncFromDateRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/sync-from-date`,
        { method: 'PATCH', body },
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/trade-commission
 * — slice A4.a. Flip the per-brokerage "treat in-transaction fees
 * as cost basis" flag. Server runs `recompute_holdings_cost_basis`
 * in the same transaction so the response returns with holdings
 * and lots already converged.
 *
 * Possible 422s:
 *   * `account-not-in-ledger`
 *   * `account-not-investment` — flag only meaningful on brokerages
 */
export function setAccountTradeCommission(
    ledgerId: string,
    accountId: string,
    enabled: boolean,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/trade-commission`,
        { method: 'PATCH', body: { enabled } },
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/active —
 * inactive-account lifecycle slice. Flips the per-account
 * `is_active` flag. Symmetric: active=true reactivates a previously
 * deactivated account.
 *
 * Server doesn't refuse a deactivation when the account still has
 * positions or non-zero balance — the SPA owns that confirm-dialog
 * flow (locked decision in follow-ups.md).
 *
 * Possible 422s:
 *   * `account-not-in-ledger`
 *   * `account-is-system` — Holdings siblings + Uncategorized
 *     cannot be deactivated.
 */
export function setAccountActive(
    ledgerId: string,
    accountId: string,
    active: boolean,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/active`,
        { method: 'PATCH', body: { active } },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/accounts/{accountId}/frequent-counterparties
 * — ADR-0043. The source account's most-used counterparty accounts +
 * categories (derived from history), to pin at the top of the
 * account/category picker.
 */
export function fetchFrequentCounterparties(
    ledgerId: string,
    accountId: string,
): Promise<FrequentCounterpartiesResponse> {
    return request<FrequentCounterpartiesResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/frequent-counterparties`,
    );
}
