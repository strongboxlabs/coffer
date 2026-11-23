// Bulk selection API types (ADR-0024).

import type { RateUnavailable } from './conversion';

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
          /** The register's account, required: a selection is made on one
           *  register. The server computes the selection Σ on it; the
           *  bulk-apply query ignores it for explicit selections — it acts on
           *  exactly `headerIds`. */
          accountId: string;
          headerIds: readonly string[];
          /** The register is widened to the category's descendants (mig 226):
           *  the server refuses a bulk change naming a row this register shows
           *  read-only, and it needs the register's scope to know which. */
          includeSubcategories?: boolean;
      }
    | {
          kind: 'all';
          /** The register's account, required: select-all covers that
           *  register's rows. */
          accountId: string;
          statusFilter: SelectionStatusFilter;
          /** ISO-8601 UTC timestamp string — when the user clicked
           *  select-all. Predicate only matches rows whose
           *  `created_at <= selectedAt`, so newly created /
           *  newly imported rows after that moment do NOT silently
           *  join the selection. */
          selectedAt: string;
          /** The user's local date (YYYY-MM-DD) when they clicked select-all.
           *  The status views ("scheduled" especially) are decided by it, as the
           *  register decides them, so the selection is what the register showed. */
          today?: string;
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
          /**
           * The register is widened to the category's DESCENDANTS (mig 226).
           *
           * The ninth dimension, and the one with teeth: the other eight only
           * change what is LISTED, while dropping this one would hand a bulk
           * delete a different set from the one on screen — a rollup parent's
           * own postings, of which it has none.
           */
          includeSubcategories?: boolean;
      };

/**
 * Mirror of API `Coffer.Api.Contracts.SelectionSummary`. Drives the
 * footer's "N selected · M read-only · Σ $X.XX" readout and the bulk
 * confirmations' counts.
 */
export interface SelectionSummary {
    /** Rows a bulk change acts on: a select-all's editable rows, or every row
     *  an explicit selection names. The delete and move confirmations state it. */
    count: number;
    /** Rows the selection covers, read-only rows included: the footer's
     *  "N selected" for a select-all. */
    totalCount: number;
    /** How many of `totalCount` the register shows read-only. */
    readOnlyCount: number;
    /** The Σ of the selected rows' own legs in the register's scope, over every
     *  row `totalCount` counts, read-only rows included, in both selection modes,
     *  in `currencyCode`. Null, with `rateUnavailable`, when it needs a missing rate. */
    sumOnAccount: number | null;
    /** The currency `sumOnAccount` is in (ADR-0114 D7): a real account's own. On a
     *  category's register, whose rows are each in the paying account's currency, the
     *  one currency the selected rows are in, or, when they are in several, the
     *  reporting currency, each row converted at its date. */
    currencyCode: string;
    /** Every missing rate the sum needed (ADR-0114 D9); absent when none is missing. */
    rateUnavailable?: RateUnavailable[];
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
    /** How many mirrored pairs the move left across currencies, so each now carries two
     *  amounts (ADR-0114 D13). Zero when the target is in the source's currency. */
    becameTwoAmount: number;
}
