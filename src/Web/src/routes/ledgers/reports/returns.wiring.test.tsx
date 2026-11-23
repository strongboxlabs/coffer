import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import {
    createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider,
} from '@tanstack/react-router';

import { PinnedReportsGroup } from '@/components/PinnedReportsGroup';
import { stubFetch } from '@/lib/annotations/fetchStub';
import { specToQuery } from '@/lib/api';
import type { ReportSpec, SavedReport } from '@/types/report';

import { createAppRouter } from '@/router';

import { resolveTile, tileChoices } from '../home/reportTiles';
import { CATALOG, findCatalogReport } from './catalog';
import { ReportsPage } from './ReportsPage';
import { ReportView, specFromSearch } from './ReportView';
import { returnsSpecFromSearch } from './returnsModel';

// How a Returns spec travels: the catalog, the query, the router, saved cards, the
// rail and the home tiles. Names and figures are invented.

const LEDGER = 'ledger-1';
const SAVED_ID = '6f1c2a9e-0000-4000-8000-0000000000aa';

afterEach(() => {
    vi.unstubAllGlobals();
    window.history.replaceState(null, '', '/');
});

const returnsEntry = () => findCatalogReport('returns')!;

describe('the catalog entry', () => {
    it('lists Returns beside the aggregation report, marked as its own kind, since inception', () => {
        expect(CATALOG.map((r) => r.key)).toEqual(['income-and-expenses', 'returns']);
        const entry = returnsEntry();
        expect(entry.name).toBe('Returns');
        expect(entry.spec.kind).toBe('returns');
        expect(entry.spec.dateMode).toBe('Static');
        expect(entry.spec.from).toBeUndefined();
        expect(entry.spec.returnsShow).toBe('index');
    });

    it('leaves the aggregation entry without a marker', () => {
        expect(findCatalogReport('income-and-expenses')!.spec.kind).toBeUndefined();
    });
});

describe('specToQuery for a returns spec', () => {
    const spec = (over: Partial<ReportSpec>): ReportSpec => ({ ...returnsEntry().spec, ...over });

    it('sends the endpoint\'s own parameters, and none of the aggregation\'s', () => {
        const q = new URLSearchParams(specToQuery(spec({})));

        expect(q.get('interval')).toBe('month');
        expect(q.get('includeAccounts')).toBe('true');
        expect([...q.keys()].sort()).toEqual(['includeAccounts', 'interval']);
    });

    it.each([
        ['Month', 'month'], ['Quarter', 'quarter'], ['Year', 'year'], ['Day', 'month'], ['None', 'month'],
    ] as const)('maps the bucket %s to the interval %s', (bucket, interval) => {
        expect(new URLSearchParams(specToQuery(spec({ bucket }))).get('interval')).toBe(interval);
    });

    it('sends one account alone, and never asks for the others beside it', () => {
        const q = new URLSearchParams(specToQuery(spec({ accountId: 'acct-a', includeAccounts: true })));

        expect(q.get('accountId')).toBe('acct-a');
        expect(q.get('includeAccounts')).toBe('false');
    });

    it('sends the ledger alone when the accounts are turned off', () => {
        const q = new URLSearchParams(specToQuery(spec({ includeAccounts: false })));

        expect(q.get('includeAccounts')).toBe('false');
        expect(q.has('accountId')).toBe(false);
    });

    it('sends the benchmark pick, whatever the view, and nothing when none is picked', () => {
        for (const returnsShow of ['index', 'gain'] as const) {
            expect(new URLSearchParams(specToQuery(spec({ benchmark: 'VT', returnsShow }))).get('benchmark')).toBe('VT');
        }
        expect(new URLSearchParams(specToQuery(spec({ benchmark: null }))).has('benchmark')).toBe(false);
    });

    it('sends a sliding window as the summary does, and a fixed one as its dates', () => {
        const sliding = new URLSearchParams(specToQuery(spec({
            dateMode: 'Sliding', windowCount: 2, windowUnit: 'Year', windowAlign: true,
        })));
        expect(Object.fromEntries(sliding)).toMatchObject({
            dateMode: 'Sliding', windowCount: '2', windowUnit: 'Year', windowAlign: 'true',
        });

        const fixed = new URLSearchParams(specToQuery(spec({ from: '2025-01-01T00:00:00Z', to: '2026-01-01T00:00:00Z' })));
        expect(fixed.get('from')).toBe('2025-01-01T00:00:00Z');
        expect(fixed.get('to')).toBe('2026-01-01T00:00:00Z');
        expect(fixed.has('dateMode')).toBe(false);
    });

    it('still serialises an unmarked spec as the aggregation', () => {
        const q = new URLSearchParams(specToQuery({
            measure: 'Income', groupBy: 'Payee', bucket: 'Quarter', dateMode: 'Sliding', windowCount: 4, windowUnit: 'Month',
        }));

        expect(q.get('measure')).toBe('Income');
        expect(q.get('groupBy')).toBe('Payee');
        expect(q.get('bucket')).toBe('Quarter');
        expect(q.has('interval')).toBe(false);
    });
});

describe('reading a spec back', () => {
    it('keeps an unmarked spec as the aggregation report', () => {
        const spec = specFromSearch({ measure: 'Income', groupBy: 'Payee' });

        expect(spec.kind).toBeUndefined();
        expect(spec.measure).toBe('Income');
    });

    it('opens a returns spec with no controls set since inception, as the index chart', () => {
        const spec = returnsSpecFromSearch({ kind: 'returns' });

        expect(spec).toMatchObject({
            kind: 'returns', bucket: 'Month', dateMode: 'Static', returnsShow: 'index', view: 'chart', includeAccounts: true,
        });
        expect(spec.windowCount).toBeUndefined();
    });

    it('turns a day or no bucket into a month, for the interval the report has', () => {
        expect(returnsSpecFromSearch({ kind: 'returns', bucket: 'Day' }).bucket).toBe('Month');
        expect(returnsSpecFromSearch({ kind: 'returns', bucket: 'None' }).bucket).toBe('Month');
        expect(returnsSpecFromSearch({ kind: 'returns', bucket: 'Year' }).bucket).toBe('Year');
    });
});

describe('the report route\'s search', () => {
    const validate = (search: Record<string, unknown>) => {
        const router = createAppRouter(new QueryClient());
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const route = (router.routesByPath as any)['/ledgers/$ledgerId/reports/view'];
        return route.options.validateSearch(search) as ReportSpec & { saved?: string; name?: string };
    };

    it('keeps the marker and the returns controls', () => {
        const parsed = validate({
            kind: 'returns', returnsShow: 'gain', accountId: 'acct-a', includeAccounts: 'false', bucket: 'Quarter', view: 'table',
        });

        expect(parsed).toMatchObject({
            kind: 'returns', returnsShow: 'gain', accountId: 'acct-a', includeAccounts: false, bucket: 'Quarter', view: 'table',
        });
    });

    it('keeps the table/chart choice and the bar shape for either report', () => {
        // Mutation caught: removing view or chartStack from validateSearch drops them here.
        expect(validate({ view: 'chart', chartStack: 'grouped' })).toMatchObject({ view: 'chart', chartStack: 'grouped' });
        expect(validate({ kind: 'returns', view: 'table' })).toMatchObject({ kind: 'returns', view: 'table' });
        const dropped = validate({ view: 'pie', chartStack: 'wide', returnsShow: 'both', kind: 'x' });
        expect(dropped.view).toBeUndefined();
        expect(dropped.chartStack).toBeUndefined();
        expect(dropped.returnsShow).toBeUndefined();
        expect(dropped.kind).toBeUndefined();
    });

    it('reads accountId and includeAccounts as given, as a string and a boolean', () => {
        expect(validate({ accountId: 'acct-a', includeAccounts: true })).toMatchObject({ accountId: 'acct-a', includeAccounts: true });
        expect(validate({ includeAccounts: 'true' }).includeAccounts).toBe(true);
        expect(validate({ includeAccounts: 'maybe' }).includeAccounts).toBeUndefined();
    });

    it('a search with none of the Returns fields still yields the aggregation, with its own fields kept', () => {
        const parsed = validate({ measure: 'Income', groupBy: 'Payee', bucket: 'Quarter', rollup: 'true', topN: '10' });

        expect(parsed).toMatchObject({ measure: 'Income', groupBy: 'Payee', bucket: 'Quarter', rollup: true, topN: 10 });
        for (const k of ['kind', 'returnsShow', 'accountId', 'includeAccounts', 'view', 'chartStack'] as const) {
            expect(parsed[k]).toBeUndefined();
        }
        expect(specFromSearch(parsed).kind).toBeUndefined();
    });

    it('drops a marker it does not know, so the link reads as the aggregation', () => {
        const parsed = validate({ kind: 'netWorth', measure: 'Income' });

        expect(parsed.measure).toBe('Income');
        expect(parsed.kind).toBeUndefined();
    });

    it('reads an older link with no marker exactly as before', () => {
        const parsed = validate({ measure: 'Spending', groupBy: 'Category', bucket: 'Month', dateMode: 'Sliding', windowCount: '12' });

        expect(parsed.kind).toBeUndefined();
        expect(parsed.windowCount).toBe(12);
    });
});

describe('a home tile', () => {
    it('resolves the Returns catalog key to its report, opened with the marker', () => {
        const resolved = resolveTile('report:catalog:returns', undefined);

        expect(resolved.state).toBe('found');
        if (resolved.state !== 'found') return;
        expect(resolved.report.name).toBe('Returns');
        expect(resolved.report.spec.kind).toBe('returns');
        expect(resolved.report.search.kind).toBe('returns');
        expect(resolved.report.id).toBe('report:catalog:returns');
    });

    it('offers it in the choices, after the saved reports and the aggregation', () => {
        expect(tileChoices([]).map((c) => c.name)).toEqual(['Income and Expenses', 'Returns']);
    });

    it('resolves a saved returns report with its marker carried', () => {
        const saved: SavedReport = {
            id: SAVED_ID, name: 'My returns', visibility: 'private', ownerUserId: 'u1',
            createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', spec: returnsEntry().spec,
        };
        const resolved = resolveTile(`report:saved:${SAVED_ID}`, [saved]);

        expect(resolved.state).toBe('found');
        if (resolved.state !== 'found') return;
        expect(resolved.report.search).toMatchObject({ kind: 'returns', saved: SAVED_ID, name: 'My returns' });
    });
});

function renderRoutes(initial: string, pins: unknown[] = [], saved: SavedReport[] = []) {
    stubFetch()
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role: 'owner' }])
        .on('GET', /\/reports\/saved$/, saved)
        .on('GET', /\/reports\/pins$/, pins);
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const router = createRouter({
        routeTree: root.addChildren([
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports', component: ReportsPage }),
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports/view', component: ReportView }),
            createRoute({
                getParentRoute: () => root,
                path: '/ledgers/$ledgerId/rail',
                component: () => <PinnedReportsGroup ledgerId={LEDGER} pathname={`/ledgers/${LEDGER}/reports/view`} />,
            }),
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId', component: () => <main>ledger</main> }),
        ]),
        history: createMemoryHistory({ initialEntries: [initial] }),
        context: { queryClient: qc },
    });
    render(
        <QueryClientProvider client={qc}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('the Reports page', () => {
    beforeEach(() => {
        window.history.replaceState(null, '', '/');
    });

    it('lists Returns in the catalog, and no longer says performance is not here', async () => {
        renderRoutes(`/ledgers/${LEDGER}/reports`);

        const link = await screen.findByRole('link', { name: /Returns/ });
        expect(link.getAttribute('href')).toContain('kind=returns');
        const footer = screen.getByText(/are not here yet/);
        expect(footer).toHaveTextContent(
            'Net worth, account balances, asset allocation, cost basis and capital gains are not here yet',
        );
        expect(footer.textContent).not.toMatch(/performance/i);
    });

    it('prints a saved returns report by what it shows, not by a measure and a group-by', async () => {
        const saved: SavedReport = {
            id: SAVED_ID, name: 'Retirement returns', visibility: 'private', ownerUserId: 'u1',
            createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
            spec: { ...returnsEntry().spec, bucket: 'Quarter', returnsShow: 'gain' },
        };
        renderRoutes(`/ledgers/${LEDGER}/reports`, [], [saved]);

        const card = await screen.findByRole('link', { name: /Retirement returns/ });
        expect(card).toHaveTextContent('Gain · quarterly · since inception');
        expect(card.textContent).not.toMatch(/Net|account/);
        expect(card.getAttribute('href')).toContain('kind=returns');
    });

    it('still prints a saved aggregation row as its measure, group-by and range', async () => {
        const saved: SavedReport = {
            id: SAVED_ID, name: 'Old report', visibility: 'private', ownerUserId: 'u1',
            createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
            spec: { measure: 'Spending', groupBy: 'Payee', bucket: 'Month', dateMode: 'Sliding', windowCount: 12, windowUnit: 'Month', windowAlign: true },
        };
        renderRoutes(`/ledgers/${LEDGER}/reports`, [], [saved]);

        const card = await screen.findByRole('link', { name: /Old report/ });
        expect(card).toHaveTextContent('Spending · payee · last 12 months');
        expect(card.getAttribute('href')).not.toContain('kind=');
    });
});

describe('the rail', () => {
    const pins = [
        { savedReportId: null, catalogKey: 'income-and-expenses', name: null, spec: null, sortOrder: 0 },
        { savedReportId: null, catalogKey: 'returns', name: null, spec: null, sortOrder: 1 },
    ];

    it('resolves a pinned Returns catalog key to its entry, linked with the marker', async () => {
        renderRoutes(`/ledgers/${LEDGER}/rail`, pins);

        const link = await screen.findByRole('link', { name: 'Returns' });
        expect(link.getAttribute('href')).toContain('kind=returns');
        expect(screen.getByRole('link', { name: 'Income and Expenses' }).getAttribute('href')).not.toContain('kind=');
    });

    it('marks only the pin whose report is open as current', async () => {
        window.history.replaceState(null, '', '/ledgers/ledger-1/reports/view?kind=returns');
        renderRoutes(`/ledgers/${LEDGER}/rail`, pins);

        const returns = await screen.findByRole('link', { name: 'Returns' });
        expect(returns).toHaveClass('bg-accent-soft');
        expect(screen.getByRole('link', { name: 'Income and Expenses' })).not.toHaveClass('bg-accent-soft');
    });

    it('marks the aggregation pin when no marker is in the URL', async () => {
        window.history.replaceState(null, '', '/ledgers/ledger-1/reports/view?measure=Spending');
        renderRoutes(`/ledgers/${LEDGER}/rail`, pins);

        const aggregation = await screen.findByRole('link', { name: 'Income and Expenses' });
        expect(aggregation).toHaveClass('bg-accent-soft');
        expect(screen.getByRole('link', { name: 'Returns' })).not.toHaveClass('bg-accent-soft');
    });
});
