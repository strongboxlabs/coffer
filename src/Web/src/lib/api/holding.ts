// Portfolio / Holdings View endpoints (slice A1).

import type { HoldingsViewDto } from '../types/holding';
import { request } from './_request';

/**
 * GET /api/ledgers/{id}/accounts/{aid}/holdings — Portfolio View
 * payload for one investment account (slice A1). Per-security
 * positions + summary block + cash balance. Read-only.
 *
 * 422 codes the call site should handle:
 *   * `ledger-not-visible`
 *   * `account-not-in-ledger`
 *   * `account-not-investment` — only investment accounts have
 *     holdings; callers should gate on `accountType` before calling.
 *   * `account-missing-holdings-sibling` — system-managed link per
 *     ADR-0019 was never created; recoverable by re-running the
 *     importer.
 */
export function fetchHoldings(
    ledgerId: string,
    accountId: string,
): Promise<HoldingsViewDto> {
    return request<HoldingsViewDto>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/accounts/${encodeURIComponent(accountId)}/holdings`,
    );
}
