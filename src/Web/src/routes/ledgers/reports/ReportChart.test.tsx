import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import { ticks } from '@/components/chart/scale';

import { ReportChart, axisLabels, axisMoneyFor } from './ReportChart';
import type { PivotRow } from './ReportView';

function row(
    name: string,
    total: number,
    byPeriod: Record<string, number> = {},
    parentId: string | null = null,
): PivotRow {
    return {
        key: name,
        name,
        parentId,
        depth: parentId === null ? 0 : 1,
        byPeriod: new Map(Object.entries(byPeriod)),
        total,
    };
}

/** The bars the chart itself draws, excluding the 8x8 legend swatches. */
const bars = (c: HTMLElement) =>
    [...c.querySelectorAll('svg rect')].filter((r) => r.getAttribute('width') !== '8');

/** The coloured bar inside a ranked row — not the track, not the zero rule. */
const rankedBar = (item: HTMLElement) =>
    item.querySelector<HTMLElement>('[class*="bg-cat-"]')!;

const PERIODS = ['2026-01', '2026-02'];

describe('the spec picks the shape', () => {
    it('lists one ROW per group when there is no time bucket', () => {
        // Ranked is HTML, not SVG. A ranking is a table with a magnitude
        // column; drawn in the shared viewBox the names would be stretched by
        // preserveAspectRatio="none", which is why the kit draws its labels and
        // rules as HTML overlays already.
        render(
            <ReportChart
                rows={[row('Bills', 300), row('Food', 200), row('Fuel', 100)]}
                periods={[]}
                bucketed={false}
            />,
        );
        expect(screen.getAllByRole('listitem')).toHaveLength(3);
    });

    it('names every group in the ranking, so no legend is needed', () => {
        // The reason for the pivot. Vertical bars had nowhere to put a name,
        // which forced a legend — and a dozen colour swatches to match up is
        // the same unreadable forest as a dozen hairline bars, moved below the
        // chart.
        render(
            <ReportChart
                rows={[row('Bills', 300), row('Groceries', 200)]}
                periods={[]}
                bucketed={false}
            />,
        );
        expect(screen.getByText('Bills')).toBeInTheDocument();
        expect(screen.getByText('Groceries')).toBeInTheDocument();
        expect(screen.getByText('$300.00')).toBeInTheDocument();
    });

    it('renders nothing at all for an empty result', () => {
        const { container } = render(
            <ReportChart rows={[]} periods={[]} bucketed={false} />,
        );
        expect(container.querySelector('svg')).toBeNull();
        expect(container.querySelector('[role="list"]')).toBeNull();
    });
});

describe('signs survive the drawing', () => {
    it('runs a negative ranked bar LEFT of the zero rule', () => {
        // A refund nets a category negative. Measured from a width alone it
        // would run right like everything else and read as spending.
        const { container } = render(
            <ReportChart
                rows={[row('Bills', 300), row('Clothing', -120)]}
                periods={[]}
                bucketed={false}
            />,
        );

        const [positive, negative] = screen.getAllByRole('listitem');

        // The negative hangs off the midpoint to the LEFT; the positive starts
        // at the midpoint and runs right.
        expect(rankedBar(negative!).style.right).toBe('50%');
        expect(rankedBar(positive!).style.left).toBe('50%');
        // ...and the rule they are read against is drawn, because something
        // crosses it.
        expect(container.querySelector('.bg-border-strong')).not.toBeNull();
    });

    it('keeps an all-negative ranking left of the line rather than rebasing it', () => {
        // Three negatives do not get a midpoint moved over to meet them, which
        // would draw them running right — as growth.
        render(
            <ReportChart
                rows={[row('A', -30), row('B', -60), row('C', -90)]}
                periods={[]}
                bucketed={false}
            />,
        );

        const drawn = screen.getAllByRole('listitem').map((i) => rankedBar(i));
        expect(drawn).toHaveLength(3);
        for (const bar of drawn) expect(bar.style.right).toBe('50%');
        // The widest reaches the left edge and no further: half the track, not
        // all of it.
        expect(drawn[0]!.style.width).toBe('50%');
    });

    it('uses the whole track and no zero rule when nothing is negative', () => {
        // The split track is not free — half of it would sit empty on the
        // ordinary report, where every category is spending.
        const { container } = render(
            <ReportChart
                rows={[row('Bills', 300), row('Food', 200)]}
                periods={[]}
                bucketed={false}
            />,
        );

        expect(screen.getByText('Bills')).toBeInTheDocument();
        const widest = rankedBar(screen.getAllByRole('listitem')[0]!);
        expect(widest.style.width).toBe('100%');
        expect(widest.style.right).toBe('');
        expect(container.querySelector('.bg-border-strong')).toBeNull();
    });

    it('stacks a negative DOWNWARD rather than into the segment above it', () => {
        // THE ASSERTION STACKING TURNS ON. An earlier version refused to stack
        // at all because one negative value puts a segment below the segment it
        // is meant to sit on — true of a single running total, false of two.
        // Each column reads as gross one way above the line and gross the other
        // below, which is what a cash-flow column means anyway.
        const { container } = render(
            <ReportChart
                rows={[
                    row('Bills', 300, { '2026-01': 300 }),
                    row('Refund', -100, { '2026-01': -100 }),
                ]}
                periods={['2026-01']}
                bucketed
                stack="stacked"
            />,
        );

        const drawn = bars(container);
        expect(drawn).toHaveLength(2);
        const tops = drawn.map((r) => Number(r.getAttribute('y')));
        const bottoms = drawn.map((r, i) => tops[i]! + Number(r.getAttribute('height')));

        // They meet at the zero line and run opposite ways: the bottom of the
        // positive is the top of the negative.
        expect(Math.min(...bottoms)).toBeCloseTo(Math.max(...tops), 1);
    });
});

describe('stacked and grouped answer different questions', () => {
    const ROWS = [
        row('A', 300, { '2026-01': 100, '2026-02': 200 }),
        row('B', 200, { '2026-01': 150, '2026-02': 50 }),
    ];

    it('stacks into ONE column per period', () => {
        const { container } = render(
            <ReportChart rows={ROWS} periods={PERIODS} bucketed stack="stacked" />,
        );
        // Two series over two periods is still four rects, but they share two
        // x positions rather than occupying four.
        expect(bars(container)).toHaveLength(4);
        expect(new Set(bars(container).map((r) => r.getAttribute('x'))).size).toBe(2);
    });

    it('draws a bar per group PER PERIOD when grouped', () => {
        const { container } = render(
            <ReportChart rows={ROWS} periods={PERIODS} bucketed stack="grouped" />,
        );
        expect(new Set(bars(container).map((r) => r.getAttribute('x'))).size).toBe(4);
    });

    it('carries every series when stacked, where grouped has to cap', () => {
        // A cap that is not stated reads as "this is everything", which is the
        // one thing a report must never imply. 40 periods leaves 15px a
        // cluster, so at a 3px floor only 5 series fit side by side — while
        // stacked needs one column per period whatever the series count, so
        // the cap applies to grouped alone.
        const many = Array.from({ length: 12 }, (_, i) =>
            row(`Cat ${i}`, 100 - i, { p0: 10 }));
        const periods = Array.from({ length: 40 }, (_, i) => `p${i}`);

        const grouped = render(
            <ReportChart rows={many} periods={periods} bucketed stack="grouped" />,
        );
        expect(screen.getByText(/more not shown/)).toBeInTheDocument();
        grouped.unmount();

        render(<ReportChart rows={many} periods={periods} bucketed stack="stacked" />);
        // Anchored on both ends of the legend, so "no note" means nothing was
        // dropped rather than nothing rendered.
        expect(screen.getByText('Cat 0')).toBeInTheDocument();
        expect(screen.getByText('Cat 11')).toBeInTheDocument();
        expect(screen.queryByText(/more not shown/)).toBeNull();
    });
});

describe('a roll-up is not drawn twice', () => {
    it('omits a parent whose children are also in the result', () => {
        // Parent and children are the same money. Drawing both makes the
        // parent look like a peer at twice its true share.
        render(
            <ReportChart
                rows={[
                    row('Bills', 300),
                    row('Electric', 100, {}, 'Bills'),
                    row('Gas', 200, {}, 'Bills'),
                ]}
                periods={[]}
                bucketed={false}
            />,
        );
        expect(screen.getAllByRole('listitem')).toHaveLength(2);
        expect(screen.getByText('Electric')).toBeInTheDocument();
        expect(screen.queryByText('Bills')).toBeNull();
    });
});

describe('the chart says what it shows', () => {
    it('names the largest series in the accessible label', () => {
        // A chart's accessible name is all a screen reader gets, so "spending
        // by category" would be a wasted sentence.
        render(
            <ReportChart
                rows={[row('Bills', 300), row('Food', 200)]}
                periods={[]}
                bucketed={false}
            />,
        );
        const label = screen.getByRole('list').getAttribute('aria-label');
        expect(label).toContain('Bills');
        expect(label).toContain('2 series');
    });
});

describe('axisMoneyFor', () => {
    it('never prints two ticks the same', () => {
        // THE DEFECT. Rounding each value to the nearest thousand on its own
        // gave the last two ticks below, half a thousand apart, the same
        // abbreviated label — an axis with two rules carrying
        // the same number, which is worse than a long label because it reads
        // as correct rather than as missing.
        const t = [0, 500, 1000, 1500, 2000];
        const labels = t.map(axisMoneyFor(t));

        expect(new Set(labels).size).toBe(t.length);
        expect(labels).toEqual(['$0', '$0.5k', '$1k', '$1.5k', '$2k']);
    });

    it('drops the digits the step does not use', () => {
        // The point of abbreviating. A step of whole thousands cannot
        // distinguish anything finer than a thousand, so a ".0" before the k
        // would be a decimal carrying no information.
        const t = [0, 2000, 4000, 6000];
        expect(t.map(axisMoneyFor(t))).toEqual(['$0', '$2k', '$4k', '$6k']);
    });

    it('keeps whole dollars when the step is finer than a tenth of the unit', () => {
        // A step of a twentieth of a thousand needs two decimals of a thousand
        // to stay distinct, and that abbreviation is longer than the number it
        // abbreviates.
        const t = [1000, 1050, 1100, 1150];
        expect(t.map(axisMoneyFor(t))).toEqual(['$1000', '$1050', '$1100', '$1150']);
    });

    it('uses ONE unit for the whole axis', () => {
        // Picked per value, the half-thousand tick would print in whole dollars
        // beside the thousand in k —
        // two scales on one axis, which is a harder read than either alone.
        const t = [0, 500, 1000];
        const labels = t.map(axisMoneyFor(t));
        expect(labels.every((l, i) => i === 0 || l.endsWith('k'))).toBe(true);
    });

    it('carries the sign, because every measure here can go negative', () => {
        const t = [-4000, -2000, 0, 2000];
        expect(t.map(axisMoneyFor(t))).toEqual(['-$4k', '-$2k', '$0', '$2k']);
    });

    it('steps up to millions, against real ticks rather than invented ones', () => {
        // Fed from `ticks` itself: a hand-written tick list can assert a
        // formatter against spacing the producer never emits, which is how an
        // axis passes its test and still prints a duplicate on screen.
        const t = ticks([0, 1_000_000], 4);
        const labels = t.map(axisMoneyFor(t));

        expect(new Set(labels).size).toBe(t.length);
        expect(labels[0]).toBe('$0');
        expect(labels.at(-1)).toBe('$1M');
    });

    it('leaves no duplicate on any axis `ticks` can produce', () => {
        for (const max of [8, 95, 420, 2_000, 7_500, 38_900, 250_000, 4_200_000]) {
            for (const count of [3, 4, 5, 6]) {
                const t = ticks([0, max], count);
                const labels = t.map(axisMoneyFor(t));
                expect(new Set(labels).size, `${max} over ${count}: ${labels.join(' ')}`)
                    .toBe(t.length);
            }
        }
    });
});

describe('axisLabels', () => {
    it('keeps every label when they fit', () => {
        expect(axisLabels(['a', 'b', 'c'])).toEqual(['a', 'b', 'c']);
    });

    it('thins the TEXT but never the columns', () => {
        // Every period still gets a column — a quiet month is a fact about the
        // ledger. Only the labels are thinned, or they overlap into a band.
        const periods = Array.from({ length: 36 }, (_, i) => `m${i}`);
        const labels = axisLabels(periods);
        expect(labels.length).toBeLessThanOrEqual(12);
        expect(labels[0]).toBe('m0');
    });
});
