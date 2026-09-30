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

/**
 * Mirror of API `Coffer.Api.Contracts.ConsistencyMismatch`. The ids are carried
 * separately from `scope` so a repair targets exactly what was reported.
 */
export interface ConsistencyMismatch {
    scope: string;
    field: string;
    stored: number;
    expected: number;
    diff: number;
    accountId?: string | null;
    securityId?: string | null;
    headerId?: string | null;
    /** ISO date (`YYYY-MM-DD`) where the projection is per-(security, day). */
    priceDate?: string | null;
    /** What KIND of finding this is, where a projection has more than one.
     *  `orphaned` is reported but not repairable. Null where the projection has
     *  a single kind. */
    reason?: string | null;
}

/** Mirror of API `Coffer.Api.Contracts.ProjectionConsistency`. */
export interface ProjectionConsistency {
    projection: string;
    healthy: boolean;
    checked: number;
    mismatchedCount: number;
    mismatches: ConsistencyMismatch[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.LedgerConsistencyReport`. Every projection
 * listed here has a repair, so the UI never reports a problem it cannot offer to
 * fix.
 */
export interface LedgerConsistencyReport {
    healthy: boolean;
    projections: ProjectionConsistency[];
    /** Advisory, not findings — see {@link UnbackedPriceAdvisory}. Takes no
     *  part in `healthy`. */
    unbackedPrices: UnbackedPriceAdvisory[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.UnbackedPriceAdvisory`. A security
 * holding prices that claim a trade produced them while no trade currently
 * does. Advisory: the price may still be right, and nothing records which
 * transaction wrote it, so it can only be inferred.
 */
export interface UnbackedPriceAdvisory {
    securityId: string;
    security: string;
    count: number;
    earliest: string;
    latest: string;
    /** Quantity held now at the latest price; 0 once the position is sold,
     *  which sorts it last without hiding it. */
    holdingValue: number;
}
