import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';

import { ticks } from '@/components/chart/scale';
import { currencySymbol, formatCurrency } from '@/lib/money';
import type { ReportRow } from '@/types/report';

import { ReportChart, axisMoneyFor } from './ReportChart';
import { CHART_HUES, OTHER_FILL, RANKED_BG, hueFill } from './chartSeries';
import { chartRowsOf, pivot as pivotRows, type ChartRow } from './ReportView';

/** The rows as the chart takes them: every figure present. */
const pivot = (cells: readonly ReportRow[]): ChartRow[] => chartRowsOf(pivotRows(cells))!;

function row(
    name: string,
    total: number,
    byPeriod: Record<string, number> = {},
    parentId: string | null = null,
): ChartRow {
    return {
        key: name,
        name,
        path: [name],
        groupId: name,
        parentId,
        depth: parentId === null ? 0 : 1,
        currencyCode: 'USD',
        byPeriod: new Map(Object.entries(byPeriod)),
        total,
        rateUnavailable: [],
    };
}

/** The bars the chart itself draws, excluding the 8x8 legend swatches. */
const bars = (c: HTMLElement) =>
    [...c.querySelectorAll('svg rect')].filter((r) => r.getAttribute('width') !== '8');

/** The filled bar inside a ranked row — not the track, not the zero rule. */
const rankedBar = (item: HTMLElement) =>
    item.querySelector<HTMLElement>(`[class~="${RANKED_BG}"]`)!;

const PERIODS = ['2026-01', '2026-02'];

describe('the spec picks the shape', () => {
    it('lists one ROW per group when there is no time bucket', () => {
        // Ranked is HTML, not SVG. A ranking is a table with a magnitude
        // column; drawn in the shared viewBox the names would be stretched by
        // preserveAspectRatio="none", which is why the kit draws its labels and
        // rules as HTML overlays already.
        render(
            <ReportChart currency="USD" groupBy="Category"
                rows={[row('Bills', 300), row('Food', 200), row('Fuel', 100)]}
                periods={[]}
                bucketed={false}
            />,
        );
        expect(screen.getAllByRole('listitem')).toHaveLength(3);
    });

    it('names every group in the ranking, so no legend is needed', () => {
        // The reason for horizontal bars. Vertical bars have nowhere to put a
        // name, which forces a legend — and a dozen colour swatches to match up is
        // the same unreadable forest as a dozen hairline bars, moved below the
        // chart.
        render(
            <ReportChart currency="USD" groupBy="Category"
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
            <ReportChart currency="USD" groupBy="Category" rows={[]} periods={[]} bucketed={false} />,
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
            <ReportChart currency="USD" groupBy="Category"
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
            <ReportChart currency="USD" groupBy="Category"
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
            <ReportChart currency="USD" groupBy="Category"
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
        // THE ASSERTION STACKING TURNS ON. "One negative value puts a segment
        // below the segment it is meant to sit on" is true of a single running
        // total, false of two.
        // Each column reads as gross one way above the line and gross the other
        // below, which is what a cash-flow column means anyway.
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category"
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
            <ReportChart currency="USD" groupBy="Category" rows={ROWS} periods={PERIODS} bucketed stack="stacked" />,
        );
        // Two series over two periods is still four rects, but they share two
        // x positions rather than occupying four.
        expect(bars(container)).toHaveLength(4);
        expect(new Set(bars(container).map((r) => r.getAttribute('x'))).size).toBe(2);
    });

    it('draws a bar per group PER PERIOD when grouped', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" rows={ROWS} periods={PERIODS} bucketed stack="grouped" />,
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
            <ReportChart currency="USD" groupBy="Category" rows={many} periods={periods} bucketed stack="grouped" />,
        );
        expect(screen.getByText(/more not shown/)).toBeInTheDocument();
        grouped.unmount();

        render(<ReportChart currency="USD" groupBy="Category" rows={many} periods={periods} bucketed stack="stacked" />);
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
            <ReportChart currency="USD" groupBy="Category"
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
            <ReportChart currency="USD" groupBy="Category"
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
        // Rounding each value to the nearest thousand on its own would give
        // the last two ticks below, half a thousand apart, the same
        // abbreviated label — an axis with two rules carrying
        // the same number, which is worse than a long label because it reads
        // as correct rather than as missing.
        const t = [0, 500, 1000, 1500, 2000];
        const labels = t.map(axisMoneyFor(t, 'USD'));

        expect(new Set(labels).size).toBe(t.length);
        expect(labels).toEqual(['$0', '$0.5k', '$1k', '$1.5k', '$2k']);
    });

    it('drops the digits the step does not use', () => {
        // The point of abbreviating. A step of whole thousands cannot
        // distinguish anything finer than a thousand, so a ".0" before the k
        // would be a decimal carrying no information.
        const t = [0, 2000, 4000, 6000];
        expect(t.map(axisMoneyFor(t, 'USD'))).toEqual(['$0', '$2k', '$4k', '$6k']);
    });

    it('keeps whole dollars when the step is finer than a tenth of the unit', () => {
        // A step of a twentieth of a thousand needs two decimals of a thousand
        // to stay distinct, and that abbreviation is longer than the number it
        // abbreviates.
        const t = [1000, 1050, 1100, 1150];
        expect(t.map(axisMoneyFor(t, 'USD'))).toEqual(['$1000', '$1050', '$1100', '$1150']);
    });

    it('uses ONE unit for the whole axis', () => {
        // Picked per value, the half-thousand tick would print in whole dollars
        // beside the thousand in k —
        // two scales on one axis, which is a harder read than either alone.
        const t = [0, 500, 1000];
        const labels = t.map(axisMoneyFor(t, 'USD'));
        expect(labels.every((l, i) => i === 0 || l.endsWith('k'))).toBe(true);
    });

    it('carries the sign, because every measure here can go negative', () => {
        const t = [-4000, -2000, 0, 2000];
        expect(t.map(axisMoneyFor(t, 'USD'))).toEqual(['-$4k', '-$2k', '$0', '$2k']);
    });

    it('prints the ledger\'s currency, not always dollars', () => {
        const t = [-2000, 0, 2000];
        const euro = currencySymbol('EUR');
        expect(euro).not.toBe('$');
        expect(t.map(axisMoneyFor(t, 'EUR'))).toEqual([`-${euro}2k`, `${euro}0`, `${euro}2k`]);
    });

    it('steps up to millions, against real ticks rather than invented ones', () => {
        // Fed from `ticks` itself: a hand-written tick list can assert a
        // formatter against spacing the producer never emits, so an axis could
        // pass its test and still print a duplicate on screen.
        const t = ticks([0, 1_000_000], 4);
        const labels = t.map(axisMoneyFor(t, 'USD'));

        expect(new Set(labels).size).toBe(t.length);
        expect(labels[0]).toBe('$0');
        expect(labels.at(-1)).toBe('$1M');
    });

    it('leaves no duplicate on any axis `ticks` can produce', () => {
        for (const max of [8, 95, 420, 2_000, 7_500, 38_900, 250_000, 4_200_000]) {
            for (const count of [3, 4, 5, 6]) {
                const t = ticks([0, max], count);
                const labels = t.map(axisMoneyFor(t, 'USD'));
                expect(new Set(labels).size, `${max} over ${count}: ${labels.join(' ')}`)
                    .toBe(t.length);
            }
        }
    });
});

// ---------------------------------------------------------------------------
// Colour, Other and direct rows. Built through `pivot` from the wire rows, so
// the path from the server's result to the drawing is the one the page takes.
// Every name and figure is synthetic.
// ---------------------------------------------------------------------------

const cell = (
    period: string | null,
    groupId: string | null,
    groupName: string,
    amount: number,
    extra: Partial<ReportRow> = {},
): ReportRow => ({ period, groupId, groupName, groupPath: [groupName], parentId: null, amount, currencyCode: 'USD', ...extra });

/** The series box's height in a tile, and on Reports' own page: whole legend lines. */
const WHOLE_LINES_OF_ROOM = 'max-h-[calc(round(down,100%_+_var(--spacing)_+_0.5px,1rem_+_var(--spacing))_-_var(--spacing))]';
const WHOLE_LINES_OF_96PX = 'max-h-[calc(round(down,96px_+_var(--spacing)_+_0.5px,1rem_+_var(--spacing))_-_var(--spacing))]';

/** A legend entry: its name (with the swatch) and its figure. */
const legendItem = (label: string) =>
    screen.getByText(label, { selector: 'figcaption span' }).closest<HTMLElement>('[data-legend-entry]')!;
const swatchOf = (label: string) => legendItem(label).querySelector('rect')!.getAttribute('class');
/** The bars drawn in one fill class, legend swatches aside. */
const barsIn = (c: HTMLElement, fill: string) => bars(c).filter((r) => r.getAttribute('class') === fill);

describe('each series has a colour of its own, and the legend shows it', () => {
    it('draws a category in its chip\'s hue family, at the chart shade', () => {
        // The larger category is not the first hue in rank order, so a fill
        // taken by rank fails here.
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="stacked" periods={['2026-01']}
                rows={pivot([
                    cell('2026-01', 'id-a', 'Dining A', 200),
                    cell('2026-01', 'id-b', 'Grocer B', 100),
                ])}
            />,
        );

        expect(swatchOf('Dining A')).toBe(hueFill('din'));
        expect(swatchOf('Grocer B')).toBe(hueFill('groc'));
        expect(barsIn(container, hueFill('din'))).toHaveLength(1);
        expect(barsIn(container, hueFill('groc'))).toHaveLength(1);
    });

    it('gives two categories that share a chip colour different fills, the legend matching each bar', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="grouped" periods={['2026-01']}
                rows={pivot([
                    cell('2026-01', 'id-a', 'Grocer A', 200),
                    cell('2026-01', 'id-b', 'Grocer B', 100),
                ])}
            />,
        );

        const fills = ['Grocer A', 'Grocer B'].map(swatchOf);
        expect(fills[0]).toBe(hueFill('groc'));
        expect(fills[1]).not.toBe(fills[0]);
        for (const f of fills) expect(barsIn(container, f!)).toHaveLength(1);
    });

    it('moves the ledger\'s Uncategorized off the colour its name would give it', () => {
        // Its chip is the neutral one, so it borrows, larger or not.
        render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="stacked" periods={['2026-01']}
                rows={pivot([
                    cell('2026-01', 'id-u', 'Grocer U', 200),
                    cell('2026-01', 'id-b', 'Grocer B', 100),
                ])}
                systemCategoryIds={new Set(['id-u'])}
            />,
        );

        expect(swatchOf('Grocer B')).toBe(hueFill('groc'));
        expect(swatchOf('Grocer U')).toBe(hueFill('din'));
    });

    it('takes hues by rank in a payee chart', () => {
        render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={['2026-01']}
                rows={pivot([
                    cell('2026-01', null, 'Dining payee A', 200),
                    cell('2026-01', null, 'Dining payee B', 100),
                ])}
            />,
        );

        expect(swatchOf('Dining payee A')).toBe(hueFill(CHART_HUES[0]));
        expect(swatchOf('Dining payee B')).toBe(hueFill(CHART_HUES[1]));
    });
});

describe('a ranking', () => {
    it('draws every bar, Other included, in one neutral fill', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed={false} periods={[]}
                rows={pivot([
                    cell(null, 'id-a', 'Dining A', 300, { drawn: true }),
                    cell(null, 'id-b', 'Grocer B', 200, { drawn: true }),
                    cell(null, 'id-c', 'Phone C', 100, { drawn: true }),
                ])}
                rest={{ count: 4, total: 50, byPeriod: [] }}
            />,
        );

        const items = screen.getAllByRole('listitem');
        expect(items).toHaveLength(4);
        expect(items[3]).toHaveTextContent('Other (4)');
        for (const item of items) expect(rankedBar(item)).toBeInTheDocument();
        // No hue anywhere in the list: not on a bar, not on a track.
        expect(screen.getByRole('list').querySelector('[class*="cat-"]')).toBeNull();
        expect(container.querySelector('svg')).toBeNull();
    });
});

describe('when a cap folds groups away, the rest are one Other', () => {
    it('draws it as one slate segment, counted, with the server\'s figures', () => {
        // A kept rolled-up parent is sent too, and not drawn. Other's figure is
        // rest's alone: no sum of the rows sent, kept parent or not, gives it.
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="stacked" periods={['2026-01']}
                rows={pivot([
                    cell('2026-01', 'id-p', 'Parent P', 900, { drawn: false }),
                    cell('2026-01', 'id-c', 'Child C', 100, { drawn: true, parentId: 'id-p' }),
                ])}
                rest={{ count: 3, total: 37, byPeriod: [{ period: '2026-01', amount: 37 }] }}
            />,
        );

        expect(swatchOf('Other (3)')).toBe(OTHER_FILL);
        expect(legendItem('Other (3)')).toHaveTextContent(formatCurrency(37, 'USD'));
        expect(screen.queryByText('Parent P')).toBeNull();

        const [other] = barsIn(container, OTHER_FILL);
        const [child] = barsIn(container, swatchOf('Child C')!);
        const height = (r: Element) => Number(r.getAttribute('height'));
        expect(height(other!) / height(child!)).toBeCloseTo(37 / 100, 1);
    });

    it('names the one group it folds instead of counting it, still in slate', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="stacked" periods={['2026-01']}
                rows={pivot([cell('2026-01', 'id-a', 'Kept A', 100, { drawn: true })])}
                rest={{
                    count: 1, total: 20, byPeriod: [{ period: '2026-01', amount: 20 }],
                    only: { groupId: 'id-b', groupName: 'Folded B', groupPath: ['Folded B'], isDirect: false },
                }}
            />,
        );

        expect(swatchOf('Folded B')).toBe(OTHER_FILL);
        expect(barsIn(container, OTHER_FILL)).toHaveLength(1);
        expect(screen.queryByText(/^Other/)).toBeNull();
    });

    it('draws the same fold in grouped bars, one per period', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="grouped" periods={PERIODS}
                rows={pivot([
                    cell('2026-01', 'id-a', 'Kept A', 100, { drawn: true }),
                    cell('2026-02', 'id-a', 'Kept A', 80, { drawn: true }),
                ])}
                rest={{
                    count: 2, total: 30,
                    byPeriod: [{ period: '2026-01', amount: 10 }, { period: '2026-02', amount: 20 }],
                }}
            />,
        );

        expect(swatchOf('Other (2)')).toBe(OTHER_FILL);
        expect(barsIn(container, OTHER_FILL)).toHaveLength(2);
    });

    it('lists Other last in a ranking, at rest\'s total', () => {
        render(
            <ReportChart currency="USD" groupBy="Category" bucketed={false} periods={[]}
                rows={pivot([cell(null, 'id-a', 'Kept A', 10, { drawn: true })])}
                rest={{ count: 5, total: 64, byPeriod: [] }}
            />,
        );

        const items = screen.getAllByRole('listitem');
        expect(items.at(-1)).toHaveTextContent('Other (5)');
        expect(items.at(-1)).toHaveTextContent(formatCurrency(64, 'USD'));
    });
});

describe('Other whose figures need a missing rate (ADR-0114 D9)', () => {
    const MISSING = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-02-03', reason: 'rate_unavailable' as const };
    const NO_RATE_TITLE = 'No CAD to USD rate for Feb 3, 2026.';
    const MIXED_REST = { count: 3, total: null, byPeriod: null, rateUnavailable: [MISSING] };

    it('is listed as "no rate", naming the missing rate, and drawn nowhere', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="grouped" periods={PERIODS}
                rows={pivot([
                    cell('2026-01', 'id-a', 'Kept A', 100, { drawn: true }),
                    cell('2026-02', 'id-a', 'Kept A', 80, { drawn: true }),
                ])}
                rest={MIXED_REST}
            />,
        );

        // Settled: the kept group's two bars are drawn.
        expect(barsIn(container, swatchOf('Kept A')!)).toHaveLength(2);
        const other = legendItem('Other (3)');
        expect(other).toHaveTextContent(/^Other \(3\)\s*·\s*no rate$/);
        expect(within(other).getByText('no rate')).toHaveAttribute('title', NO_RATE_TITLE);
        expect(other.querySelector('rect')).toBeNull();
        expect(barsIn(container, OTHER_FILL)).toHaveLength(0);
    });

    it('names a period\'s missing rate when only that period has none', () => {
        render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="grouped" periods={PERIODS}
                rows={pivot([cell('2026-01', 'id-a', 'Kept A', 100, { drawn: true })])}
                rest={{
                    count: 2, total: null,
                    byPeriod: [{ period: '2026-01', amount: 5 }, { period: '2026-02', amount: null, rateUnavailable: [MISSING] }],
                }}
            />,
        );

        expect(within(legendItem('Other (2)')).getByText('no rate')).toHaveAttribute('title', NO_RATE_TITLE);
    });

    it('names the one group it folds, as "no rate"', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Category" bucketed stack="grouped" periods={['2026-01']}
                rows={pivot([cell('2026-01', 'id-a', 'Kept A', 100, { drawn: true })])}
                rest={{
                    count: 1, total: null, byPeriod: null, rateUnavailable: [MISSING],
                    only: { groupId: 'id-b', groupName: 'Folded B', groupPath: ['Folded B'], isDirect: false },
                }}
            />,
        );

        const folded = legendItem('Folded B');
        expect(folded).toHaveTextContent(/^Folded B\s*·\s*no rate$/);
        expect(within(folded).getByText('no rate')).toHaveAttribute('title', NO_RATE_TITLE);
        expect(barsIn(container, swatchOf('Kept A')!)).toHaveLength(1);
        expect(barsIn(container, OTHER_FILL)).toHaveLength(0);
    });

    it('has no bar in a ranking, and reads "no rate"', () => {
        render(
            <ReportChart currency="USD" groupBy="Category" bucketed={false} periods={[]}
                rows={pivot([cell(null, 'id-a', 'Kept A', 10, { drawn: true })])}
                rest={MIXED_REST}
            />,
        );

        const [kept, other] = screen.getAllByRole('listitem');
        expect(rankedBar(kept!)).toBeInTheDocument();
        expect(other).toHaveTextContent('Other (3)');
        expect(within(other!).getByText('no rate')).toHaveAttribute('title', NO_RATE_TITLE);
        expect(rankedBar(other!)).toBeNull();
    });
});

// The layout below is asserted on its STRUCTURE: jsdom lays nothing out, so a
// width or an overflow can only be read off the classes and styles that make
// it. The real browser is the check that they do.
describe('a chart over time, laid out', () => {
    const series = ['A', 'B', 'C'].map((n, i) => row(`Category ${n}`, 300 - i * 50, { '2026-01': 100, '2026-02': 200 - i * 50 }));

    it('puts each period label under its own column', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={['2026-01', '2026-02', '2026-03']} rows={series} />,
        );
        const labels = container.querySelector<HTMLElement>('[data-axis-labels]')!;
        expect(labels.style.gridTemplateColumns).toBe('repeat(3, minmax(0px, 1fr))');
        expect([...labels.children].map((c) => c.textContent)).toEqual(['2026-01', '2026-02', '2026-03']);
        expect(labels.className).not.toContain('justify-between');
        // Each column centres its own label, which never wraps; the last moves back
        // by what it is wider than its column, so it ends at the plot's edge.
        const columns = [...labels.children] as HTMLElement[];
        for (const column of columns) expect(column.className).toContain('justify-center');
        const label = (i: number) => columns[i]!.querySelector<HTMLElement>('[data-axis-label]')!;
        expect(label(2).className).toContain('translate-x-[min(0px,calc(50cqw_-_50%))]');
        expect(label(0).className).not.toContain('translate-x');
        expect(label(1).className).not.toContain('translate-x');
    });

    // jsdom applies no container query, so the thinning is read off the classes
    // that do it (components/chart/axisLabels.test.ts has the rule itself).
    it('thins the period labels by the width of their columns, every period keeping its column', () => {
        const periods = Array.from({ length: 13 }, (_, i) => `2026-${String((i % 12) + 1).padStart(2, '0')}`);
        const { container } = render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={periods} rows={series} />,
        );
        const labels = container.querySelector<HTMLElement>('[data-axis-labels]')!;
        expect(labels.style.gridTemplateColumns).toBe('repeat(13, minmax(0px, 1fr))');
        expect(labels.className).toContain('@container/axis');
        const columns = [...labels.querySelectorAll<HTMLElement>('[data-axis-column]')];
        expect(columns).toHaveLength(13);
        for (const column of columns) expect(column.className).toContain('@container/period');
        const label = (i: number) => columns[i]!.querySelector<HTMLElement>('[data-axis-label]')!;
        // The first is drawn at every width; the rest each hide below their own threshold.
        expect(label(0).className).not.toMatch(/@max-/);
        expect(label(1).className).toContain('@max-[8ch]/period:hidden');
        expect(label(2).className).toContain('@max-[4ch]/period:hidden');
        expect(label(4).className).toContain('@max-[2ch]/period:hidden');
        // Too near the last for a spacing of four: it gives way at two.
        expect(label(8).className).toContain('@max-[4ch]/period:hidden');
        // The last is drawn while the axis has room for it and the first.
        expect(label(12).className).toContain('@max-[12ch]/axis:hidden');
        expect(label(12).className).not.toMatch(/\/period:/);
        for (const i of [0, 1, 12]) {
            expect(label(i).className).toContain('whitespace-nowrap');
            expect(label(i)).toHaveTextContent(periods[i]!);
        }
    });

    it('draws the gridlines beneath the bars, and zero over them', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={PERIODS} rows={series} />,
        );
        const svg = container.querySelector('svg[role="img"]')!;
        const gridline = container.querySelector('.border-dashed')!;
        const zero = container.querySelector('[data-zero-rule]')!;
        // Earlier in the same box paints beneath; later paints over.
        expect(gridline.parentElement).toBe(svg.parentElement);
        expect(gridline.compareDocumentPosition(svg) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(svg.compareDocumentPosition(zero) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    });

    it('in a tile, gives the plot a floor and the legend its whole lines, the fold on a line that never scrolls', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={PERIODS} rows={series} fit
                rest={{ count: 4, total: 40, byPeriod: [{ period: '2026-01', amount: 20 }, { period: '2026-02', amount: 20 }] }} />,
        );
        const plot = container.querySelector('svg[role="img"]')!.parentElement!.parentElement!;
        expect(plot.className).toContain('min-h-fixed-64px');
        const seriesBox = container.querySelector<HTMLElement>('[data-legend-series]')!;
        // The series are laid out in the room the figure leaves them, which shrinks
        // only when the plot is at its floor; the box is the whole lines that room
        // holds, and scrolls only past them.
        const room = seriesBox.parentElement!;
        expect(room).toHaveAttribute('data-legend-room');
        expect(room.className).toContain('min-h-0');
        expect(seriesBox.className).toContain('overflow-y-auto');
        expect(seriesBox.className).toContain(WHOLE_LINES_OF_ROOM);
        const fold = container.querySelector<HTMLElement>('[data-legend-fold]')!;
        expect(within(fold).getByText('Other (4)')).toBeInTheDocument();
        expect(within(seriesBox).queryByText('Other (4)')).toBeNull();
        expect(fold.className).toContain('shrink-0');
        expect(fold.className).not.toContain('overflow');
    });

    it('keeps a fold with no figures on the line that never scrolls', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={PERIODS} rows={series} fit
                rest={{ count: 4, total: null, byPeriod: null }} />,
        );
        const fold = container.querySelector<HTMLElement>('[data-legend-fold]')!;
        expect(within(fold).getByText('Other (4)').closest('[data-legend-entry]')).toHaveTextContent(/^Other \(4\)\s*·\s*no rate$/);
    });

    // jsdom lays nothing out, so a whole line is read off what makes one: every line
    // one rem, a gap apart, and the box rounded down to a whole number of them.
    it('sets every legend line one rem high, so a box of whole lines is a whole number of them', () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={PERIODS} rows={series} fit
                rest={{ count: 4, total: 40, byPeriod: [{ period: '2026-01', amount: 20 }, { period: '2026-02', amount: 20 }] }} />,
        );
        const caption = container.querySelector<HTMLElement>('figcaption')!;
        expect(caption.className).toContain('leading-[1rem]');
        for (const box of caption.querySelectorAll<HTMLElement>('[data-legend-series], [data-legend-fold]')) {
            expect(box.className).toContain('gap-y-1');
        }
        for (const entry of caption.querySelectorAll<HTMLElement>('[data-legend-entry]')) {
            expect(entry.className).toContain('gap-y-1');
        }
    });

    it("caps the series on Reports' own page at the whole lines 96px holds", () => {
        const { container } = render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={PERIODS} rows={series} />,
        );
        const seriesBox = container.querySelector<HTMLElement>('[data-legend-series]')!;
        expect(seriesBox.className).toContain(WHOLE_LINES_OF_96PX);
        expect(seriesBox.className).toContain('overflow-y-auto');
        expect(seriesBox.parentElement!.tagName).toBe('FIGCAPTION');
    });

    // Asserted on structure, for the reason above: an entry's figure goes to a line of
    // its own when name and figure do not fit one, so nothing runs past the tile.
    it("lets an entry's figure take a line of its own rather than run past the tile", () => {
        render(
            <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={PERIODS} rows={series} fit
                rest={{ count: 4, total: 40, byPeriod: [{ period: '2026-01', amount: 20 }, { period: '2026-02', amount: 20 }] }} />,
        );
        for (const label of ['Other (4)', 'Category A']) {
            const entry = legendItem(label);
            expect(entry.className).toContain('flex-wrap');
            expect(entry.className).toContain('max-w-full');
            expect(entry.className).toContain('min-w-0');
            const name = screen.getByText(label, { selector: 'figcaption span' });
            expect(name.className).toContain('truncate');
            // The swatch and name hold together, and give way before the entry overflows.
            const group = name.parentElement!;
            expect(group.parentElement).toBe(entry);
            expect(group.className).toContain('min-w-0');
            expect(group.className).toContain('max-w-full');
            expect(group.querySelector('rect')).not.toBeNull();
            // The figure is the entry's own item, beside the name, so it can wrap.
            const figure = within(entry).getByText(/\$\d/);
            expect(figure.parentElement).toBe(entry);
            expect(group).not.toContainElement(figure);
        }
    });
});

describe('a ranking at a narrow width', () => {
    it('lets the name give way, keeps the bar a floor, and never cuts a figure', () => {
        render(
            <ReportChart currency="USD" groupBy="Payee" bucketed={false} periods={[]}
                rows={[row('A long payee name that will not fit', 123_456.78), row('Payee B', 9.5)]} />,
        );
        const items = screen.getAllByRole('listitem');
        for (const item of items) {
            const name = item.querySelector<HTMLElement>('[data-ranked-name]')!;
            expect(name.className).toMatch(/\bshrink\b/);
            expect(name.className).toContain('min-w-0');
            expect(name.className).toContain('basis-fixed-160px');
            expect(name.className).not.toMatch(/\bshrink-0\b|w-fixed-160px/);
            // The text inside is what truncates, its whole name the title (GroupName).
            const text = name.firstElementChild as HTMLElement;
            expect(text.className).toContain('min-w-0');
            expect(text.className).toContain('truncate');
            expect(text).toHaveAttribute('title');
            expect(item.querySelector<HTMLElement>('[data-ranked-track]')!.className).toContain('min-w-fixed-48px');
            const figure = item.querySelector<HTMLElement>('[data-ranked-figure]')!;
            expect(figure.className).toContain('shrink-0');
            expect(figure.className).toContain('whitespace-nowrap');
            // As wide as the widest figure, in the monospace figures' own characters.
            expect(figure.style.width).toBe(`${formatCurrency(123_456.78, 'USD').length}ch`);
        }
    });
});

describe('a chart past nine series draws every one and names each', () => {
    // Twelve payees, largest first: three past the nine hues.
    const twelve = () => pivot(Array.from({ length: 12 }, (_, i) =>
        cell('2026-01', null, `Payee ${String.fromCharCode(65 + i)}`, 1200 - i * 100)));
    const chart = () => render(
        <ReportChart currency="USD" groupBy="Payee" bucketed stack="stacked" periods={['2026-01']} rows={twelve()} />,
    );

    it('keeps all twelve in the legend, each in its own fill, with no Other', () => {
        chart();
        const fills = Array.from({ length: 12 }, (_, i) => swatchOf(`Payee ${String.fromCharCode(65 + i)}`));
        expect(new Set(fills).size).toBe(12);
        expect(fills[9]).not.toBe(fills[0]);
        expect(screen.queryByText(/^Other/)).toBeNull();
    });

    it('names a segment in its tooltip, with its period and figure', () => {
        const { container } = chart();
        const titles = [...container.querySelectorAll('rect > title')].map((t) => t.textContent);
        expect(titles).toContain(`Payee J · 2026-01 · ${formatCurrency(300, 'USD')}`);
        expect(titles).toHaveLength(12);
    });

    it('lights the hovered series and dims the rest, in the bars and the legend', () => {
        const { container } = chart();
        const target = [...container.querySelectorAll('rect')].find(
            (r) => r.querySelector('title')?.textContent?.startsWith('Payee J'),
        )!;
        fireEvent.mouseOver(target);

        const dimmedBars = bars(container).filter((r) => r.getAttribute('class')!.includes('opacity-25'));
        expect(dimmedBars).toHaveLength(11);
        expect(target.getAttribute('class')).not.toContain('opacity-25');
        expect(legendItem('Payee A').className).toContain('opacity-40');
        expect(legendItem('Payee J').className).not.toContain('opacity-40');

        fireEvent.mouseOut(target);
        expect(bars(container).some((r) => r.getAttribute('class')!.includes('opacity-25'))).toBe(false);
    });

    it('lights a series from its legend entry', () => {
        const { container } = chart();
        fireEvent.mouseOver(legendItem('Payee C'));
        expect(bars(container).filter((r) => r.getAttribute('class')!.includes('opacity-25'))).toHaveLength(11);
    });
});
