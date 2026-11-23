// Shared display helpers for ledger operations (ADR-0055/0086) — used by the
// Activity timeline and the dashboard's recent-activity widget so the two never
// drift. Covers every operation family: ingest (sync / file / Moneydance import),
// quote refresh, and snapshot restore.

import type { LedgerOperationSummary } from './types';

/** Bootstrap "system" user (migration 014) — automated/scheduled runs. */
export const SYSTEM_USER_ID = '00000000-0000-0000-0000-000000000001';

/** The provider key of a standalone exchange-rate and benchmark backfill's run
 *  (ADR-0114 D11): a quote-family row with no prices. */
export const MARKET_SERIES_BACKFILL ='market-series-backfill';

/** The provider key of a Demo's marker (ADR-0115 D9): the row that records which of the
 *  Demo's examples were added. Its details hold one entry per examples step, 1 when the
 *  step was added. */
export const DEMO_EXAMPLES = 'demo';

/** How many examples steps a Demo has: the server's step list, whose details a marker
 *  may record only part of while it runs. */
export const DEMO_EXAMPLE_STEPS = 11;

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/**
 * What a quote run stored besides prices (ADR-0114 D5, D11), each part only when it is
 * not zero: exchange-rate days, benchmark days, securities whose currency was confirmed,
 * and fetched prices re-stamped to it. The Activity log and the Securities refresh
 * notice read the same counts, from `details` and from the run's outcome.
 */
export function marketSeriesParts(c: {
    ratesInserted?: number;
    benchmarkDaysInserted?: number;
    currenciesConfirmed?: number;
    rowsRestamped?: number;
}): string[] {
    const parts: string[] = [];
    if ((c.ratesInserted ?? 0) > 0) parts.push(plural(c.ratesInserted!, 'rate day', 'rate days'));
    if ((c.benchmarkDaysInserted ?? 0) > 0) parts.push(plural(c.benchmarkDaysInserted!, 'benchmark day', 'benchmark days'));
    if ((c.currenciesConfirmed ?? 0) > 0) parts.push(plural(c.currenciesConfirmed!, 'currency confirmed', 'currencies confirmed'));
    if ((c.rowsRestamped ?? 0) > 0) parts.push(`${c.rowsRestamped!} re-stamped`);
    return parts;
}

export function ledgerOperationLabel(key: string): string {
    switch (key) {
        case 'simplefin':
            return 'SimpleFIN';
        case 'ofx':
            return 'OFX';
        case 'qif':
            return 'QIF';
        case 'csv-generic':
            return 'CSV import';
        case 'csv-fidelity':
            return 'Fidelity CSV import';
        // The key migration 132 gave every file run recorded before runs carried their
        // reader's key; no run is recorded under it since.
        case 'file':
            return 'File import';
        case 'moneydance':
            return 'Moneydance import';
        case DEMO_EXAMPLES:
            return 'Demo examples';
        case 'quote-refresh':
            return 'Quote refresh';
        case MARKET_SERIES_BACKFILL:
            return 'Rates and benchmarks backfill';
        case 'snapshot-restore':
            return 'Snapshot restore';
        default:
            return key;
    }
}

/** The name of the source a run's error came from (`providerKey`, migration 303). An
 *  unknown key shows as itself rather than as nothing. */
export function errorSourceLabel(key: string): string {
    switch (key) {
        case 'simplefin-holdings':
            return 'SimpleFIN holdings';
        case 'yahoo':
            return 'Yahoo Finance';
        case 'market-series':
            return 'Rates and benchmarks';
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
        // Only a SimpleFIN sync triggers a run after it: the quote refresh that reads
        // the holdings the sync just fetched (ADR-0033 §5).
        case 'post-sync':
            return 'after SimpleFIN sync';
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
    if (run.providerKey === DEMO_EXAMPLES) {
        // A Demo's marker counts no rows: it records each examples step as added (1) or
        // not (0), and the ones not added, with why, are in its detail.
        const done = Object.values(d).filter((value) => value === 1).length;
        return `${done} of ${DEMO_EXAMPLE_STEPS} example steps added`;
    }
    if (run.family === 'quote') {
        const series = marketSeriesParts({
            ratesInserted: d.rates_inserted,
            benchmarkDaysInserted: d.benchmark_days_inserted,
            currenciesConfirmed: d.currencies_confirmed,
            rowsRestamped: d.rows_restamped,
        });
        // A standalone backfill stores no prices, so it leads with what it did store.
        if (run.providerKey === MARKET_SERIES_BACKFILL) {
            return series.length > 0 ? series.join(' · ') : 'Nothing new';
        }
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
        parts.push(...series);
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
