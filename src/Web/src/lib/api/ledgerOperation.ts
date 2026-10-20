import type { LedgerOperationSummary } from '../types/ledgerOperation';
import { request } from './_request';

export interface LedgerOperationFilter {
    /** provider_key; omit/empty = all providers. */
    provider?: string;
    /** runs started in the last N days; omit = all time. */
    days?: number;
    limit?: number;
}

/**
 * Ledger-wide provider-activity timeline (ADR-0055 slice C) — every provider
 * run across families, newest first.
 */
export function fetchLedgerOperations(
    ledgerId: string,
    filter: LedgerOperationFilter = {},
): Promise<LedgerOperationSummary[]> {
    const params = new URLSearchParams();
    if (filter.provider) params.set('provider', filter.provider);
    if (filter.days !== undefined) params.set('days', String(filter.days));
    if (filter.limit !== undefined) params.set('limit', String(filter.limit));
    const qs = params.toString();
    return request<LedgerOperationSummary[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ledger-operations${qs ? `?${qs}` : ''}`,
    );
}
