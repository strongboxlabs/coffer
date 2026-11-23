import { describe, expect, it } from 'vitest';

import { ledgerOperationLabel, summarizeLedgerOperation } from './ledgerOperationDisplay';
import type { LedgerOperationSummary } from './types';

// summarizeLedgerOperation reads only `family` + `details`, so a partial run + cast
// is enough to exercise the quote-family source attribution (D).
function quoteRun(details: Record<string, number>): LedgerOperationSummary {
    return { family: 'quote', details } as LedgerOperationSummary;
}

describe('summarizeLedgerOperation — quote source attribution', () => {
    it('names each provider that moved prices, with counts', () => {
        const s = summarizeLedgerOperation(quoteRun({
            prices_inserted: 0,
            prices_updated: 6,
            prices_from_fetch: 4,
            prices_from_simplefin: 2,
        }));
        expect(s).toBe('0 new · 6 updated · from Yahoo 4, SimpleFIN 2');
    });

    it('shows a single source when only one moved prices', () => {
        const s = summarizeLedgerOperation(quoteRun({
            prices_inserted: 3,
            prices_from_simplefin: 3,
        }));
        expect(s).toBe('3 new · from SimpleFIN 3');
    });

    it('omits the source clause when no per-source counts are present', () => {
        // Legacy runs (pre-D) carry no per-source keys — the summary is unchanged.
        const s = summarizeLedgerOperation(quoteRun({ prices_inserted: 0, prices_updated: 6 }));
        expect(s).toBe('0 new · 6 updated');
    });
});

// Like quoteRun: summarize reads only family/providerKey/details, so a partial run
// + cast is enough. details is typed as Record<string, number> (the target field
// type) so the object-literal assertion stays comparable to LedgerOperationSummary.
function ingestRun(providerKey: string, details: Record<string, number>): LedgerOperationSummary {
    return { family: 'ingest', providerKey, details } as LedgerOperationSummary;
}

describe('summarizeLedgerOperation — rates and benchmarks (ADR-0114 D11)', () => {
    it('names a standalone backfill and says what it stored, not "0 new"', () => {
        const run = {
            family: 'quote', providerKey: 'market-series-backfill',
            details: { prices_inserted: 0, prices_updated: 0, rates_inserted: 240, benchmark_days_inserted: 1 },
        } as unknown as LedgerOperationSummary;
        expect(ledgerOperationLabel(run.providerKey)).toBe('Rates and benchmarks backfill');
        expect(summarizeLedgerOperation(run)).toBe('240 rate days · 1 benchmark day');
    });

    it('says a backfill that stored nothing stored nothing', () => {
        const run = { family: 'quote', providerKey: 'market-series-backfill', details: { rates_inserted: 0 } } as unknown as LedgerOperationSummary;
        expect(summarizeLedgerOperation(run)).toBe('Nothing new');
    });

    it('adds a refresh\'s rates, benchmark days and currency checks after its prices', () => {
        const s = summarizeLedgerOperation(quoteRun({
            prices_inserted: 2, rates_inserted: 1, benchmark_days_inserted: 3, currencies_confirmed: 2, rows_restamped: 5,
        }));
        expect(s).toBe('2 new · 1 rate day · 3 benchmark days · 2 currencies confirmed · 5 re-stamped');
    });
});

describe('summarizeLedgerOperation — new operation kinds (ADR-0086)', () => {
    it('totals Moneydance import step counts and shows elapsed', () => {
        const run = ingestRun('moneydance', {
            duration_seconds: 12, accounts: 40, transactions: 1200, securities: 8,
        });
        // duration_seconds is excluded from the row total; 40+1200+8 = 1248.
        expect(summarizeLedgerOperation(run)).toBe('1,248 rows imported · 12s');
    });

    it('omits the elapsed clause when duration is absent', () => {
        const run = ingestRun('moneydance', { accounts: 2, transactions: 3 });
        expect(summarizeLedgerOperation(run)).toBe('5 rows imported');
    });

    it('labels a snapshot restore', () => {
        // Restore stores snapshot_id (a GUID) which is not in the numeric map.
        const run = { family: 'snapshot', providerKey: 'snapshot-restore', details: {} as Record<string, number> } as LedgerOperationSummary;
        expect(summarizeLedgerOperation(run)).toBe('Restored from a snapshot');
    });
});

describe('summarizeLedgerOperation — a Demo marker (ADR-0115 D9)', () => {
    it('labels the marker as the Demo examples, not as an import', () => {
        expect(ledgerOperationLabel('demo')).toBe('Demo examples');
    });

    it('counts the steps recorded as added, out of every examples step', () => {
        // The server's details: one entry per step, 1 added and 0 not.
        const run = ingestRun('demo', {
            resolve: 1, folders: 1, investments: 0, budgets: 1, register: 0, 'card-statement': 0,
            reminders: 1, reports: 1, home: 1, snapshot: 0, check: 1,
        });
        expect(summarizeLedgerOperation(run)).toBe('7 of 11 example steps added');
    });

    it('counts every step on a completed marker', () => {
        const run = ingestRun('demo', {
            resolve: 1, folders: 1, investments: 1, budgets: 1, register: 1, 'card-statement': 1,
            reminders: 1, reports: 1, home: 1, snapshot: 1, check: 1,
        });
        expect(summarizeLedgerOperation(run)).toBe('11 of 11 example steps added');
    });

    it('counts none while a marker records nothing yet, rather than reading as an import', () => {
        // A running marker's details are empty; the ingest summary would say "0 new".
        expect(summarizeLedgerOperation(ingestRun('demo', {}))).toBe('0 of 11 example steps added');
    });
});

describe('ledgerOperationLabel — file imports by their reader', () => {
    // A file run is recorded under its reader's key, so each CSV reader's key needs a
    // name; the raw key is what an unnamed one shows.
    it('names a CSV import by its reader', () => {
        expect(ledgerOperationLabel('csv-generic')).toBe('CSV import');
        expect(ledgerOperationLabel('csv-fidelity')).toBe('Fidelity CSV import');
    });

    it('still names a run recorded before runs carried their reader', () => {
        expect(ledgerOperationLabel('file')).toBe('File import');
    });
});
