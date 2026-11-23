import type { ReactNode } from 'react';
import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { PortfolioBar, HoldingsTable } from './HoldingsPanel';
import * as apiModule from '@/lib/api';
import { stubFetch } from '@/lib/annotations/fetchStub';
import type { HoldingsViewDto } from '@/lib/types';

// Portfolio View (fix/register-lookfeel + the view-tab split):
//   * PortfolioBar — a one-line summary + Activity / Holdings view switch
//     carrying the positions count; no dedicated price-refresh chrome (the
//     breadcrumb "Sync this account" ↻ already refreshes prices);
//   * HoldingsTable — the per-security table, gray header / white rows
//     (ADR-0045), with an empty state when there are no positions.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000200';
const SECURITY_ID = '00000000-0000-0000-0000-0000000000s1';

const HOLDINGS: HoldingsViewDto = {
    accountId: ACCOUNT_ID,
    accountName: 'Brokerage',
    currencyCode: 'USD',
    summary: {
        portfolioValue: 1000,
        costBasis: 800,
        unrealizedGain: 200,
        percentChange: 25,
        cashBalance: 50,
        total: 1050,
    },
    positions: [
        {
            securityId: SECURITY_ID,
            ticker: 'ETFA',
            name: 'Index ETF A',
            assetClass: null,
            quantity: 10,
            costBasis: 800,
            costPerShare: 80,
            currentPrice: 100,
            priceAsOf: '2026-05-01',
            currentValue: 1000,
            unrealizedGain: 200,
            percentChange: 25,
        },
    ],
};

function withClient(node: ReactNode) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    return render(
        <QueryClientProvider client={queryClient}>{node}</QueryClientProvider>,
    );
}

describe('PortfolioBar', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue(HOLDINGS);
    });

    it('renders a one-line summary + a holdings link carrying the count', async () => {
        withClient(
            <PortfolioBar
                ledgerId={LEDGER_ID}
                accountId={ACCOUNT_ID}
                view="activity"
                onViewChange={vi.fn()}
            />,
        );
        // Compact summary stats render once the query resolves.
        expect(await screen.findByText('Total')).toBeInTheDocument();
        expect(screen.getByText('Portfolio')).toBeInTheDocument();
        expect(screen.getByText('Cash')).toBeInTheDocument();
        expect(screen.getByText('Unrealized')).toBeInTheDocument();
        // The view toggle (a quiet link) carries the positions count.
        expect(
            screen.getByRole('button', { name: /holdings \(1\)/i }),
        ).toBeInTheDocument();
        // No dedicated price-refresh affordance — the breadcrumb sync ↻
        // is the price-refresh path.
        expect(
            screen.queryByRole('button', { name: /refresh prices/i }),
        ).not.toBeInTheDocument();
    });

    it('the holdings link calls onViewChange', async () => {
        const onViewChange = vi.fn();
        withClient(
            <PortfolioBar
                ledgerId={LEDGER_ID}
                accountId={ACCOUNT_ID}
                view="activity"
                onViewChange={onViewChange}
            />,
        );
        const holdingsLink = await screen.findByRole('button', {
            name: /holdings/i,
        });
        await userEvent.setup().click(holdingsLink);
        expect(onViewChange).toHaveBeenCalledWith('holdings');
    });

    it('shows a back-to-activity link when on the holdings view', async () => {
        const onViewChange = vi.fn();
        withClient(
            <PortfolioBar
                ledgerId={LEDGER_ID}
                accountId={ACCOUNT_ID}
                view="holdings"
                onViewChange={onViewChange}
            />,
        );
        const activityLink = await screen.findByRole('button', {
            name: /activity/i,
        });
        await userEvent.setup().click(activityLink);
        expect(onViewChange).toHaveBeenCalledWith('activity');
    });
});

describe('HoldingsTable', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue(HOLDINGS);
    });

    it('positions table reads gray header band / white rows (ADR-0045)', async () => {
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);
        const securityHeader = await screen.findByText('Security');
        const headerRow = securityHeader.closest('tr');
        expect(headerRow).not.toBeNull();
        expect(headerRow!.className).toContain('bg-surface-header');

        const tickerCell = screen.getByText('ETFA');
        const dataRow = tickerCell.closest('tr');
        expect(dataRow).not.toBeNull();
        expect(dataRow!.className).toContain('bg-surface');
        expect(dataRow!.className).not.toContain('bg-surface-header');
    });

    it('shows a gain in default text with no plus, never green (ADR-0023 §H)', async () => {
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);
        const percent = await screen.findByText('25.00%');
        expect(percent.className).toContain('text-text');
        expect(percent.className).not.toContain('state-success');
        expect(screen.queryByText('+25.00%')).toBeNull();
    });

    it('shows a loss in red', async () => {
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue({
            ...HOLDINGS,
            positions: [{ ...HOLDINGS.positions[0]!, unrealizedGain: -50, percentChange: -5 }],
        });
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);
        expect((await screen.findByText('-5.00%')).className).toContain('text-state-danger');
    });

    it('renders an empty state when there are no positions', async () => {
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue({
            ...HOLDINGS,
            positions: [],
        });
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);
        expect(await screen.findByText(/no holdings yet/i)).toBeInTheDocument();
    });
});

// A position priced in another currency, and a missing rate (ADR-0114 D5, D7, D9),
// with the server mocked at fetch in the wire shape. Every figure is invented.
describe('a position priced in another currency', () => {
    const GBP_USD = { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-05-01', reason: 'rate_unavailable' as const };
    const FOREIGN = {
        securityId: '00000000-0000-0000-0000-0000000000s2',
        ticker: 'LSEF',
        name: 'London Fund',
        assetClass: null,
        quantity: 10,
        costBasis: 1200,
        costPerShare: 120,
        currentPrice: 100,
        priceAsOf: '2026-05-01',
        currentValue: 1000,
        priceCurrencyCode: 'GBP',
        accountValue: 1250,
        unrealizedGain: 50,
        percentChange: 4.17,
    };

    function stubHoldings(view: HoldingsViewDto) {
        vi.restoreAllMocks();
        return stubFetch().on('GET', /\/accounts\/[^/]+\/holdings$/, view);
    }

    afterEach(() => {
        vi.unstubAllGlobals();
    });

    it('prints the price in its own currency and the value in the account’s, the price-currency value beneath', async () => {
        stubHoldings({ ...HOLDINGS, positions: [FOREIGN] });
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);

        const row = (await screen.findByText('LSEF')).closest('tr')!;
        const cells = within(row).getAllByRole('cell');
        expect(cells[2]).toHaveTextContent('£100.00');
        // Cost is the account's, as its trades are.
        expect(cells[4]).toHaveTextContent('$1,200.00');
        const value = cells[5]!;
        expect(value.querySelector('[data-position-value="account"]')).toHaveTextContent('$1,250.00');
        expect(value.querySelector('[data-position-value="price"]')).toHaveTextContent('£1,000.00');
        // The gain compares the account-currency value with the cost.
        expect(cells[6]).toHaveTextContent('$50.00');
    });

    it('keeps a position in the account’s currency on one line', async () => {
        stubHoldings(HOLDINGS);
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);

        const row = (await screen.findByText('ETFA')).closest('tr')!;
        const value = within(row).getAllByRole('cell')[5]!;
        expect(value).toHaveTextContent(/^\$1,000\.00$/);
        expect(value.querySelector('[data-position-value="price"]')).toBeNull();
    });

    it('reads "no rate", naming the pair, where the value and gain need a missing rate', async () => {
        stubHoldings({
            ...HOLDINGS,
            positions: [{ ...FOREIGN, accountValue: null, unrealizedGain: null, percentChange: null, rateUnavailable: [GBP_USD] }],
        });
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);

        const row = (await screen.findByText('LSEF')).closest('tr')!;
        const marks = within(row).getAllByText('no rate');
        // Value, gain and % change.
        expect(marks).toHaveLength(3);
        for (const m of marks) expect(m).toHaveAttribute('title', 'No GBP to USD rate for May 1, 2026.');
        // The price and its own-currency value still print.
        expect(within(row).getAllByRole('cell')[2]).toHaveTextContent('£100.00');
        expect(row.querySelector('[data-position-value="price"]')).toHaveTextContent('£1,000.00');
    });

    it('dashes a position with no price at all, which needs no rate', async () => {
        stubHoldings({
            ...HOLDINGS,
            positions: [{ ...HOLDINGS.positions[0]!, currentPrice: null, currentValue: null, unrealizedGain: null, percentChange: null }],
        });
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);

        const row = (await screen.findByText('ETFA')).closest('tr')!;
        expect(within(row).getAllByRole('cell')[5]).toHaveTextContent('–');
        expect(within(row).queryByText('no rate')).toBeNull();
    });

    it('marks each summary figure that needs a missing rate, and prints the rest', async () => {
        stubHoldings({
            ...HOLDINGS,
            summary: {
                portfolioValue: null, costBasis: 2000, unrealizedGain: null, percentChange: null,
                cashBalance: 50, total: null, rateUnavailable: [GBP_USD],
            },
            positions: [FOREIGN],
        });
        withClient(<PortfolioBar ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} view="activity" onViewChange={vi.fn()} />);

        const stat = async (label: string) => (await screen.findByText(label)).parentElement!;
        expect(await stat('Cash')).toHaveTextContent('$50.00');
        for (const label of ['Total', 'Portfolio', 'Unrealized']) {
            const mark = within(await stat(label)).getByText('no rate');
            expect(mark).toHaveAttribute('title', 'No GBP to USD rate for May 1, 2026.');
        }
        expect(within(await stat('Cash')).queryByText('no rate')).toBeNull();
    });
});

// The prices (price, cost per share) print through formatPrice, so a price finer than
// the currency's places keeps its digits, and the money through formatCurrency at the
// currency's places, the gain's sign read off the figure as it prints (ADR-0114 D15,
// ADR-0023 §H). Against the wire; every figure is invented.
describe('prices and money at their own places', () => {
    const POSITION = HOLDINGS.positions[0]!;

    function stubHoldings(view: HoldingsViewDto) {
        vi.restoreAllMocks();
        return stubFetch().on('GET', /\/accounts\/[^/]+\/holdings$/, view);
    }

    async function cellsOf(view: HoldingsViewDto) {
        const stub = stubHoldings(view);
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);
        const row = (await screen.findByText('ETFA')).closest('tr')!;
        expect(stub.unrouted).toEqual([]);
        return within(row).getAllByRole('cell');
    }

    afterEach(() => {
        vi.unstubAllGlobals();
    });

    it('prints a four-place price and cost per share with all four places', async () => {
        const cells = await cellsOf({ ...HOLDINGS, positions: [{ ...POSITION, currentPrice: 100.1234, costPerShare: 80.5678 }] });

        expect(cells[2]).toHaveTextContent(/^\$100\.1234$/);
        expect(cells[3]).toHaveTextContent(/^\$80\.5678$/);
    });

    it('prints a three-place holding\'s money at three places, and its price at no fewer', async () => {
        const cells = await cellsOf({
            ...HOLDINGS,
            currencyCode: 'KWD',
            positions: [{
                ...POSITION, costBasis: 800.1256, costPerShare: 80, currentPrice: 100,
                currentValue: 1000.0004, unrealizedGain: 199.8748,
            }],
        });

        expect(cells[2]!.textContent).toMatch(/^KWD\s100\.000$/);
        expect(cells[3]!.textContent).toMatch(/^KWD\s80\.000$/);
        expect(cells[4]!.textContent).toMatch(/^KWD\s800\.126$/);
        expect(cells[5]!.textContent).toMatch(/^KWD\s1,000\.000$/);
        expect(cells[6]!.textContent).toMatch(/^KWD\s199\.875$/);
    });

    for (const residue of [-0.004, 0.004]) {
        it(`prints a gain smaller than one minor unit (${residue < 0 ? 'a loss' : 'a gain'}) as a plain zero, never red`, async () => {
            const cells = await cellsOf({
                ...HOLDINGS,
                positions: [{ ...POSITION, unrealizedGain: residue, percentChange: residue / 10 }],
            });

            for (const cell of [cells[6]!, cells[7]!]) {
                expect(cell.textContent).not.toContain('-');
                expect(cell.className).not.toContain('text-state-danger');
            }
            expect(cells[6]).toHaveTextContent(/^\$0\.00$/);
            expect(cells[7]).toHaveTextContent(/^0\.00%$/);
        });
    }

    it('keeps a percent that prints as a loss red when the gain beside it prints as a zero', async () => {
        const cells = await cellsOf({
            ...HOLDINGS,
            positions: [{ ...POSITION, unrealizedGain: -0.004, percentChange: -0.008 }],
        });

        expect(cells[6]).toHaveTextContent(/^\$0\.00$/);
        expect(cells[6]!.className).not.toContain('text-state-danger');
        expect(cells[7]).toHaveTextContent(/^-0\.01%$/);
        expect(cells[7]!.className).toContain('text-state-danger');
    });

    it('reds the summary\'s gain when its percent prints as a loss and the gain as a zero', async () => {
        stubHoldings({ ...HOLDINGS, summary: { ...HOLDINGS.summary, unrealizedGain: -0.004, percentChange: -0.008 } });
        withClient(<PortfolioBar ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} view="activity" onViewChange={vi.fn()} />);

        const stat = (await screen.findByText('Unrealized')).parentElement!;
        const figure = within(stat).getByText(/\$0\.00/);
        expect(figure).toHaveTextContent(/^\$0\.00 \(-0\.01%\)$/);
        expect(figure.className).toContain('text-state-danger');
    });

    it('prints the summary\'s gain smaller than one minor unit as a plain zero, never red', async () => {
        stubHoldings({ ...HOLDINGS, summary: { ...HOLDINGS.summary, unrealizedGain: -0.004, percentChange: -0.0005 } });
        withClient(<PortfolioBar ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} view="activity" onViewChange={vi.fn()} />);

        const stat = (await screen.findByText('Unrealized')).parentElement!;
        const figure = within(stat).getByText(/\$0\.00/);
        expect(figure).toHaveTextContent(/^\$0\.00 \(0\.00%\)$/);
        expect(figure.className).not.toContain('text-state-danger');
    });
});
