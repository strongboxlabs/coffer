import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { NetWorthHistoryPoint } from '@/lib/types';

import { fxPair, homeStub, missingRate, overview, renderHome } from '../homeTestFixtures';

// The Net worth widget through the real page, API client and router, with the server
// mocked at fetch, when a rate a figure needs is missing (ADR-0114 D9; the
// converted-totals board CT-3). Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const TODAY_MISSING = missingRate('2026-03-12');

const point = (asOf: string, netWorth: number | null, over: Partial<NetWorthHistoryPoint> = {}): NetWorthHistoryPoint => ({
    asOf: `${asOf}T23:59:59.999999Z`, netWorth, unpricedSecurityCount: 0, noHistory: false,
    rateUnavailable: netWorth === null ? [TODAY_MISSING] : [], ...over,
});

/** Four month-ends with figures, then today with or without one. */
const history = (today: number | null, over: Partial<NetWorthHistoryPoint> = {}) => ({
    fromUtc: '', toUtc: '', interval: 'month', currencyCode: 'USD',
    points: [
        point('2025-11-30', 700), point('2025-12-31', 800), point('2026-01-31', 850), point('2026-02-28', 900),
        point('2026-03-12', today, over),
    ],
});

const panel = () => screen.findByRole('heading', { level: 2, name: 'Net worth' })
    .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

describe('Net worth: a rate is missing', () => {
    it('names the pair, the day and how far rates are fetched under the headline, with a link to the rates', async () => {
        const stub = homeStub({
            overview: overview({
                netWorth: null, totalAssets: null, rateUnavailable: [TODAY_MISSING],
                comparison: { since: '2026-02-28', netWorth: { then: 900, change: null }, noHistory: false, rateUnavailable: [] },
            }),
            fxRates: { reportingCurrency: 'USD', pairs: [fxPair({ fetchedThroughDate: '2026-03-06' })] },
        });
        stub.on('GET', /\/reports\/net-worth-history$/, history(null));
        renderHome();

        const widget = await panel();
        const line = await within(widget).findByText(/^No CAD to USD rate for Mar 12, 2026\./);
        await waitFor(() => expect(line).toHaveTextContent(
            'No CAD to USD rate for Mar 12, 2026. Rates are fetched through Mar 6. Exchange rates →'));
        expect(within(line).getByRole('link', { name: 'Exchange rates →' }))
            .toHaveAttribute('href', expect.stringContaining('/settings?tab=rates'));
    });

    it('still draws the month-ends that have their rates, and says "no rate" in today\'s slot', async () => {
        const stub = homeStub({
            overview: overview({ netWorth: null, totalAssets: null, rateUnavailable: [TODAY_MISSING] }),
        });
        stub.on('GET', /\/reports\/net-worth-history$/, history(null));
        renderHome();

        const widget = await panel();
        const chart = await within(widget).findByRole('img', { name: /^Net worth at 4 month-ends and today; no bar for Today\./ });
        expect(chart.querySelectorAll('rect').length).toBeGreaterThanOrEqual(4);
        const slot = widget.querySelector('[data-no-rate="2026-03-12"]');
        expect(slot).not.toBeNull();
        expect(slot).toHaveTextContent('no rate');
        // Today's column still takes a selection, and its readout names the missing rate.
        await userEvent.setup().click(within(widget).getByRole('button', { name: 'Today: no rate' }));
        const readout = within(widget).getByText(/^Selected Today:/);
        expect(within(readout).getByText('no rate')).toHaveAttribute('title', 'No CAD to USD rate for Mar 12, 2026.');
        // The headline's line already names the missing day, so the chart adds none.
        expect(within(widget).getAllByText(/^No CAD to USD rate for Mar 12, 2026\./)).toHaveLength(1);
    });

    it('names a month-end whose rate is missing under the chart, when the headline has its rates', async () => {
        const monthEnd = missingRate('2026-01-31');
        const stub = homeStub();
        stub.on('GET', /\/reports\/net-worth-history$/, {
            ...history(1_000),
            points: history(1_000).points.map((p) => (p.asOf.startsWith('2026-01-31')
                ? { ...p, netWorth: null, rateUnavailable: [monthEnd] }
                : p)),
        });
        renderHome();

        const widget = await panel();
        expect(await within(widget).findByText(/^No CAD to USD rate for Jan 31, 2026\./)).toBeInTheDocument();
        expect(widget.querySelector('[data-no-rate="2026-01-31"]')).toHaveTextContent('no rate');
        expect(widget.querySelector('[data-no-rate="2026-03-12"]')).toBeNull();
    });

    it('draws every bar and no "no rate" when every point has its rates', async () => {
        const stub = homeStub();
        stub.on('GET', /\/reports\/net-worth-history$/, history(1_000));
        renderHome();

        const widget = await panel();
        expect(await within(widget).findByRole('img', { name: /^Net worth at 4 month-ends and today, higher today/ })).toBeInTheDocument();
        expect(widget.querySelector('[data-no-rate]')).toBeNull();
        expect(within(widget).queryByText(/rate for/)).toBeNull();
    });
});
