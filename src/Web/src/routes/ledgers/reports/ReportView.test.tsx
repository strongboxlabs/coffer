import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { ReportSpec } from '@/types/report';

import { periodsOf, pivot, specFromSearch } from './ReportView';
import { RangePicker, describeRange } from './RangePicker';

describe('specFromSearch', () => {
    it('fills a bare URL in with a runnable spec', () => {
        // The bare path has to work: /reports/view with nothing on it is what
        // a hand-typed URL and a stale bookmark both look like.
        const spec = specFromSearch({});

        expect(spec.measure).toBe('Spending');
        expect(spec.groupBy).toBe('Category');
        expect(spec.dateMode).toBe('Sliding');
        expect(spec.windowCount).toBe(12);
        expect(spec.windowAlign).toBe(true);
    });

    it('does not invent a sliding window for a fixed range', () => {
        // A static spec with a count would be ambiguous in the stored row —
        // the resolver reads dateMode, but a leftover count is the kind of
        // thing a later reader treats as meaningful.
        const spec = specFromSearch({ dateMode: 'Static', from: '2025-01-01T00:00:00Z' });

        expect(spec.dateMode).toBe('Static');
        expect(spec.windowCount).toBeUndefined();
    });

    it('carries the bar shape through the URL', () => {
        // A report is SHARED as a URL and saved as one. A field the parser
        // drops is not a missing default, it is a chart that comes back as a
        // different chart than the one that was sent.
        expect(specFromSearch({}).chartStack).toBe('stacked');
        expect(specFromSearch({ chartStack: 'grouped' }).chartStack).toBe('grouped');
    });
});

describe('describeRange', () => {
    const base: ReportSpec = {
        measure: 'Spending', groupBy: 'Category', bucket: 'Month', dateMode: 'Sliding',
    };

    it('names a preset rather than describing its parts', () => {
        expect(describeRange({ ...base, windowCount: 12, windowUnit: 'Month', windowAlign: true }))
            .toBe('Last 12 months');
        expect(describeRange({ ...base, windowCount: 1, windowUnit: 'Year', windowAlign: true }))
            .toBe('Year to date');
    });

    it('distinguishes an aligned window from a rolling one of the same length', () => {
        // Same count and unit, different meaning: aligned gives twelve whole
        // months, rolling gives twelve months back from this instant. A label
        // that showed them identically would make the control a coin flip.
        const aligned = describeRange({ ...base, windowCount: 3, windowUnit: 'Month', windowAlign: true });
        const rolling = describeRange({ ...base, windowCount: 3, windowUnit: 'Month', windowAlign: false });

        expect(aligned).toBe('Last 3 months');
        expect(rolling).not.toBe(aligned);
    });

    it('reads out a fixed range as its dates', () => {
        expect(describeRange({
            ...base, dateMode: 'Static',
            from: '2025-01-01T00:00:00Z', to: '2026-01-01T00:00:00Z',
        })).toBe('2025-01-01 → 2026-01-01');
    });
});

describe('RangePicker', () => {
    const spec: ReportSpec = {
        measure: 'Spending', groupBy: 'Category', bucket: 'Month',
        dateMode: 'Sliding', windowCount: 12, windowUnit: 'Month', windowAlign: true,
    };

    it('groups presets by whether they move with today', async () => {
        const user = userEvent.setup();
        render(<RangePicker spec={spec} onChange={vi.fn()} />);
        await user.click(screen.getByRole('button', { name: /last 12 months/i }));

        // The mode is a heading, not a toggle — choosing a range and choosing
        // whether it slides is one act.
        expect(screen.getByText(/sliding — moves with today/i)).toBeInTheDocument();
        expect(screen.getByText('Fixed')).toBeInTheDocument();
    });

    it('a sliding preset clears any fixed dates it replaces', async () => {
        // Leaving `from`/`to` behind would store a spec that says Sliding and
        // carries a fixed range, and the next reader cannot tell which the
        // author meant.
        const onChange = vi.fn();
        const user = userEvent.setup();
        render(<RangePicker
            spec={{ ...spec, dateMode: 'Static', from: '2025-01-01T00:00:00Z', to: '2026-01-01T00:00:00Z' }}
            onChange={onChange} />);

        await user.click(screen.getByRole('button', { name: /2025-01-01/ }));
        await user.click(screen.getByRole('button', { name: 'Last 3 months' }));

        expect(onChange).toHaveBeenCalledWith(expect.objectContaining({
            dateMode: 'Sliding', windowCount: 3, windowUnit: 'Month', windowAlign: true,
            from: undefined, to: undefined,
        }));
    });

    it('a calendar year is a fixed range with an exclusive upper bound', async () => {
        const onChange = vi.fn();
        const user = userEvent.setup();
        render(<RangePicker spec={spec} onChange={onChange} />);
        await user.click(screen.getByRole('button', { name: /last 12 months/i }));

        const year = new Date().getUTCFullYear() - 1;
        await user.click(screen.getByRole('button', { name: String(year) }));

        // Upper bound is the NEXT January, because the API's `to` is
        // exclusive — an inclusive-looking 31 December would silently drop
        // everything posted on the last day of the year.
        expect(onChange).toHaveBeenCalledWith(expect.objectContaining({
            dateMode: 'Static',
            from: `${year}-01-01T00:00:00Z`,
            to: `${year + 1}-01-01T00:00:00Z`,
        }));
    });
});

describe('pivot', () => {
    const cell = (
        groupId: string, groupName: string, parentId: string | null, amount: number,
    ) => ({ period: null, groupId, groupName, parentId, amount });

    it('nests to any depth, not just two levels', () => {
        // The bug this exists for: the walk handled roots and their direct
        // children, and everything deeper was appended at the END with child
        // styling — so on a real ledger many unrelated categories appeared
        // beneath whichever parent happened to be last.
        const rows = pivot([
            cell('leaf', 'Hotels', 'mid', 10),
            cell('root', 'Personal', null, 100),
            cell('mid', 'Vacation', 'root', 50),
        ]);

        expect(rows.map((r) => [r.name, r.depth])).toEqual([
            ['Personal', 0],
            ['Vacation', 1],
            ['Hotels', 2],
        ]);
    });

    it('treats a row whose parent is absent as top level', () => {
        // Ordering and indentation used to disagree here: this function
        // called it a root, the renderer checked parentId === null and drew
        // it indented, so it looked like a child of the row above.
        const rows = pivot([
            cell('a', 'Alpha', null, 1),
            cell('orphan', 'Zeta', 'not-in-result', 2),
        ]);

        expect(rows.find((r) => r.name === 'Zeta')!.depth).toBe(0);
    });

    it('keeps siblings alphabetical within their parent', () => {
        const rows = pivot([
            cell('p', 'Parent', null, 0),
            cell('b', 'Beta', 'p', 1),
            cell('a', 'Alpha', 'p', 1),
        ]);

        expect(rows.map((r) => r.name)).toEqual(['Parent', 'Alpha', 'Beta']);
    });

    it('still shows a row caught in a cycle rather than dropping it', () => {
        // A cycle cannot be reached from the root walk. Dropping those rows
        // would leave a report whose total counts money no line accounts for.
        const rows = pivot([
            cell('x', 'Ex', 'y', 5),
            cell('y', 'Why', 'x', 7),
        ]);

        expect(rows).toHaveLength(2);
        expect(rows.every((r) => r.depth === 0 || r.depth === 1)).toBe(true);
    });
});

describe('periodsOf', () => {
    const row = (period: string, amount: number) => ({
        period, groupId: 'g', groupName: 'G', parentId: null, amount,
    });

    it('keeps a month with no activity as a column', () => {
        // The bug: the axis came from the rows, so a quiet month produced no
        // column at all — the gap read as "no column" rather than as zero, and
        // the months either side of it then looked adjacent when they are not.
        const periods = periodsOf(
            [row('2026-01', 10), row('2026-03', 20)],
            'Month',
            '2026-01-01T00:00:00Z',
            '2026-03-31T00:00:00Z',
        );

        expect(periods).toEqual(['2026-01', '2026-02', '2026-03']);
    });

    it('keeps trailing periods with no activity', () => {
        // "Last 12 months" means twelve columns. A report that quietly shrank
        // to the months that happen to have data would answer a different
        // question from the one its own range states.
        const periods = periodsOf(
            [row('2026-01', 10)],
            'Month',
            '2026-01-01T00:00:00Z',
            '2026-04-30T00:00:00Z',
        );

        expect(periods).toEqual(['2026-01', '2026-02', '2026-03', '2026-04']);
    });

    it('labels quarters and years the way the server does', () => {
        expect(periodsOf([], 'Quarter', '2026-01-01T00:00:00Z', '2026-07-01T00:00:00Z'))
            .toEqual(['2026-Q1', '2026-Q2', '2026-Q3']);
        expect(periodsOf([], 'Year', '2024-01-01T00:00:00Z', '2026-01-01T00:00:00Z'))
            .toEqual(['2024', '2025', '2026']);
    });

    it('falls back to the periods present when the range is open-ended', () => {
        // Nothing says how far an open axis should run, so the data is the
        // only honest source.
        expect(periodsOf([row('2026-05', 1), row('2026-02', 1)], 'Month', null, null))
            .toEqual(['2026-02', '2026-05']);
    });

    it('never drops a period the server labelled', () => {
        // If a boundary is computed one way here and another way there, the
        // figure must still appear rather than vanish from a table whose
        // total already counts it.
        const periods = periodsOf(
            [row('2026-12', 5)],
            'Month',
            '2026-01-01T00:00:00Z',
            '2026-02-01T00:00:00Z',
        );

        expect(periods).toContain('2026-12');
    });

    it('has no axis at all when the spec asked for no time bucket', () => {
        expect(periodsOf([row('2026-01', 1)], 'None', '2026-01-01T00:00:00Z', '2026-02-01T00:00:00Z'))
            .toEqual([]);
    });
});
