import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { problem, stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { Benchmark, RateUnavailable } from '@/lib/types';
import type {
    ReturnsBenchmark, ReturnsReason, ReturnsSeries, ReturnsSeriesPoint, ReturnsSeriesResult,
} from '@/types/report';

import { ReportView } from './ReportView';

// The Returns report with a benchmark (ADR-0114 D1, D3), through the real page, the
// server mocked at fetch with the wire shapes. Every symbol, fund name, account and
// figure is invented.

const LEDGER = 'ledger-1';
const ENDS = ['2026-01-31', '2026-02-28', '2026-03-31', '2026-04-30', '2026-05-31', '2026-06-30'];

afterEach(() => {
    vi.unstubAllGlobals();
});

interface Cell {
    index: number | null;
    reason?: ReturnsReason;
    bench?: number | null;
    benchReason?: ReturnsReason;
    benchDate?: string;
    benchMissing?: RateUnavailable[];
    missing?: RateUnavailable[];
}

function seriesOf(scope: 'ledger' | 'account', name: string | null, id: string | null, currency: string, cells: Cell[]): ReturnsSeries {
    const points: ReturnsSeriesPoint[] = cells.map((c, i) => {
        const prev = cells.slice(0, i).reverse().find((x) => x.index !== null)?.index ?? null;
        const bench = c.bench === undefined ? null : c.bench;
        return {
            periodEnd: `${ENDS[i]}T00:00:00Z`,
            index: c.index,
            indexUnavailableReason: c.index === null ? (c.reason ?? 'no_invested_base') : null,
            periodReturn: c.index !== null && prev !== null ? c.index / prev - 1 : null,
            periodReturnUnavailableReason: c.index !== null && prev === null ? 'before_first_funding' : null,
            coveredDays: c.index === null ? 0 : 30 * (i + 1),
            gain: c.index === null ? null : (c.index - 100) * 10,
            gainUnavailableReason: c.index === null ? (c.reason ?? 'no_invested_base') : null,
            mwrTrailingYear: null,
            mwrUnavailableReason: 'under_a_year_covered',
            rateUnavailable: c.missing ?? [],
            benchmark: bench,
            benchmarkUnavailableReason: bench === null
                ? (c.benchReason ?? (c.index === null ? (c.reason ?? 'no_invested_base') : null))
                : null,
            benchmarkUnavailableDate: c.benchDate ?? null,
            benchmarkRateUnavailable: c.benchMissing ?? [],
        };
    });
    return { scope, accountId: id, accountName: name, currencyCode: currency, points };
}

/** A ledger line with a benchmark beside it the whole way. */
const LEDGER_VS = seriesOf('ledger', null, null, 'USD', [
    { index: null, reason: 'before_first_funding' },
    { index: 100, bench: 100 },
    { index: 104, bench: 101.5 },
    { index: 108, bench: 102.5 },
    { index: 110, bench: 103.25 },
    { index: 112.4, bench: 104.4 },
]);
/** Nothing invested for two periods: the index and the benchmark pause together. */
const PAUSED = seriesOf('ledger', null, null, 'USD', [
    { index: 100, bench: 100 },
    { index: 103, bench: 102 },
    { index: null },
    { index: null },
    { index: 105, bench: 103.5 },
    { index: 107, bench: 105.25 },
]);
const ACCOUNT_VS = seriesOf('account', 'Brokerage A', 'acct-a', 'USD', [
    { index: null, reason: 'before_first_funding' },
    { index: 100, bench: 100 },
    { index: 103, bench: 101.5 },
    { index: 105, bench: 102.5 },
    { index: 106, bench: 103.25 },
    { index: 109, bench: 104.4 },
]);

const VT: ReturnsBenchmark = { symbol: 'VT', name: 'Total World Fund', currencyCode: 'USD', unavailableReason: null };
const REMOVED: ReturnsBenchmark = { symbol: 'VT', name: null, currencyCode: null, unavailableReason: 'benchmark_not_in_ledger' };

/** Drawable: history stored, and a rate into every series currency. */
const DRAWABLE = { unavailableReason: null, noRatesInto: [] };
const LIST: Benchmark[] = [
    { id: 'bm-1', symbol: 'VT', name: 'Total World Fund', currencyCode: 'USD', position: 1, availability: DRAWABLE },
    { id: 'bm-2', symbol: 'GBF', name: 'Global Bond Fund', currencyCode: 'USD', position: 2, availability: DRAWABLE },
    { id: 'bm-3', symbol: 'XCE.TO', name: 'Canada Equity Fund', currencyCode: 'CAD', position: 3, availability: DRAWABLE },
];

function resultOf(series: ReturnsSeries[], benchmark: ReturnsBenchmark | null): ReturnsSeriesResult {
    return {
        from: '2026-01-01T00:00:00Z', to: '2026-06-30T00:00:00Z', interval: 'month', series, benchmark,
        computedAt: '2026-07-01T00:00:00Z', engineVersion: 'test',
    };
}

const ACCOUNTS = [
    { id: 'acct-a', ledgerId: LEDGER, name: 'Brokerage A', accountType: 'investment', currencyCode: 'USD', isActive: true },
    { id: 'acct-c', ledgerId: LEDGER, name: 'Checking', accountType: 'bank', currencyCode: 'USD', isActive: true },
];
/** A brokerage in another currency than the reporting one. */
const BROKERAGE_C = { id: 'acct-cad', ledgerId: LEDGER, name: 'Brokerage C', accountType: 'investment', currencyCode: 'CAD', isActive: true };
/** VT with history, and no stored rate from its currency into CAD. */
const NO_RATE_INTO_CAD: Benchmark = { ...LIST[0]!, availability: { unavailableReason: null, noRatesInto: ['CAD'] } };
const USD_INTO_CAD = 'Priced in USD, and these lines are in CAD. No USD → CAD rate is stored, so it cannot be converted.';

interface Options {
    role?: 'owner' | 'editor' | 'viewer';
    benchmarks?: Benchmark[] | ReturnType<typeof problem> | 'pending';
    accounts?: typeof ACCOUNTS;
    reportingCurrency?: string;
    /** A returns-series request this says yes to is never answered. */
    holdSeries?: (q: URLSearchParams) => boolean;
}

function renderReturns(
    search: string,
    answer: ReturnsSeriesResult | ((q: URLSearchParams) => ReturnsSeriesResult),
    { role = 'owner', benchmarks = LIST, accounts = ACCOUNTS, reportingCurrency = 'USD', holdSeries }: Options = {},
) {
    const stub = stubFetch()
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role, currencyCode: reportingCurrency }])
        // As the API answers: active accounts only, unless the read asks for inactive ones too.
        .on('GET', /\/accounts$/, (call: { search: URLSearchParams }) => (
            call.search.get('includeInactive') === 'true' ? accounts : accounts.filter((a) => a.isActive)))
        .on('GET', /\/benchmarks$/, benchmarks === 'pending' ? [] : benchmarks)
        .on('GET', /\/reports\/returns-series$/, (call: { search: URLSearchParams }) => (
            typeof answer === 'function' ? answer(call.search) : answer));
    if (benchmarks === 'pending') {
        const inner = globalThis.fetch;
        vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => (
            /\/benchmarks$/.test(String(input)) ? new Promise<Response>(() => {}) : inner(input, init)));
    }
    if (holdSeries) {
        const inner = globalThis.fetch;
        vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
            const url = new URL(String(input), 'http://localhost');
            return /\/reports\/returns-series$/.test(url.pathname) && holdSeries(url.searchParams)
                ? new Promise<Response>(() => {})
                : inner(input, init);
        });
    }
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const router = createRouter({
        routeTree: root.addChildren([
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports/view', component: ReportView }),
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/settings', component: () => <p>Settings page</p> }),
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
    return { stub, router };
}

const lastQuery = (stub: FetchStub) => stub.callsTo('GET', /\/reports\/returns-series$/).at(-1)!.search;
const benchLine = (chart: HTMLElement) => [...chart.querySelectorAll('polyline')]
    .filter((p) => p.getAttribute('class')?.includes('stroke-cat-xfer-text'));
/** The legend entry that names `name`. */
const legendEntry = (figure: Element, name: string) => [...figure.querySelectorAll('figcaption [data-legend-entry]')]
    .find((e) => e.textContent?.includes(name))!;

describe('the Benchmark control', () => {
    it('lists None and the ledger’s benchmarks in their order, each with its currency, the pick marked', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns&benchmark=VT', resultOf([LEDGER_VS], VT));

        const toggle = await screen.findByRole('button', { name: 'Benchmark: Total World Fund (VT)' });
        expect(toggle).toHaveAttribute('aria-haspopup', 'listbox');
        await user.click(toggle);

        const list = screen.getByRole('listbox', { name: 'Benchmark' });
        const options = within(list).getAllByRole('option');
        expect(options.map((o) => o.textContent)).toEqual([
            'None', 'Total World Fund (VT)USD', 'Global Bond Fund (GBF)USD', 'Canada Equity Fund (XCE.TO)CAD',
        ]);
        expect(options[1]).toHaveAttribute('aria-selected', 'true');
        expect(options[0]).toHaveAttribute('aria-selected', 'false');
        expect(screen.getByText('Compare with')).toBeInTheDocument();
    });

    it('sends the pick as ?benchmark= and keeps it in the URL, and None takes it out', async () => {
        const user = userEvent.setup();
        const { stub, router } = renderReturns('kind=returns', (q) => resultOf([LEDGER_VS], q.get('benchmark') ? VT : null));

        const toggle = await screen.findByRole('button', { name: 'Benchmark: None' });
        expect(lastQuery(stub).has('benchmark')).toBe(false);
        await user.click(toggle);
        await user.click(screen.getByRole('option', { name: /Global Bond Fund \(GBF\)/ }));

        await waitFor(() => expect(lastQuery(stub).get('benchmark')).toBe('GBF'));
        expect(router.state.location.search).toMatchObject({ benchmark: 'GBF' });
        expect(screen.getByRole('button', { name: 'Benchmark: Global Bond Fund (GBF)' })).toHaveAttribute('aria-expanded', 'false');

        await user.click(screen.getByRole('button', { name: 'Benchmark: Global Bond Fund (GBF)' }));
        await user.click(screen.getByRole('option', { name: 'None' }));
        await waitFor(() => expect(lastQuery(stub).has('benchmark')).toBe(false));
        expect(screen.getByRole('button', { name: 'Benchmark: None' })).toBeInTheDocument();
    });

    it('closes on Escape and gives focus back to the button', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_VS], null));

        const toggle = await screen.findByRole('button', { name: 'Benchmark: None' });
        await user.click(toggle);
        expect(screen.getByRole('listbox', { name: 'Benchmark' })).toBeInTheDocument();
        await user.keyboard('{Escape}');

        expect(toggle).toHaveAttribute('aria-expanded', 'false');
        expect(toggle).toHaveFocus();
    });

    it('with no benchmarks, lists None and points an editor to Settings → Quotes', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), { role: 'editor', benchmarks: [] });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: None' }));
        const list = screen.getByRole('listbox', { name: 'Benchmark' });
        expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual(['None']);
        const pointer = await screen.findByText(/This ledger has no benchmarks yet\./);
        expect(pointer).toHaveTextContent('This ledger has no benchmarks yet. Add one in Settings → Quotes.');
        const link = within(pointer).getByRole('link', { name: 'Settings → Quotes' });
        expect(link).toHaveAttribute('href', `/ledgers/${LEDGER}/settings?tab=quotes`);
    });

    it('tells a viewer who adds benchmarks, rather than asking them to', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), { role: 'viewer', benchmarks: [] });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: None' }));
        const pointer = await screen.findByText(/This ledger has no benchmarks yet\./);
        expect(pointer).toHaveTextContent('This ledger has no benchmarks yet. An owner or editor adds them in Settings → Quotes.');
        expect(pointer.textContent).not.toContain('Add one');
    });

    it('lists a saved pick the ledger no longer holds as disabled, with its reason', async () => {
        const user = userEvent.setup();
        const { stub } = renderReturns('kind=returns&benchmark=VT', resultOf([LEDGER_VS], REMOVED), {
            benchmarks: [LIST[1]!],
        });

        const toggle = await screen.findByRole('button', { name: 'Benchmark: VT' });
        await user.click(toggle);
        const gone = await screen.findByRole('option', { name: /^VT/ });
        expect(gone).toHaveAttribute('aria-disabled', 'true');
        expect(gone).toHaveAttribute('aria-selected', 'true');
        expect(gone).toHaveAccessibleDescription('No longer one of this ledger’s benchmarks, so it draws nothing.');

        const before = stub.callsTo('GET', /\/reports\/returns-series$/).length;
        await user.click(gone);
        // Still open, still the pick, nothing asked again.
        expect(screen.getByRole('listbox', { name: 'Benchmark' })).toBeInTheDocument();
        expect(stub.callsTo('GET', /\/reports\/returns-series$/)).toHaveLength(before);
        expect(lastQuery(stub).get('benchmark')).toBe('VT');
    });

    it('disables a benchmark with no rate into the line’s currency, with its reason, and picks nothing', async () => {
        const user = userEvent.setup();
        const { stub, router } = renderReturns('kind=returns', resultOf([LEDGER_VS], null), {
            benchmarks: [LIST[0]!, { ...LIST[2]!, availability: { unavailableReason: null, noRatesInto: ['USD'] } }],
        });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: None' }));
        const xce = screen.getByRole('option', { name: /Canada Equity Fund \(XCE\.TO\)/ });
        expect(xce).toHaveAttribute('aria-disabled', 'true');
        expect(xce).toHaveAccessibleDescription(
            'Priced in CAD, and these lines are in USD. No CAD → USD rate is stored, so it cannot be converted.');
        expect(screen.getByRole('option', { name: /Total World Fund \(VT\)/ })).not.toHaveAttribute('aria-disabled');

        const before = stub.callsTo('GET', /\/reports\/returns-series$/).length;
        await user.click(xce);
        expect(screen.getByRole('listbox', { name: 'Benchmark' })).toBeInTheDocument();
        expect(stub.callsTo('GET', /\/reports\/returns-series$/)).toHaveLength(before);
        expect(router.state.location.search).not.toHaveProperty('benchmark');
    });

    it('reads the rates for the drawn account line’s currency, not the reporting currency', async () => {
        const user = userEvent.setup();
        const cad = seriesOf('account', 'Brokerage C', 'acct-cad', 'CAD', [
            { index: null, reason: 'before_first_funding' }, { index: 100 }, { index: 101 },
            { index: 102 }, { index: 103 }, { index: 104 },
        ]);
        renderReturns('kind=returns&accountId=acct-cad', resultOf([cad], null), {
            accounts: [...ACCOUNTS, BROKERAGE_C],
            benchmarks: [NO_RATE_INTO_CAD, LIST[2]!],
        });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: None' }));
        expect(screen.getByRole('option', { name: /Total World Fund \(VT\)/ })).toHaveAccessibleDescription(USD_INTO_CAD);
        expect(screen.getByRole('option', { name: /Canada Equity Fund \(XCE\.TO\)/ })).not.toHaveAttribute('aria-disabled');
    });

    // ADR-0114 D1: the picker says what stands in a benchmark's way against the line
    // about to be drawn, which is the spec's, never the previous spec's still on screen.
    it('before the series answers, reads the rates for the account the report names', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns&accountId=acct-cad', resultOf([], null), {
            accounts: [...ACCOUNTS, BROKERAGE_C],
            benchmarks: [NO_RATE_INTO_CAD, LIST[2]!],
            holdSeries: () => true,
        });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: None' }));
        await waitFor(() => expect(screen.getByRole('option', { name: /Total World Fund \(VT\)/ }))
            .toHaveAccessibleDescription(USD_INTO_CAD));
        expect(screen.getByRole('option', { name: /Total World Fund \(VT\)/ })).toHaveAttribute('aria-disabled', 'true');
        expect(screen.getByRole('option', { name: /Canada Equity Fund \(XCE\.TO\)/ })).not.toHaveAttribute('aria-disabled');
        // Still held: nothing but the spec could have named the currency.
        expect(screen.getByText('Running…')).toBeInTheDocument();
    });

    it('while a new account’s series runs, reads that account’s rates, not the previous line’s', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), {
            accounts: [...ACCOUNTS, BROKERAGE_C],
            benchmarks: [NO_RATE_INTO_CAD, LIST[2]!],
            holdSeries: (q) => q.get('accountId') === 'acct-cad',
        });

        // Settled on the ledger's line, in the reporting currency: VT is pickable.
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        await screen.findByRole('option', { name: 'Brokerage C' });
        await user.click(screen.getByRole('button', { name: 'Benchmark: None' }));
        expect(screen.getByRole('option', { name: /Total World Fund \(VT\)/ })).not.toHaveAttribute('aria-disabled');
        await user.keyboard('{Escape}');

        await user.selectOptions(screen.getByLabelText('Accounts'), 'acct-cad');
        // The ledger's series stays on screen behind Running… while the account's is held.
        expect(await screen.findByText('Running…')).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: 'Benchmark: None' }));
        const vt = screen.getByRole('option', { name: /Total World Fund \(VT\)/ });
        expect(vt).toHaveAttribute('aria-disabled', 'true');
        expect(vt).toHaveAccessibleDescription(USD_INTO_CAD);
        expect(screen.getByRole('option', { name: /Canada Equity Fund \(XCE\.TO\)/ })).not.toHaveAttribute('aria-disabled');
    });

    it.each([
        ['yahoo-off', 'No history is stored for it yet, and Yahoo Finance is off for you on this ledger, so nothing fetches it. Turn Yahoo on in Settings → Quotes.'],
        ['no-history', 'No history is stored for it yet. It is fetched with the next refresh.'],
    ] as const)('disables a benchmark with no history (%s), saying why', async (reason, words) => {
        const user = userEvent.setup();
        renderReturns('kind=returns&benchmark=GBF', resultOf([LEDGER_VS], null), {
            benchmarks: [LIST[0]!, { ...LIST[1]!, availability: { unavailableReason: reason, noRatesInto: [] } }],
        });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: Global Bond Fund (GBF)' }));
        const gbf = screen.getByRole('option', { name: /Global Bond Fund \(GBF\)/ });
        expect(gbf).toHaveAttribute('aria-disabled', 'true');
        // A saved pick stays the pick, and says why it draws nothing.
        expect(gbf).toHaveAttribute('aria-selected', 'true');
        expect(gbf).toHaveAccessibleDescription(words);
    });

    it('says the list is loading while it is', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), { benchmarks: 'pending' });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: None' }));
        expect(screen.getByText('Loading benchmarks…')).toBeInTheDocument();
        expect(within(screen.getByRole('listbox', { name: 'Benchmark' })).getAllByRole('option')).toHaveLength(1);
    });

    it('says why when the list cannot be read', async () => {
        const user = userEvent.setup();
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), {
            benchmarks: problem(403, 'You can no longer see this ledger.', 'ledger-not-visible'),
        });

        await user.click(await screen.findByRole('button', { name: 'Benchmark: None' }));
        expect(await screen.findByRole('alert')).toHaveTextContent('You can no longer see this ledger.');
    });

    it('hides on Gain and keeps the pick in the spec and the request', async () => {
        const user = userEvent.setup();
        const { stub } = renderReturns('kind=returns&benchmark=VT', resultOf([LEDGER_VS], VT));
        await screen.findByRole('button', { name: 'Benchmark: Total World Fund (VT)' });

        await user.click(screen.getByRole('button', { name: 'Gain' }));
        await screen.findByRole('img', { name: /^Ledger cumulative gain/ });

        expect(screen.queryByRole('button', { name: /^Benchmark:/ })).toBeNull();
        expect(lastQuery(stub).get('benchmark')).toBe('VT');
        expect(screen.getByText(/The benchmark, Total World Fund \(VT\), is an index with no gain in money, so it is drawn on Index only\./))
            .toBeInTheDocument();
        // The subtitle names no comparison the view does not draw.
        expect(screen.getByText('Gain · monthly · since inception')).toBeInTheDocument();
    });

    it('shows in the table, where the benchmark has a column', async () => {
        renderReturns('kind=returns&view=table&benchmark=VT', resultOf([LEDGER_VS], VT));

        expect(await screen.findByRole('button', { name: 'Benchmark: Total World Fund (VT)' })).toBeInTheDocument();
        expect(screen.getByText('Table · monthly · since inception · vs VT')).toBeInTheDocument();
    });

    it('drops a symbol of the wrong shape from the URL rather than sending it', async () => {
        const { stub } = renderReturns('kind=returns&benchmark=vt%20x', resultOf([LEDGER_VS], null));

        expect(await screen.findByRole('button', { name: 'Benchmark: None' })).toBeInTheDocument();
        await waitFor(() => expect(stub.callsTo('GET', /\/reports\/returns-series$/).length).toBeGreaterThan(0));
        expect(lastQuery(stub).has('benchmark')).toBe(false);
    });
});

describe('the benchmark line', () => {
    it('is one dashed slate line, non-scaling, ending in its value alone, named in the legend', async () => {
        renderReturns('kind=returns&benchmark=VT', resultOf([LEDGER_VS, ACCOUNT_VS], VT));

        const chart = await screen.findByRole('img', {
            name: 'Ledger time-weighted return index went from 100 to 112 over 6 months, with 1 account line and the benchmark, Total World Fund (VT), at 104.',
        });
        const figure = chart.closest('figure')!;
        const lines = benchLine(chart);
        expect(lines).toHaveLength(1);
        expect(lines[0]).toHaveAttribute('stroke-dasharray', '4 4');
        expect(lines[0]).toHaveAttribute('vector-effect', 'non-scaling-stroke');
        // The other lines keep the kit's scaling, solid stroke.
        const others = [...chart.querySelectorAll('polyline:not([data-hit])')].filter((p) => !lines.includes(p as SVGPolylineElement));
        expect(others.length).toBe(2);
        for (const p of others) expect(p.hasAttribute('stroke-dasharray')).toBe(false);

        expect(figure.querySelector('[data-point="end"][data-series="benchmark"]')).toHaveClass('bg-cat-xfer-text');

        // Keyed as every series is: the square swatch in the slate, and its latest value.
        const entry = legendEntry(figure, 'Benchmark: Total World Fund (VT)');
        expect(entry.querySelector('[data-legend-value]')).toHaveTextContent(/^104\.4$/);
        expect(entry.querySelector('[data-legend-chip] rect')).toHaveClass('fill-cat-xfer-text');
    });

    it('is drawn against the ledger’s line, not an account’s', async () => {
        // The account's own benchmark differs from the ledger's: the chart reads the ledger's.
        const account = seriesOf('account', 'Brokerage A', 'acct-a', 'USD', [
            { index: null, reason: 'before_first_funding' },
            { index: 100, bench: 100 }, { index: 101, bench: 120 }, { index: 102, bench: 130 },
            { index: 103, bench: 140 }, { index: 104, bench: 150 },
        ]);
        renderReturns('kind=returns&benchmark=VT', resultOf([LEDGER_VS, account], VT));

        const chart = await screen.findByRole('img', { name: /and the benchmark, Total World Fund \(VT\), at 104\./ });
        expect(legendEntry(chart.closest('figure')!, 'Benchmark:').querySelector('[data-legend-value]')).toHaveTextContent(/^104\.4$/);
    });

    it('is drawn against the one account when the report names one', async () => {
        renderReturns('kind=returns&accountId=acct-a&benchmark=VT', resultOf([ACCOUNT_VS], VT));

        const chart = await screen.findByRole('img', {
            name: '1 account index line over 6 months, with the benchmark, Total World Fund (VT), at 104.',
        });
        expect(benchLine(chart)).toHaveLength(1);
    });

    it('pauses with the portfolio: a break with an open dot at each end, nothing carried across', async () => {
        renderReturns('kind=returns&benchmark=VT', resultOf([PAUSED], VT));

        const chart = await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        const figure = chart.closest('figure')!;
        expect(benchLine(chart)).toHaveLength(2);
        const open = [...figure.querySelectorAll('[data-point="gap-edge"]')].map((d) => d.getAttribute('data-series'));
        expect(open.filter((s) => s === 'benchmark')).toHaveLength(2);
        expect(open.filter((s) => s === 'ledger')).toHaveLength(2);
    });

    it('is not drawn on Gain', async () => {
        renderReturns('kind=returns&returnsShow=gain&benchmark=VT', resultOf([LEDGER_VS], VT));

        const chart = await screen.findByRole('img', { name: /^Ledger cumulative gain/ });
        expect(chart.querySelectorAll('polyline').length).toBeGreaterThan(0);
        expect(benchLine(chart)).toHaveLength(0);
        expect(within(chart.closest('figure')!.querySelector('figcaption')!).getByText('All accounts (ledger)')).toBeInTheDocument();
        expect(chart.closest('figure')!.querySelector('[data-swatch="benchmark"]')).toBeNull();
    });

    it('is not drawn for a symbol the ledger no longer holds, and the footnote says so', async () => {
        // The server sends no benchmark values for it; even if a point carried one, nothing is drawn.
        renderReturns('kind=returns&benchmark=VT', resultOf([LEDGER_VS], REMOVED));

        const chart = await screen.findByRole('img', { name: /^Ledger time-weighted return index went from 100 to 112 over 6 months\.$/ });
        expect(chart.querySelectorAll('polyline:not([data-hit])')).toHaveLength(1);
        expect(benchLine(chart)).toHaveLength(0);
        expect(screen.getByText(/VT is no longer one of this ledger’s benchmarks, so no benchmark is drawn\. It comes back if VT is added again in Settings → Quotes\./))
            .toBeInTheDocument();
    });

    it('says so on Gain too, never calling it the ledger\'s benchmark', async () => {
        renderReturns('kind=returns&returnsShow=gain&benchmark=VT', resultOf([LEDGER_VS], REMOVED));

        await screen.findByRole('img', { name: /^Ledger cumulative gain/ });
        expect(screen.getByText(/VT is no longer one of this ledger’s benchmarks, so no benchmark is drawn\./)).toBeInTheDocument();
        expect(screen.queryByText(/is an index with no gain in money/)).toBeNull();
    });
});

describe('the footnotes', () => {
    it('names the fund, says total return, set to 100 at the same start, paused when nothing is invested', async () => {
        renderReturns('kind=returns&benchmark=VT', resultOf([LEDGER_VS], VT));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        expect(screen.getByText(
            'Index is the time-weighted return from the start of the range, or from the first money invested in it, set to 100. It is not annualised. '
            + 'The benchmark, Total World Fund (VT), is a total return (the fund’s dividend-adjusted close), set to 100 at the same start. '
            + 'While nothing is invested it pauses too, and it resumes from its own last value.',
        )).toBeInTheDocument();
        expect(screen.getByText('Index · monthly · since inception · vs VT')).toBeInTheDocument();
    });

    it('keeps "Not benchmarked." with None', async () => {
        renderReturns('kind=returns', resultOf([LEDGER_VS], null));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        expect(screen.getByText(/It is not annualised\. Not benchmarked\.$/)).toBeInTheDocument();
    });

    it('says a benchmark in another currency is converted into the line’s at each day’s rate', async () => {
        const cad: ReturnsBenchmark = { symbol: 'XCE.TO', name: 'Canada Equity Fund', currencyCode: 'CAD', unavailableReason: null };
        renderReturns('kind=returns&benchmark=XCE.TO', resultOf([LEDGER_VS], cad));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        expect(screen.getByText(/Its closes are in CAD, converted into USD at each day’s rate\.$/)).toBeInTheDocument();
    });

    it('says where the benchmark has no stored history, and that its line breaks there', async () => {
        const unfetched = seriesOf('ledger', null, null, 'USD', [
            { index: null, reason: 'before_first_funding' },
            { index: 100, bench: 100 },
            { index: 104, bench: 101.5 },
            { index: 108, bench: 102.5 },
            { index: 110, bench: null, benchReason: 'benchmark_unavailable', benchDate: '2026-05-29' },
            { index: 112.4, bench: null, benchReason: 'benchmark_unavailable', benchDate: '2026-06-01' },
        ]);
        renderReturns('kind=returns&benchmark=VT', resultOf([unfetched], VT));
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        expect(screen.getByText('No VT history is stored for May 29, 2026 and 1 other point, so the benchmark line breaks there.'))
            .toBeInTheDocument();
    });

    it('names the missing rate a point needed, with the link to the rates', async () => {
        const gbpUsd: RateUnavailable = { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-06-30', reason: 'rate_unavailable' };
        const missingAtEnd = seriesOf('ledger', null, null, 'USD', [
            { index: null, reason: 'before_first_funding' },
            { index: 100, bench: 100 },
            { index: 104, bench: 101.5 },
            { index: 108, bench: 102.5 },
            { index: 110, bench: 103.25 },
            { index: null, reason: 'rate_unavailable', missing: [gbpUsd] },
        ]);
        renderReturns('kind=returns&benchmark=VT', resultOf([missingAtEnd], VT));
        await screen.findByRole('img', { name: /Ledger time-weighted return index went from 100 to 110/ });

        const note = screen.getByText(/No GBP to USD rate for Jun 30, 2026\./);
        expect(within(note).getByRole('link', { name: 'Exchange rates →' }))
            .toHaveAttribute('href', `/ledgers/${LEDGER}/settings?tab=rates`);
    });
});

describe('the table’s benchmark column', () => {
    it('sits beside Index, named for the symbol, with each block’s own rebased value', async () => {
        renderReturns('kind=returns&view=table&benchmark=VT', resultOf([LEDGER_VS, ACCOUNT_VS], VT));

        const table = await screen.findByRole('table');
        const heads = within(table).getAllByRole('columnheader')
            .filter((h) => h.getAttribute('scope') !== 'colgroup')
            .map((h) => h.textContent);
        expect(heads).toEqual([
            'Period end', 'Index', 'VT index, benchmark, total return', 'Period return', 'Days covered', 'Gain',
            'Money-weighted, 12 mo', 'Note',
        ]);
        const head = within(table).getByRole('columnheader', { name: 'VT index, benchmark, total return' });
        expect(head).toHaveAttribute('title', 'Total World Fund (VT): total return, set to 100 at this block’s first invested point');
        // A heading spans every column.
        expect(within(table).getByRole('columnheader', { name: /^Brokerage A/ })).toHaveAttribute('colspan', '8');

        const rows = within(table).getAllByRole('row').filter((r) => r.textContent?.startsWith('2026-06-30'));
        expect(within(rows[0]!).getAllByRole('cell').map((c) => c.textContent).slice(1, 3)).toEqual(['112.4', '104.4']);
        expect(within(rows[1]!).getAllByRole('cell').map((c) => c.textContent).slice(1, 3)).toEqual(['109.0', '104.4']);
        expect(screen.getByText(/VT index is Total World Fund \(VT\) as a total return, set to 100 at each block’s first invested point\./))
            .toBeInTheDocument();
    });

    it('dashes both where nothing is invested, and its note names both', async () => {
        renderReturns('kind=returns&view=table&benchmark=VT', resultOf([PAUSED], VT));

        const table = await screen.findByRole('table');
        const paused = within(table).getAllByRole('row').find((r) => r.textContent?.startsWith('2026-03-31'))!;
        const cells = within(paused).getAllByRole('cell').map((c) => c.textContent);
        expect(cells.slice(1, 3)).toEqual(['—', '—']);
        expect(cells[7]).toBe('No invested balance at this point; the index and the benchmark resume from their last values when money returns.');
    });

    it('says in the note when the benchmark has no stored history for a point the index has', async () => {
        const unfetched = seriesOf('ledger', null, null, 'USD', [
            { index: 100, bench: 100 },
            { index: 104, bench: null, benchReason: 'benchmark_unavailable', benchDate: '2026-02-27' },
        ]);
        renderReturns('kind=returns&view=table&benchmark=VT', resultOf([unfetched], VT));

        const table = await screen.findByRole('table');
        const row = within(table).getAllByRole('row').find((r) => r.textContent?.startsWith('2026-02-28'))!;
        const cells = within(row).getAllByRole('cell').map((c) => c.textContent);
        expect(cells.slice(1, 3)).toEqual(['104.0', '—']);
        expect(cells[7]).toBe('No stored VT history for Feb 27, 2026.');
    });

    it('marks a benchmark value whose conversion needs a missing rate "no rate", naming it', async () => {
        const cadUsd: RateUnavailable = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-02-27', reason: 'rate_unavailable' };
        const cad: ReturnsBenchmark = { symbol: 'XCE.TO', name: 'Canada Equity Fund', currencyCode: 'CAD', unavailableReason: null };
        const series = seriesOf('ledger', null, null, 'USD', [
            { index: 100, bench: 100 },
            { index: 104, bench: null, benchReason: 'rate_unavailable', benchMissing: [cadUsd] },
        ]);
        renderReturns('kind=returns&view=table&benchmark=XCE.TO', resultOf([series], cad));

        const table = await screen.findByRole('table');
        const row = within(table).getAllByRole('row').find((r) => r.textContent?.startsWith('2026-02-28'))!;
        const cell = within(row).getAllByRole('cell')[2]!;
        const mark = within(cell).getByText('no rate');
        expect(mark).toHaveAttribute('title', 'No CAD to USD rate for Feb 27, 2026.');
        expect(within(row).getAllByRole('cell')[7]).toHaveTextContent('No CAD to USD rate for Feb 27, 2026.');
        expect(screen.getByText(/Its closes are in CAD, converted into USD at each day’s rate\.$/)).toBeInTheDocument();
    });

    it('marks an index that needs a missing rate "no rate" and names the pair in the note', async () => {
        const eurUsd: RateUnavailable = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-02-27', reason: 'rate_unavailable' };
        const series = seriesOf('ledger', null, null, 'USD', [
            { index: 100 },
            { index: null, reason: 'rate_unavailable', missing: [eurUsd] },
        ]);
        renderReturns('kind=returns&view=table', resultOf([series], null));

        const table = await screen.findByRole('table');
        const row = within(table).getAllByRole('row').find((r) => r.textContent?.startsWith('2026-02-28'))!;
        const cells = within(row).getAllByRole('cell');
        expect(within(cells[1]!).getByText('no rate')).toHaveAttribute('title', 'No EUR to USD rate for Feb 27, 2026.');
        expect(cells[6]).toHaveTextContent('No EUR to USD rate for Feb 27, 2026.');
        // And the note under the table, with the link.
        const note = screen.getAllByText(/^No EUR to USD rate for Feb 27, 2026\./).find((n) => n.tagName === 'P')!;
        expect(within(note).getByRole('link', { name: 'Exchange rates →' })).toBeInTheDocument();
    });

    it('has no column for a symbol the ledger no longer holds, and the footnote says why', async () => {
        renderReturns('kind=returns&view=table&benchmark=VT', resultOf([LEDGER_VS], REMOVED));

        const table = await screen.findByRole('table');
        expect(within(table).getByRole('columnheader', { name: 'Index' })).toBeInTheDocument();
        expect(within(table).queryByRole('columnheader', { name: /VT index/ })).toBeNull();
        expect(screen.getByText(/VT is no longer one of this ledger’s benchmarks, so it has no column\./)).toBeInTheDocument();
    });
});

describe('the ledger-level conversion note', () => {
    it('says the ledger line is converted when a brokerage is in another currency', async () => {
        const accounts = [...ACCOUNTS, { id: 'acct-e', ledgerId: LEDGER, name: 'Brokerage E', accountType: 'investment', currencyCode: 'EUR', isActive: true }];
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), { accounts });
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        const note = await screen.findByText(/Totals are in USD, with EUR accounts converted at the rate for each figure's date \(the latest rate from today on\)\./);
        expect(within(note).getByRole('link', { name: 'Exchange rates →' })).toBeInTheDocument();
    });

    it('draws nothing when every brokerage is in the reporting currency, whatever a bank account is in', async () => {
        const accounts = [...ACCOUNTS, { id: 'acct-e', ledgerId: LEDGER, name: 'Euro Checking', accountType: 'bank', currencyCode: 'EUR', isActive: true }];
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), { accounts });
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });
        await screen.findByRole('option', { name: 'Brokerage A' });

        expect(screen.getByText('Index · monthly · since inception')).toBeInTheDocument();
        expect(screen.queryByText(/Totals are in/)).toBeNull();
    });

    it('names a closed brokerage\'s currency, since the ledger line still combines it', async () => {
        const accounts = [...ACCOUNTS, { id: 'acct-e', ledgerId: LEDGER, name: 'Closed Brokerage E', accountType: 'investment', currencyCode: 'EUR', isActive: false }];
        renderReturns('kind=returns', resultOf([LEDGER_VS], null), { accounts });
        await screen.findByRole('img', { name: /Ledger time-weighted return index/ });

        expect(await screen.findByText(/^Totals are in USD, with EUR accounts converted/)).toBeInTheDocument();
    });
});
