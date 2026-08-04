// Feed connection + sync API response types (Phase 5 / SimpleFIN).

/**
 * Mirror of API `Coffer.Api.Contracts.FeedConnectionSummary`
 * (Phase 5 / migration 036). One row of the SimpleFIN
 * connection list. The sealed access URL stays server-side; the
 * SPA only deals with the audit-safe surface.
 */
export interface FeedConnectionSummary {
    id: string;
    ledgerId: string;
    /** `"simplefin"` today; expandable when other providers land. */
    provider: string;
    /** Display name from SimpleFIN's first-connect probe. NULL
     *  until the probe (or first sync, slice 2b) populates;
     *  SPA renders "SimpleFIN" as the fallback. */
    institutionName: string | null;
    /** One of `active` / `needs_reauth` / `error` /
     *  `disconnected`. DB CHECK constraint enforces validity. */
    status: string;
    /** ISO-8601 UTC. Null on never-synced connections. */
    lastSyncedAt: string | null;
    createdAt: string;
}

/** Body of `POST /api/ledgers/{ledgerId}/feed-connections`. The
 *  user pastes the one-shot setup token they generated at
 *  simplefin.org/setup; the server exchanges + seals + persists. */
export interface CreateFeedConnectionRequest {
    setupToken: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.FeedConnectionAccountDto`
 * (slice 2c.4). One row of the per-connection bank-side account
 * directory. `boundLedgerAccountId` is null on unmapped rows;
 * the SPA's unified accounts panel uses it to decide between
 * "Bound to X" and the "Pick a Coffer account..." dropdown.
 */
export interface FeedConnectionAccountDto {
    simpleFinAccountId: string;
    name: string;
    orgName: string | null;
    currency: string | null;
    balance: number | null;
    /** ISO-8601 UTC timestamp string. */
    lastSeenAt: string;
    boundLedgerAccountId: string | null;
    boundLedgerAccountName: string | null;
    /** Slice 2c.5: per-account sync watermark (ISO-8601 UTC).
     *  Drives the next sync's start-date. `null` = "no successful
     *  sync yet, full 90-day window next time." Always `null` on
     *  unmapped rows. The SPA shows this on the row's "Sync from"
     *  popover so the user can see what window the next sync will
     *  request. */
    boundLedgerAccountSyncFrom: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SyncErrorDto`. One
 * SimpleFIN v2 `errlist[]` entry, surfaced verbatim. `code` is the
 * structured `prefix.subcode` string (e.g. `auth.revoked`); the
 * SPA today renders `message` and uses `code` for telemetry only.
 */
export interface SyncErrorDto {
    code: string;
    message: string;
    simpleFinConnectionId: string | null;
    simpleFinAccountId: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SyncResultDto` (Phase 5 /
 * slice 2b). One sync-run summary: counts + the SimpleFIN
 * accounts the user hasn't yet mapped to a Coffer account.
 *
 * `connectionStatus` reflects the post-sync
 * `feed_connections.status` — normally `"active"`; flips to
 * `"needs_reauth"` when SimpleFIN returned 403, so the SPA can
 * render a Re-connect CTA instead of a generic error.
 *
 * `errors[]` mirrors SimpleFIN v2's `errlist[]` — non-fatal
 * per-connection / per-account messages.
 */
export interface SyncResultDto {
    accountsDiscovered: number;
    /** Bank-posted rows the sync just landed in `txn_headers` with
     *  `needs_review = true` — plus any previously-pending FITIDs
     *  the sync promoted to cleared on this run (slice 2c). */
    transactionsForReview: number;
    /** Bank-pending rows (SimpleFIN `pending: true`) the sync
     *  landed in `txn_headers` with `is_pending = true` AND
     *  `needs_review = true`. Flipped to `is_pending = false` in
     *  place on a future sync that returns the same FITID with
     *  `pending: false`. */
    transactionsStillPending: number;
    alreadyKnown: number;
    /** One of `active` / `needs_reauth` / `error` /
     *  `disconnected` — the post-sync connection status. */
    connectionStatus: string;
    errors: readonly SyncErrorDto[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.SyncAllConnectionEntry`.
 * Exactly one of `result` / `failureCode` is non-null:
 * * `result` non-null → the sync completed; `connectionStatus`
 *   and `errors[]` carry the detail.
 * * `failureCode` non-null → pre-flight rejection (lock held,
 *   access URL missing/corrupted, etc.). Same code strings the
 *   per-connection sync endpoint returns in its 422 envelope.
 */
export interface SyncAllConnectionEntry {
    connectionId: string;
    result: SyncResultDto | null;
    failureCode: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SyncAllResultDto` (slice
 * 2c.3). One entry per active feed connection on the ledger,
 * plus `hadAnyFailure` for the SPA's partial-failure banner.
 */
export interface SyncAllResultDto {
    connections: readonly SyncAllConnectionEntry[];
    hadAnyFailure: boolean;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SyncRunSummary` (slice 2c.1
 * / migration 038). One row in the per-connection sync activity
 * log. `status` is one of `running` / `completed` / `partial` /
 * `failed` / `needs_reauth`.
 */
export interface SyncRunSummary {
    id: string;
    feedConnectionId: string | null;
    status: string;
    txnsFetched: number;
    txnsInserted: number;
    txnsPromoted: number;
    txnsAlreadyKnown: number;
    txnsStillPending: number;
    errorMessage: string | null;
    /** ISO-8601 UTC timestamp string. */
    startedAt: string;
    /** ISO-8601 UTC timestamp string. NULL while status='running'. */
    completedAt: string | null;
    triggeredByUserId: string | null;
    /** Count of `sync_run_errors` rows attached to this run. */
    errorCount: number;
    /** Count of `sync_run_promotions` rows attached to this run. */
    promotionCount: number;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SyncRunPromotionDto`. One
 * promote-on-clear event captured during a sync — the bank
 * cleared a previously-pending charge at a different amount
 * than the original hold.
 */
export interface SyncRunPromotionDto {
    headerId: string;
    wasAmount: number;
    becameAmount: number;
    /** ISO-8601 UTC timestamp string. */
    promotedAt: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SyncRunDetail`. Full
 * per-run detail — summary plus the child errors + promotions.
 * Backs the expandable per-run panel on the SPA.
 */
export interface SyncRunDetail {
    summary: SyncRunSummary;
    errors: readonly SyncErrorDto[];
    promotions: readonly SyncRunPromotionDto[];
}
