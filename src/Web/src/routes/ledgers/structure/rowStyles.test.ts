import { describe, expect, it } from 'vitest';

import { matchesQuery, normalizeQuery, rowLinkClass, transactionCountLabel } from './rowStyles';

describe('rowStyles', () => {
    it('normalizes a query by trimming it and ignoring case', () => {
        expect(normalizeQuery('  Fuel ')).toBe('fuel');
        expect(normalizeQuery('   ')).toBe('');
    });

    it('matches any of the texts, ignoring case, and skips the ones that are missing', () => {
        expect(matchesQuery('bank', 'Checking', 'First BANK')).toBe(true);
        expect(matchesQuery('bank', 'Checking', null, undefined)).toBe(false);
        expect(matchesQuery('bank')).toBe(false);
    });

    it('lets an empty query match everything, even a row with no text', () => {
        expect(matchesQuery('')).toBe(true);
        expect(matchesQuery('', null)).toBe(true);
    });

    it('says a count a reader can say aloud', () => {
        expect(transactionCountLabel(0)).toBe('0 transactions');
        expect(transactionCountLabel(1)).toBe('1 transaction');
        expect(transactionCountLabel(12)).toBe('12 transactions');
    });

    it('sets an inactive name apart by more than colour', () => {
        expect(rowLinkClass()).not.toContain('line-through');
        expect(rowLinkClass(true)).toContain('line-through');
        expect(rowLinkClass(true)).toContain('text-text-subtle');
    });
});
