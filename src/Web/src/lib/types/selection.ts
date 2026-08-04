// Bulk selection API types (ADR-0024).

/**
 * Status-filter discriminator the SPA carries in `'all'`-kind
 * selections. Mirrors the API's `Coffer.Api.Contracts.SelectionRequest`
 * enum and the register's status views (one active at a time). Each maps
 * to the SAME predicate the register view uses, so a select-all matches
 * exactly what the view shows: `'cleared'` / `'uncleared'` / `'reconciling'`
 * are the three recon states (not pending, posted on/before today),
 * `'scheduled'` is posted after today, and `'needs_review'` is the bank-feed
 * review flag (a separate dimension from recon status).
 */
export type SelectionStatusFilter =
    | 'all'
    | 'cleared'
    | 'uncleared'
    | 'reconciling'
    | 'scheduled'
    | 'needs_review'
    // ADR-0072 D1: scopes an `all`-kind selection to the soft-hidden
    // rows (the Hidden view). The bulk unhide / move endpoints run
    // against this scope.
    | 'hidden';

/**
 * Discriminated selection state — directly mirrors the API's
 * `SelectionRequest` shape. Two modes:
 *
 *   * `explicit` — user clicked specific row checkboxes.
 *     `headerIds` enumerates them.
 *   * `all` — user clicked the header "select all". The selection
 *     is *every row matching the current filter as of `selectedAt`,
 *     minus `excludeIds`*. Captures Gmail's "all 1247 selected"
 *     semantics in one round-trip.
 */
export type SelectionRequest =
    | {
          kind: 'explicit';
          /** Account scope so the server can compute the selection Σ on this
           *  account (the sum is account-scoped). The bulk-apply query ignores
           *  it for explicit selections — it acts on exactly `headerIds` — so
           *  this only feeds the summary sum. */
          accountId?: string;
          headerIds: readonly string[];
      }
    | {
          kind: 'all';
          accountId?: string;
          statusFilter: SelectionStatusFilter;
          /** ISO-8601 UTC timestamp string — when the user clicked
           *  select-all. Predicate only matches rows whose
           *  `created_at <= selectedAt`, so newly created /
           *  newly imported rows after that moment do NOT silently
           *  join the selection. */
          selectedAt: string;
          /** Header ids the user individually unchecked after
           *  entering `'all'` mode. */
          excludeIds: readonly string[];
          /** Structured/search filter (mig 164) — mirrors the register's active
           *  filter so a select-all covers exactly what's shown, not the whole
           *  account. Non-status dimensions only (status is `statusFilter`). */
          search?: string;
          dateFrom?: string;
          dateTo?: string;
          amountMin?: number;
          amountMax?: number;
          securityId?: string;
          tag?: string;
          categoryId?: string;
      };

/**
 * Mirror of API `Coffer.Api.Contracts.SelectionSummary`. Drives the
 * footer's "N selected · Σ $X.XX" readout. `sumOnAccount` is null for
 * ledger-wide selections (no single currency to sum into).
 */
export interface SelectionSummary {
    count: number;
    sumOnAccount: number | null;
}

/** Mirror of API `BulkReconStatusResponse`. */
export interface BulkReconStatusResponse {
    updated: number;
}

/** Mirror of API `BulkDeleteResponse`. */
export interface BulkDeleteResponse {
    hardDeleted: number;
    softHidden: number;
}

/** Mirror of API `BulkUnhideResponse` (ADR-0072 D2). */
export interface BulkUnhideResponse {
    unhidden: number;
}

/** Mirror of API `BulkMoveAccountResponse` (ADR-0072 D3). */
export interface BulkMoveAccountResponse {
    moved: number;
}
