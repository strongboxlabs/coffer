import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { problem, stubFetch, type FetchStub, type StubbedCall } from '@/lib/annotations/fetchStub';
import { formatLedgerDate as formatDateLabel } from '@/lib/dates';
import { currencyAffix, formatCurrency, formatPrice, formatQuantity } from '@/lib/money';
import type { SecurityDetail, SecurityPriceRow, SecurityTransaction } from '@/lib/types';

import { SecurityDetailPage } from './SecurityDetailPage';

/**
 * The security detail page against the wire: every read and write goes
 * through `fetch`, answered by the stub. Each test ends with no unrouted
 * request.
 */

const LEDGER = '00000000-0000-0000-0000-0000000000aa';
const SECURITY = '00000000-0000-0000-0000-0000000000f1';
const PRICE = '00000000-0000-0000-0000-0000000000f2';

const DETAIL: SecurityDetail = {
    id: SECURITY, ticker: 'SECA', cusip: null, name: 'Security A', assetClass: 'equity',
    exchange: null, isActive: true, totalQuantity: 10, totalCostBasis: 100, latestPrice: 12,
    latestPriceAsOf: '2026-10-01', recentPrices: [], quoteSymbol: null, autoPrice: true,
    quoteSymbolPublic: true, vehicleType: null, region: null, equitySize: null, equityStyle: null,
    fiDuration: null, fiCredit: null, taxCharacter: null, classificationSource: null,
    classificationConfidence: null, priceCurrencyCode: 'USD', holdingsCurrencyCode: 'USD',
    costBasisCurrencyCode: 'USD', costBasisRateUnavailable: [],
    marketValue: 120, marketValueCurrencyCode: 'USD', marketValueRateUnavailable: [],
    unrealizedGain: 20, unrealizedGainCurrencyCode: 'USD', unrealizedGainRateUnavailable: [],
};

/** The detail's totals all in one reporting currency, as the server sends them. */
const inReporting = (code: string): Partial<SecurityDetail> => ({
    costBasisCurrencyCode: code, marketValueCurrencyCode: code, unrealizedGainCurrencyCode: code,
});

const PRICE_ROW: SecurityPriceRow = {
    id: PRICE, asOf: '2026-10-01', price: 12, currencyCode: 'USD', high: null, low: null,
    volume: null, source: 'manual', unbacked: false,
};

const PRICES = /\/securities\/[^/]+\/prices$/;
const ONE_PRICE = /\/securities\/[^/]+\/prices\/[^/]+$/;

const BROKERAGE = '00000000-0000-0000-0000-0000000000b1';

const TRADE: SecurityTransaction = {
    headerId: '00000000-0000-0000-0000-0000000000e1', accountId: BROKERAGE, accountName: 'Brokerage A',
    postedAt: '2026-09-01T00:00:00Z', action: 'buy', amount: -100, quantity: 10, unitPrice: 10,
    payee: null, currencyCode: 'USD',
};

let stub: FetchStub;

/** The page's reads; by default no transactions. No accounts read is served: a trade
 *  carries its account's currency, so the page asks for none, and one it did ask for
 *  would be unrouted. */
function serve(opts: {
    priceCurrency?: string;
    trades?: SecurityTransaction[];
    detail?: Partial<SecurityDetail>;
} = {}) {
    const priceRow = { ...PRICE_ROW, currencyCode: opts.priceCurrency ?? 'USD' };
    const trades = opts.trades ?? [];
    stub
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger A', role: 'owner' }])
        .on('GET', /\/securities\/[^/]+$/, { ...DETAIL, ...opts.detail })
        .on('GET', /\/securities\/[^/]+\/transactions$/, { items: trades, cursorForOlder: null, totalCount: trades.length })
        .on('GET', PRICES, { items: [priceRow], cursorForOlder: null, totalCount: 1, defaultCurrencyCode: priceRow.currencyCode })
        .on('POST', PRICES, priceRow)
        .on('PATCH', ONE_PRICE, priceRow)
        .on('DELETE', ONE_PRICE, undefined);
}

function renderPage() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
    const root = createRootRoute();
    const page = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/securities/$securityId',
        component: SecurityDetailPage,
    });
    const catalog = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/securities',
        component: () => <main>catalog</main>,
    });
    const ledger = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>ledger</main>,
    });
    const register = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: () => <main>register</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([page, catalog, ledger, register]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/securities/${SECURITY}`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    const keys = () => invalidated.mock.calls.map((c) => c[0]?.queryKey);
    return { keys };
}

/** A price write moves every figure that values the security: the accounts and the home screen's reads. */
function expectValuationRefreshed(keys: () => unknown[]) {
    for (const key of [
        ['accounts', LEDGER], ['home', LEDGER], ['overview', LEDGER], ['budget-progress', LEDGER],
    ]) {
        expect(keys()).toContainEqual(key);
    }
}

beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
    localStorage.clear();
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('SecurityDetailPage: where the price dialog opens', () => {
    // The dialog's content names its first field (ADR-0023 §L): Add price
    // focuses Price, the date already filled in; Edit price names none, so it
    // opens on its first field.
    it('opens Add price on Price', async () => {
        serve();
        renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: '+ Add price' }));

        const dialog = await screen.findByRole('dialog', { name: 'Add price' });
        expect(within(dialog).getByLabelText('Price *')).toHaveFocus();
        expect(stub.unrouted).toEqual([]);
    });

    it('opens Edit price on its first field, the date', async () => {
        serve();
        renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByText('edit →'));

        const dialog = await screen.findByRole('dialog', { name: 'Edit price' });
        expect(within(dialog).getByLabelText('Date *')).toHaveFocus();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('SecurityDetailPage: a price write', () => {
    it('adding a price refreshes the accounts and the home screen\'s reads', async () => {
        serve();
        const { keys } = renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: '+ Add price' }));
        const dialog = await screen.findByRole('dialog', { name: 'Add price' });
        await user.type(within(dialog).getByLabelText('Price *'), '13');
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add price' })).toBeNull());
        expect(stub.callsTo('POST', PRICES)).toHaveLength(1);
        expectValuationRefreshed(keys);
        expect(stub.unrouted).toEqual([]);
    });

    it('editing a price refreshes the accounts and the home screen\'s reads', async () => {
        serve();
        const { keys } = renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByText('edit →'));
        const dialog = await screen.findByRole('dialog', { name: 'Edit price' });
        const price = within(dialog).getByLabelText('Price *');
        await user.clear(price);
        await user.type(price, '14');
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit price' })).toBeNull());
        expect(stub.callsTo('PATCH', ONE_PRICE)).toHaveLength(1);
        expectValuationRefreshed(keys);
        expect(stub.unrouted).toEqual([]);
    });

    it('deleting a price refreshes the accounts and the home screen\'s reads', async () => {
        serve();
        const { keys } = renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByText('edit →'));
        const dialog = await screen.findByRole('dialog', { name: 'Edit price' });
        await user.click(within(dialog).getByRole('button', { name: 'Delete price' }));
        const confirm = await screen.findByRole('dialog', { name: 'Delete price?' });
        await user.click(within(confirm).getByRole('button', { name: 'Delete' }));

        await waitFor(() => expect(stub.callsTo('DELETE', ONE_PRICE)).toHaveLength(1));
        await waitFor(() => expectValuationRefreshed(keys));
        expect(stub.unrouted).toEqual([]);
    });
});

describe('SecurityDetailPage: a price row from the keyboard', () => {
    // Each price row's edit → is a button, so the keyboard reaches Edit price
    // and Delete price; a price deleted takes its row's button with it, and
    // focus goes to the next row's, else the previous row's, else + Add price
    // (ADR-0023 §L).
    const row = (id: string, asOf: string): SecurityPriceRow => ({ ...PRICE_ROW, id, asOf });

    function servePrices(rows: SecurityPriceRow[]) {
        let live = rows;
        serve();
        stub.on('GET', PRICES, () => ({ items: live, cursorForOlder: null, totalCount: live.length, defaultCurrencyCode: 'USD' }));
        stub.on('DELETE', ONE_PRICE, (call: StubbedCall) => {
            live = live.filter((r) => !call.path.endsWith(`/${r.id}`));
            return undefined;
        });
    }

    async function deleteFromKeyboard(user: ReturnType<typeof userEvent.setup>, name: string) {
        const edit = await screen.findByRole('button', { name });
        edit.focus();
        await user.keyboard('{Enter}');
        const dialog = await screen.findByRole('dialog', { name: 'Edit price' });
        await user.click(within(dialog).getByRole('button', { name: 'Delete price' }));
        const confirm = await screen.findByRole('dialog', { name: 'Delete price?' });
        await user.click(within(confirm).getByRole('button', { name: 'Delete' }));
        await waitFor(() => expect(screen.queryByRole('button', { name })).toBeNull());
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    }

    it('gives focus to the next row\'s edit button when a price is deleted', async () => {
        servePrices([row('p1', '2026-10-03'), row('p2', '2026-10-02'), row('p3', '2026-10-01')]);
        renderPage();
        const user = userEvent.setup();

        await deleteFromKeyboard(user, `Edit price for ${formatDateLabel('2026-10-02')}`);

        expect(screen.getByRole('button', { name: `Edit price for ${formatDateLabel('2026-10-01')}` })).toHaveFocus();
        expect(stub.unrouted).toEqual([]);
    });

    it('gives focus to + Add price when the last price is deleted', async () => {
        servePrices([row('p1', '2026-10-01')]);
        renderPage();
        const user = userEvent.setup();

        await deleteFromKeyboard(user, `Edit price for ${formatDateLabel('2026-10-01')}`);

        expect(screen.getByRole('button', { name: '+ Add price' })).toHaveFocus();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('SecurityDetailPage: a trade\'s action', () => {
    /** A table's row cells, by a cell unique to the row. */
    const rowOf = (text: string) => screen.getByText(text).closest('tr')!;
    const trade = (n: number, accountName: string, action: string | null): SecurityTransaction => ({
        ...TRADE, headerId: `00000000-0000-0000-0000-0000000000e${n}`, accountName, action,
    });

    it('reads the word the register\'s chip and the editor\'s picker use, never the stored value', async () => {
        serve({
            trades: [
                trade(1, 'Brokerage D', 'dividend_cash'),
                trade(2, 'Brokerage R', 'dividend_reinvest'),
                trade(3, 'Brokerage X', 'divx'),
                trade(4, 'Brokerage T', 'transfer_shares'),
                trade(5, 'Brokerage N', null),
            ],
        });
        renderPage();

        // Anchored on a trade's own row, which needs the trades read to have landed.
        await waitFor(() => expect(within(rowOf('Brokerage R')).getByText('Reinvest')).toBeInTheDocument());
        expect(within(rowOf('Brokerage D')).getByText('Dividend')).toBeInTheDocument();
        expect(within(rowOf('Brokerage X')).getByText('DividendXfr')).toBeInTheDocument();
        expect(within(rowOf('Brokerage T')).getByText('Transfer shares')).toBeInTheDocument();
        // A trade with no action has no word: its cell is the dash, the only one in a row
        // whose figures are all known.
        expect(within(rowOf('Brokerage N')).getByText('—')).toBeInTheDocument();
        for (const stored of ['dividend_cash', 'dividend_reinvest', 'divx', 'transfer_shares']) {
            expect(screen.queryByText(stored)).toBeNull();
        }
        expect(stub.unrouted).toEqual([]);
    });

    it('sets the word as the register\'s chip does: not uppercased or letter-spaced, small and muted', async () => {
        serve({ trades: [trade(1, 'Brokerage R', 'dividend_reinvest')] });
        renderPage();

        const cell = (await screen.findByText('Reinvest')).closest('td')!;
        expect(cell).not.toHaveClass('uppercase');
        expect(cell.className).not.toMatch(/\btracking-/);
        expect(cell).toHaveClass('text-[0.6875rem]', 'text-text-muted');
        // Anchor: the column heads keep their uppercase, so the check reads the right cell.
        expect(screen.getByRole('columnheader', { name: 'Action' }).closest('thead')).toHaveClass('uppercase');
        expect(stub.unrouted).toEqual([]);
    });
});

describe('SecurityDetailPage: a look-through sleeve removed', () => {
    // A Remove takes its sleeve away and focus goes to the next row's Remove,
    // the previous row's, or Add sleeve, never to the page (ADR-0023 §L); the
    // rows' own keys never reach the server.
    const COMPONENTS = /\/securities\/[^/]+\/components$/;

    async function openEdit(sleeves: number) {
        serve({ detail: { assetClass: 'multi_asset' } });
        stub.on('GET', COMPONENTS, Array.from({ length: sleeves }, (_, i) => ({
            assetClass: 'equity', region: null, weight: 10 * (i + 1),
        })));
        stub.on('PUT', COMPONENTS, undefined);
        stub.on('PATCH', /\/securities\/[^/]+$/, { ...DETAIL, assetClass: 'multi_asset' });
        const { keys } = renderPage();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: 'Edit' }));
        const dialog = await screen.findByRole('dialog', { name: 'Edit security' });
        await waitFor(() => expect(within(dialog).getAllByRole('button', { name: 'Remove' })).toHaveLength(sleeves));
        return { user, dialog, keys };
    }

    it('gives focus to the previous row\'s Remove when the last row goes, and to the next row\'s from the middle', async () => {
        const { user, dialog } = await openEdit(3);

        const removes = () => within(dialog).getAllByRole('button', { name: 'Remove' });
        const [, middle, last] = removes();
        last!.focus();
        await user.keyboard('{Enter}');
        expect(removes()).toHaveLength(2);
        expect(middle).toHaveFocus();

        removes()[0]!.focus();
        await user.keyboard('{Enter}');
        expect(removes()).toHaveLength(1);
        expect(middle).toHaveFocus();
        expect(within(dialog).getByRole('spinbutton')).toHaveValue(20);
    });

    it('gives focus to Add sleeve when the only row goes, and sends only the sleeves\' fields', async () => {
        const { user, dialog, keys } = await openEdit(1);

        within(dialog).getByRole('button', { name: 'Remove' }).focus();
        await user.keyboard('{Enter}');

        expect(within(dialog).getByRole('button', { name: 'Add sleeve' })).toHaveFocus();
        await user.click(within(dialog).getByRole('button', { name: 'Add sleeve' }));
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(stub.callsTo('PUT', COMPONENTS)).toHaveLength(1));
        expect(stub.callsTo('PUT', COMPONENTS)[0]!.body).toEqual({
            components: [{ assetClass: 'equity', region: null, weight: 0 }],
        });
        // An edited security can value its holdings differently, which the
        // accounts and the home screen's figures include.
        await waitFor(() => expectValuationRefreshed(keys));
    });
});

describe('SecurityDetailPage: each figure in its currency', () => {
    /** A hero figure's value, by its label. */
    const stat = (label: string) => screen.getByText(label).nextElementSibling!;
    /** A table's row cells, by a cell unique to the row. */
    const rowOf = (text: string) => screen.getByText(text).closest('tr')!;

    it('an account and its prices in one currency: every figure is in it', async () => {
        serve({
            priceCurrency: 'EUR', trades: [{ ...TRADE, currencyCode: 'EUR' }],
            detail: { priceCurrencyCode: 'EUR', holdingsCurrencyCode: 'EUR', ...inReporting('EUR') },
        });
        renderPage();

        // Anchored on the transaction row, which needs the trades and the detail.
        await waitFor(() => expect(within(rowOf('Brokerage A')).getByText(formatCurrency(-100, 'EUR'))).toBeInTheDocument());
        expect(within(rowOf('Brokerage A')).getByText(formatPrice(10, 'EUR'))).toBeInTheDocument();
        expect(stat('Latest price')).toHaveTextContent(formatPrice(12, 'EUR'));
        expect(stat('Cost basis')).toHaveTextContent(formatCurrency(100, 'EUR'));
        expect(screen.getByText(`avg ${formatPrice(10, 'EUR')}/sh`)).toBeInTheDocument();
        expect(stat('Market value')).toHaveTextContent(formatCurrency(120, 'EUR'));
        expect(stat('Unrealized')).toHaveTextContent(formatCurrency(20, 'EUR'));
        expect(within(rowOf('Manual')).getByText(formatPrice(12, 'EUR'))).toBeInTheDocument();
        expect(screen.queryByText(/\$/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('the hero takes its currencies from the detail read, not from the rows the page has loaded', async () => {
        // The loaded price row and trade say USD; the server says what the
        // hero's figures are in.
        serve({ trades: [TRADE], detail: { priceCurrencyCode: 'EUR', holdingsCurrencyCode: 'EUR', ...inReporting('EUR') } });
        renderPage();

        await waitFor(() => expect(within(rowOf('Brokerage A')).getByText(formatCurrency(-100, 'USD'))).toBeInTheDocument());
        expect(stat('Latest price')).toHaveTextContent(formatPrice(12, 'EUR'));
        expect(stat('Cost basis')).toHaveTextContent(formatCurrency(100, 'EUR'));
        expect(stat('Market value')).toHaveTextContent(formatCurrency(120, 'EUR'));
        expect(stat('Unrealized')).toHaveTextContent(formatCurrency(20, 'EUR'));
        expect(stub.unrouted).toEqual([]);
    });

    it('a price in another currency than the reporting one: the price in its own, every total converted, none mixed', async () => {
        // The shares at a pound price give no euro value at any one rate the page could
        // know: the value and the gain are the server's, converted through each holding
        // account (ADR-0114 D7), never the price times the quantity.
        serve({
            priceCurrency: 'GBP', trades: [{ ...TRADE, currencyCode: 'EUR' }],
            detail: {
                priceCurrencyCode: 'GBP', holdingsCurrencyCode: 'EUR', ...inReporting('EUR'),
                marketValue: 150, unrealizedGain: 50,
            },
        });
        renderPage();

        await waitFor(() => expect(within(rowOf('Brokerage A')).getByText(formatCurrency(-100, 'EUR'))).toBeInTheDocument());
        expect(stat('Latest price')).toHaveTextContent(formatPrice(12, 'GBP'));
        expect(stat('Market value')).toHaveTextContent(formatCurrency(150, 'EUR'));
        expect(stat('Cost basis')).toHaveTextContent(formatCurrency(100, 'EUR'));
        expect(stat('Unrealized')).toHaveTextContent(formatCurrency(50, 'EUR'));
        expect(screen.queryByText('mixed')).toBeNull();
        expect(within(rowOf('Manual')).getByText(formatPrice(12, 'GBP'))).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });

    it('a gain that prints as a zero is a plain zero, never a red negative one', async () => {
        // The server's gain is a negative residue below one minor unit, which survives
        // the wire (a bare negative zero would not: JSON writes it as a
        // plain one). It prints as a zero, so it is one: no sign, no loss tone
        // (ADR-0023 §H).
        serve({ detail: { latestPrice: 0.7, totalQuantity: 3, totalCostBasis: 2.1, marketValue: 2.1, unrealizedGain: -0.0000004 } });
        renderPage();

        await waitFor(() => expect(stat('Latest price')).toHaveTextContent(formatPrice(0.7, 'USD')));
        const gain = stat('Unrealized');
        expect(gain).toHaveTextContent(formatCurrency(0, 'USD'));
        expect(gain.textContent).not.toContain('-');
        expect(gain).not.toHaveClass('text-state-danger');
        expect(stub.unrouted).toEqual([]);
    });

    it('a cost basis that needs a missing rate: the cost and the gain say no rate, the price and value do not', async () => {
        const missing = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-01-30', reason: 'rate_unavailable' as const };
        serve({
            trades: [TRADE],
            detail: {
                totalCostBasis: null, costBasisRateUnavailable: [missing],
                unrealizedGain: null, unrealizedGainRateUnavailable: [missing],
            },
        });
        renderPage();

        await waitFor(() => expect(within(rowOf('Brokerage A')).getByText(formatCurrency(-100, 'USD'))).toBeInTheDocument());
        expect(stat('Latest price')).toHaveTextContent(formatPrice(12, 'USD'));
        expect(stat('Market value')).toHaveTextContent(formatCurrency(120, 'USD'));
        const title = 'No EUR to USD rate for Jan 30, 2026.';
        expect(stat('Cost basis')).toHaveTextContent('no rate');
        expect(within(stat('Cost basis') as HTMLElement).getByTitle(title)).toBeInTheDocument();
        expect(screen.queryByText(/^avg /)).toBeNull();
        expect(stat('Unrealized')).toHaveTextContent('no rate');
        expect(within(stat('Unrealized') as HTMLElement).getByTitle(title)).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });

    it('a market value that needs a missing rate: the value and the gain say no rate, the price and cost do not', async () => {
        const missing = { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-01-30', reason: 'rate_unavailable' as const };
        serve({
            trades: [TRADE],
            detail: {
                priceCurrencyCode: 'GBP',
                marketValue: null, marketValueRateUnavailable: [missing],
                unrealizedGain: null, unrealizedGainRateUnavailable: [missing],
            },
        });
        renderPage();

        await waitFor(() => expect(within(rowOf('Brokerage A')).getByText(formatCurrency(-100, 'USD'))).toBeInTheDocument());
        expect(stat('Latest price')).toHaveTextContent(formatPrice(12, 'GBP'));
        expect(stat('Cost basis')).toHaveTextContent(formatCurrency(100, 'USD'));
        const title = 'No GBP to USD rate for Jan 30, 2026.';
        expect(stat('Market value')).toHaveTextContent('no rate');
        expect(within(stat('Market value') as HTMLElement).getByTitle(title)).toBeInTheDocument();
        expect(stat('Unrealized')).toHaveTextContent('no rate');
        expect(within(stat('Unrealized') as HTMLElement).getByTitle(title)).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });

    it('no current price: the value and the gain are a dash, with no rate to name', async () => {
        serve({
            trades: [TRADE],
            detail: { latestPrice: null, latestPriceAsOf: null, marketValue: null, unrealizedGain: null },
        });
        renderPage();

        await waitFor(() => expect(within(rowOf('Brokerage A')).getByText(formatCurrency(-100, 'USD'))).toBeInTheDocument());
        expect(stat('Cost basis')).toHaveTextContent(formatCurrency(100, 'USD'));
        expect(stat('Market value')).toHaveTextContent('—');
        expect(stat('Unrealized')).toHaveTextContent('—');
        expect(screen.queryByText('no rate')).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('each trade is in the currency it carries, with no accounts read and no dash while one loads', async () => {
        // Two brokerages in two currencies, a closed one among them: the trades read says
        // which each row is in, so nothing waits on the account list.
        const OTHER = '00000000-0000-0000-0000-0000000000b2';
        serve({
            priceCurrency: 'EUR',
            trades: [
                { ...TRADE, currencyCode: 'EUR' },
                {
                    ...TRADE, headerId: '00000000-0000-0000-0000-0000000000e2', accountId: OTHER,
                    accountName: 'Brokerage B', amount: -50, unitPrice: 5, currencyCode: 'GBP',
                },
            ],
            detail: { priceCurrencyCode: 'EUR', holdingsCurrencyCode: 'EUR', ...inReporting('EUR') },
        });
        renderPage();

        // Anchored on a trade's own row, which needs the trades read to have landed.
        await waitFor(() => expect(within(rowOf('Brokerage A')).getByText(formatCurrency(-100, 'EUR'))).toBeInTheDocument());
        expect(within(rowOf('Brokerage A')).getByText(formatPrice(10, 'EUR'))).toBeInTheDocument();
        const other = rowOf('Brokerage B');
        expect(within(other).getByText(formatCurrency(-50, 'GBP'))).toBeInTheDocument();
        expect(within(other).getByText(formatPrice(5, 'GBP'))).toBeInTheDocument();
        expect(within(other).getByText(formatQuantity(10))).toBeInTheDocument();
        expect(within(other).queryByText('—')).toBeNull();
        expect(screen.queryByText(/\$/)).toBeNull();
        // The page asked for no account list: one it asked for would be unrouted here.
        expect(stub.unrouted).toEqual([]);
    });
});

// The prices (the latest one, the average cost per share, a trade's unit price) print
// through formatPrice and keep digits finer than the currency's places; the money (cost
// basis, market value, gain, a trade's amount) prints through formatCurrency at the
// currency's places, the gain's sign read off the figure as it prints (ADR-0114 D15,
// ADR-0023 §H). Every figure is invented.
describe('SecurityDetailPage: prices and money at their own places', () => {
    /** A hero figure's value, by its label. */
    const stat = (label: string) => screen.getByText(label).nextElementSibling!;
    /** The trade row's cells: date, account, action, quantity, unit price, amount. */
    const tradeCells = async () =>
        within(await waitFor(() => screen.getByText('Brokerage A').closest('tr')!)).getAllByRole('cell');

    it('prints the money at the currency\'s places, and the average cost and the unit price with their finer digits', async () => {
        serve({
            trades: [{ ...TRADE, amount: -100.1234, unitPrice: 10.01234 }],
            detail: { totalQuantity: 10, totalCostBasis: 100.1234, marketValue: 120.5678, unrealizedGain: 20.4444 },
        });
        renderPage();

        const cells = await tradeCells();
        expect(cells[5]).toHaveTextContent(/^-\$100\.12$/);
        expect(cells[4]).toHaveTextContent(/^\$10\.01234$/);
        expect(stat('Cost basis')).toHaveTextContent(/^\$100\.12$/);
        expect(stat('Market value')).toHaveTextContent(/^\$120\.57$/);
        expect(stat('Unrealized')).toHaveTextContent(/^\$20\.44$/);
        expect(screen.getByText('avg $10.01234/sh')).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });

    for (const residue of [-0.004, 0.004]) {
        it(`prints a gain smaller than one minor unit (${residue < 0 ? 'a loss' : 'a gain'}) as a plain zero, never red`, async () => {
            serve({ trades: [TRADE], detail: { unrealizedGain: residue } });
            renderPage();

            await tradeCells();
            const gain = stat('Unrealized');
            expect(gain).toHaveTextContent(/^\$0\.00$/);
            expect(gain).not.toHaveClass('text-state-danger');
            expect(stub.unrouted).toEqual([]);
        });
    }

    it('prints a three-place holding\'s money at three places, and its prices at no fewer', async () => {
        serve({
            priceCurrency: 'KWD',
            trades: [{ ...TRADE, amount: -800.1256, unitPrice: 80, currencyCode: 'KWD' }],
            detail: {
                priceCurrencyCode: 'KWD', holdingsCurrencyCode: 'KWD', ...inReporting('KWD'),
                latestPrice: 100, totalQuantity: 10, totalCostBasis: 800.1256,
                marketValue: 1000.0004, unrealizedGain: 199.8748,
            },
        });
        renderPage();

        const cells = await tradeCells();
        expect(cells[5]!.textContent).toMatch(/^-KWD\s800\.126$/);
        expect(cells[4]!.textContent).toMatch(/^KWD\s80\.000$/);
        expect(stat('Latest price').textContent).toMatch(/^KWD\s100\.000$/);
        expect(stat('Cost basis').textContent).toMatch(/^KWD\s800\.126$/);
        expect(stat('Market value').textContent).toMatch(/^KWD\s1,000\.000$/);
        expect(stat('Unrealized').textContent).toMatch(/^KWD\s199\.875$/);
        expect(screen.getByText(/^avg KWD\s80\.01256\/sh$/)).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('SecurityDetailPage: by role (ADR-0023 §L)', () => {
    const asRole = (role: string) => {
        serve({ detail: {} });
        stub.on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger A', role }]);
    };
    /** The prices table once its row and the reader's role have both arrived. */
    const settledPrices = async () => {
        await screen.findByText('Ledger A');
        const cell = await screen.findByText(formatPrice(12, 'USD'), { selector: 'td' });
        return cell.closest('tr') as HTMLElement;
    };

    it('gives a viewer the security and its prices, and no edit of either', async () => {
        asRole('viewer');
        renderPage();
        const row = await settledPrices();

        expect(within(row).queryAllByRole('button')).toEqual([]);
        expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
        expect(screen.queryByRole('button', { name: '+ Add price' })).toBeNull();
        await userEvent.setup().click(row);
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it.each(['owner', 'editor'])('keeps edit, add price and each price\'s edit for an %s', async (role) => {
        asRole(role);
        renderPage();
        const row = await settledPrices();

        expect(within(row).getByRole('button', { name: /^Edit price for / })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: '+ Add price' })).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('SecurityDetailPage: a new price\'s day', () => {
    const zone = process.env.TZ;
    afterEach(() => {
        process.env.TZ = zone;
        vi.useRealTimers();
    });

    it('starts on the reader\'s own today, not the UTC one', async () => {
        process.env.TZ = 'America/Los_Angeles';
        vi.useFakeTimers({ toFake: ['Date'] });
        // Late on the 11th on the coast; already the 12th in UTC.
        vi.setSystemTime(new Date('2026-03-12T05:00:00Z'));
        serve();
        renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: '+ Add price' }));
        const dialog = await screen.findByRole('dialog', { name: 'Add price' });
        expect(within(dialog).getByLabelText('Date *')).toHaveValue('2026-03-11');
    });
});

// ADR-0114 D5: a price names its currency. An added one starts on the security's
// default, an edited one on its own; with no default the field starts empty, and
// nothing defaults to USD.
// Price, High and Low show the symbol of the dialog's own Currency field, live as it
// is typed, and none while the code is partial or invalid; a price is not money, so it
// takes any step and is never rounded (ADR-0114 D5, D14, D15). Volume is a count.
describe('SecurityDetailPage: the price dialog\'s symbol', () => {
    async function openBlank() {
        serve();
        stub.on('GET', PRICES, { items: [], cursorForOlder: null, totalCount: 0, defaultCurrencyCode: null });
        renderPage();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: '+ Add price' }));
        const dialog = within(await screen.findByRole('dialog', { name: 'Add price' }));
        return { user, dialog };
    }
    const affixOf = (input: HTMLElement) =>
        input.parentElement!.querySelector('[data-money-affix]')?.textContent ?? null;
    const PRICED = ['Price *', 'High', 'Low'];

    it('shows none while the code is partial', async () => {
        const { user, dialog } = await openBlank();
        await user.type(dialog.getByLabelText('Currency *'), 'U');

        for (const name of PRICED) expect([name, affixOf(dialog.getByLabelText(name))]).toEqual([name, null]);
    });

    it('follows the code as it is typed and changed', async () => {
        const { user, dialog } = await openBlank();
        const currency = dialog.getByLabelText('Currency *');
        await user.type(currency, 'usd');
        for (const name of PRICED) expect([name, affixOf(dialog.getByLabelText(name))]).toEqual([name, currencyAffix('USD')!.symbol]);

        await user.clear(currency);
        await user.type(currency, 'EUR');
        for (const name of PRICED) expect([name, affixOf(dialog.getByLabelText(name))]).toEqual([name, currencyAffix('EUR')!.symbol]);
    });

    it('keeps a six-place price on blur, at any step', async () => {
        const { user, dialog } = await openBlank();
        await user.type(dialog.getByLabelText('Currency *'), 'JPY');
        const price = dialog.getByLabelText('Price *') as HTMLInputElement;
        expect(price).toHaveAttribute('step', 'any');
        await user.type(price, '12.345678');
        await user.tab();
        expect(price.value).toBe('12.345678');
    });

    it('gives Volume no symbol', async () => {
        const { user, dialog } = await openBlank();
        await user.type(dialog.getByLabelText('Currency *'), 'USD');
        expect(affixOf(dialog.getByLabelText('Volume'))).toBeNull();
    });
});

describe("SecurityDetailPage: a price's currency", () => {
    async function openAdd() {
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: '+ Add price' }));
        const dialog = within(await screen.findByRole('dialog', { name: 'Add price' }));
        return { user, dialog };
    }

    it("starts an added price on the security's default currency and sends it", async () => {
        serve({ priceCurrency: 'CAD' });
        renderPage();
        const { user, dialog } = await openAdd();
        expect(dialog.getByLabelText('Currency *')).toHaveValue('CAD');
        await user.type(dialog.getByLabelText('Price *'), '13');
        await user.click(dialog.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(stub.callsTo('POST', PRICES)).toHaveLength(1));
        expect(stub.callsTo('POST', PRICES)[0]!.body).toEqual(expect.objectContaining({ price: 13, currencyCode: 'CAD' }));
    });

    it('takes the default currency when the prices page arrives after the dialog opened', async () => {
        serve({ priceCurrency: 'CAD' });
        // Hold the prices read until the dialog is open.
        const answered = globalThis.fetch;
        let release!: () => void;
        const gate = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            const path = new URL(input instanceof Request ? input.url : String(input), 'http://localhost').pathname;
            if (PRICES.test(path) && (init?.method ?? 'GET') === 'GET') await gate;
            return answered(input, init);
        });
        renderPage();
        const { dialog } = await openAdd();
        const currency = dialog.getByLabelText('Currency *');
        expect(currency).toHaveValue('');
        expect(dialog.queryByText(/Nothing settles this security's currency yet/)).toBeNull();

        release();

        await waitFor(() => expect(currency).toHaveValue('CAD'));
        expect(dialog.queryByText(/Nothing settles this security's currency yet/)).toBeNull();
    });

    it('starts empty with no default, says why, and sends the code typed in capitals', async () => {
        serve();
        stub.on('GET', PRICES, { items: [], cursorForOlder: null, totalCount: 0, defaultCurrencyCode: null });
        renderPage();
        const { user, dialog } = await openAdd();
        const currency = dialog.getByLabelText('Currency *');
        expect(currency).toHaveValue('');
        expect(dialog.getByText(/Nothing settles this security's currency yet/)).toBeInTheDocument();
        await user.type(dialog.getByLabelText('Price *'), '13');
        expect(dialog.getByRole('button', { name: 'Save' })).toBeDisabled();
        await user.type(currency, 'eur');
        await user.click(dialog.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(stub.callsTo('POST', PRICES)).toHaveLength(1));
        expect(stub.callsTo('POST', PRICES)[0]!.body).toEqual(expect.objectContaining({ currencyCode: 'EUR' }));
    });

    it.each([
        ['security-price-currency-required', 'Name the currency this price is quoted in: the security has no price yet, and the accounts holding it do not share a currency to take.'],
        ['security-price-currency-invalid', 'The currency must be a 3-letter code, such as USD or EUR.'],
    ] as const)('says %s in words and marks the field', async (code, words) => {
        serve();
        stub.on('POST', PRICES, () => problem(422, 'server detail', code));
        renderPage();
        const { user, dialog } = await openAdd();
        await user.type(dialog.getByLabelText('Price *'), '13');
        await user.click(dialog.getByRole('button', { name: 'Save' }));
        expect(await dialog.findByRole('alert')).toHaveTextContent(words);
        expect(dialog.getByLabelText('Currency *')).toHaveAttribute('aria-invalid', 'true');
    });

    it('says an edited price\'s security-price-currency-invalid in words and marks the field', async () => {
        serve();
        stub.on('PATCH', ONE_PRICE, () => problem(422, 'server detail', 'security-price-currency-invalid'));
        renderPage();
        const user = userEvent.setup();
        await user.click(await screen.findByText('edit →'));
        const dialog = within(await screen.findByRole('dialog', { name: 'Edit price' }));
        await user.clear(dialog.getByLabelText('Currency *'));
        await user.type(dialog.getByLabelText('Currency *'), 'EURO');
        await user.click(dialog.getByRole('button', { name: 'Save' }));
        expect(await dialog.findByRole('alert')).toHaveTextContent('The currency must be a 3-letter code, such as USD or EUR.');
        expect(dialog.queryByText('server detail')).toBeNull();
        expect(dialog.getByLabelText('Currency *')).toHaveAttribute('aria-invalid', 'true');
    });

    it('starts an edited price on its own currency and sends one only when it changes', async () => {
        serve({ priceCurrency: 'CAD' });
        renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByText('edit →'));
        let dialog = within(await screen.findByRole('dialog', { name: 'Edit price' }));
        expect(dialog.getByLabelText('Currency *')).toHaveValue('CAD');
        await user.click(dialog.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(stub.callsTo('PATCH', ONE_PRICE)).toHaveLength(1));
        expect(stub.callsTo('PATCH', ONE_PRICE)[0]!.body).not.toHaveProperty('currencyCode');
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

        await user.click(await screen.findByText('edit →'));
        dialog = within(await screen.findByRole('dialog', { name: 'Edit price' }));
        await user.clear(dialog.getByLabelText('Currency *'));
        await user.type(dialog.getByLabelText('Currency *'), 'USD');
        await user.click(dialog.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(stub.callsTo('PATCH', ONE_PRICE)).toHaveLength(2));
        expect(stub.callsTo('PATCH', ONE_PRICE)[1]!.body).toEqual(expect.objectContaining({ currencyCode: 'USD' }));
    });
});
