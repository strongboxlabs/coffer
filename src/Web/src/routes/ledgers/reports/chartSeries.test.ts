import { describe, expect, it } from 'vitest';

import { categoryChipVariant } from '@/lib/categoryChip';
import type { ReportGroupBy } from '@/types/report';

import {
    ALL_GROUPS, CHART_HUES, SHADES, assignHues, chartTopN, hueFill, otherLabel, shadesOf,
    type SeriesIdentity,
} from './chartSeries';
import { fullGroupName, groupLabel, groupNameParts, leafOnly } from './groupLabel';

// Every name here is synthetic. A name only matters through the chip's name
// patterns, so the fixtures carry a pattern word and a letter.

const cat = (key: string, name: string, isSystemCategory = false): SeriesIdentity => ({
    key, groupId: `id-${key}`, name, isSystemCategory,
});

const own = (s: SeriesIdentity) => categoryChipVariant(s.name, 'category', s.groupId, s.isSystemCategory);

/** A category with no name pattern whose id hashes to the slate chip. */
function slateCategory(key: string): SeriesIdentity {
    for (let i = 0; i < 1000; i++) {
        const s = { key, groupId: `slate-${i}`, name: `Category ${key}`, isSystemCategory: false };
        if (own(s) === 'xfer') return s;
    }
    throw new Error('no id hashes to slate');
}

describe('a category chart', () => {
    it('gives each category the colour its chip has', () => {
        const series = [cat('a', 'Dining A'), cat('b', 'Grocer B'), cat('c', 'Phone C')];
        const hues = assignHues(series, 'Category');

        expect(hues.get('a')).toBe('din');
        expect(hues.get('b')).toBe('groc');
        expect(hues.get('c')).toBe('phone');
    });

    it('never gives two series the same hue, nor slate to any', () => {
        // Nine categories, most of them sharing a chip colour.
        const series = [
            cat('a', 'Grocer A'), cat('b', 'Grocer B'), cat('c', 'Grocer C'),
            cat('d', 'Dining D'), cat('e', 'Dining E'), cat('f', 'Rent F'),
            slateCategory('g'), cat('h', 'Uncategorized H', true), cat('i', 'Phone I'),
        ];
        const hues = [...assignHues(series, 'Category').values()];

        expect(hues).toHaveLength(CHART_HUES.length);
        expect(new Set(hues).size).toBe(CHART_HUES.length);
        for (const h of hues) expect(CHART_HUES).toContain(h);
    });

    it('lets the larger of two categories that share a colour keep it', () => {
        const series = [cat('large', 'Grocer A'), cat('small', 'Grocer B')];
        const hues = assignHues(series, 'Category');

        expect(hues.get('large')).toBe('groc');
        expect(hues.get('small')).not.toBe('groc');
    });

    it('borrows only after every category has taken its own colour', () => {
        // The clash is second in size and its first free hue is the third
        // category's own colour. Borrowing in the same pass would take it and
        // push the third category off its own colour.
        const series = [cat('first', 'Grocer A'), cat('clash', 'Grocer B'), cat('third', 'Dining C')];
        const hues = assignHues(series, 'Category');

        expect(hues.get('first')).toBe('groc');
        expect(hues.get('third')).toBe('din');
        expect(hues.get('clash')).toBe('house');
    });

    it('moves a category whose own colour is slate onto a hue no other category holds', () => {
        // Slate is Other's. The largest category here owns slate, and borrows
        // without taking the smaller category's own colour.
        const slate = slateCategory('slate');
        expect(own(slate)).toBe('xfer');
        const hues = assignHues([slate, cat('small', 'Grocer B')], 'Category');

        expect(hues.get('small')).toBe('groc');
        expect(hues.get('slate')).toBe('din');
    });

    it('moves the ledger\'s Uncategorized, whose chip is the neutral one, onto a hue', () => {
        const system = cat('system', 'Grocer A', true);
        expect(own(system)).toBe('default');
        const hues = assignHues([system, cat('other', 'Grocer B')], 'Category');

        // The system category does not hold the colour its name would give it.
        expect(hues.get('other')).toBe('groc');
        expect(hues.get('system')).toBe('din');
    });
});

describe('a chart grouped by something with no colour of its own', () => {
    it.each<ReportGroupBy>(['Payee', 'Account', 'Tag', 'CategoryKind'])(
        'takes hues by rank when grouped by %s',
        (groupBy) => {
            // Names that would clash on a hue late in the order as categories,
            // so the category rule would give a different sequence.
            const series = ['a', 'b', 'c', 'd'].map((k) => cat(k, `Phone ${k}`));
            const asCategories = [...assignHues(series, 'Category').values()];
            expect(asCategories).not.toEqual(CHART_HUES.slice(0, 4));

            expect([...assignHues(series, groupBy).values()]).toEqual(CHART_HUES.slice(0, 4));
        },
    );
});

describe('chartTopN', () => {
    it('asks for every group when the report has no cap', () => {
        expect(chartTopN({})).toBe(ALL_GROUPS);
    });

    it('keeps the report\'s cap whatever it is: the colour count never lowers it', () => {
        expect(chartTopN({ topN: 5 })).toBe(5);
        expect(chartTopN({ topN: 20 })).toBe(20);
        expect(chartTopN({ topN: 50 })).toBe(50);
    });
});

describe('shades, past the nine hues', () => {
    const many = (n: number): SeriesIdentity[] =>
        Array.from({ length: n }, (_, i) => cat(`s${i}`, `Series ${i}`));

    it('gives the first nine the family\'s own colour and the next nine a deeper one', () => {
        const series = many(20);
        const shades = shadesOf(series, assignHues(series, 'Payee'));
        expect(series.map((s) => shades.get(s.key))).toEqual([
            ...Array(9).fill(0), ...Array(9).fill(1), 2, 2,
        ]);
    });

    it('starts over after the last shade, so any number of series gets a fill', () => {
        const series = many(40);
        const shades = shadesOf(series, assignHues(series, 'Payee'));
        expect(Math.max(...shades.values())).toBe(SHADES - 1);
        expect(shades.get('s27')).toBe(0);
    });

    it('draws no two of the first 27 series in the same fill', () => {
        const series = many(27);
        const hues = assignHues(series, 'Payee');
        const shades = shadesOf(series, hues);
        const fills = series.map((s) => hueFill(hues.get(s.key)!, shades.get(s.key)));
        expect(new Set(fills).size).toBe(27);
    });
});

describe('otherLabel', () => {
    it('counts the groups it folds', () => {
        expect(fullGroupName(otherLabel({ count: 2 }, 'Category'))).toBe('Other (2)');
    });

    it('names the one group the server says it folds', () => {
        const only = { groupId: 'cut-b', groupName: 'Cut B', groupPath: ['Cut B'], isDirect: false };
        expect(fullGroupName(otherLabel({ count: 1, only }, 'Category'))).toBe('Cut B');
    });

    it('names a folded subcategory under its whole path', () => {
        const only = { groupId: 'cut-l', groupName: 'Cut L', groupPath: ['Cut P', 'Cut L'], isDirect: false };
        expect(otherLabel({ count: 1, only }, 'Category')).toEqual({ parents: ['Cut P'], leaf: 'Cut L' });
    });

    it('counts a single group when the server does not name it', () => {
        expect(fullGroupName(otherLabel({ count: 1 }, 'Category'))).toBe('Other (1)');
        expect(fullGroupName(otherLabel({ count: 1, only: null }, 'Category'))).toBe('Other (1)');
    });

    it('names a single folded kind as the table does', () => {
        const only = { groupId: 'kind-adjustment', groupName: 'adjustment', groupPath: ['adjustment'], isDirect: false };
        expect(fullGroupName(otherLabel({ count: 1, only }, 'CategoryKind'))).toBe(groupLabel('CategoryKind', 'adjustment'));
        expect(groupLabel('CategoryKind', 'adjustment')).not.toBe('adjustment');
    });
});

describe('groupNameParts', () => {
    it('puts a category under every category above it, top level first', () => {
        const parts = groupNameParts('Category', 'Name C', ['Name A', 'Name B', 'Name C']);
        expect(parts).toEqual({ parents: ['Name A', 'Name B'], leaf: 'Name C' });
        expect(fullGroupName(parts)).toBe('Name A/Name B/Name C');
    });

    it('names a top-level category by itself', () => {
        expect(groupNameParts('Category', 'Name A', ['Name A'])).toEqual({ parents: [], leaf: 'Name A' });
    });

    it('never splits a name on the separator: the server sends the parts', () => {
        const parts = groupNameParts('Category', 'Name B/C', ['Name A', 'Name B/C']);
        expect(parts).toEqual({ parents: ['Name A'], leaf: 'Name B/C' });
    });

    it.each(['Payee', 'Account', 'Tag'] as const)('gives a %s no parents', (groupBy) => {
        expect(groupNameParts(groupBy, 'Name A', ['Name A'])).toEqual({ parents: [], leaf: 'Name A' });
    });

    it('names a kind as the table does', () => {
        expect(groupNameParts('CategoryKind', 'expense', ['expense']).leaf).toBe(groupLabel('CategoryKind', 'expense'));
    });

    it('drops the parents only when asked for the leaf', () => {
        expect(leafOnly('Category', 'Name C')).toEqual({ parents: [], leaf: 'Name C' });
    });
});
