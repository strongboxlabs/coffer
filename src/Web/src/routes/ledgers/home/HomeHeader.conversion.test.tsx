import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';

import { account, CHECKING, homeStub, renderHome } from './homeTestFixtures';

// The Overview's one ledger-level note on converted totals (ADR-0114 D7; the
// converted-totals board CT-1b), through the real page and API client with the server
// mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const NOTE = /^Totals are in USD, with CAD accounts converted at the rate for each figure's date \(the latest rate from today on\)\./;

const header = () => screen.findByRole('heading', { level: 1, name: 'Overview' })
    .then((h) => h.closest('header') as HTMLElement);

describe('the Overview\'s conversion note', () => {
    it('says under the header that totals are converted, naming the other currency, and links to the rates', async () => {
        homeStub({
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
                account({ id: 'acct-b', name: 'Account B', accountType: 'bank', currencyCode: 'CAD' }),
            ],
        });
        renderHome();

        const note = await within(await header()).findByText(NOTE);
        expect(note).toHaveTextContent('An account\'s own figures stay in its currency.');
        expect(within(note).getByRole('link', { name: 'Exchange rates →' }))
            .toHaveAttribute('href', expect.stringContaining('/settings?tab=rates'));
        // One note on the page, not one per widget.
        expect(screen.getAllByText(NOTE)).toHaveLength(1);
    });

    it('counts a closed account, as net worth does', async () => {
        homeStub({
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
                account({ id: 'acct-b', name: 'Account B', accountType: 'bank', currencyCode: 'CAD', isActive: false }),
            ],
        });
        renderHome();

        expect(await within(await header()).findByText(NOTE)).toBeInTheDocument();
    });

    it('names the reporting currency the ledger list carries', async () => {
        homeStub({
            reportingCurrency: 'CAD',
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
                account({ id: 'acct-b', name: 'Account B', accountType: 'bank', currencyCode: 'CAD' }),
            ],
        });
        renderHome();

        expect(await within(await header()).findByText(/^Totals are in CAD, with USD accounts converted/)).toBeInTheDocument();
    });

    it('draws nothing when every account is in the reporting currency, whatever a category carries', async () => {
        homeStub({
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
                account({ id: 'cat-a', name: 'Category A', accountType: 'category', categoryKind: 'expense', currencyCode: 'CAD' }),
            ],
        });
        renderHome();

        const head = await header();
        // Settled: the attention sentence reads the same accounts read the note does.
        await within(head).findAllByText(/Nothing needs you today/);
        expect(within(head).queryByText(/^Totals are in/)).toBeNull();
    });
});
