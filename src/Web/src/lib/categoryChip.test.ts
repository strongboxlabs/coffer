import { describe, it, expect } from 'vitest';

import { categoryChipVariant } from './categoryChip';

describe('categoryChipVariant', () => {
    it('returns `default` when no counterparty is given', () => {
        expect(categoryChipVariant(null, null, null)).toBe('default');
    });

    it('returns `xfer` for real-account counterparties (transfers)', () => {
        expect(categoryChipVariant('Eastbank Checking', 'bank', 'a1')).toBe('xfer');
        expect(categoryChipVariant('Apple Card', 'credit_card', 'a2')).toBe('xfer');
        expect(categoryChipVariant('Workplace 401(k)', 'investment', 'a3')).toBe('xfer');
        expect(categoryChipVariant('Holdings · AAPL', 'holding', 'a4')).toBe('xfer');
    });

    it('matches known category-name patterns case-insensitively', () => {
        // Patterns are first-match wins; assertions cover the substrings.
        expect(categoryChipVariant('Groceries', 'category', 'c1')).toBe('groc');
        expect(categoryChipVariant('Bills:Electricity', 'category', 'c2')).toBe('util');
        expect(categoryChipVariant('Dining Out', 'category', 'c3')).toBe('din');
        expect(categoryChipVariant('Mortgage', 'category', 'c4')).toBe('house');
        expect(categoryChipVariant('Subscriptions:Streaming', 'category', 'c5')).toBe('sub');
        expect(categoryChipVariant('Uber Rides', 'category', 'c6')).toBe('tran');
        expect(categoryChipVariant('Salary', 'category', 'c7')).toBe('sal');
        expect(categoryChipVariant('Phone Bill', 'category', 'c8')).toBe('phone');
        expect(categoryChipVariant('Entertainment', 'category', 'c9')).toBe('rec');
    });

    it('falls back to a deterministic hash-assigned variant for unknown names', () => {
        // Same id → same variant across calls. We don't assert which
        // bucket lands; only that it's one of the auto-assign pool and
        // the result is stable.
        const accountId = '00000000-0000-0000-0000-0000000000aa';
        const first = categoryChipVariant('Hobbies', 'category', accountId);
        const second = categoryChipVariant('Hobbies', 'category', accountId);
        expect(first).toBe(second);
        expect([
            'groc', 'din', 'house', 'util', 'sub',
            'tran', 'sal', 'xfer', 'phone', 'rec',
        ]).toContain(first);
    });

    it('different account ids spread across the variant pool', () => {
        // 20 distinct ids should hit at least 4 different variants —
        // sanity check on the hash distribution.
        const seen = new Set<string>();
        for (let i = 0; i < 20; i++) {
            seen.add(
                categoryChipVariant(
                    `Category-${i}`,
                    'category',
                    `id-${i}`,
                ),
            );
        }
        expect(seen.size).toBeGreaterThanOrEqual(4);
    });
});
