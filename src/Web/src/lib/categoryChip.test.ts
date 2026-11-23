import { describe, it, expect } from 'vitest';

import { categoryChipVariant, categoryDotClass, systemCategoryIds } from './categoryChip';

describe('categoryChipVariant', () => {
    it('returns `default` when no counterparty is given', () => {
        expect(categoryChipVariant(null, null, null, false)).toBe('default');
    });

    it('returns `xfer` for real-account counterparties (transfers)', () => {
        expect(categoryChipVariant('Example Bank Checking', 'bank', 'a1', false)).toBe('xfer');
        expect(categoryChipVariant('Rewards Card', 'credit_card', 'a2', false)).toBe('xfer');
        expect(categoryChipVariant('Workplace 401(k)', 'investment', 'a3', false)).toBe('xfer');
        expect(categoryChipVariant('Holdings · IDX', 'holding', 'a4', false)).toBe('xfer');
    });

    it('matches known category-name patterns case-insensitively', () => {
        // Patterns are first-match wins; assertions cover the substrings.
        expect(categoryChipVariant('Groceries', 'category', 'c1', false)).toBe('groc');
        expect(categoryChipVariant('Bills:Electricity', 'category', 'c2', false)).toBe('util');
        expect(categoryChipVariant('Dining Out', 'category', 'c3', false)).toBe('din');
        expect(categoryChipVariant('Mortgage', 'category', 'c4', false)).toBe('house');
        expect(categoryChipVariant('Subscriptions:Streaming', 'category', 'c5', false)).toBe('sub');
        expect(categoryChipVariant('Transit Fares', 'category', 'c6', false)).toBe('tran');
        expect(categoryChipVariant('Salary', 'category', 'c7', false)).toBe('sal');
        expect(categoryChipVariant('Phone Bill', 'category', 'c8', false)).toBe('phone');
        expect(categoryChipVariant('Entertainment', 'category', 'c9', false)).toBe('rec');
    });

    it('falls back to a deterministic hash-assigned variant for unknown names', () => {
        // Same id → same variant across calls. We don't assert which
        // bucket lands; only that it's one of the auto-assign pool and
        // the result is stable.
        const accountId = '00000000-0000-0000-0000-0000000000aa';
        const first = categoryChipVariant('Hobbies', 'category', accountId, false);
        const second = categoryChipVariant('Hobbies', 'category', accountId, false);
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
                    false,
                ),
            );
        }
        expect(seen.size).toBeGreaterThanOrEqual(4);
    });

    it('draws the system Uncategorized category neutral, whatever its name would hash to', () => {
        // Some id makes the name hash to a colour; the system flag wins over it.
        const ids = Array.from({ length: 20 }, (_, i) => `uncat-${i}`);
        const hashed = ids.map((id) => categoryChipVariant('Uncategorized', 'category', id, false));
        expect(hashed.some((v) => v !== 'default')).toBe(true);
        for (const id of ids) {
            expect(categoryChipVariant('Uncategorized', 'category', id, true)).toBe('default');
            expect(categoryDotClass('Uncategorized', id, true)).toBe('bg-chip-neutral-text');
        }
    });

    it('leaves a person\'s own category of the same name its hashed colour', () => {
        const id = Array.from({ length: 20 }, (_, i) => `own-${i}`)
            .find((candidate) => categoryChipVariant('Uncategorized', 'category', candidate, false) !== 'default')!;
        expect(categoryChipVariant('Uncategorized', 'category', id, false)).not.toBe('default');
    });
});

describe('systemCategoryIds', () => {
    it('is the system categories only: not a holdings sibling, not a person\'s category', () => {
        const ids = systemCategoryIds([
            { id: 'uncat', isSystem: true, accountType: 'category' },
            { id: 'holdings', isSystem: true, accountType: 'investment' },
            { id: 'own', isSystem: false, accountType: 'category' },
        ]);
        expect([...ids]).toEqual(['uncat']);
        expect(systemCategoryIds(undefined).size).toBe(0);
    });
});
