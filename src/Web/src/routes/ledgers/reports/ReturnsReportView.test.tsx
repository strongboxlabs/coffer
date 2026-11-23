import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { RateUnavailable } from '@/lib/types';
import type { ReturnsReason, ReturnsSeries, ReturnsSeriesPoint, ReturnsSeriesResult } from '@/types/report';

import { ReportView } from './ReportView';
import { formatIndex } from './returnsModel';

// The Returns view through the real page, the server mocked at fetch with the
// wire shape the endpoint sends. Every account, name and figure is invented.

const LEDGER = 'ledger-1';
const ENDS = ['2026-01-31', '2026-02-28', '2026-03-31', '2026-04-30', '2026-05-31', '2026-06-30'];

afterEach(() => {
    vi.unstubAllGlobals();
});

interface Cell { index: number | null; gain?: number | null; reason?: ReturnsReason; mwr?: number | null; mwrReason?: ReturnsReason }

/** One series from a row of cells, one per period end. */
function seriesOf(
    scope: 'ledger' | 'account', name: string | null, id: string | null, currency: string, cells: Cell[],
): ReturnsSeries {
    const points: ReturnsSeriesPoint[] = cells.map((c, i) => {
        const prev = cells.slice(0, i).reverse().find((x) => x.index !== null)?.index ?? null;
        return {
            periodEnd: `${ENDS[i]}T00:00:00Z`,
            index: c.index,
            indexUnavailableReason: c.index === null ? (c.reason ?? 'no_invested_base') : null,
            periodReturn: c.index !== null && prev !== null ? c.index / prev - 1 : null,
            periodReturnUnavailableReason: c.index !== null && prev === null ? 'before_first_funding' : null,
            coveredDays: c.index === null ? 0 : 30 * (i + 1),
            gain: c.gain ?? null,
            gainUnavailableReason: c.gain == null ? 'no_invested_base' : null,
            mwrTrailingYear: c.mwr ?? null,
            mwrUnavailableReason: c.mwr == null ? (c.mwrReason ?? 'under_a_year_covered') : null,
            rateUnavailable: [],
            benchmark: null,
            benchmarkUnavailableReason: null,
            benchmarkUnavailableDate: null,
            benchmarkRateUnavailable: [],
        };
    });
    return { scope, accountId: id, accountName: name, currencyCode: currency, points };
}

const LEDGER_SERIES = seriesOf('ledger', null, null, 'USD', [
    { index: null, reason: 'before_first_funding' },
    { index: 100, gain: 0 },
    { index: 104, gain: 400 },
    { index: 108, gain: 900 },
    { index: 110, gain: 1000 },
    { index: 112.4, gain: 1240, mwr: 0.071 },
]);
/** Stops for two periods in the middle, then resumes: an interior gap. */
const GAPPED = seriesOf('account', 'Brokerage A', 'acct-a', 'USD', [
    { index: 100, gain: 0 },
    { index: 103, gain: 300 },
    { index: null },
    { index: null },
    { index: 105, gain: 500 },
    { index: 107, gain: 700 },
]);
const STEADY = seriesOf('account', 'Brokerage B', 'acct-b', 'EUR', [
    { index: null, reason: 'before_first_funding' },
    { index: 100, gain: 0 },
    { index: 101, gain: 80 },
    { index: 102, gain: 150 },
    { index: 104, gain: 310 },
    { index: 106, gain: 460 },
]);

function resultOf(series: ReturnsSeries[]): ReturnsSeriesResult {
    return {
        from: '2026-01-01T00:00:00Z', to: '2026-06-30T00:00:00Z', interval: 'month', series, benchmark: null,
        computedAt: '2026-07-01T00:00:00Z', engineVersion: 'test',
    };
}

const ACCOUNTS = [
    { id: 'acct-a', ledgerId: LEDGER, name: 'Brokerage A', accountType: 'investment', currencyCode: 'USD', isActive: true },
    { id: 'acct-b', ledgerId: LEDGER, name: 'Brokerage B', accountType: 'investment', currencyCode: 'EUR', isActive: true },
    { id: 'acct-c', ledgerId: LEDGER, name: 'Checking', accountType: 'bank', currencyCode: 'USD', isActive: true },
];

function renderReturns(search: string, answer: ReturnsSeriesResult | ((q: URLSearchParams) => ReturnsSeriesResult)) {
    const stub = stubFetch()
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role: 'owner' }])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/reports\/returns-series$/, (call: { search: URLSearchParams }) => (
            typeof answer === 'function' ? answer(call.search) : answer));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const router = createRouter({
        routeTree: root.addChildren([
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports/view', component: ReportView }),
        ]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/reports/view?${search}`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    return stub;
}

const lastQuery = (stub: FetchStub) => stub.callsTo('GET', /\/reports\/returns-series$/).at(-1)!.search;
/** The legend entry that names `name`, and the latest value it gives. */
const legendEntry = (figure: Element, name: string) => [...figure.querySelectorAll('figcaption [data-legend-entry]')]
    .find((e) => e.textContent?.includes(name))!;
const legendValue = (figure: Element, name: string) =>
    legendEntry(figure, name).querySelector('[data-legend-value]')!.textContent;
/** The value axis's tick labels, top to bottom (ValueAxis: one hidden rule per tick). */
const valueAxisLabels = (figure: Element) => [...figure.querySelectorAll('div[aria-hidden].border-t > span')]
    .map((s) => s.textContent ?? '');

describe('the Returns view: the request', () => {
    it('asks the returns endpoint, and not the summary, for the marked spec', async () => {
        const stub = renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        const q = lastQuery(stub);
        expect(q.get('interval')).toBe('month');
        expect(q.get('includeAccounts')).toBe('true');
        expect(q.get('today')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
        // Since inception: no window parameters at all.
        expect(q.has('windowCount')).toBe(false);
        expect(q.has('measure')).toBe(false);
        expect(stub.callsTo('GET', /\/reports\/summary$/)).toHaveLength(0);
    });

    it('sends the interval, one account, and a sliding window as chosen', async () => {
        const stub = renderReturns(
            'kind=returns&bucket=Quarter&accountId=acct-a&dateMode=Sliding&windowCount=3&windowUnit=Year&windowAlign=true',
            resultOf([GAPPED]),
        );
        await screen.findByRole('img');

        const q = lastQuery(stub);
        expect(q.get('interval')).toBe('quarter');
        expect(q.get('accountId')).toBe('acct-a');
        expect(q.get('includeAccounts')).toBe('false');
        expect(q.get('dateMode')).toBe('Sliding');
        expect(q.get('windowCount')).toBe('3');
        expect(q.get('windowUnit')).toBe('Year');
        expect(q.get('windowAlign')).toBe('true');
    });

    it('offers no Day interval, and the window picker offers Since inception', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_SERIES]));
        await screen.findByRole('img');

        const period = screen.getByLabelText('Period');
        expect(within(period).getAllByRole('option').map((o) => o.textContent)).toEqual(['Month', 'Quarter', 'Year']);

        await user.click(screen.getByRole('button', { name: 'Range: Since inception' }));
        expect(screen.getByRole('button', { name: 'Since inception' })).toBeInTheDocument();
    });

    it('lists the investment accounts, and not the others, in the accounts selector', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES]));
        const select = await screen.findByLabelText('Accounts');
        await screen.findByRole('option', { name: 'Brokerage A' });

        expect(within(select).getAllByRole('option').map((o) => o.textContent))
            .toEqual(['All, plus each', 'All accounts', 'Brokerage A', 'Brokerage B']);
    });

    it('choosing an account asks for that account alone', async () => {
        const user = userEvent.setup();
        const stub = renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED]));
        await screen.findByRole('option', { name: 'Brokerage A' });

        await user.selectOptions(screen.getByLabelText('Accounts'), 'acct-a');

        await waitFor(() => expect(lastQuery(stub).get('accountId')).toBe('acct-a'));
        expect(lastQuery(stub).get('includeAccounts')).toBe('false');
    });
});

describe('the Returns view: the index chart', () => {
    it('names its finding, draws the ledger and each account, and gives each its latest value in the legend', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));

        const chart = await screen.findByRole('img', {
            name: 'Ledger time-weighted return index went from 100 to 112 over 6 months, with 2 account lines.',
        });
        const figure = chart.closest('figure')!;
        const legend = within(figure.querySelector('figcaption')!);
        expect(legend.getByText('All accounts (ledger)')).toBeInTheDocument();
        expect(legend.getByText('Brokerage A')).toBeInTheDocument();
        expect(legend.getByText('Brokerage B')).toBeInTheDocument();

        // The latest value, as the table prints an index, beside each name.
        const latest = (s: ReturnsSeries) => formatIndex(s.points.filter((p) => p.index !== null).at(-1)!.index!);
        expect(legendValue(figure, 'All accounts (ledger)')).toBe(latest(LEDGER_SERIES));
        expect(legendValue(figure, 'Brokerage A')).toBe(latest(GAPPED));
        expect(legendValue(figure, 'Brokerage B')).toBe(latest(STEADY));
        expect(legendValue(figure, 'All accounts (ledger)')).toBe('112.4');
    });

    it('breaks the line at a gap, with open dots at both ends of it, and carries nothing across', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;

        // Ledger (leading nulls only) and steady: one run each. Gapped: two. Each run has
        // its band for the pointer beside it.
        expect(chart.querySelectorAll('polyline:not([data-hit])')).toHaveLength(4);
        expect(chart.querySelectorAll('polyline[data-hit]')).toHaveLength(4);
        const open = figure.querySelectorAll('[data-point="gap-edge"]');
        expect(open).toHaveLength(2);
        expect([...open].every((d) => d.getAttribute('data-series') === 'acct:acct-a')).toBe(true);
        // A filled dot ends each of the three lines.
        expect(figure.querySelectorAll('[data-point="end"]')).toHaveLength(3);
    });

    it('draws no ledger line where every point needs a missing rate, and still draws each account', async () => {
        const unconverted = seriesOf('ledger', null, null, 'USD', ENDS.map(() => ({ index: null, reason: 'rate_unavailable' as const })));
        renderReturns('kind=returns', resultOf([unconverted, GAPPED, STEADY]));

        const chart = await screen.findByRole('img', { name: /^2 account index lines/ });
        const figure = chart.closest('figure')!;
        const legend = within(figure.querySelector('figcaption')!);
        expect(legend.getByText('Brokerage A')).toBeInTheDocument();
        expect(legend.queryByText('All accounts (ledger)')).toBeNull();
        expect(figure.querySelector('[data-line="ledger"]')).toBeNull();
        expect(figure.querySelector('[data-line="acct:acct-a"]')).not.toBeNull();
    });

    it('says there is no invested balance when no series has a value', async () => {
        const none = seriesOf('ledger', null, null, 'USD', ENDS.map(() => ({ index: null, reason: 'before_first_funding' as const })));
        renderReturns('kind=returns', resultOf([none]));

        expect(await screen.findByText('No invested balance in this window')).toBeInTheDocument();
        expect(screen.queryByRole('img')).toBeNull();
    });

    it('names the missing rate, not an empty account, when every point needs one', async () => {
        const base = seriesOf('ledger', null, null, 'USD', ENDS.map(() => ({ index: null, reason: 'rate_unavailable' as const })));
        const unconverted: ReturnsSeries = {
            ...base,
            points: base.points.map((p, i) => ({
                ...p,
                rateUnavailable: [{ fromCurrency: 'CAD', toCurrency: 'USD', date: ENDS[i]!, reason: 'rate_unavailable' as const }],
            })),
        };
        renderReturns('kind=returns&includeAccounts=false', resultOf([unconverted]));

        expect(await screen.findByText(/^No CAD to USD rate for Jan 31, 2026 and 5 other days\./)).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Exchange rates →' })).toBeInTheDocument();
        expect(screen.queryByText('No invested balance in this window')).toBeNull();
    });

    it('says the same for a window with no series at all', async () => {
        renderReturns('kind=returns', resultOf([]));

        expect(await screen.findByText('No invested balance in this window')).toBeInTheDocument();
    });
});

describe('the Returns view: the gain chart', () => {
    it('draws gain in money, each account in its own currency, against a zero rule', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        await user.click(screen.getByRole('button', { name: 'Gain' }));

        const chart = await screen.findByRole('img', { name: /^Ledger cumulative gain ended at \$1,240\.00 over 6 months/ });
        const figure = chart.closest('figure')!;
        expect(figure.querySelector('[data-zero-rule]')).not.toBeNull();
        // Each latest gain in the legend, as the aggregation key prints a total.
        expect(legendValue(figure, 'All accounts (ledger)')).toBe('$1,240.00');
        expect(legendValue(figure, 'Brokerage A')).toBe('$700.00');
        // The euro account keeps its own currency; nothing is converted.
        expect(legendValue(figure, 'Brokerage B')).toBe('€460.00');
    });

    // Each account's line stays in its own currency (ADR-0114 D7): with two currencies on
    // the plot, an axis in either would misread the other's line.
    it('names no currency on the axis when the account lines are in two currencies', async () => {
        renderReturns('kind=returns&returnsShow=gain', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const figure = (await screen.findByRole('img', { name: /^Ledger cumulative gain/ })).closest('figure')!;

        const labels = valueAxisLabels(figure);
        expect(labels.length).toBeGreaterThan(1);
        for (const label of labels) expect(label).toMatch(/^-?[\d.]+$/);
    });

    // The control for the test above: the same selector finds the money labels.
    it('names the currency on the axis when every line is in the same one', async () => {
        renderReturns('kind=returns&returnsShow=gain', resultOf([LEDGER_SERIES, GAPPED]));
        const figure = (await screen.findByRole('img', { name: /^Ledger cumulative gain/ })).closest('figure')!;

        const labels = valueAxisLabels(figure);
        expect(labels.length).toBeGreaterThan(1);
        expect(labels.some((l) => l.startsWith('$'))).toBe(true);
    });

    it('keeps the choice in the URL', async () => {
        renderReturns('kind=returns&returnsShow=gain', resultOf([LEDGER_SERIES]));

        expect(await screen.findByRole('img', { name: /cumulative gain/ })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Gain' })).toHaveAttribute('aria-pressed', 'true');
    });
});

describe('the Returns view: visual fixes', () => {
    it('keeps each table row on one line and the Money-weighted header short', async () => {
        renderReturns('kind=returns&view=table', resultOf([LEDGER_SERIES]));

        const table = await screen.findByRole('table');
        const rows = within(table).getAllByRole('row');
        const first = within(rows[1]!).getAllByRole('cell');
        // Date, the figures, and the short reason never wrap; only the Note may.
        for (const i of [0, 1, 2, 3, 4, 5]) expect(first[i]!.className, `cell ${i}`).toContain('whitespace-nowrap');
        expect(first[6]!.className).not.toContain('whitespace-nowrap');

        const head = within(table).getByRole('columnheader', { name: 'Money-weighted, 12 mo' });
        expect(head).toHaveAttribute('title', 'Money-weighted return over the 12 months ending at this period end');
    });

    it('draws an open 8px ring at both ends of a gap and where a line starts after the others', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;

        const late = [...figure.querySelectorAll('[data-point="late-start"]')].map((d) => d.getAttribute('data-series'));
        expect(late.sort()).toEqual(['acct:acct-b', 'ledger']);
        for (const d of figure.querySelectorAll('[data-point="gap-edge"], [data-point="late-start"]')) {
            expect(d.className).toContain('size-icon-2xs');
            expect(d.className).toContain('border-2');
            expect(d.className).toContain('bg-surface');
        }
    });

    it('keys each line with the square swatch every report uses, in its line’s hue', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;

        const chip = (name: string) => legendEntry(figure, name).querySelector('[data-legend-chip] rect')!;
        expect(chip('Brokerage A')).toHaveClass('fill-cat-groc-chart');
        expect(chip('Brokerage B')).toHaveClass('fill-cat-din-chart');
        expect(chip('All accounts (ledger)')).toHaveClass('fill-accent');
        const strokes = [...chart.querySelectorAll('polyline:not([data-hit])')].map((p) => p.getAttribute('class'));
        expect(strokes.some((c) => c?.includes('stroke-cat-groc-chart'))).toBe(true);
        expect(strokes.some((c) => c?.includes('stroke-cat-din-chart'))).toBe(true);
    });

    it('puts no column of values beside the plot, on Index or on Gain', async () => {
        // Coloured values stacked at the plot's edge read as an axis; each latest value is
        // in the legend instead.
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;
        expect(figure.querySelector('[data-end-label]')).toBeNull();
        expect(figure.querySelector('.w-fixed-56px, .w-fixed-80px, .right-fixed-56px, .right-fixed-80px')).toBeNull();

        await user.click(screen.getByRole('button', { name: 'Gain' }));
        const gainFigure = (await screen.findByRole('img', { name: /^Ledger cumulative gain/ })).closest('figure')!;
        expect(gainFigure.querySelector('[data-end-label]')).toBeNull();
        expect(gainFigure.querySelector('.w-fixed-56px, .w-fixed-80px, .right-fixed-56px, .right-fixed-80px')).toBeNull();
    });
});

describe('the Returns view: lighting a line', () => {
    const lit = (figure: Element, key: string) => !figure.querySelector(`[data-line="${key}"]`)!.getAttribute('class')?.includes('opacity-25');

    it('lights a line from its legend entry and dims the rest, and lets go when the pointer leaves', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const figure = (await screen.findByRole('img', { name: /Ledger time-weighted return index/ })).closest('figure')!;

        const entry = legendEntry(figure, 'Brokerage A');
        fireEvent.mouseOver(entry);
        expect(lit(figure, 'acct:acct-a')).toBe(true);
        expect(lit(figure, 'ledger')).toBe(false);
        expect(lit(figure, 'acct:acct-b')).toBe(false);
        expect(legendEntry(figure, 'Brokerage B')).toHaveClass('opacity-40');
        expect(entry).not.toHaveClass('opacity-40');

        fireEvent.mouseOut(entry);
        for (const key of ['ledger', 'acct:acct-a', 'acct:acct-b']) expect(lit(figure, key)).toBe(true);
    });

    it('lights a line under the pointer, with its legend entry, and names the nearest period end and its value', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;
        // jsdom lays nothing out: give the plot a width, so the pointer's place along it
        // picks a period end. Six columns over 600px: the last is centred at 550px.
        vi.spyOn(chart, 'getBoundingClientRect').mockReturnValue(
            { left: 0, top: 0, width: 600, height: 200, right: 600, bottom: 200, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);

        const hit = figure.querySelector('polyline[data-hit="ledger"]')!;
        fireEvent.mouseOver(hit, { clientX: 560 });
        fireEvent.mouseMove(hit, { clientX: 560 });

        expect(lit(figure, 'ledger')).toBe(true);
        expect(lit(figure, 'acct:acct-a')).toBe(false);
        expect(legendEntry(figure, 'Brokerage A')).toHaveClass('opacity-40');
        expect(screen.getByRole('tooltip')).toHaveTextContent('All accounts (ledger) · 2026-06-30 · 112.4');

        fireEvent.mouseOut(hit);
        expect(screen.queryByRole('tooltip')).toBeNull();
        expect(lit(figure, 'acct:acct-a')).toBe(true);
    });

    it('keeps the band under the pointer when it lights an account, and draws the lit line again on top', async () => {
        // Re-sorting the lines to lift the lit one would remount the band the pointer is
        // over, and a band that is gone never hears the pointer leave: the chart would stay lit.
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;
        const bands = () => [...figure.querySelectorAll('polyline[data-hit]')];
        const before = bands();
        const hit = figure.querySelector('polyline[data-hit="acct:acct-b"]')!;

        fireEvent.mouseOver(hit);
        expect(hit.isConnected).toBe(true);
        expect(bands()).toEqual(before);
        // The copy on top is drawn in the line's colour, and the pointer passes through it.
        const copy = figure.querySelector('[data-lit-copy="acct:acct-b"]')!;
        expect(copy.getAttribute('pointer-events')).toBe('none');
        expect(copy.querySelector('polyline')!.getAttribute('class')).toContain('stroke-cat-din-chart');

        fireEvent.mouseOut(hit);
        expect(figure.querySelector('[data-lit-copy]')).toBeNull();
    });

    it('lets go of a lit line when the pointer leaves the plot, whatever a band heard', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_SERIES, GAPPED, STEADY]));
        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;

        fireEvent.mouseOver(figure.querySelector('polyline[data-hit="acct:acct-a"]')!);
        expect(lit(figure, 'ledger')).toBe(false);
        // The pointer leaves the plot box without a band's own leave being heard.
        fireEvent.mouseLeave(chart.closest('figure > div')!);
        expect(lit(figure, 'ledger')).toBe(true);
    });
});

describe('the Returns view: a rate missing only for the year before the range', () => {
    // Every drawn figure is there; only the money-weighted one needed a rate the ledger
    // does not hold, from before the range.
    const gbp: RateUnavailable = { fromCurrency: 'GBP', toCurrency: 'USD', date: '2025-03-31', reason: 'rate_unavailable' };
    const withYearGap: ReturnsSeries = {
        ...LEDGER_SERIES,
        points: LEDGER_SERIES.points.map((p, i) => (i === 2
            ? { ...p, mwrTrailingYear: null, mwrUnavailableReason: 'rate_unavailable' as const, rateUnavailable: [gbp] }
            : p)),
    };

    it('says nothing about it on the chart, which draws every line whole', async () => {
        renderReturns('kind=returns&includeAccounts=false', resultOf([withYearGap]));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        expect(screen.queryByText(/No GBP to USD rate/)).toBeNull();
    });

    it('names it under the table, whose money-weighted cell needed it', async () => {
        renderReturns('kind=returns&includeAccounts=false&view=table', resultOf([withYearGap]));
        await screen.findByRole('table');
        expect(screen.getByText(/No GBP to USD rate for Mar 31, 2025\./)).toBeInTheDocument();
    });
});

describe('the Returns view: the table', () => {
    it('lists each period end with the server figures, and a dash with its reason where there is none', async () => {
        renderReturns('kind=returns&view=table', resultOf([LEDGER_SERIES]));

        const table = await screen.findByRole('table');
        const rows = within(table).getAllByRole('row');
        // Header, then six period ends.
        expect(rows).toHaveLength(7);

        const first = within(rows[1]!).getAllByRole('cell').map((c) => c.textContent);
        expect(first[0]).toBe('2026-01-31');
        expect(first.slice(1, 4)).toEqual(['—', '—', '—']);
        expect(first[5]).toBe('under a year covered');
        expect(first[6]).toBe('Nothing is invested yet, so there is no return to measure.');

        const second = within(rows[2]!).getAllByRole('cell').map((c) => c.textContent);
        expect(second.slice(0, 4)).toEqual(['2026-02-28', '100.0', '—', '60']);
        expect(second[6]).toBe('First invested point.');

        const third = within(rows[3]!).getAllByRole('cell').map((c) => c.textContent);
        expect(third.slice(1, 3)).toEqual(['104.0', '+4.0%']);

        // A year that opened invested: its money-weighted rate shows, not a reason.
        const last = within(rows[6]!).getAllByRole('cell').map((c) => c.textContent);
        expect([...last.slice(1, 4), last[5]]).toEqual(['112.4', '+2.2%', '180', '7.1%']);
    });

    it('calls the first row of a range of dates nothing, and since inception the first invested point', async () => {
        // Brokerage A is valued from the first period end. In a range that opens with it
        // invested, that row is only where the range opens.
        renderReturns('kind=returns&view=table&includeAccounts=false&from=2026-01-01', resultOf([GAPPED]));
        const ranged = await screen.findByRole('table');
        const note = (table: HTMLElement) => within(within(table).getAllByRole('row')[1]!).getAllByRole('cell').at(-1)!.textContent;
        expect(note(ranged)).toBe('');
    });

    it('still names the first invested point since inception', async () => {
        renderReturns('kind=returns&view=table&includeAccounts=false', resultOf([GAPPED]));
        const table = await screen.findByRole('table');
        expect(within(within(table).getAllByRole('row')[1]!).getAllByRole('cell').at(-1)!.textContent).toBe('First invested point.');
    });

    it('positions its scroll box, so the screen-reader text in its cells cannot stretch the page', async () => {
        // That text is absolutely placed: with no positioned box around it, it is placed in
        // the page's scroll area as far down as its row, and the page scrolls beside the table.
        renderReturns('kind=returns&view=table', resultOf([LEDGER_SERIES]));
        const table = await screen.findByRole('table');
        expect(table.querySelector('.sr-only')).not.toBeNull();
        expect(table.parentElement).toHaveClass('relative', 'overflow-auto');
    });

    it('puts Gain after Days covered and before Money-weighted, in each series\' own currency', async () => {
        renderReturns('kind=returns&view=table', resultOf([LEDGER_SERIES, STEADY]));

        const table = await screen.findByRole('table');
        const heads = within(table).getAllByRole('columnheader').map((h) => h.textContent);
        const named = heads.filter((h) => h !== 'All accounts (ledger)' && !h?.startsWith('Brokerage'));
        expect(named).toEqual(['Period end', 'Index', 'Period return', 'Days covered', 'Gain', 'Money-weighted, 12 mo', 'Note']);

        const gainCol = named.indexOf('Gain');
        const rowFor = (end: string, nth: number) => within(table).getAllByRole('row')
            .filter((r) => r.textContent?.startsWith(end))[nth]!;
        // Ledger is first in the table, the euro account second; both end on 2026-06-30.
        const ledgerLast = within(rowFor('2026-06-30', 0)).getAllByRole('cell')[gainCol]!;
        const euroLast = within(rowFor('2026-06-30', 1)).getAllByRole('cell')[gainCol]!;
        expect(ledgerLast.textContent).toBe('$1,240.00');
        expect(euroLast.textContent).toBe('€460.00');
    });

    it('shows a dash and the plain-sentence reason for a gain the server could not give', async () => {
        renderReturns('kind=returns&view=table', resultOf([LEDGER_SERIES]));

        const table = await screen.findByRole('table');
        const cell = within(within(table).getAllByRole('row')[1]!).getAllByRole('cell')[4]!;
        expect(cell.textContent).toBe('—No invested balance at this point; the index resumes from its last value when money returns.');
        expect(cell).toHaveAttribute('title', 'No invested balance at this point; the index resumes from its last value when money returns.');
    });

    it('renders the server fractions as percentages: 0.034 is +3.4%, 0.0712 is 7.1%', async () => {
        const one = seriesOf('ledger', null, null, 'USD', [
            { index: 100, gain: 0 },
            { index: 103.4, gain: 340, mwr: 0.0712 },
        ]);
        renderReturns('kind=returns&view=table', resultOf([one]));

        const table = await screen.findByRole('table');
        const cells = within(within(table).getAllByRole('row')[2]!).getAllByRole('cell').map((c) => c.textContent);
        // seriesOf derives periodReturn as the index ratio less one (a fraction, up to float noise).
        expect(cells[2]).toBe('+3.4%');
        expect(cells[5]).toBe('7.1%');
        expect(cells[1]).toBe('103.4');
    });

    it('notes where a series resumes after a gap', async () => {
        renderReturns('kind=returns&view=table&accountId=acct-a', resultOf([GAPPED]));

        const table = await screen.findByRole('table');
        const rows = within(table).getAllByRole('row');
        const resumed = within(rows[5]!).getAllByRole('cell').map((c) => c.textContent);
        expect(resumed[0]).toBe('2026-05-31');
        expect(resumed[6]).toContain('Resumes after a gap');
        const stopped = within(rows[3]!).getAllByRole('cell').map((c) => c.textContent);
        expect(stopped[1]).toBe('—');
        expect(stopped[6]).toBe('No invested balance at this point; the index resumes from its last value when money returns.');
    });

    it('heads each series when there is more than one, and says on each point why a missing rate leaves it empty', async () => {
        const unconverted = seriesOf('ledger', null, null, 'USD', ENDS.map(() => ({ index: null, reason: 'rate_unavailable' as const })));
        renderReturns('kind=returns&view=table', resultOf([unconverted, STEADY]));

        const table = await screen.findByRole('table');
        expect(within(table).getByText('All accounts (ledger)')).toBeInTheDocument();
        expect(within(table).getAllByText('A figure here needs an exchange rate the ledger does not hold.')).toHaveLength(ENDS.length);
        expect(within(table).getByText('Brokerage B')).toBeInTheDocument();
        expect(within(table).getAllByRole('row').some((r) => r.textContent?.startsWith('2026-06-30'))).toBe(true);
    });

    it('switches to the table from the chart', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_SERIES]));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        await user.click(screen.getByRole('button', { name: 'Table' }));

        expect(await screen.findByRole('table')).toBeInTheDocument();
    });
});

describe('a spec with no marker', () => {
    it('opens as the aggregation report, exactly as an older saved row does', async () => {
        const stub = stubFetch()
            .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role: 'owner' }])
            .on('GET', /\/accounts$/, [])
            .on('GET', /\/reports\/summary$/, {
                rows: [], total: 0, fromUtc: '2025-11-01T00:00:00Z', toUtc: '2026-11-01T00:00:00Z',
                periodTotals: [], groupTotals: [], currencyCode: 'USD',
            });
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const root = createRootRoute();
        const router = createRouter({
            routeTree: root.addChildren([
                createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports/view', component: ReportView }),
            ]),
            // A saved spec as it was written before the marker existed.
            history: createMemoryHistory({
                initialEntries: [`/ledgers/${LEDGER}/reports/view?measure=Income&groupBy=Payee&bucket=Month&dateMode=Sliding&windowCount=12&windowUnit=Month&windowAlign=true`],
            }),
            context: { queryClient },
        });
        render(
            <QueryClientProvider client={queryClient}>
                {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
                <RouterProvider router={router as any} />
            </QueryClientProvider>,
        );

        expect(await screen.findByRole('heading', { name: 'Income and Expenses' })).toBeInTheDocument();
        await waitFor(() => expect(stub.callsTo('GET', /\/reports\/summary$/).length).toBeGreaterThan(0));
        expect(stub.callsTo('GET', /\/reports\/summary$/).at(-1)!.search.get('groupBy')).toBe('Payee');
        expect(stub.callsTo('GET', /\/reports\/returns-series$/)).toHaveLength(0);
    });
});
