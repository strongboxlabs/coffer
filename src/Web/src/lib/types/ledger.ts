// Ledger-level API response types.

/**
 * Mirror of API `Coffer.Api.Contracts.LedgerSummary`. The role is the
 * user's grant role on this ledger (`"owner"` today; the API may
 * add more granular roles later).
 */
export interface LedgerSummary {
    id: string;
    name: string;
    role: string;
}

/**
 * One row of balance drift from `POST /api/ledgers/{id}/balances/health`.
 * The endpoint runs the canonical recompute as a side effect, so a
 * non-empty list means drift was present AND has now been healed.
 * Each entry tells the user (and us) which row went stale and by how
 * much, for diagnostic purposes.
 */
export interface BalanceHealthDriftDto {
    accountId: string;
    accountName: string;
    headerId: string;
    postedAt: string;
    storedBefore: number;
    recomputedAfter: number;
    diff: number;
}

/**
 * Mirror of API `Coffer.Api.Contracts.BalanceHealthReport`. `healthy`
 * equals `drifted.length === 0` — when false, the listed rows were
 * drifted at snapshot time and the recompute has already corrected
 * them.
 */
export interface BalanceHealthReport {
    healthy: boolean;
    accountsChecked: number;
    rowsChecked: number;
    driftedCount: number;
    drifted: BalanceHealthDriftDto[];
}
