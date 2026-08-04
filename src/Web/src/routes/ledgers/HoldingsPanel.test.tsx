import type { ReactNode } from 'react';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { PortfolioBar, HoldingsTable } from './HoldingsPanel';
import * as apiModule from '@/lib/api';
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

    it('renders an empty state when there are no positions', async () => {
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue({
            ...HOLDINGS,
            positions: [],
        });
        withClient(<HoldingsTable ledgerId={LEDGER_ID} accountId={ACCOUNT_ID} />);
        expect(await screen.findByText(/no holdings yet/i)).toBeInTheDocument();
    });
});
