// Account + account-group API response types.
// Also carries the per-account PATCH bodies that target an account
// resource (feed-mapping, sync-from-date); the related feed-connection
// summary lives in [./feed.ts].

/**
 * Mirror of API `Coffer.Api.Contracts.AccountSummary`. Unified account
 * shape: real accounts (`bank` / `credit_card` / `investment` /
 * `asset` / `liability` / `loan`) and budget categories (`category`,
 * with `categoryKind` set to `"income"` or `"expense"`) live in the
 * same table (ADR-0002, ADR-0017). The discriminator is
 * `accountType`.
 */
export interface AccountSummary {
    id: string;
    ledgerId: string;
    parentId: string | null;
    name: string;
    accountType: string;
    categoryKind: string | null;
    currencyCode: string;
    isActive: boolean;
    isSystem: boolean;
    /** Slice 2c.2: non-null on accounts already bound to a
     *  SimpleFIN connection. The feed-mapping wizard filters
     *  these out so the user can't double-map. */
    feedConnectionId: string | null;
    /** Slice 2c.2: aggregated count of bank-feed `txn_headers`
     *  rows touching this account where `needs_review = true`.
     *  Drives the sidebar review-dot (present-vs-absent per
     *  ADR-0021, not rendered as a number). 0 for categories
     *  + system rows. */
    needsReviewCount: number;
    /** Slice A1.d: for investment brokerage accounts, the id of
     *  the system-managed Holdings sibling sub-account (ADR-0019).
     *  The brokerage register suppresses this id from counterparty
     *  chips so the user never sees "Account Holdings" against
     *  their own Buys / Sells. Null on every non-investment account
     *  and on the Holdings sibling itself. */
    holdingsAccountId: string | null;
    /** Slice A4.a / migration 056: on a brokerage (investment)
     *  account, when TRUE the recompute function adds fee-marked
     *  postings to cost basis. The DB CHECK constraint forces
     *  FALSE on every non-investment account. Drives the
     *  "Treat in-transaction fees as cost basis" toggle in the
     *  account settings dialog. */
    isTradeCommission: boolean;
    /** ADR-0050: institution label (nullable). Surfaced so the account
     *  editor can prefill + edit it. The API always sends the key
     *  (value null on categories / accounts with none recorded);
     *  optional here only so existing test fixtures need not change. */
    institutionName?: string | null;
}

/** Editable amortization terms (ADR-0050 slice 3), mirror of API
 *  `LoanTermsDto`. Carried on a loan account's create / edit / detail. Numbers
 *  are JSON numbers; dates are ISO `YYYY-MM-DD` strings. */
export interface LoanTermsInput {
    originalPrincipal: number;
    /** Annual rate as a percent, e.g. 3.65. */
    annualInterestRate: number;
    points: number;
    paymentCount: number;
    paymentsPerYear: number;
    firstPaymentDate: string | null;
    escrowAmount: number;
    interestAccountId: string | null;
    escrowAccountId: string | null;
    paymentIsComputed: boolean;
    fixedPayment: number | null;
}

/** Body of `POST /api/ledgers/{ledgerId}/accounts` (ADR-0050). Create an
 *  account of any type; `categoryKind` is required iff
 *  `accountType === 'category'`, and `parentId` is category-only. */
export interface CreateAccountRequest {
    name: string;
    accountType: string;
    categoryKind?: string | null;
    parentId?: string | null;
    /** Defaults to USD server-side when omitted. */
    currencyCode?: string | null;
    institutionName?: string | null;
    accountNumber?: string | null;
    routingNumber?: string | null;
    accountUrl?: string | null;
    notes?: string | null;
    isActive?: boolean;
    /** Starting balance (ADR-0050 slice 3); must be 0 for categories. */
    openingBalance?: number;
    /** Account "Start Date" (ISO date) — optional. */
    openedOn?: string | null;
    /** REQUIRED when `accountType === 'loan'`; must be omitted otherwise. */
    loanTerms?: LoanTermsInput | null;
}

/** Body of `PATCH /api/ledgers/{ledgerId}/accounts/{accountId}` (ADR-0050).
 *  Partial — omit a field to leave it unchanged. Text fields: an empty string
 *  clears (→ null). `accountType` is immutable, so it is intentionally absent. */
export interface UpdateAccountRequest {
    name?: string;
    currencyCode?: string;
    institutionName?: string;
    accountNumber?: string;
    routingNumber?: string;
    accountUrl?: string;
    notes?: string;
    isActive?: boolean;
    /** Reclassify a category (income ⇄ expense); ignored on other types. */
    categoryKind?: string;
    /** Starting balance (omit = unchanged); categories must keep 0. */
    openingBalance?: number;
    /** Start date (omit = unchanged); use `clearOpenedOn` to null it. */
    openedOn?: string | null;
    clearOpenedOn?: boolean;
    /** Full loan terms (omit = unchanged); only on loan accounts. */
    loanTerms?: LoanTermsInput | null;
    /** Tax treatment (ADR-0066): 'taxable' | 'tax_deferred' | 'tax_free' |
     *  'other'. Omit = unchanged; '' clears (→ null). */
    taxStatus?: string;
}

/** Full editable shape of one account — `GET /api/ledgers/{lid}/accounts/{aid}`
 *  (ADR-0050). Carries the metadata {@link AccountSummary} omits so the editor
 *  can prefill on edit. */
export interface AccountDetail {
    id: string;
    ledgerId: string;
    parentId: string | null;
    name: string;
    accountType: string;
    categoryKind: string | null;
    currencyCode: string;
    isActive: boolean;
    isSystem: boolean;
    institutionName: string | null;
    accountNumber: string | null;
    routingNumber: string | null;
    accountUrl: string | null;
    notes: string | null;
    openingBalance: number;
    openedOn: string | null;
    /** Tax treatment (ADR-0066); null = unknown. */
    taxStatus: string | null;
    /** Present only on loan accounts that have terms; null otherwise. */
    loanTerms: LoanTermsInput | null;
    /** Present only on loan accounts with a managed payment reminder set up
     *  (the scheduled auto-payment); null otherwise. */
    managedReminder: ManagedReminder | null;
}

/** A loan account's managed payment reminder (ADR-0050 ext) — the scheduled
 *  auto-payment whose split is computed from the loan terms. The editor shows
 *  its cadence + next due + a link. */
export interface ManagedReminder {
    reminderId: string;
    rrule: string | null;
    nextDue: string | null;
}

/** Body of `POST /api/ledgers/{ledgerId}/accounts/{accountId}/payment-reminder`
 *  — set up a loan's managed payment reminder. No amounts (the split is derived
 *  from the loan terms); cadence comes from payments-per-year on `startDate`. */
export interface SetupPaymentReminderRequest {
    /** The bank account the payment is drawn from. */
    sourceAccountId: string;
    /** yyyy-MM-dd. */
    startDate: string;
}

/** Body of `POST /api/ledgers/{ledgerId}/accounts/loan-payment-preview`
 *  (ADR-0050 slice 3) — stateless amortization preview for the editor. */
export interface LoanPaymentPreviewRequest {
    originalPrincipal: number;
    annualInterestRate: number;
    paymentCount: number;
    paymentsPerYear: number;
    escrowAmount: number;
    paymentIsComputed: boolean;
    fixedPayment: number | null;
}

/** Response of the payment-preview endpoint. `periodicPayment` is the P&I
 *  portion; `totalPayment` adds escrow. Zero when the terms are incomplete. */
export interface LoanPaymentPreviewResponse {
    periodicPayment: number;
    escrowAmount: number;
    totalPayment: number;
}

/**
 * Mirror of API `Coffer.Api.Contracts.AccountGroupSummary` (migration
 * 033). One user-curated sidebar tab; the implicit "All" tab is
 * rendered client-side and is never returned from the API.
 */
export interface AccountGroupSummary {
    id: string;
    name: string;
    sortOrder: number;
    memberAccountIds: readonly string[];
}

/** Body of `POST /api/ledgers/{ledgerId}/account-groups`. */
export interface CreateAccountGroupRequest {
    name: string;
}

/** Body of `PATCH /api/ledgers/{ledgerId}/account-groups/{groupId}`.
 *  Rename only at v1 — `sort_order` reorder is a deferred follow-up. */
export interface PatchAccountGroupRequest {
    name?: string;
}

/** Body of
 *  `PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/sync-from-date`
 *  (slice 2c.5). `null` clears the watermark — next sync asks for
 *  the full 90-day window. */
export interface PatchAccountSyncFromDateRequest {
    /** ISO-8601 UTC date string, or null to clear. Must not be in
     *  the future — server returns 422 `sync-from-date-in-future`
     *  otherwise. */
    syncFromDate: string | null;
}

/** Body of
 *  `PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping`. */
export interface PatchAccountFeedMappingRequest {
    feedConnectionId: string;
    simpleFinAccountId: string;
}

/** One ranked counterparty (ADR-0043). `useCount` = how many of the
 *  source account's transactions posted against this counterparty. */
export interface FrequentCounterparty {
    id: string;
    name: string;
    accountType: string;
    categoryKind: string | null;
    useCount: number;
}

/** Response of
 *  `GET /api/ledgers/{lid}/accounts/{aid}/frequent-counterparties` —
 *  the source account's most-used counterparties, split by domain so
 *  the picker can pin frequent accounts and categories separately. */
export interface FrequentCounterpartiesResponse {
    accounts: FrequentCounterparty[];
    categories: FrequentCounterparty[];
}
