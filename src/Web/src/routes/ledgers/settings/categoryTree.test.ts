import { describe, expect, it } from 'vitest';

import type { AccountSummary, CategoryNode } from '@/lib/types';

import { canMergeAway, categoryIdsMatching, eligibleMergeTarget } from './categoryTree';

// The helpers the Categories tab and its merge dialog share, so the row menu and the dialog cannot
// disagree about whether a category can be merged away, and a search never shows a match without
// the path that leads to it.

function node(id: string, name: string, parentId: string | null = null): CategoryNode {
    return {
        id, name, parentId, isSystem: false, isActive: true, categoryKind: 'expense',
        transactionCount: 0, childCount: 0, total: 0,
    } as CategoryNode;
}

function cat(id: string, parentId: string | null = null, over: Partial<AccountSummary> = {}): AccountSummary {
    return {
        id, ledgerId: 'l', parentId, name: id, accountType: 'category', categoryKind: 'expense',
        currencyCode: 'USD', isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
        holdingsAccountId: null, isTradeCommission: false, institutionName: null, ...over,
    } as AccountSummary;
}

describe('categoryIdsMatching', () => {
    const nodes = [
        node('auto', 'Auto'), node('fuel', 'Fuel', 'auto'), node('tires', 'Tires', 'auto'),
        node('home', 'Home'), node('rent', 'Rent', 'home'),
    ];

    it('keeps a match and every ancestor of it, and nothing else', () => {
        expect([...categoryIdsMatching(nodes, 'fuel')].sort()).toEqual(['auto', 'fuel']);
    });

    it('keeps a parent whose own name matches without keeping its children', () => {
        expect([...categoryIdsMatching(nodes, 'home')]).toEqual(['home']);
    });

    it('matches regardless of case in the name', () => {
        expect([...categoryIdsMatching(nodes, 'rent')].sort()).toEqual(['home', 'rent']);
    });

    it('keeps nothing when nothing matches', () => {
        expect(categoryIdsMatching(nodes, 'zzz').size).toBe(0);
    });

    it('ends the walk at a parent that is not in the list, instead of looping or throwing', () => {
        const orphan = [node('kid', 'Kid', 'missing')];
        expect([...categoryIdsMatching(orphan, 'kid')]).toEqual(['kid']);
    });
});

describe('eligibleMergeTarget', () => {
    const exclude = new Set(['src']);

    it('takes an active category that is not the source or beneath it, of whatever kind', () => {
        expect(eligibleMergeTarget(exclude)(cat('other', null, { categoryKind: 'income' }))).toBe(true);
    });

    it('refuses the source, an inactive category, and an account that is not a category', () => {
        const eligible = eligibleMergeTarget(exclude);
        expect(eligible(cat('src'))).toBe(false);
        expect(eligible(cat('old', null, { isActive: false }))).toBe(false);
        expect(eligible(cat('bank', null, { accountType: 'bank', categoryKind: null }))).toBe(false);
    });
});

describe('canMergeAway', () => {
    it('is true when another active category exists', () => {
        expect(canMergeAway('a', [cat('a'), cat('b')])).toBe(true);
    });

    it('is false for the only category', () => {
        expect(canMergeAway('a', [cat('a')])).toBe(false);
    });

    it('is false when the only other category is beneath it, because it would end up under itself', () => {
        expect(canMergeAway('a', [cat('a'), cat('b', 'a')])).toBe(false);
        expect(canMergeAway('b', [cat('a'), cat('b', 'a')])).toBe(true);
    });
});
