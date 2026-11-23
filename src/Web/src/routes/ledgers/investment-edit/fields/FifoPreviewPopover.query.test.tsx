import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import * as apiModule from '@/lib/api';
import type { InvestmentLotDto } from '@/lib/types';
import { FifoPreviewPopover } from './FifoPreviewPopover';
import { formatCurrency } from '@/lib/money';

/**
 * The preview measures a sale against the position on its own date, without
 * itself (FU-097). Asking for today's lots would make editing an old sale on a
 * since-closed account show "No open lots" and warn on every edit.
 */
const lot: InvestmentLotDto = { lotId: 'leg-1', acquiredAt: '2014-01-10T00:00:00Z', quantity: 10, unitCost: 10 };

function renderPopover(sharesInput: number, currency: string | null = 'USD') {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <FifoPreviewPopover
                action="sellx"
                brokerageAccountId="brokerage-1"
                ledgerId="ledger-1"
                securityId="security-1"
                sharesInput={sharesInput}
                postedAt="2014-02-01"
                excludeHeaderId="header-1"
                currency={currency}
            />
        </QueryClientProvider>,
    );
}

describe('FifoPreviewPopover', () => {
    afterEach(() => vi.restoreAllMocks());

    it("asks for the lots open on the sale's date, without the sale being edited", async () => {
        const fetch = vi.spyOn(apiModule, 'fetchOpenLots').mockResolvedValue([lot]);

        renderPopover(8);

        expect(await screen.findByText('Will close 1 lot')).toBeTruthy();
        // The instant the save sends for that date (UTC midnight), and the row's own id.
        expect(fetch).toHaveBeenCalledWith('ledger-1', 'brokerage-1', 'security-1', {
            asOf: '2014-02-01T00:00:00.000Z',
            excludeHeaderId: 'header-1',
        });
        // Anchored on the plan above, so this is the settled frame, not the loading one.
        expect(screen.queryByText(/Exceeds the position held on this date/)).toBeNull();
    });

    it('warns, before Save, that a sale larger than that position will be refused', async () => {
        vi.spyOn(apiModule, 'fetchOpenLots').mockResolvedValue([lot]);

        renderPopover(12);

        expect(await screen.findByText(/Exceeds the position held on this date by 2/)).toBeTruthy();
        expect(screen.getByText(/Save will be refused/)).toBeTruthy();
    });

    it("prints each lot's cost and the total basis in the brokerage's currency", async () => {
        vi.spyOn(apiModule, 'fetchOpenLots').mockResolvedValue([lot]);

        renderPopover(8, 'EUR');

        expect(await screen.findByText('Will close 1 lot')).toBeTruthy();
        expect(screen.getByText(/8\/10 sh @/)).toHaveTextContent(formatCurrency(10, 'EUR'));
        expect(screen.getByText(formatCurrency(80, 'EUR'), { selector: '.font-semibold' })).toBeTruthy();
        expect(screen.queryByText(/\$/)).toBeNull();
    });

    it('prints no cost while the brokerage currency is not known', async () => {
        vi.spyOn(apiModule, 'fetchOpenLots').mockResolvedValue([lot]);

        renderPopover(8, null);

        expect(await screen.findByText('Will close 1 lot')).toBeTruthy();
        expect(screen.getByText('Total basis closed')).toBeTruthy();
        expect(screen.queryByText(/[$€£]/)).toBeNull();
        expect(screen.queryByText(/80\.00/)).toBeNull();
    });
});
