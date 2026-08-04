// Shared display helpers for ledger operations (ADR-0055/0086) — used by the
// Activity timeline and the dashboard's recent-activity widget so the two never
// drift. Covers every operation family: ingest (sync / file / Moneydance import),
// quote refresh, and snapshot restore.

import type { LedgerOperationSummary } from './types';

/** Bootstrap "system" user (migration 014) — automated/scheduled runs. */
export const SYSTEM_USER_ID = '00000000-0000-0000-0000-000000000001';

export function ledgerOperationLabel(key: string): string {
    switch (key) {
        case 'simplefin':
            return 'SimpleFIN';
        case 'ofx':
            return 'OFX';
        case 'qif':
            return 'QIF';
        case 'file':
            return 'File import';
        case 'moneydance':
            return 'Moneydance import';
        case 'quote-refresh':
            return 'Quote refresh';
        case 'snapshot-restore':
            return 'Snapshot restore';
        default:
            return key;
    }
}

export function triggerLabel(via: string): string {
    switch (via) {
        case 'manual':
            return 'manual';
        case 'file-upload':
            return 'upload';
        case 'post-sync':
            return 'after sync';
        case 'scheduled':
            return 'scheduled';
        default:
            return via;
    }
}

export function whoLabel(userId: string | null): string {
    if (userId === null) return 'unknown';
    if (userId === SYSTEM_USER_ID) return 'system';
    return 'you';
}

export function statusClass(status: string): string {
    switch (status) {
        case 'completed':
            return 'bg-state-success-soft text-state-success';
        case 'partial':
            return 'bg-state-warning-soft text-state-warning';
        case 'failed':
        case 'needs_reauth':
            return 'bg-state-danger-soft text-state-danger';
        case 'running':
            return 'bg-accent-soft text-accent';
        default:
            return 'bg-surface-muted text-text-muted';
    }
}

export function familyClass(family: string): string {
    switch (family) {
        case 'quote':
            return 'bg-accent-soft text-accent';
        case 'snapshot':
            return 'bg-state-warning-soft text-state-warning';
        default:
            return 'bg-surface-muted text-text-muted';
    }
}

/** Family-appropriate one-line count summary, read from `details`. */
export function summarizeLedgerOperation(run: LedgerOperationSummary): string {
    const d = run.details;
    if (run.family === 'snapshot') {
        // Restore records snapshot_id (a GUID, so absent from the numeric details
        // map); the line just states what happened.
        return 'Restored from a snapshot';
    }
    if (run.providerKey === 'moneydance') {
        // Moneydance bootstrap import: details is duration_seconds + one written
        // count per pipeline step. Summarize as total rows + elapsed.
        const duration = d.duration_seconds ?? 0;
        const rows = Object.entries(d)
            .filter(([key]) => key !== 'duration_seconds')
            .reduce((sum, [, value]) => sum + (value ?? 0), 0);
        const parts = [`${rows.toLocaleString()} rows imported`];
        if (duration > 0) parts.push(`${duration}s`);
        return parts.join(' · ');
    }
    if (run.family === 'quote') {
        const parts = [`${d.prices_inserted ?? 0} new`];
        if ((d.prices_updated ?? 0) > 0) parts.push(`${d.prices_updated} updated`);
        if ((d.securities_unresolved ?? 0) > 0)
            parts.push(`${d.securities_unresolved} unresolved`);
        // Which provider actually moved the prices (ADR-0070 sources): `fetch`
        // is the market-data provider (Yahoo today), `simplefin` the bank feed.
        const from: string[] = [];
        if ((d.prices_from_fetch ?? 0) > 0) from.push(`Yahoo ${d.prices_from_fetch}`);
        if ((d.prices_from_simplefin ?? 0) > 0) from.push(`SimpleFIN ${d.prices_from_simplefin}`);
        if (from.length > 0) parts.push(`from ${from.join(', ')}`);
        return parts.join(' · ');
    }
    const parts = [`${d.txns_inserted ?? 0} new`];
    if ((d.txns_already_known ?? 0) > 0) parts.push(`${d.txns_already_known} known`);
    if ((d.txns_promoted ?? 0) > 0) parts.push(`${d.txns_promoted} promoted`);
    if ((d.txns_still_pending ?? 0) > 0) parts.push(`${d.txns_still_pending} pending`);
    return parts.join(' · ');
}

/** Relative "2h ago" formatter. */
export function formatRelative(iso: string): string {
    const then = new Date(iso).getTime();
    const now = Date.now();
    const diffSec = Math.max(1, Math.round((now - then) / 1000));
    if (diffSec < 60) return `${diffSec}s ago`;
    const diffMin = Math.round(diffSec / 60);
    if (diffMin < 60) return `${diffMin}m ago`;
    const diffHr = Math.round(diffMin / 60);
    if (diffHr < 48) return `${diffHr}h ago`;
    const diffDay = Math.round(diffHr / 24);
    return `${diffDay}d ago`;
}
