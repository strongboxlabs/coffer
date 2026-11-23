import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { problem, stubFetch, type FetchStub, type StubbedCall } from '@/lib/annotations/fetchStub';
import type { ReportResult, ReportRow } from '@/types/report';

import { ReportView } from './ReportView';
import { ALL_GROUPS, hueFill } from './chartSeries';
import { groupLabel } from './groupLabel';

// The chart and the table ask the server different questions: the chart marks
// which rows it draws (so it always sends a cut, All being every group), the
// table sends the report's own cap. Through the real page with the server
// mocked at fetch. Every name and figure is invented.

const LEDGER = 'ledger-1';
const PERIOD = '2026-03';

afterEach(() => {
    vi.unstubAllGlobals();
});

/** Twelve groups, largest first. */
const GROUPS = Array.from({ length: 12 }, (_, i) => ({
    groupId: `cat-${i}`,
    groupName: `Category ${String.fromCharCode(65 + i)}`,
    amount: 200 - i * 10,
}));

/** The summary as the server answers it: cut to `topN` with the rest folded. */
function summary(topN: string | null): ReportResult {
    const kept = topN === null ? GROUPS : GROUPS.slice(0, Number(topN));
    const cut = GROUPS.slice(kept.length);
    const rows: ReportRow[] = kept.map((g) => ({
        period: PERIOD, groupId: g.groupId, groupName: g.groupName, groupPath: [g.groupName], parentId: null, amount: g.amount, currencyCode: 'USD',
        ...(topN === null ? {} : { drawn: true }),
    }));
    const total = GROUPS.reduce((s, g) => s + g.amount, 0);
    return {
        rows,
        total,
        fromUtc: '2026-03-01T00:00:00Z',
        toUtc: '2026-04-01T00:00:00Z',
        periodTotals: [{ period: PERIOD, amount: total }],
        groupTotals: GROUPS.map(({ groupId, groupName, amount }) => ({ groupId, groupName, groupPath: [groupName], amount, currencyCode: 'USD' })),
        currencyCode: 'USD',
        ...(topN === null ? {} : {
            rest: {
                count: cut.length,
                total: cut.reduce((s, g) => s + g.amount, 0),
                byPeriod: [{ period: PERIOD, amount: cut.reduce((s, g) => s + g.amount, 0) }],
            },
        }),
    };
}

interface RenderOptions {
    answer?: (topN: string | null) => ReportResult;
    /** The accounts list, or a handler answering each read of it. */
    accounts?: unknown;
    /** Hold every accounts response until the returned release is called. */
    holdAccounts?: boolean;
}

function renderReport(search: string, options: RenderOptions = {}) {
    const answer = options.answer ?? summary;
    const stub = stubFetch()
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role: 'owner' }])
        .on('GET', /\/accounts$/, options.accounts ?? [])
        .on('GET', /\/reports\/summary$/, (call: { search: URLSearchParams }) => answer(call.search.get('topN')));

    let releaseAccounts = () => {};
    if (options.holdAccounts) {
        const answerNow = globalThis.fetch;
        const gate = new Promise<void>((resolve) => { releaseAccounts = resolve; });
        vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            const path = new URL(raw, 'http://localhost').pathname;
            return /\/accounts$/.test(path) ? gate.then(() => answerNow(input, init)) : answerNow(input, init);
        });
    }

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
    return { stub, queryClient, releaseAccounts };
}

const sentTopN = (stub: FetchStub) =>
    stub.callsTo('GET', /\/reports\/summary$/).map((c) => c.search.get('topN'));

/** A legend entry's swatch class, in the chart on screen. */
const swatch = (label: string) =>
    within(screen.getByRole('img').closest('figure')!.querySelector('figcaption')!)
        .getByText(label).parentElement!.querySelector('rect')!.getAttribute('class');

/**
 * Every fill a legend entry is ever painted with, frame by frame: what the
 * reader could have seen, not only where the page ends up.
 */
function recordSwatches(label: string): { seen: string[]; stop: () => void } {
    const seen: string[] = [];
    const record = () => {
        for (const caption of document.querySelectorAll('figcaption')) {
            const entry = [...caption.querySelectorAll('span')].find((s) => s.textContent === label);
            const fill = entry?.parentElement?.querySelector('rect')?.getAttribute('class');
            if (fill) seen.push(fill);
        }
    };
    const observer = new MutationObserver(record);
    observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
    return { seen, stop: () => observer.disconnect() };
}

describe('the chart and the table ask for different cuts', () => {
    // The colour count never decides the cut: a chart over All asks for every
    // group (so "All" is all), and over a cap asks for that cap, whatever it is.
    it.each([
        ['no cap', '', String(ALL_GROUPS)],
        ['a cap of 20', '&topN=20', '20'],
        ['a cap of 5', '&topN=5', '5'],
    ])('a chart over a report with %s asks for that cut', async (_shape, cap, expected) => {
        const { stub } = renderReport(`groupBy=Category&bucket=Month&view=chart${cap}`);
        expect(await screen.findByRole('img')).toBeInTheDocument();
        expect(sentTopN(stub)).toEqual([expected]);
    });

    it('draws all twelve groups over All, past the nine colour families, with no Other', async () => {
        renderReport('groupBy=Category&bucket=Month&view=chart');
        const figure = (await screen.findByRole('img')).closest('figure')!;
        const caption = within(figure.querySelector('figcaption')!);
        for (const g of GROUPS) expect(caption.getByText(g.groupName)).toBeInTheDocument();
        expect(caption.queryByText(/^Other/)).toBeNull();
    });

    it.each([
        ['no cap', '', null],
        ['a cap', '&topN=20', '20'],
    ])('a table over a report with %s sends the report\'s own cap', async (_shape, cap, expected) => {
        const { stub } = renderReport(`groupBy=Category&bucket=Month${cap}`);
        expect(await screen.findByRole('table')).toBeInTheDocument();
        expect(sentTopN(stub)).toEqual([expected]);
    });
});

describe('roll-up belongs to the table', () => {
    const rollupSent = (stub: FetchStub) =>
        stub.callsTo('GET', /\/reports\/summary$/).map((c) => c.search.get('rollup'));

    it('offers Roll up on a table and asks for it', async () => {
        const { stub } = renderReport('groupBy=Category&bucket=Month&rollup=true');
        expect(await screen.findByRole('table')).toBeInTheDocument();
        expect(screen.getByLabelText('Roll up')).toBeChecked();
        expect(rollupSent(stub)).toEqual(['true']);
    });

    it('offers no Roll up on a chart, and draws it flat even when the report has it on', async () => {
        const { stub } = renderReport('groupBy=Category&bucket=Month&view=chart&rollup=true');
        expect(await screen.findByRole('img')).toBeInTheDocument();
        expect(screen.queryByLabelText('Roll up')).toBeNull();
        expect(rollupSent(stub).every((r) => r !== 'true')).toBe(true);
    });
});

describe('a category chart', () => {
    // Two categories whose names give the same chip colour; the larger is the
    // system Uncategorized, whose chip is the neutral one, so it borrows and the
    // smaller keeps the colour.
    const twoGrocers = (): ReportResult => ({
        ...summary('9'),
        rows: [
            { period: PERIOD, groupId: 'cat-u', groupName: 'Grocer U', groupPath: ['Grocer U'], parentId: null, amount: 200, currencyCode: 'USD', drawn: true },
            { period: PERIOD, groupId: 'cat-b', groupName: 'Grocer B', groupPath: ['Grocer B'], parentId: null, amount: 100, currencyCode: 'USD', drawn: true },
        ],
        rest: { count: 0, total: 0, byPeriod: [] },
    });
    const ACCOUNTS = [
        { id: 'cat-u', name: 'Grocer U', accountType: 'category', isSystem: true },
        { id: 'cat-b', name: 'Grocer B', accountType: 'category', isSystem: false },
    ];
    const summarySettled = (queryClient: QueryClient) =>
        queryClient.getQueryCache().findAll({ queryKey: ['report-summary'] })[0]?.state.status;

    it('reads the ledger\'s Uncategorized from its accounts and moves it off its name\'s colour', async () => {
        renderReport('groupBy=Category&bucket=Month&view=chart', { answer: twoGrocers, accounts: ACCOUNTS });

        await screen.findByRole('img');
        expect(swatch('Grocer B')).toBe(hueFill('groc'));
        expect(swatch('Grocer U')).toBe(hueFill('din'));
    });

    it('draws no series until the accounts list arrives, then Uncategorized once, in its borrowed hue', async () => {
        const { queryClient, releaseAccounts } = renderReport('groupBy=Category&bucket=Month&view=chart', {
            answer: twoGrocers, accounts: ACCOUNTS, holdAccounts: true,
        });
        const paint = recordSwatches('Grocer U');

        // The summary has settled; the accounts list is still held.
        await waitFor(() => expect(summarySettled(queryClient)).toBe('success'));
        expect(screen.getByText('Running…')).toBeInTheDocument();
        expect(screen.queryByRole('img')).toBeNull();
        expect(document.querySelector('figcaption')).toBeNull();

        releaseAccounts();
        await screen.findByRole('img');
        expect(swatch('Grocer U')).toBe(hueFill('din'));
        expect(swatch('Grocer B')).toBe(hueFill('groc'));
        paint.stop();
        expect(paint.seen.length).toBeGreaterThan(0);
        expect(new Set(paint.seen)).toEqual(new Set([hueFill('din')]));
    });

    it('treats no category as Uncategorized when the list cannot be read, and keeps those colours after a later read', async () => {
        const user = userEvent.setup();
        let reads = 0;
        const { queryClient } = renderReport('groupBy=Category&bucket=Month&view=chart', {
            answer: twoGrocers,
            // The page's own read; the conversion note's (inactive included) is another query.
            accounts: (call: StubbedCall) => (call.search.has('includeInactive') ? ACCOUNTS
                : reads++ === 0 ? problem(500, 'Unavailable.') : ACCOUNTS),
        });
        const paint = recordSwatches('Grocer U');

        // The fallback: an ordinary category, which as the larger keeps its own colour.
        await screen.findByRole('img');
        expect(swatch('Grocer U')).toBe(hueFill('groc'));
        expect(screen.queryByRole('alert')).toBeNull();

        await act(() => queryClient.refetchQueries({ queryKey: ['accounts', LEDGER] }));
        expect(reads).toBe(2);
        expect(queryClient.getQueryData(['accounts', LEDGER])).toEqual(ACCOUNTS);
        // Redraw the chart from the page's current state, so the list that has
        // now arrived is what the page renders with when the colours are read.
        await user.click(screen.getByRole('button', { name: 'Grouped' }));
        expect(screen.getByRole('button', { name: 'Grouped' })).toHaveAttribute('aria-pressed', 'true');
        expect(swatch('Grocer U')).toBe(hueFill('groc'));
        paint.stop();
        expect(new Set(paint.seen)).toEqual(new Set([hueFill('groc')]));
    });
});

describe('a chart grouped by kind', () => {
    // The table's names for the kinds, from the one function the page uses.
    const KINDS = ['income', 'expense'] as const;
    const byKind = (bucket: string | null) => (): ReportResult => ({
        ...summary(null),
        rows: [
            { period: bucket, groupId: null, groupName: 'income', groupPath: ['income'], parentId: null, amount: 300, currencyCode: 'USD' },
            { period: bucket, groupId: null, groupName: 'expense', groupPath: ['expense'], parentId: null, amount: -200, currencyCode: 'USD' },
        ],
        groupTotals: [],
    });

    it('names each kind in the legend and the accessible label as the table does', async () => {
        const user = userEvent.setup();
        renderReport('measure=Net&groupBy=CategoryKind&bucket=Month', { answer: byKind(PERIOD) });

        const table = await screen.findByRole('table');
        const names = KINDS.map((k) => groupLabel('CategoryKind', k));
        for (const name of names) expect(within(table).getByText(name)).toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: 'Chart' }));
        const img = await screen.findByRole('img');
        const legend = within(img.closest('figure')!.querySelector('figcaption')!);
        for (const name of names) expect(legend.getByText(name)).toBeInTheDocument();
        expect(img.getAttribute('aria-label')).toContain(names[0]);
        for (const kind of KINDS) expect(legend.queryByText(kind)).toBeNull();
    });

    it('names each kind on a ranking\'s bars as the table does', async () => {
        renderReport('measure=Net&groupBy=CategoryKind&bucket=None&view=chart', { answer: byKind(null) });

        const list = await screen.findByRole('list', { name: /series/ });
        const names = KINDS.map((k) => groupLabel('CategoryKind', k));
        for (const name of names) expect(within(list).getByTitle(name)).toHaveTextContent(name);
        expect(list.getAttribute('aria-label')).toContain(names[0]);
    });
});

describe('a subcategory is named by its path wherever nothing beside it names its parent', () => {
    // Category P → Category L, and a root Category R. Flat, the parent has no
    // postings and so no row. Rolled up, it has one, and the child sits under it.
    const DEEP = ['Category P', 'Category L'];
    const FULL = DEEP.join('/');
    // The whole name reads in one colour, the text's around it: a quieter parent
    // reads as a highlighted leaf. So no element inside the name sets its own.
    const expectOneColour = (name: HTMLElement) => {
        for (const el of [name, ...name.querySelectorAll('*')]) {
            expect(el.className).not.toMatch(/\btext-text-/);
        }
    };
    const answer = (rollup: boolean, bucket: string | null) => (): ReportResult => {
        const rows: ReportRow[] = [
            { period: bucket, groupId: 'cat-l', groupName: 'Category L', groupPath: DEEP, parentId: 'cat-p', amount: 40, currencyCode: 'USD' },
            { period: bucket, groupId: 'cat-r', groupName: 'Category R', groupPath: ['Category R'], parentId: null, amount: 10, currencyCode: 'USD' },
        ];
        if (rollup) {
            rows.push({ period: bucket, groupId: 'cat-p', groupName: 'Category P', groupPath: ['Category P'], parentId: null, amount: 40, currencyCode: 'USD' });
        }
        return { ...summary(null), rows, groupTotals: [] };
    };

    it('shows a top-level subcategory in the table under its whole path', async () => {
        renderReport('groupBy=Category&bucket=Month&rollup=false', { answer: answer(false, PERIOD) });

        const table = await screen.findByRole('table');
        const name = within(table).getByTitle(FULL);
        expect(name).toHaveTextContent(FULL);
        expectOneColour(name);
        expect(within(table).getByTitle('Category R')).toHaveTextContent('Category R');
    });

    it('lets a table name wrap, as plain text did, so it never widens the sticky column', async () => {
        // A cell has no width to cut a name to: kept on one line, a long name
        // makes the auto-layout column as wide as itself, and the sticky column
        // then covers the figures. Every element of the name has to allow breaks.
        renderReport('groupBy=Category&bucket=Month&rollup=false', { answer: answer(false, PERIOD) });

        const table = await screen.findByRole('table');
        for (const name of [within(table).getByTitle(FULL), within(table).getByTitle('Category R')]) {
            for (const el of [name, ...name.querySelectorAll('*')]) {
                expect(el.className).not.toMatch(/\b(truncate|whitespace-nowrap|flex|shrink-0)\b/);
            }
        }
    });

    it('names an indented row by its own name, its parent being the row above', async () => {
        renderReport('groupBy=Category&bucket=Month&rollup=true', { answer: answer(true, PERIOD) });

        const table = await screen.findByRole('table');
        expect(within(table).getByTitle('Category L')).toHaveTextContent(/^Category L$/);
        expect(within(table).queryByTitle(FULL)).toBeNull();
    });

    it('names a series by its whole path in the legend and the accessible label', async () => {
        renderReport('groupBy=Category&bucket=Month&view=chart', { answer: answer(false, PERIOD) });

        const img = await screen.findByRole('img');
        const legend = within(img.closest('figure')!.querySelector('figcaption')!);
        expect(legend.getByTitle(FULL)).toHaveTextContent(FULL);
        expectOneColour(legend.getByTitle(FULL));
        expect(img.getAttribute('aria-label')).toContain(FULL);
    });

    it('names a ranked bar by its whole path, and keeps the leaf whole when the name is cut', async () => {
        renderReport('groupBy=Category&bucket=None&view=chart', { answer: answer(false, null) });

        const list = await screen.findByRole('list', { name: /series/ });
        const name = within(list).getByTitle(FULL);
        expect(name).toHaveTextContent(FULL);
        // The parents give way first: they truncate, the leaf does not shrink.
        expect(within(name).getByText('Category P')).toHaveClass('truncate');
        expect(within(name).getByText('/Category L')).toHaveClass('shrink-0');
        expectOneColour(name);
    });
});

describe('switching from the table to the chart', () => {
    it('keeps every group in the table and draws every group on the chart, none folded', async () => {
        const user = userEvent.setup();
        renderReport('groupBy=Category&bucket=Month');

        const table = await screen.findByRole('table');
        for (const g of GROUPS) expect(within(table).getByText(g.groupName)).toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: 'Chart' }));
        const chart = (await screen.findByRole('img')).closest('figure')!;
        const legend = within(chart.querySelector('figcaption')!);
        for (const g of GROUPS) expect(legend.getByText(g.groupName)).toBeInTheDocument();
        expect(legend.queryByText(/^Other/)).toBeNull();
    });
});
