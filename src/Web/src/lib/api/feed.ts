// Feed connection + sync endpoints (Phase 5 / SimpleFIN slice 2a-2c).
// Per-account sync surfaces (mapping, sync-from-date) live in
// [./account.ts] since they target an account resource.

import type {
    CreateFeedConnectionRequest,
    FeedConnectionAccountDto,
    FeedConnectionSummary,
    SyncAllResultDto,
    SyncResultDto,
    SyncRunDetail,
    SyncRunSummary,
} from '../types/feed';
import { request } from './_request';

/** GET /api/ledgers/{ledgerId}/feed-connections — list SimpleFIN
 *  connections in this ledger, ordered by most-recently-synced. */
export function fetchFeedConnections(
    ledgerId: string,
): Promise<FeedConnectionSummary[]> {
    return request<FeedConnectionSummary[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/feed-connections`,
    );
}

/**
 * POST /api/ledgers/{ledgerId}/feed-connections — exchange a
 * SimpleFIN setup token for an access URL, seal it under the
 * ledger's LEK, persist. Returns the created connection summary.
 *
 * 422 codes:
 *   * `feed-connection-setup-token-required` — empty/whitespace token.
 *   * `feed-connection-setup-token-invalid`  — base64url malformed
 *      OR SimpleFIN rejected the exchange (token expired / consumed).
 *   * `ledger-encryption-key-missing` — pre-035 ledger; needs the
 *      backfill slice or ledger re-creation.
 *   * `ledger-not-visible` — caller has no grant on this ledger.
 */
export function createFeedConnection(
    ledgerId: string,
    body: CreateFeedConnectionRequest,
): Promise<FeedConnectionSummary> {
    return request<FeedConnectionSummary>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/feed-connections`,
        { method: 'POST', body },
    );
}

/** DELETE /api/ledgers/{ledgerId}/feed-connections/{id} — remove a
 *  connection. Children (accounts.feed_connection_id, sync_runs)
 *  set NULL via FK cascade. 422 `feed-connection-not-found` if
 *  the id is unknown or RLS-hidden. */
export function deleteFeedConnection(
    ledgerId: string,
    connectionId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/feed-connections/${encodeURIComponent(connectionId)}`,
        { method: 'DELETE' },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/feed-connections/{cid}/sync — pull
 * the latest accounts + transactions from SimpleFIN, FITID-dedup
 * against existing txn_headers, land unmatched rows directly in
 * txn_headers with needs_review=true for user review. Returns a summary
 * including any SimpleFIN accounts the user hasn't yet mapped
 * to a Coffer account (the SPA renders these as the mapping
 * wizard).
 *
 * 422 codes:
 *   * `feed-connection-not-found`
 *   * `feed-connection-access-url-missing` / `-corrupted`
 *   * `ledger-not-visible`
 */
export function syncFeedConnection(
    ledgerId: string,
    connectionId: string,
): Promise<SyncResultDto> {
    return request<SyncResultDto>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/feed-connections/${encodeURIComponent(connectionId)}/sync`,
        { method: 'POST' },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/accounts/{accountId}/sync — slice
 * 2c.3 per-account sync. Narrows the SimpleFIN call to the one
 * account bound to this Coffer account. Returns the same
 * `SyncResultDto` shape as the per-connection sync.
 *
 * Possible 422s:
 *   * `account-not-in-ledger`
 *   * `account-not-bound-to-feed` — not mapped on the bank-feeds page yet
 *   * `feed-sync-in-progress` — another sync on the same connection
 *   * `feed-connection-access-url-{missing,corrupted}`
 */
export function syncAccount(
    ledgerId: string,
    accountId: string,
): Promise<SyncResultDto> {
    return request<SyncResultDto>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/sync`,
        { method: 'POST' },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/sync-all — slice 2c.3. Sync every
 * feed connection on the ledger sequentially. Returns one
 * `SyncAllConnectionEntry` per connection; each entry's `result`
 * vs `failureCode` discriminates between completed syncs and
 * pre-flight rejections.
 */
export function syncAllConnections(
    ledgerId: string,
): Promise<SyncAllResultDto> {
    return request<SyncAllResultDto>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/sync-all`,
        { method: 'POST' },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/feed-connections/{connectionId}/accounts
 * — slice 2c.4. Per-connection unified accounts list (mapped +
 * unmapped together). Independent of any recent sync; reads the
 * persisted `feed_connection_accounts` directory.
 */
export function fetchFeedConnectionAccounts(
    ledgerId: string,
    connectionId: string,
): Promise<FeedConnectionAccountDto[]> {
    return request<FeedConnectionAccountDto[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/feed-connections/${encodeURIComponent(connectionId)}/accounts`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/sync-runs?connectionId=... — recent
 * runs for one connection, newest first (slice 2c.1). The SPA's
 * sync-activity panel reads this; default limit caps to the
 * server's per-page maximum.
 */
export function fetchSyncRuns(
    ledgerId: string,
    connectionId: string,
    limit?: number,
): Promise<SyncRunSummary[]> {
    const params = new URLSearchParams({ connectionId });
    if (limit !== undefined) params.set('limit', String(limit));
    return request<SyncRunSummary[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/sync-runs/?${params.toString()}`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/sync-runs/{runId} — full detail for
 * one run (errors + promotions). Backs the expandable per-run
 * detail view.
 */
export function fetchSyncRunDetail(
    ledgerId: string,
    runId: string,
): Promise<SyncRunDetail> {
    return request<SyncRunDetail>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/sync-runs/${encodeURIComponent(runId)}`,
    );
}
