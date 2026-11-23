import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import type { RateUnavailable } from '@/lib/types';

import { ConversionNote, ExchangeRatesLink, MissingRateNote } from './ConversionNote';

// The ledger-level conversion note and the missing-rate line (ADR-0114 D7, D9).
// Synthetic currencies and days only.

const LEDGER = 'ledger-1';
const MISSING: RateUnavailable[] = [{ fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-11-10', reason: 'rate_unavailable' }];
const link = <a href="#rates">Exchange rates →</a>;

describe('ConversionNote', () => {
    it('says the totals are in the reporting currency and which accounts convert, then the link', () => {
        render(<ConversionNote reportingCurrency="USD" currencies={['USD', 'CAD']} ratesLink={link} />);
        const note = screen.getByText(/^Totals are in USD/);
        expect(note).toHaveTextContent(
            'Totals are in USD, with CAD accounts converted at the rate for each figure\'s date (the latest rate from today on). An account\'s own figures stay in its currency. Exchange rates →',
        );
        expect(note).toHaveClass('text-xs', 'text-text-muted');
    });

    it('draws nothing on a ledger whose accounts are all in the reporting currency', () => {
        const { container } = render(<ConversionNote reportingCurrency="USD" currencies={['USD']} ratesLink={link} />);
        expect(container).toBeEmptyDOMElement();
    });
});

describe('MissingRateNote', () => {
    it('names each missing pair and day, then what the caller adds, then the link', () => {
        render(<MissingRateNote missing={MISSING} ratesLink={link}>Rates are fetched through Nov 6.</MissingRateNote>);
        expect(screen.getByText(/^No CAD to USD/)).toHaveTextContent(
            'No CAD to USD rate for Nov 10, 2026. Rates are fetched through Nov 6. Exchange rates →',
        );
    });

    it('draws nothing when nothing is missing', () => {
        const { container } = render(<MissingRateNote missing={[]} ratesLink={link} />);
        expect(container).toBeEmptyDOMElement();
    });
});

describe('ExchangeRatesLink', () => {
    it('opens the ledger\'s Exchange rates tab', async () => {
        const root = createRootRoute();
        const router = createRouter({
            routeTree: root.addChildren([
                createRoute({ getParentRoute: () => root, path: '/here', component: () => <ExchangeRatesLink ledgerId={LEDGER} /> }),
                createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/settings', component: () => <main>settings</main> }),
            ]),
            history: createMemoryHistory({ initialEntries: ['/here'] }),
        });
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        render(<RouterProvider router={router as any} />);

        const a = await screen.findByRole('link', { name: 'Exchange rates →' });
        expect(a).toHaveAttribute('href', `/ledgers/${LEDGER}/settings?tab=rates`);
    });
});
