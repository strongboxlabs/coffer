// Account + account-group API response types.
// Also carries the per-account PATCH bodies that target an account
// resource (feed-mapping, sync-from-date); the related feed-connection
// summary lives in [./feed.ts].

import type { RateUnavailable } from './conversion';

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
    /** The file-import provider this account last imported with (mig 223). Null until
     *  the first such import; preselects the brokerage picker. */
    importProviderKey?: string | null;
}

/** Editable amortization terms (ADR-0050 slice 3), mirror of API
 *  `LoanTermsDto`. Carried on a loan account's create / edit / detail. Numbers
 *  are JSON numbers; dates are ISO `YYYY-MM-DD` strings. */
export interface LoanTermsInput {
    originalPrincipal: number;
    /** Annual rate as a percent, not a fraction: a value of 5 means 5%. */
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
    /** The file-import provider this account last imported with (mig 223). Null until
     *  the first such import; preselects the brokerage picker. */
    importProviderKey?: string | null;
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
    /** A new currency relabels (ADR-0114 D13): every stored figure is kept and reads in
     *  it. Refused when a two-amount pair would land in one currency with magnitudes that
     *  differ (`relabel-pair-not-representable`: `count` pairs, and `mergedInto`, each a
     *  {@link RelabelMergedInto}, when any of them is on a transaction merged into
     *  another, which `ApiError.mergedInto` carries) or stored figures carry more places than
     *  it allows (`relabel-places-exceed-currency`: `count`, how many transactions have a
     *  leg on the account or its Holdings sibling that does not fit, a category's mirror
     *  not counted again and a transaction merged into another left out (ADR-0114 D15);
     *  `accountFigures`, the account-level figures that do not fit,
     *  each `'opening_balance'` or `'loan_terms'` ({@link UnfitAmountKind} values), empty
     *  when none does; `currencyCode` and `places`). The response says how many mirrored
     *  pairs now carry two amounts ({@link UpdateAccountResponse}). */
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

/** Response of `PATCH /api/ledgers/{ledgerId}/accounts/{accountId}`. Mirror of API
 *  `UpdateAccountResponse`. */
export interface UpdateAccountResponse {
    /** How many mirrored pairs a currency change left across currencies, so each now
     *  carries two amounts, equal in magnitude and opposite in sign. Zero when the
     *  currency did not change. */
    becameTwoAmount: number;
}

/** One entry of a `relabel-pair-not-representable` refusal's `mergedInto` (ADR-0114 D13,
 *  decided by the owner, 2026-11-18): a transaction that a refused pair's transaction was
 *  merged into. The merged row is in no register; this one is, so the refusal links to
 *  it. Followed to the end of a chain: a transaction since merged into another names the
 *  row a register shows (settled by Claude, 2026-11-18). Mirror of API
 *  `RelabelMergedInto`. */
export interface RelabelMergedInto {
    /** The transaction to open. */
    headerId: string;
    /** The account whose register opens at it: the one it was entered from. */
    accountId: string;
    /** That account's name. */
    account: string;
    /** Its posted date (ISO date). */
    date: string;
    payee: string | null;
    /** Hidden: its register shows it only in the Hidden view (ADR-0072 D1). */
    hidden: boolean;
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
    /** The loan's currency, as the editor's Currency field holds it (ADR-0114 D15): the
     *  payment rounds up to one of its minor units. The server refuses a preview whose
     *  code is missing or not three characters once trimmed (`account-currency-invalid`)
     *  and never defaults one. Any other three-character code previews at its places, 2 for a code an
     *  account kept from before the rule. */
    currencyCode?: string | null;
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
 * Mirror of API `Coffer.Api.Contracts.AccountFolderDto` (migration 258,
 * FU-006). One sidebar folder: ledger-wide, one account type, nested under
 * `parentId` (null = the top level of its type's section). The list is flat;
 * the sidebar builds the tree. `accountIds` are the accounts directly in this
 * folder, not those in its sub-folders.
 */
export interface AccountFolder {
    id: string;
    accountType: string;
    parentId: string | null;
    name: string;
    accountIds: readonly string[];
}

/** Body of `POST /api/ledgers/{ledgerId}/account-folders`. */
export interface CreateAccountFolderRequest {
    accountType: string;
    name: string;
    parentId?: string | null;
}

/** Body of `PATCH /api/ledgers/{ledgerId}/account-folders/{folderId}`: rename. */
export interface RenameAccountFolderRequest {
    name: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.AccountBalanceDto` (FU-052). One real
 * account's current balance, the figure the dashboard shows: cash, plus
 * holdings market value for an investment account. Liabilities are negative.
 * Inactive accounts are included.
 */
export interface AccountBalance {
    accountId: string;
    balance: number;
    currencyCode: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.AccountSubtotalsDto`:
 * `GET /api/ledgers/{ledgerId}/accounts/subtotals?today`. The sidebar's section and
 * folder totals. Each combines accounts, so it is in the ledger's reporting currency,
 * every account's current balance converted at the latest rate and summed on the
 * server (ADR-0114 D7); null, with `rateUnavailable` naming each missing pair and day,
 * when a rate is missing (ADR-0114 D9). Inactive accounts count (ADR-0085).
 */
export interface AccountSubtotals {
    /** The ledger's reporting currency, which every total here is in. */
    currencyCode: string;
    /** One per account type the ledger has an account of. */
    types: AccountTypeSubtotal[];
    /** One per folder, empty ones included. */
    folders: AccountFolderSubtotal[];
}

export interface AccountTypeSubtotal {
    accountType: string;
    amount: number | null;
    rateUnavailable: RateUnavailable[];
}

export interface AccountFolderSubtotal {
    folderId: string;
    /** Every account beneath the folder at any depth that the total is over. */
    accountIds: string[];
    /** Zero for a folder with nothing beneath it. */
    amount: number | null;
    rateUnavailable: RateUnavailable[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.BalanceForecastDto`:
 * `GET /api/ledgers/{ledgerId}/accounts/balance-forecast?today&days`.
 * Each account's balance from the caller's today through `days` ahead, from the
 * register's committed rows and the reminders not yet posted. A projection, never a
 * fact: nothing in it is counted anywhere else.
 */
export interface BalanceForecast {
    /** One row per account in scope, by name: every account a reminder is paid from. */
    accounts: BalanceForecastAccount[];
    /** The accounts summed in the ledger's reporting currency, each converted at the
     *  latest rate on every day (ADR-0114 D7); null when there are none, or when a
     *  rate is missing, which `rateUnavailable` then names (ADR-0114 D9). */
    combined: BalanceForecastCombined | null;
    /** An estimated amount was placed on an account in scope. */
    includesEstimates: boolean;
    /** The un-posted occurrences dated before today that were placed, all on day 1:
     *  the reminders Bills calls overdue. */
    overdueOnDay1: number;
    /** The reminders that would move an account in scope but could not be placed. */
    excluded: BalanceForecastExclusion[];
    /** Every missing rate the combined line needed. */
    rateUnavailable: RateUnavailable[];
}

/** One account's projection. */
export interface BalanceForecastAccount {
    accountId: string;
    name: string;
    currencyCode: string;
    /** Day 0: the register's own current balance for the caller's today. */
    today: number;
    /** The lowest balance over `points`, day 0 included, and the first date reached. */
    lowest: BalanceForecastLowest;
    /** One point per day, day 0 through the window's last day: the balance at the
     *  end of that day. */
    points: BalanceForecastPoint[];
}

/** The accounts in scope summed, in the ledger's reporting currency. */
export interface BalanceForecastCombined {
    currencyCode: string;
    today: number;
    lowest: BalanceForecastLowest;
    points: BalanceForecastPoint[];
}

export interface BalanceForecastLowest {
    amount: number;
    /** `YYYY-MM-DD`. */
    date: string;
    /** An estimated amount went into this figure, so it is approximate. */
    estimated: boolean;
}

export interface BalanceForecastPoint {
    /** `YYYY-MM-DD`. */
    date: string;
    balance: number;
}

/**
 * A reminder left out of the projection. `no-lines`: its template names no account;
 * `override-not-placeable`: a computed amount cannot be placed on the accounts in
 * scope; `cross-currency`: a managed loan payment across currencies, which can never
 * post (ADR-0114 D13); `rate-unavailable`: an estimating reminder between currencies
 * whose other side needs the latest derived rate, and a stored leg of it is missing,
 * named in `rateUnavailable`.
 */
export interface BalanceForecastExclusion {
    reminderId: string;
    reason: string;
    /** On a `rate-unavailable` row, each missing stored leg (account currency into the
     *  reporting currency) and its day; absent on every other row. */
    rateUnavailable?: RateUnavailable[];
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
