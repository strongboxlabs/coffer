import { describe, expect, it } from 'vitest';

import { summarizeLedgerOperation } from './ledgerOperationDisplay';
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
