import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { StubbedCall } from '@/lib/annotations/fetchStub';
import { formatCurrency } from '@/lib/money';
import type { RateUnavailable } from '@/lib/types';
import type { ReportResult, ReportRow, SavedReport } from '@/types/report';

import { ALL_GROUPS, hueFill } from '../../reports/chartSeries';
import { REPORT_TILES_MAX } from '../dashboardCatalog';
import {
    account,
    CHECKING,
    customiseWhenRead,
    emptyReport,
    homeStub,
    layoutWithTiles,
    LEDGER,
    renderHome,
    type HomeFixture,
} from '../homeTestFixtures';

// Report tiles on the home screen (ADR-0104 D2, as amended), through the real page,
// API client and router, with the server mocked at fetch. Every name and figure is
// invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const SAVED_A = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const SAVED_GONE = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const CATALOG_TILE = 'report:catalog:income-and-expenses';
const savedTile = (id: string) => `report:saved:${id}`;

/** A saved ranking: expenses by payee, no time bucket, over three whole months. */
const savedReport = (over: Partial<SavedReport> = {}): SavedReport => ({
    id: SAVED_A, name: 'Report A', visibility: 'private', ownerUserId: 'u1',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    spec: { measure: 'Spending', groupBy: 'Payee', bucket: 'None', dateMode: 'Sliding', windowCount: 3, windowUnit: 'Month', windowAlign: true },
    ...over,
});

const result = (over: Partial<ReportResult>): ReportResult => ({ ...emptyReport(), ...over });

/** The payee ranking the saved report asks for. */
const byPayee = (over: Partial<ReportResult> = {}): ReportResult => result({
    rows: [
        { period: null, groupId: null, groupName: 'Payee A', groupPath: ['Payee A'], parentId: null, amount: 300, currencyCode: 'USD', drawn: true },
        { period: null, groupId: null, groupName: 'Payee B', groupPath: ['Payee B'], parentId: null, amount: 100, currencyCode: 'USD', drawn: true },
    ],
    total: 450,
    rest: { count: 1, total: 50, byPeriod: [], only: null },
    ...over,
});

/** The catalog report, by category over two months: two categories whose names give
 *  one chip colour, the larger the ledger's Uncategorized. */
const byCategory = (over: Partial<ReportResult> = {}): ReportResult => {
    const rows: ReportRow[] = [
        { period: '2026-02', groupId: 'cat-u', groupName: 'Grocer U', groupPath: ['Grocer U'], parentId: null, amount: 200, currencyCode: 'USD', drawn: true },
        { period: '2026-03', groupId: 'cat-b', groupName: 'Grocer B', groupPath: ['Grocer B'], parentId: null, amount: 100, currencyCode: 'USD', drawn: true },
    ];
    return result({
        rows, total: 300, fromUtc: '2026-02-01T00:00:00Z', toUtc: '2026-04-01T00:00:00Z',
        periodTotals: [{ period: '2026-02', amount: 200 }, { period: '2026-03', amount: 100 }],
        groupTotals: [], rest: { count: 0, total: 0, byPeriod: [] },
        ...over,
    });
};

const ACCOUNTS = [
    account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
    account({ id: 'cat-u', name: 'Grocer U', accountType: 'category', categoryKind: 'expense', isSystem: true }),
    account({ id: 'cat-b', name: 'Grocer B', accountType: 'category', categoryKind: 'expense' }),
];

/** Each tile's report, answered by the dimension it asks for. */
const answer = (payee: ReportResult, category: ReportResult) => (call: StubbedCall) =>
    (call.search.get('groupBy') === 'Payee' ? payee : category);

function stub(f: HomeFixture) {
    const s = homeStub({ accounts: ACCOUNTS, savedReports: [savedReport()], summary: answer(byPayee(), byCategory()), ...f });
    s.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
    return s;
}

/** A search parameter as the router wrote it, a string's JSON quotes taken off. */
const param = (url: URL, key: string) => url.searchParams.get(key)?.replace(/^"(.*)"$/, '$1');

/** A tile's panel, found by its heading. */
const tile = (name: RegExp) =>
    screen.findByRole('heading', { level: 2, name }).then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

type Saved = { widgets: { id: string; key: string; visible: boolean; column: string; settings: Record<string, unknown> }[] };
const lastPut = (s: ReturnType<typeof stub>) => s.callsTo('PUT', /\/preferences\/dashboard$/).at(-1)!.body as Saved;

async function startEditing() {
    const user = userEvent.setup({ delay: null });
    await user.click(await customiseWhenRead());
    const tray = await screen.findByRole('region', { name: 'Hidden widgets' });
    return { user, tray };
}

/** "Add a report…", once the saved reports it lists have been read. */
const addReport = (tray: HTMLElement) => waitFor(() => {
    const button = within(tray).getByRole('button', { name: 'Add a report…' });
    expect(button).not.toHaveAttribute('aria-busy');
    return button;
});

describe('adding a report tile', () => {
    it('lists the saved reports, then the catalog\'s, and adds one at the end of the main column', async () => {
        const s = stub({});
        renderHome();
        const { user, tray } = await startEditing();

        await user.click(await addReport(tray));
        const menu = await screen.findByRole('menu', { name: 'Add a report' });
        expect(within(menu).getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['Report A', 'Income and Expenses', 'Returns']);
        await user.click(within(menu).getByRole('menuitem', { name: 'Report A' }));

        const frame = await screen.findByRole('group', { name: /^Report A, main column, 6 of 6$/ });
        await waitFor(() => expect(frame).toHaveFocus());
        expect(within(tray).getByText(`1 of ${REPORT_TILES_MAX}`)).toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: 'Done' }));
        await waitFor(() => expect(s.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        expect(lastPut(s).widgets.filter((w) => w.key === 'report')).toEqual([{
            id: savedTile(SAVED_A), key: 'report', visible: true, column: 'main', settings: { view: 'chart', height: 'compact' },
        }]);
    });

    it('lists a report already on the screen as added, and adds it no second time', async () => {
        stub({ prefs: layoutWithTiles([{ id: savedTile(SAVED_A) }]) });
        renderHome();
        const { user, tray } = await startEditing();

        await user.click(await addReport(tray));
        const menu = await screen.findByRole('menu', { name: 'Add a report' });
        const added = within(menu).getByRole('menuitem', { name: 'Report A · added' });
        expect(added).toBeDisabled();
        await user.click(added);
        expect(screen.getAllByRole('group', { name: /^Report A, / })).toHaveLength(1);
        // The catalog's report is still offered.
        expect(within(menu).getByRole('menuitem', { name: 'Income and Expenses' })).toBeEnabled();
    });

    it(`stops at ${REPORT_TILES_MAX} on the screen`, async () => {
        const saved = Array.from({ length: REPORT_TILES_MAX - 1 }, (_, i) =>
            savedReport({ id: `0000000${i}-0000-4000-8000-000000000000`, name: `Report ${i + 1}` }));
        stub({
            savedReports: [...saved, savedReport({ id: SAVED_A, name: 'Report A' })],
            prefs: layoutWithTiles([
                ...saved.map((r) => ({ id: savedTile(r.id) })),
                { id: CATALOG_TILE },
                { id: savedTile(SAVED_A), visible: false },
            ]),
        });
        renderHome();
        const { tray } = await startEditing();

        await waitFor(() => expect(within(tray).getByRole('button', { name: 'Add a report…' })).toBeDisabled());
        expect(within(tray).getByText(`${REPORT_TILES_MAX} of ${REPORT_TILES_MAX}`)).toBeInTheDocument();
        // A tile hidden earlier waits in the tray, and cannot come back past the most shown.
        expect(within(tray).getByRole('button', { name: 'Add Report A' })).toBeDisabled();
    });
});

describe('a tile\'s report', () => {
    it('shows a saved report and a catalog report, each named, each opening its report', async () => {
        stub({ prefs: layoutWithTiles([{ id: savedTile(SAVED_A) }, { id: CATALOG_TILE }]) });
        const { router } = renderHome();
        const user = userEvent.setup({ delay: null });

        const saved = await tile(/^Report A · last 3 months$/);
        const catalog = await tile(/^Income and Expenses · last 12 months$/);

        const savedLink = new URL(within(saved).getByRole('link', { name: 'Open report →' }).getAttribute('href')!, 'http://x');
        expect(savedLink.pathname).toBe(`/ledgers/${LEDGER}/reports/view`);
        expect(param(savedLink, 'saved')).toBe(SAVED_A);
        expect(param(savedLink, 'name')).toBe('Report A');
        expect(param(savedLink, 'groupBy')).toBe('Payee');

        // A catalog report opens as the catalog's card opens it: no name to go stale.
        const catalogHref = new URL(within(catalog).getByRole('link', { name: 'Open report →' }).getAttribute('href')!, 'http://x');
        expect(param(catalogHref, 'groupBy')).toBe('Category');
        expect(catalogHref.searchParams.has('name')).toBe(false);
        expect(catalogHref.searchParams.has('saved')).toBe(false);

        await user.click(within(saved).getByRole('link', { name: 'Open report →' }));
        await waitFor(() => expect(router.state.location.pathname).toBe(`/ledgers/${LEDGER}/reports/view`));
        expect(await screen.findByText('elsewhere')).toBeInTheDocument();
    });

    it('offers nothing but "Open report →" and the widget menu', async () => {
        stub({ prefs: layoutWithTiles([{ id: savedTile(SAVED_A) }]) });
        renderHome();
        const panel = await tile(/^Report A/);
        await within(panel).findByRole('list', { name: /series/ });
        expect(within(panel).getAllByRole('link').map((l) => l.textContent)).toEqual(['Open report →']);
        expect(within(panel).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Report A actions']);
    });

    it('sends the local today with the report, and the chart\'s cut', async () => {
        const s = stub({ prefs: layoutWithTiles([{ id: savedTile(SAVED_A) }]) });
        renderHome();
        await within(await tile(/^Report A/)).findByRole('list', { name: /series/ });
        const call = s.callsTo('GET', /\/reports\/summary$/).find((c) => c.search.get('groupBy') === 'Payee')!;
        expect(call.search.get('today')).toBe('2026-03-12');
        // A report with no cap is All: every group, never the colour count.
        expect(call.search.get('topN')).toBe(String(ALL_GROUPS));
    });
});

describe('a saved report that no longer exists', () => {
    const prefs = layoutWithTiles([{ id: savedTile(SAVED_GONE) }, { id: savedTile(SAVED_A) }]);

    it('is not drawn outside the edit', async () => {
        stub({ prefs });
        renderHome();
        // Settled: the saved reports are read, since the tile they name is drawn.
        await within(await tile(/^Report A/)).findByRole('list', { name: /series/ });
        expect(screen.queryByText(/Report no longer exists/)).toBeNull();
        expect(screen.queryByRole('heading', { level: 2, name: 'Report' })).toBeNull();
    });

    it('says so while the layout is edited, and Remove takes it off the layout', async () => {
        const s = stub({ prefs });
        renderHome();
        const { user } = await startEditing();

        const frame = await screen.findByRole('group', { name: /^Report, main column, \d+ of \d+$/ });
        expect(within(frame).getByText(/Report no longer exists/)).toBeInTheDocument();
        await user.click(within(frame).getByRole('button', { name: 'Remove' }));

        await waitFor(() => expect(screen.queryByRole('group', { name: /^Report, main column/ })).toBeNull());
        // Focus goes on to the next frame, never to the page.
        await waitFor(() => expect(screen.getByRole('group', { name: /^Report A, main column/ })).toHaveFocus());
        await user.click(screen.getByRole('button', { name: 'Done' }));
        await waitFor(() => expect(s.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        expect(lastPut(s).widgets.filter((w) => w.key === 'report').map((w) => w.id)).toEqual([savedTile(SAVED_A)]);
    });
});

describe('a tile\'s height and view', () => {
    it.each([
        ['compact', 'h-fixed-176px'],
        ['tall', 'h-fixed-256px'],
    ])('holds the %s height whatever it draws', async (height, heightClass) => {
        stub({ prefs: layoutWithTiles([{ id: CATALOG_TILE, settings: { height } }, { id: savedTile(SAVED_A), settings: { height, view: 'table' } }]) });
        renderHome();
        const chart = await tile(/^Income and Expenses/);
        await within(chart).findByRole('img');
        const table = await tile(/^Report A/);
        await within(table).findByRole('table');
        for (const panel of [chart, table]) {
            expect(panel.querySelector('[data-report-tile-body]')!.className).toContain(heightClass);
        }
    });

    it('draws a chart over time in the report\'s colours, with its legend inside the height', async () => {
        stub({ prefs: layoutWithTiles([{ id: CATALOG_TILE }]) });
        renderHome();
        const panel = await tile(/^Income and Expenses/);
        const figure = (await within(panel).findByRole('img')).closest('figure')!;
        const legend = figure.querySelector('figcaption')!;
        // The legend takes its whole lines inside the tile's height: its box is the
        // whole lines its room holds, never a fixed cap that slices one.
        const series = legend.querySelector<HTMLElement>('[data-legend-series]')!;
        expect(series.className).toContain('max-h-[calc(round(down,100%');
        expect(series.className).not.toContain('max-h-fixed');
        expect(within(legend).getByText('Grocer B').parentElement!.querySelector('rect')!.getAttribute('class')).toBe(hueFill('groc'));
    });

    it('draws a ranking neutral, with the server\'s total under it', async () => {
        stub({ prefs: layoutWithTiles([{ id: savedTile(SAVED_A) }]) });
        renderHome();
        const panel = await tile(/^Report A/);
        const list = await within(panel).findByRole('list', { name: /series/ });
        expect(within(list).getAllByRole('listitem').map((r) => r.textContent)).toEqual([
            'Payee A$300.00', 'Payee B$100.00', 'Other (1)$50.00',
        ]);
        // The total is the server's, not the bars added up.
        const total = within(panel).getByText('Total, all payees').parentElement!;
        expect(total).toHaveTextContent('$450.00');
    });

    it('lists every group in a table, with the server\'s totals', async () => {
        const s = stub({ prefs: layoutWithTiles([{ id: CATALOG_TILE, settings: { view: 'table' } }]) });
        renderHome();
        const panel = await tile(/^Income and Expenses/);
        const table = await within(panel).findByRole('table');
        const total = within(table).getByText('Total', { selector: 'td' }).closest('tr')!;
        expect(total).toHaveTextContent('$300.00');
        // A table asks for no cut: it lists every group.
        expect(s.callsTo('GET', /\/reports\/summary$/).find((c) => c.search.get('groupBy') === 'Category')!.search.has('topN')).toBe(false);
    });
});

describe('a category chart on a tile', () => {
    /** Hold every read of the active accounts list (the one the chart waits on) until released. */
    function holdAccounts(): () => void {
        let release = () => {};
        const gate = new Promise<void>((resolve) => { release = resolve; });
        const answerNow = globalThis.fetch;
        vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            const url = new URL(raw, 'http://localhost');
            const held = /\/accounts$/.test(url.pathname) && !url.searchParams.has('includeInactive');
            return held ? gate.then(() => answerNow(input, init)) : answerNow(input, init);
        });
        return release;
    }

    it('draws nothing until the ledger\'s accounts arrive, then Uncategorized once, in its borrowed hue', async () => {
        stub({ prefs: layoutWithTiles([{ id: CATALOG_TILE }]) });
        const release = holdAccounts();
        const { queryClient } = renderHome();
        const seen: string[] = [];
        const observer = new MutationObserver(() => {
            for (const caption of document.querySelectorAll('figcaption')) {
                const entry = [...caption.querySelectorAll('span')].find((e) => e.textContent === 'Grocer U');
                const fill = entry?.parentElement?.querySelector('rect')?.getAttribute('class');
                if (fill) seen.push(fill);
            }
        });
        observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });

        // The report has settled; the accounts list is still held.
        await waitFor(() => expect(queryClient.getQueryCache().findAll({ queryKey: ['home', LEDGER, 'report'] })[0]?.state.status).toBe('success'));
        const panel = await tile(/^Income and Expenses/);
        expect(within(panel).getByLabelText('Loading Income and Expenses')).toHaveAttribute('aria-busy', 'true');
        expect(within(panel).queryByRole('img')).toBeNull();

        release();
        const figure = (await within(panel).findByRole('img')).closest('figure')!;
        expect(within(figure.querySelector('figcaption')!).getByText('Grocer U').parentElement!.querySelector('rect')!.getAttribute('class'))
            .toBe(hueFill('din'));
        observer.disconnect();
        expect(seen.length).toBeGreaterThan(0);
        expect(new Set(seen)).toEqual(new Set([hueFill('din')]));
    });
});

describe('a report with a missing rate (ADR-0114 D9)', () => {
    const missing: RateUnavailable = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-02', reason: 'rate_unavailable' };

    it('draws no chart, of totals or of a ranking, and names the missing rate in its place', async () => {
        stub({
            prefs: layoutWithTiles([{ id: CATALOG_TILE }, { id: savedTile(SAVED_A) }]),
            summary: answer(
                byPayee({ rest: { count: 1, total: null, byPeriod: [], only: null, rateUnavailable: [missing] } }),
                byCategory({ total: null, rateUnavailable: [missing] }),
            ),
        });
        renderHome();

        for (const name of [/^Income and Expenses/, /^Report A/]) {
            const t = await tile(name);
            expect(await within(t).findByText(/^No CAD to USD rate for Mar 2, 2026\./)).toBeInTheDocument();
            expect(within(t).queryByRole('img')).toBeNull();
        }
    });

    it('reads a table\'s figure that needs a missing rate "no rate", and keeps every other', async () => {
        stub({
            prefs: layoutWithTiles([{ id: CATALOG_TILE, settings: { view: 'table' } }]),
            summary: answer(byPayee(), byCategory({
                total: null, rateUnavailable: [missing],
                periodTotals: [{ period: '2026-02', amount: 200 }, { period: '2026-03', amount: null, rateUnavailable: [missing] }],
            })),
        });
        renderHome();
        const table = await within(await tile(/^Income and Expenses/)).findByRole('table');
        expect(within(table).getAllByText('$200.00')).toHaveLength(3);
        const total = within(table).getByText('Total', { selector: 'td' }).closest('tr')!;
        const marked = within(total as HTMLElement).getAllByText('no rate');
        expect(marked).toHaveLength(2);
        expect(marked[0]).toHaveAttribute('title', 'No CAD to USD rate for Mar 2, 2026.');
    });
});

describe('a report by account, the accounts in their own currencies (ADR-0114 D7)', () => {
    const byAccount = (over: Partial<ReportResult> = {}): ReportResult => result({
        rows: [
            { period: null, groupId: 'acct-a', groupName: 'Account A', groupPath: ['Account A'], parentId: null, amount: 300, currencyCode: 'USD', drawn: true },
            { period: null, groupId: 'acct-b', groupName: 'Account B', groupPath: ['Account B'], parentId: null, amount: 80, currencyCode: 'EUR', drawn: true },
        ],
        total: 420,
        rest: { count: 0, total: 0, byPeriod: [] },
        ...over,
    });
    const savedByAccount = savedReport({ spec: { ...savedReport().spec, groupBy: 'Account' } });

    it('draws no chart, and says why in its place', async () => {
        stub({ prefs: layoutWithTiles([{ id: savedTile(SAVED_A) }]), savedReports: [savedByAccount], summary: () => byAccount() });
        renderHome();
        const t = await tile(/^Report A/);
        expect(await within(t).findByText(/^No chart: each account here is in its own currency and the totals are in USD, so a chart would draw EUR and USD on one scale\./))
            .toBeInTheDocument();
        expect(within(t).queryByRole('img')).toBeNull();
    });

    it('prints each account in its own currency in a table, and the total in the reporting one', async () => {
        stub({
            prefs: layoutWithTiles([{ id: savedTile(SAVED_A), settings: { view: 'table' } }]),
            savedReports: [savedByAccount], summary: () => byAccount(),
        });
        renderHome();
        const table = await within(await tile(/^Report A/)).findByRole('table');
        const row = within(table).getByText('Account B').closest('tr')!;
        expect(row).toHaveTextContent(formatCurrency(80, 'EUR'));
        expect(row).not.toHaveTextContent('$');
        const total = within(table).getByText('Total', { selector: 'td' }).closest('tr')!;
        expect(total).toHaveTextContent('$420.00');
    });
});

describe('a viewer', () => {
    it('sees the tile and its link, and arranges their own layout with it', async () => {
        stub({ role: 'viewer', prefs: layoutWithTiles([{ id: savedTile(SAVED_A) }]) });
        renderHome();
        expect(await screen.findByText('View only')).toBeInTheDocument();
        const panel = await tile(/^Report A/);
        await within(panel).findByRole('list', { name: /series/ });
        expect(within(panel).getByRole('link', { name: 'Open report →' })).toBeInTheDocument();
        // The layout is the reader's own (RequireLedgerMembership): a viewer adds reports too.
        const { tray } = await startEditing();
        expect(await addReport(tray)).toBeEnabled();
    });
});

describe('the Returns report on a tile', () => {
    const RETURNS_TILE = 'report:catalog:returns';
    const point = (end: string, index: number | null, gain: number | null) => ({
        periodEnd: `${end}T00:00:00Z`, index, indexUnavailableReason: index === null ? 'before_first_funding' : null,
        periodReturn: null, periodReturnUnavailableReason: null, coveredDays: index === null ? 0 : 30,
        gain, gainUnavailableReason: gain === null ? 'before_first_funding' : null, mwrTrailingYear: null, mwrUnavailableReason: 'under_a_year_covered',
        rateUnavailable: [] as RateUnavailable[],
        benchmark: null, benchmarkUnavailableReason: null, benchmarkUnavailableDate: null, benchmarkRateUnavailable: [],
    });
    const returns = (index: number | null) => ({
        from: '2026-01-01T00:00:00Z', to: '2026-03-12T00:00:00Z', interval: 'month', computedAt: '2026-03-12T15:00:00Z', engineVersion: 'test',
        benchmark: null,
        series: [{
            scope: 'ledger', accountId: null, accountName: null, currencyCode: 'USD',
            points: [point('2026-01-31', index === null ? null : 100, index === null ? null : 0), point('2026-02-28', index, index === null ? null : 50)],
        }],
    });

    it('asks the returns endpoint and draws its chart, linking to the Returns view', async () => {
        const s = stub({ prefs: layoutWithTiles([{ id: RETURNS_TILE }]) });
        s.on('GET', /\/reports\/returns-series$/, returns(105));
        renderHome();

        const panel = await tile(/^Returns/);
        expect(await within(panel).findByRole('img', { name: /Ledger time-weighted return index went from 100 to 105/ })).toBeInTheDocument();
        expect(s.callsTo('GET', /\/reports\/returns-series$/).at(-1)!.search.get('interval')).toBe('month');
        const link = within(panel).getByRole('link', { name: 'Open report →' });
        expect(link.getAttribute('href')).toContain('kind=');
        // The legend gives way to the plot in the tile's fixed height, and scrolls.
        expect(panel.querySelector('[data-returns-legend]')).toHaveClass('min-h-0', 'overflow-y-auto');
    });

    it('draws its table when the tile is set to one', async () => {
        const s = stub({ prefs: layoutWithTiles([{ id: RETURNS_TILE, settings: { view: 'table' } }]) });
        s.on('GET', /\/reports\/returns-series$/, returns(105));
        renderHome();

        const panel = await tile(/^Returns/);
        expect(await within(panel).findByRole('table')).toBeInTheDocument();
    });

    it('says so when nothing is invested', async () => {
        const s = stub({ prefs: layoutWithTiles([{ id: RETURNS_TILE }]) });
        s.on('GET', /\/reports\/returns-series$/, returns(null));
        renderHome();

        const panel = await tile(/^Returns/);
        expect(await within(panel).findByText('No invested balance in this window.')).toBeInTheDocument();
    });

    it('names the missing rate, not an empty account, when every point needs one', async () => {
        const s = stub({ prefs: layoutWithTiles([{ id: RETURNS_TILE }]) });
        const base = returns(null);
        const missing: RateUnavailable = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-01-31', reason: 'rate_unavailable' };
        const answer = {
            ...base,
            series: base.series.map((sr) => ({
                ...sr,
                points: sr.points.map((p) => ({ ...p, indexUnavailableReason: 'rate_unavailable', rateUnavailable: [missing] })),
            })),
        };
        s.on('GET', /\/reports\/returns-series$/, answer);
        renderHome();

        const panel = await tile(/^Returns/);
        expect(await within(panel).findByText(/^No CAD to USD rate for Jan 31, 2026\./)).toBeInTheDocument();
        expect(within(panel).queryByText('No invested balance in this window.')).toBeNull();
    });
});
