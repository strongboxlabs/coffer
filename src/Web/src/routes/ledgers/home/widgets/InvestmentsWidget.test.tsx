import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';

import { problem, type StubbedCall } from '@/lib/annotations/fetchStub';
import type { LedgerOverview, RateUnavailable } from '@/lib/types';

import { account, CHECKING, homeStub, layoutWith, renderHome, type HomeFixture } from '../homeTestFixtures';

// The Investments widget through the real page, API client and router, with the
// server mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const ACCOUNTS = [
    account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
    account({ id: 'acct-brokerage', name: 'Brokerage A', accountType: 'investment' }),
];

const PORTFOLIO: LedgerOverview['portfolio'] = {
    value: 1_000, costBasis: 900, unrealizedGain: 100, percentChange: 11.11, cashBalance: 25,
    currencyCode: 'USD', rateUnavailable: [],
};

const stub = (f: Omit<HomeFixture, 'accounts'> & { portfolio?: Partial<LedgerOverview['portfolio']> } = {}) => {
    const { portfolio, ...rest } = f;
    return homeStub({
        accounts: ACCOUNTS,
        allocation: { excludedBrokerageCash: 25 },
        ...rest,
        overview: { investmentsValue: 1_025, ...rest.overview, portfolio: { ...PORTFOLIO, ...portfolio } },
    });
};

/** The returns report's reason for a rate it could not compute because an exchange rate is
 *  missing, verbatim from the server (InvestmentReportingRepository). */
const SERVER_RATE_REASON =
    'An exchange rate this figure needs is missing (rateUnavailable names each pair and date), '
    + 'and a return is never computed over a partial sum.';

const panel = () => screen.findByRole('heading', { level: 2, name: 'Investments' })
    .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

/** A figure's row in the panel, by its label. */
const figure = async (label: string) =>
    (await within(await panel()).findByText(label, { selector: 'dt' })).parentElement as HTMLElement;

/** The return block: from its window's subhead to its last rate. */
const returnBlock = async () =>
    (await within(await panel()).findByText(/^Return /)).closest('dl') as HTMLElement;

describe('Investments: the return block', () => {
    it('leads with the gain incl. income, as news: default text when it is not negative, and no plus', async () => {
        stub({ returns: { gain: 50 } });
        renderHome();

        const block = await returnBlock();
        expect(within(block).getAllByRole('term')[0]).toHaveTextContent('Gain incl. income');
        const gain = within(await figure('Gain incl. income')).getByText('$50.00');
        expect(gain).toHaveClass('text-text');
        expect(gain).not.toHaveClass('text-state-danger');
    });

    it('prints a gain below zero in danger', async () => {
        stub({ returns: { gain: -25 } });
        renderHome();

        expect(within(await figure('Gain incl. income')).getByText('-$25.00')).toHaveClass('text-state-danger');
    });

    it('names its window without "annualized", and says it in each rate\'s label instead', async () => {
        stub();
        renderHome();

        const block = await returnBlock();
        expect(within(block).getByText(/^Return /)).toHaveTextContent(/^Return YTD$/);
        expect(within(await figure('Time-weighted, annualized')).getByText('4.00%')).toBeInTheDocument();
        expect(within(await figure('Time-weighted, annualized')).getByText('over 71 days')).toBeInTheDocument();
        expect(within(await figure('Money-weighted, annualized')).getByText('5.00%')).toBeInTheDocument();
    });

    it('steps a one-year window back a calendar year, from a leap day to the last day of February', async () => {
        vi.setSystemTime(new Date(2028, 1, 29, 15, 0, 0));
        const s = stub({ prefs: layoutWith({ investments: { settings: { returnWindow: '1y' } } }) });
        // The server refuses a date that does not exist, as it does.
        s.on('GET', /\/reports\/returns$/, (call: StubbedCall) => {
            const from = call.search.get('from');
            const day = from === null ? null : new Date(from);
            if (day !== null && (Number.isNaN(day.getTime()) || day.toISOString().slice(0, 10) !== from!.slice(0, 10))) {
                return problem(400, 'from must be an ISO-8601 instant.');
            }
            return { scope: 'ledger', startDate: from, endDate: '2028-02-29T23:59:59.999999Z', startValue: 1_000, endValue: 1_100,
                netContributions: 50, gain: 50, moneyWeightedReturn: 0.05, moneyWeightedUnavailableReason: null,
                timeWeightedReturn: 0.04, timeWeightedUnavailableReason: null, timeWeightedCoveredYears: 1, timeWeightedCoveredDays: 365,
                contributionsIn: 50, contributionsOut: 0, currencyCode: 'USD', rateUnavailable: [] };
        });
        renderHome();

        expect(await figure('Gain incl. income')).toHaveTextContent('$50.00');
        expect(within(await returnBlock()).getByText(/^Return /)).toHaveTextContent('Return 1 year');
        expect(within(await panel()).queryByRole('alert')).toBeNull();
        expect(s.callsTo('GET', /\/reports\/returns$/)[0]!.search.get('from')).toBe('2027-02-28T00:00:00Z');
    });
});

describe('Investments: currency', () => {
    it('prints every figure in the ledger\'s reporting currency, as the reports name it', async () => {
        stub({
            overview: { currencyCode: 'EUR' }, portfolio: { currencyCode: 'EUR' },
            returns: { gain: 50, currencyCode: 'EUR' },
            allocation: {
                buckets: [{ bucket: 'us_equity', marketValue: 1_000, percent: 100, currencyCode: 'EUR', rateUnavailable: [] }],
                excludedBrokerageCash: 25, currencyCode: 'EUR',
            },
        });
        renderHome();

        expect(await figure('Holdings')).toHaveTextContent('€1,000.00');
        expect(await figure('Brokerage cash')).toHaveTextContent('€25.00');
        expect(await figure('Invested total')).toHaveTextContent('€1,025.00');
        expect(await figure('Unrealized gain')).toHaveTextContent('€100.00');
        expect(await figure('Gain incl. income')).toHaveTextContent('€50.00');
        const p = await panel();
        expect(await within(p).findByText('Us equity')).toBeInTheDocument();
        expect(within(p).getByText('Us equity').closest('li')).toHaveAttribute('title', '€1,000.00');
        expect(within(p).getByText(/^Excludes/)).toHaveTextContent('Excludes €25.00 brokerage cash.');
        expect(within(p).queryByText('no rate')).toBeNull();
    });

    it('reads "no rate" for the gain, both rates and every allocation figure when a rate is missing', async () => {
        const missing: RateUnavailable = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-12', reason: 'rate_unavailable' };
        stub({
            returns: {
                gain: null, rateUnavailable: [missing],
                // The returns report's reasons are prose, as the server sends them.
                timeWeightedReturn: null, timeWeightedUnavailableReason: SERVER_RATE_REASON,
                moneyWeightedReturn: null, moneyWeightedUnavailableReason: SERVER_RATE_REASON,
            },
            allocation: {
                buckets: [{ bucket: 'us_equity', marketValue: null, percent: null, currencyCode: 'USD', rateUnavailable: [missing] }],
                totalMarketValue: null, excludedBrokerageCash: null, rateUnavailable: [missing],
            },
        });
        renderHome();

        const title = 'No CAD to USD rate for Mar 12, 2026.';
        for (const label of ['Gain incl. income', 'Time-weighted, annualized', 'Money-weighted, annualized']) {
            const row = await figure(label);
            expect(within(row).getByText('no rate')).toHaveAttribute('title', title);
            expect(row).not.toHaveTextContent('%');
        }
        const p = await panel();
        const bucket = (await within(p).findByText('Us equity')).closest('li')!;
        expect(within(bucket).getByText('no rate')).toHaveAttribute('title', title);
        expect(bucket).not.toHaveTextContent('%');
        expect(bucket).not.toHaveAttribute('title');
        expect(bucket.querySelector('.bg-accent')).toBeNull();
        const excluded = within(p).getByText(/^Excludes/);
        expect(within(excluded).getByText('no rate')).toHaveAttribute('title', title);
        expect(within(p).queryByText(SERVER_RATE_REASON)).toBeNull();
        expect(p).not.toHaveTextContent('partial sum');
    });

    it('shows the server\'s reason for a rate it could not compute when no rate is missing', async () => {
        // Verbatim from the server (InvestmentReportingRepository's TwrReason).
        const reason = 'Time-weighted return needs at least two portfolio valuations to chain a '
            + 'sub-period between; this window has fewer.';
        stub({
            returns: {
                timeWeightedReturn: null, timeWeightedUnavailableReason: reason,
                timeWeightedCoveredDays: null, timeWeightedCoveredYears: null, rateUnavailable: [],
            },
        });
        renderHome();

        const row = await figure('Time-weighted, annualized');
        expect(within(row).getByText(reason)).toBeInTheDocument();
        expect(within(row).queryByText('no rate')).toBeNull();
        expect(within(await figure('Money-weighted, annualized')).getByText('5.00%')).toBeInTheDocument();
    });
});

describe('Investments: a residue smaller than the printed figure', () => {
    it('prints a negative residue under a cent as a plain zero, never a red one', async () => {
        stub({ portfolio: { unrealizedGain: -0.004 }, returns: { gain: -0.003, timeWeightedReturn: -0.00001 } });
        renderHome();

        for (const label of ['Unrealized gain', 'Gain incl. income']) {
            const zero = within(await figure(label)).getByText('$0.00');
            expect(zero).not.toHaveClass('text-state-danger');
        }
        const rate = within(await figure('Time-weighted, annualized')).getByText('0.00%');
        expect(rate).not.toHaveClass('text-state-danger');
    });

    it('still prints a negative of a cent or more in danger', async () => {
        stub({ portfolio: { unrealizedGain: -0.01 } });
        renderHome();

        expect(within(await figure('Unrealized gain')).getByText('-$0.01')).toHaveClass('text-state-danger');
    });

    it('prints a change that rounds to zero percent with no sign', async () => {
        stub({ portfolio: { unrealizedGain: -0.004, percentChange: -0.004 } });
        renderHome();

        const row = await figure('Unrealized gain');
        expect(within(row).getByText('(0.00%)')).toBeInTheDocument();
        expect(row).not.toHaveTextContent('-0.00');
    });

    it('still prints a change of a hundredth of a percent or more below zero with its sign', async () => {
        stub({ portfolio: { unrealizedGain: -0.12, percentChange: -0.012 } });
        renderHome();

        expect(within(await figure('Unrealized gain')).getByText('(-0.01%)')).toBeInTheDocument();
    });
});
