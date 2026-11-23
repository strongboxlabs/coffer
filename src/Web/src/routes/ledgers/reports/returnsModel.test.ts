import { describe, expect, it } from 'vitest';

import {
    axisLabelsFor, benchmarkLabel, benchmarkSymbol, describeReturnsSpec, returnsSpecFromSearch, spreadLabels,
} from './returnsModel';

const monthEnds = (count: number, startYear = 2019): string[] =>
    Array.from({ length: count }, (_, i) => {
        const y = startYear + Math.floor(i / 12);
        const m = (i % 12) + 1;
        const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
        return `${y}-${String(m).padStart(2, '0')}-${last}T00:00:00Z`;
    });

describe('axisLabelsFor', () => {
    it('labels a long monthly axis by year, at January, and leaves the last period blank', () => {
        const labels = axisLabelsFor(monthEnds(84), 'month');
        const drawn = labels.flatMap((l, i) => (l === '' ? [] : [i]));
        expect(drawn).toEqual([0, 12, 24, 36, 48, 60, 72]);
        expect(labels[0]).toBe('2019');
        expect(labels[72]).toBe('2025');
        expect(labels.at(-1)).toBe('');
    });

    it('spaces the year labels evenly even when the window starts mid-year', () => {
        const ends = monthEnds(40).slice(5);
        const drawn = axisLabelsFor(ends, 'month').flatMap((l, i) => (l === '' ? [] : [i]));
        const gaps = drawn.slice(1).map((d, i) => d - drawn[i]!);
        expect(drawn.length).toBeGreaterThan(2);
        expect(new Set(gaps)).toEqual(new Set([12]));
    });

    it('labels quarters by year at Q1', () => {
        const ends = ['2020-03-31', '2020-06-30', '2020-09-30', '2020-12-31', '2021-03-31', '2021-06-30'].map((d) => `${d}T00:00:00Z`);
        expect(axisLabelsFor(ends, 'quarter')).toEqual(['2020', '', '', '', '2021', '']);
    });

    it('keeps the period labels where there is less than a year to space by', () => {
        expect(axisLabelsFor(monthEnds(6), 'month')).toEqual(['2019-01', '2019-02', '2019-03', '2019-04', '2019-05', '2019-06']);
    });

    it('keeps year labels for a yearly axis', () => {
        expect(axisLabelsFor(['2020-12-31T00:00:00Z', '2021-12-31T00:00:00Z'], 'year')).toEqual(['2020', '2021']);
    });
});

describe('spreadLabels', () => {
    it('keeps at least the gap between neighbours, whatever order they came in', () => {
        const out = spreadLabels([50, 52, 51, 10], 8);
        const sorted = [...out].sort((a, b) => a - b);
        for (let k = 1; k < sorted.length; k++) expect(sorted[k]! - sorted[k - 1]!).toBeGreaterThanOrEqual(8 - 1e-9);
        expect(out[3]).toBe(10);
    });

    it('moves a stack that would run past the limit back up, still apart', () => {
        const out = spreadLabels([95, 96, 97], 8, 97);
        expect(Math.max(...out)).toBeLessThanOrEqual(97);
        const sorted = [...out].sort((a, b) => a - b);
        expect(sorted[1]! - sorted[0]!).toBeGreaterThanOrEqual(8 - 1e-9);
        expect(sorted[2]! - sorted[1]!).toBeGreaterThanOrEqual(8 - 1e-9);
    });
});

describe('the benchmark in a returns spec (ADR-0114 D1)', () => {
    it('keeps a symbol of the server’s shape and drops any other', () => {
        expect(benchmarkSymbol('VT')).toBe('VT');
        expect(benchmarkSymbol('XCE.TO')).toBe('XCE.TO');
        expect(benchmarkSymbol('^SP500TR')).toBe('^SP500TR');
        expect(benchmarkSymbol('A'.repeat(32))).toBe('A'.repeat(32));
        expect(benchmarkSymbol('A'.repeat(33))).toBeUndefined();
        expect(benchmarkSymbol('vt')).toBeUndefined();
        expect(benchmarkSymbol('V T')).toBeUndefined();
        expect(benchmarkSymbol('')).toBeUndefined();
        expect(benchmarkSymbol(null)).toBeUndefined();
        expect(benchmarkSymbol(7)).toBeUndefined();
    });

    it('carries the pick from the search, a saved null reading as none', () => {
        expect(returnsSpecFromSearch({ kind: 'returns', benchmark: 'VT' }).benchmark).toBe('VT');
        expect(returnsSpecFromSearch({ kind: 'returns', benchmark: null }).benchmark).toBeUndefined();
        expect(returnsSpecFromSearch({ kind: 'returns', returnsShow: 'gain', benchmark: 'VT' }).benchmark).toBe('VT');
    });

    it('names the pick where it is compared, and not on Gain', () => {
        const spec = returnsSpecFromSearch({ kind: 'returns', benchmark: 'VT' });
        expect(describeReturnsSpec(spec)).toBe('Index · monthly · since inception · vs VT');
        expect(describeReturnsSpec(spec, false)).toBe('Table · monthly · since inception · vs VT');
        expect(describeReturnsSpec({ ...spec, returnsShow: 'gain' })).toBe('Gain · monthly · since inception');
        expect(describeReturnsSpec({ ...spec, benchmark: undefined })).toBe('Index · monthly · since inception');
    });

    it('labels a fund by name and symbol, and a nameless one by its symbol', () => {
        expect(benchmarkLabel('Total World Fund', 'VT')).toBe('Total World Fund (VT)');
        expect(benchmarkLabel('VT', 'VT')).toBe('VT');
        expect(benchmarkLabel(null, 'VT')).toBe('VT');
    });
});
