import type { LedgerOverview, NetWorthHistory } from '../types/overview';
import { request } from './_request';
import { todayQuery, withToday } from './_today';

/**
 * Ledger overview aggregate (ADR-0056 slice 1) — net worth, per-account
 * balances grouped by type, and the investment roll-up, in one call. Every figure
 * is current on the user's local date, which is sent as the register sends it.
 *
 * With `since`, a month-end before today, the result also carries net worth at the
 * end of that day; the server refuses any other date (422 `overview-since-invalid`).
 */
export function fetchLedgerOverview(ledgerId: string, since?: string): Promise<LedgerOverview> {
    const query = since ? `${todayQuery()}&since=${encodeURIComponent(since)}` : todayQuery();
    return request<LedgerOverview>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/overview${query}`,
    );
}

/**
 * Net worth at the end of each month in `[from, to]`, the last point at the end of
 * the user's today as the register counts it, so it equals the overview's figure.
 */
export function fetchNetWorthHistory(
    ledgerId: string,
    from: string,
    to: string,
): Promise<NetWorthHistory> {
    const params = withToday(new URLSearchParams({ from, to, interval: 'Month' }));
    return request<NetWorthHistory>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/net-worth-history?${params.toString()}`,
    );
}
