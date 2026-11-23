// Account + account-folder endpoints, plus the per-account PATCH
// surfaces that target an account resource (feed-mapping,
// sync-from-date, trade-commission). Feed-connection management
// lives in [./feed.ts].

import type {
    AccountSummary,
    AccountDetail,
    AccountBalance,
    AccountFolder,
    AccountSubtotals,
    BalanceForecast,
    CreateAccountFolderRequest,
    RenameAccountFolderRequest,
    PatchAccountFeedMappingRequest,
    PatchAccountSyncFromDateRequest,
    FrequentCounterpartiesResponse,
    CreateAccountRequest,
    UpdateAccountRequest,
    UpdateAccountResponse,
    LoanPaymentPreviewRequest,
    LoanPaymentPreviewResponse,
    SetupPaymentReminderRequest,
} from '../types/account';
import { request } from './_request';
import { todayQuery, withToday } from './_today';

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
 * `account-parent-invalid`, `category-currency-not-reporting`.
 *
 * `category-currency-not-reporting`: a category create named a currency other than
 * the ledger's reporting currency. A category has no currency of its own (ADR-0109
 * D9); with no `currencyCode` sent, the server stamps the reporting one. The SPA's
 * category create sends none, so it never meets this refusal.
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
 * Answers how many mirrored pairs a currency change left across currencies
 * ({@link UpdateAccountResponse}, ADR-0114 D13). 422 codes: `account-not-in-ledger`,
 * `account-is-system`, `account-patch-empty`, `account-name-required`,
 * `account-category-kind-invalid`, `account-currency-invalid`,
 * `category-currency-not-reporting`, `amount-places-exceed-currency`,
 * `relabel-pair-not-representable`, `relabel-places-exceed-currency`.
 *
 * `relabel-pair-not-representable`: `count` pairs; when any is on a transaction merged
 * into another, which no register shows, `mergedInto` names each transaction it was merged
 * into (`ApiError.mergedInto`, ADR-0114 D13, decided by the owner, 2026-11-18).
 *
 * `category-currency-not-reporting`: a category edit named a currency other than the
 * ledger's reporting currency (ADR-0109 D9). The SPA's category rename sends only the
 * name, and the account editor edits real accounts only, so it never meets this refusal.
 */
export function updateAccount(
    ledgerId: string,
    accountId: string,
    body: UpdateAccountRequest,
): Promise<UpdateAccountResponse> {
    return request<UpdateAccountResponse>(
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

/**
 * GET /api/ledgers/{ledgerId}/accounts/balances — every real account's
 * current balance (FU-052), inactive ones included, for the sidebar. The
 * dashboard's figure: cash, plus holdings market value for an investment
 * account; liabilities negative. Keyed under `['accounts', ledgerId]` so
 * every existing accounts invalidation refreshes it. Sends the user's local
 * date, as the register does, so a balance stops exactly where the register's
 * Scheduled rows begin.
 */
export function fetchAccountBalances(ledgerId: string): Promise<AccountBalance[]> {
    return request<AccountBalance[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/balances${todayQuery()}`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/accounts/subtotals — the sidebar's section and folder
 * totals, each converted into the reporting currency on the server (ADR-0114 D7), so
 * the rail never adds balances. Sends the user's local date, as the balances do.
 */
export function fetchAccountSubtotals(ledgerId: string): Promise<AccountSubtotals> {
    return request<AccountSubtotals>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/subtotals${todayQuery()}`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/accounts/balance-forecast — the balance of every
 * account a reminder is paid from, from the user's local today through `days` ahead
 * (the server clamps it), from the register's committed rows and the reminders not
 * yet posted.
 */
export function fetchBalanceForecast(ledgerId: string, { days }: { days: number }): Promise<BalanceForecast> {
    const params = withToday(new URLSearchParams({ days: String(days) }));
    return request<BalanceForecast>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/balance-forecast?${params.toString()}`,
    );
}

// ----------------------------------------------------------------------
// Account folders (sidebar, migration 258, FU-006)
// ----------------------------------------------------------------------

/**
 * GET /api/ledgers/{ledgerId}/account-folders — every folder in the ledger,
 * flat and ordered by name, each with the accounts directly in it. Any grant
 * reads; every write below is owner or editor only.
 */
export function fetchAccountFolders(ledgerId: string): Promise<AccountFolder[]> {
    return request<AccountFolder[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-folders`,
    );
}

/**
 * POST /api/ledgers/{ledgerId}/account-folders — create a folder, at the top
 * level of its type's section or inside `parentId`. Returns the folder (201).
 * Codes: `account-folder-name-required`, `account-folder-name-too-long`,
 * `account-folder-name-taken`, `account-folder-type-invalid`,
 * `account-folder-type-mismatch`, `account-folder-not-found`.
 */
export function createAccountFolder(
    ledgerId: string,
    body: CreateAccountFolderRequest,
): Promise<AccountFolder> {
    return request<AccountFolder>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-folders`,
        { method: 'POST', body },
    );
}

/** PATCH /api/ledgers/{ledgerId}/account-folders/{folderId} — rename. */
export function renameAccountFolder(
    ledgerId: string,
    folderId: string,
    body: RenameAccountFolderRequest,
): Promise<AccountFolder> {
    return request<AccountFolder>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-folders/${encodeURIComponent(folderId)}`,
        { method: 'PATCH', body },
    );
}

/**
 * PUT /api/ledgers/{ledgerId}/account-folders/{folderId}/parent — move a
 * folder into another of its type, or to the top level with null. 204.
 * Codes: `account-folder-type-mismatch`, `account-folder-cycle`,
 * `account-folder-name-taken`, `account-folder-not-found`.
 */
export function moveAccountFolder(
    ledgerId: string,
    folderId: string,
    parentId: string | null,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-folders/${encodeURIComponent(folderId)}/parent`,
        { method: 'PUT', body: { parentId } },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/account-folders/{folderId} — 204. Deletes
 * nothing else: the folder's sub-folders and accounts move up one level.
 */
export function deleteAccountFolder(ledgerId: string, folderId: string): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/account-folders/${encodeURIComponent(folderId)}`,
        { method: 'DELETE' },
    );
}

/**
 * PUT /api/ledgers/{ledgerId}/accounts/{accountId}/folder — put an account in
 * a folder of its type, or take it out of any with null. 204. Codes:
 * `account-folder-type-mismatch`, `account-folder-not-found`,
 * `account-not-in-ledger`, `account-is-system`, `account-is-category`.
 */
export function setAccountFolder(
    ledgerId: string,
    accountId: string,
    folderId: string | null,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/folder`,
        { method: 'PUT', body: { folderId } },
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
