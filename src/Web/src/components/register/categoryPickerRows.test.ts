import { describe, it, expect } from 'vitest';
import type { AccountSummary } from '@/lib/types';
import {
    buildCategoryTreeRows,
    categoryPathMatches,
    categoryPathSegments,
} from './categoryPickerRows';

// Minimal category factory — the helpers read only id / name / parentId.
function cat(id: string, name: string, parentId: string | null): AccountSummary {
    return { id, name, parentId, accountType: 'category', categoryKind: 'expense' } as AccountSummary;
}

//  Bills
//    Cable Television
//    Electricity
//  Auto
//    Gas
const bills = cat('bills', 'Bills', null);
const electricity = cat('elec', 'Electricity', 'bills');
const cable = cat('cable', 'Cable Television', 'bills');
const auto = cat('auto', 'Auto', null);
const gas = cat('gas', 'Gas', 'auto');
const ALL = [bills, electricity, cable, auto, gas];
const byId = new Map(ALL.map((c) => [c.id, c]));

describe('categoryPathSegments', () => {
    it('walks the parentId chain root -> leaf, lowercased', () => {
        expect(categoryPathSegments(electricity, byId)).toEqual(['bills', 'electricity']);
        expect(categoryPathSegments(bills, byId)).toEqual(['bills']);
    });
});

describe('categoryPathMatches', () => {
    const segs = ['bills', 'electricity'];

    it('empty query matches everything', () => {
        expect(categoryPathMatches(segs, '')).toBe(true);
    });
    it('plain query matches any path component (leaf or ancestor)', () => {
        expect(categoryPathMatches(segs, 'electric')).toBe(true);   // leaf
        expect(categoryPathMatches(segs, 'bills')).toBe(true);      // ancestor
        expect(categoryPathMatches(segs, 'water')).toBe(false);
    });
    it('path query navigates: Bills/El -> Bills/Electricity', () => {
        expect(categoryPathMatches(segs, 'Bills/El')).toBe(true);
        expect(categoryPathMatches(['auto', 'gas'], 'Bills/El')).toBe(false);
    });
    it('path query anchors at the node, so it does not match a shallower ancestor', () => {
        // "Bills/El" should NOT match the "Bills" node itself (run must end at node).
        expect(categoryPathMatches(['bills'], 'Bills/El')).toBe(false);
    });
    it('matches a run anywhere in the path (Bills/El under Home)', () => {
        expect(categoryPathMatches(['home', 'bills', 'electricity'], 'Bills/El')).toBe(true);
    });
    it('trailing slash matches descendants of the run, not the run node itself', () => {
        expect(categoryPathMatches(['bills', 'electricity'], 'Bills/')).toBe(true);  // a child
        expect(categoryPathMatches(['bills'], 'Bills/')).toBe(false);                // Bills itself
    });
});

describe('buildCategoryTreeRows', () => {
    it('browse (empty query): full tree, root-first, siblings alpha, indented', () => {
        const rows = buildCategoryTreeRows(ALL, byId, '');
        expect(rows.map((r) => [r.account.name, r.depth])).toEqual([
            ['Auto', 0],
            ['Gas', 1],
            ['Bills', 0],
            ['Cable Television', 1],
            ['Electricity', 1],
        ]);
    });

    it('filter by leaf shows the leaf UNDER its ancestor (pruned tree)', () => {
        const rows = buildCategoryTreeRows(ALL, byId, 'electricity');
        expect(rows.map((r) => r.account.name)).toEqual(['Bills', 'Electricity']);
    });

    it('trailing-slash path lists the parent subtree (children only)', () => {
        const rows = buildCategoryTreeRows(ALL, byId, 'Bills/');
        // Bills shown as context; its children shown; Auto branch pruned.
        expect(rows.map((r) => r.account.name)).toEqual(['Bills', 'Cable Television', 'Electricity']);
    });

    it('re-parents a category whose parent is not in the eligible set to a root', () => {
        // Only Electricity eligible (its parent Bills is filtered out) -> it roots.
        const rows = buildCategoryTreeRows([electricity], byId, '');
        expect(rows.map((r) => [r.account.name, r.depth])).toEqual([['Electricity', 0]]);
    });
});
