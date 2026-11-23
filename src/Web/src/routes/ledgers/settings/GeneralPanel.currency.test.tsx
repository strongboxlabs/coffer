import { beforeEach, describe, expect, it, vi } from 'vitest';
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

import * as apiModule from '@/lib/api';
import type { LedgerConsistencyReport, LedgerSummary } from '@/lib/types';

import { GeneralPanel } from './GeneralPanel';

// Settings → General, the parts ADR-0114 adds: the Reporting currency panel (owner
// only, ADR-0114 D7) and the maintenance check's fetched-price currencies (ADR-0114 D5): its label, its
// "not confirmed" kind, the repair's Yahoo refusal, and what a repair could not
// rewrite. Every ledger, security and figure here is invented.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderPanel(queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
    const root = createRootRoute();
    const settings = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <GeneralPanel ledgerId={LEDGER_ID} />,
    });
    const router = createRouter({
        routeTree: root.addChildren([settings]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    return queryClient;
}

function mockLedger(role: string, currencyCode = 'USD') {
    const ledgers: LedgerSummary[] = [{ id: LEDGER_ID, name: 'Sample Ledger', role, currencyCode }];
    return vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
}

/** The Reporting currency panel, once drawn. */
async function currencyPanel() {
    const head = await screen.findByText('Reporting currency', { selector: 'span' });
    return head.closest('div.bg-surface') as HTMLElement;
}

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
});

describe('General: the reporting currency', () => {
    it('lets an owner change it, sending only the currency, with Change currency off until it differs', async () => {
        let stored = 'USD';
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockImplementation(async () => [
            { id: LEDGER_ID, name: 'Sample Ledger', role: 'owner', currencyCode: stored },
        ]);
        const update = vi.spyOn(apiModule, 'updateLedger').mockImplementation(async (_id, body) => {
            stored = body.currencyCode!;
        });
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
        renderPanel(queryClient);
        const user = userEvent.setup();

        const panel = within(await currencyPanel());
        const field = await panel.findByLabelText('Currency');
        await waitFor(() => expect(field).toHaveValue('USD'));
        const change = panel.getByRole('button', { name: 'Change currency' });
        expect(change).toBeDisabled();
        expect(panel.getByText(/Totals that combine accounts are shown in this currency/)).toBeInTheDocument();
        expect(panel.getByText(/Changing the currency rewrites nothing/)).toBeInTheDocument();

        await user.selectOptions(field, 'CAD');
        expect(change).toBeEnabled();
        await user.click(change);

        await waitFor(() => expect(update).toHaveBeenCalledWith(LEDGER_ID, { currencyCode: 'CAD' }));
        await waitFor(() => expect(field).toHaveValue('CAD'));
        await waitFor(() => expect(change).toBeDisabled());
        await waitFor(() => expect(field).toHaveFocus());
        // Every converted figure reads again in the new currency.
        const keys = invalidated.mock.calls.map((c) => c[0]?.queryKey);
        expect(keys).toContainEqual(['ledgers']);
        expect(keys).toContainEqual(['overview', LEDGER_ID]);
        expect(keys).toContainEqual(['fx-rates', LEDGER_ID]);
        // No success notice: the field holding the new value is the answer.
        expect(screen.queryByRole('status')).toBeNull();
    });

    // ADR-0023 §L: a field whose request is pending refuses input, so no pick is made
    // that the landing change would throw away.
    it('holds the field while the change saves', async () => {
        mockLedger('owner');
        let land!: () => void;
        vi.spyOn(apiModule, 'updateLedger').mockReturnValue(new Promise<void>((resolve) => { land = resolve; }));
        renderPanel();
        const user = userEvent.setup();
        const panel = within(await currencyPanel());
        const field = await panel.findByLabelText('Currency');
        await waitFor(() => expect(field).toHaveValue('USD'));
        await user.selectOptions(field, 'CAD');
        await user.click(panel.getByRole('button', { name: 'Change currency' }));

        await waitFor(() => expect(field).toHaveAttribute('aria-disabled', 'true'));
        await user.selectOptions(field, 'EUR');
        expect(field).toHaveValue('CAD');
        land();
    });

    it('offers a stored currency the list does not hold', async () => {
        mockLedger('owner', 'HKD');
        renderPanel();
        const field = await within(await currencyPanel()).findByLabelText('Currency');
        await waitFor(() => expect(field).toHaveValue('HKD'));
    });

    it('says a refused change and keeps the pick', async () => {
        mockLedger('owner');
        vi.spyOn(apiModule, 'updateLedger').mockRejectedValue(new apiModule.ApiError(
            422, 'Another change to this ledger is still being written. Try again in a moment.', 'ledger-write-busy',
        ));
        renderPanel();
        const user = userEvent.setup();
        const panel = within(await currencyPanel());
        const field = await panel.findByLabelText('Currency');
        await waitFor(() => expect(field).toHaveValue('USD'));
        await user.selectOptions(field, 'EUR');
        await user.click(panel.getByRole('button', { name: 'Change currency' }));
        expect(await panel.findByRole('alert')).toHaveTextContent('Another change to this ledger is still being written.');
        expect(field).toHaveValue('EUR');
    });

    it.each(['editor', 'viewer'] as const)('shows an %s the currency as text, with the help and no control', async (role) => {
        mockLedger(role, 'CAD');
        renderPanel();
        const panel = await currencyPanel();
        // Settled: the stored currency, from the read that carries the role.
        await waitFor(() => expect(panel.querySelector('[data-reporting-currency]')).toHaveTextContent('CAD'));
        expect(within(panel).queryByLabelText('Currency')).toBeNull();
        expect(within(panel).queryByRole('button', { name: 'Change currency' })).toBeNull();
        expect(within(panel).getByText('Only an owner can change the reporting currency.')).toBeInTheDocument();
        expect(within(panel).getByText(/Totals that combine accounts are shown in this currency/)).toBeInTheDocument();
    });

    it('sits between the ledger name and Maintenance', async () => {
        mockLedger('owner');
        renderPanel();
        await screen.findByText('Danger zone', { selector: 'span' });
        const heads = ['Ledger name', 'Reporting currency', 'Maintenance', 'Danger zone']
            .map((t) => screen.getByText(t, { selector: 'span' }));
        for (let i = 1; i < heads.length; i += 1) {
            expect(heads[i - 1]!.compareDocumentPosition(heads[i]!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        }
    });
});

/** A report with one quote-currency finding of each kind. */
const QUOTE_REPORT: LedgerConsistencyReport = {
    healthy: false,
    projections: [{
        projection: 'quote_currency', healthy: false, checked: 3, mismatchedCount: 2,
        mismatches: [
            {
                scope: 'SAMA — Sample Fund A', field: '12 fetched prices in USD, a currency Yahoo has not confirmed',
                stored: 0, expected: 0, diff: 0, securityId: 'sec-a', reason: 'unconfirmed',
            },
            {
                scope: 'SAMB — Sample Fund B', field: '2 fetched prices in USD, but Yahoo quotes it in CAD',
                stored: 0, expected: 0, diff: 0, securityId: 'sec-b', reason: 'value',
            },
        ],
    }],
    unbackedPrices: [],
    oversold: [],
};

describe('General: the fetched-price currencies', () => {
    it('labels the finding, counts securities, says what not confirmed means and what the repair does', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(QUOTE_REPORT);
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        const title = await screen.findByText(/^Fetched price currencies —/);
        expect(title.textContent).toBe('Fetched price currencies — 2 of 3 securities out of step');
        const listed = screen.getAllByRole('listitem').map((li) => li.textContent ?? '');
        expect(listed).toContain('SAMA — Sample Fund A · 12 fetched prices in USD, a currency Yahoo has not confirmed');
        expect(listed).toContain('SAMB — Sample Fund B · 2 fetched prices in USD, but Yahoo quotes it in CAD');
        expect(listed.some((t) => /stored/.test(t))).toBe(false);
        expect(screen.getByText(/^Not confirmed: prices fetched before Coffer read each quote's currency were all recorded as USD/))
            .toBeInTheDocument();
        expect(screen.getByText(/^Repair asks Yahoo Finance for each security's currency/)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Repair fetched price currencies' })).toBeInTheDocument();
    });

    it('says the repair needs Yahoo Finance on, with the way to Quotes, and keeps the report', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(QUOTE_REPORT);
        vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(new apiModule.ApiError(
            422, 'server detail', 'consistency-repair-needs-yahoo',
        ));
        mockLedger('editor');
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        await user.click(await screen.findByRole('button', { name: 'Repair fetched price currencies' }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(
            'This repair asks Yahoo Finance for each security\'s currency, and Yahoo Finance is off for you on this ledger. '
            + 'Nothing was changed. Turn it on in Quotes, then repair again.',
        );
        expect(within(alert).getByRole('link', { name: 'Quotes' })).toHaveAttribute(
            'href', `/ledgers/${LEDGER_ID}/settings?tab=quotes`,
        );
        // Refused before anything was rewritten: the report still stands.
        expect(screen.getByText(/^Fetched price currencies —/)).toHaveTextContent('2 of 3 securities out of step');
    });

    it('lists what a repair could not rewrite, each with why, until the next check', async () => {
        const after: LedgerConsistencyReport = {
            ...QUOTE_REPORT,
            projections: [{ ...QUOTE_REPORT.projections[0]!, mismatchedCount: 2 }],
        };
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(QUOTE_REPORT);
        vi.spyOn(apiModule, 'repairProjection').mockResolvedValue({
            projection: 'quote_currency',
            repaired: QUOTE_REPORT.projections[0]!,
            notRepaired: [
                { ...QUOTE_REPORT.projections[0]!.mismatches[0]!, field: 'its symbol is not public, so Yahoo was not asked', reason: 'quote-symbol-not-public' },
                { ...QUOTE_REPORT.projections[0]!.mismatches[1]!, field: 'SAMB: Yahoo returned 429 (rate limited).', reason: 'rate-limited' },
                { ...QUOTE_REPORT.projections[0]!.mismatches[1]!, scope: 'SAMC — Sample Fund C', field: 'A reason with no label yet.', reason: 'brand-new-code', securityId: null },
            ],
            after,
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        await user.click(await screen.findByRole('button', { name: 'Repair fetched price currencies' }));

        const box = (await screen.findByText(/^Fetched price currencies —\s+not repaired$/)).closest('div')!;
        expect(box).toHaveTextContent('Fetched price currencies — 3 not repaired');
        const items = within(box).getAllByRole('listitem').map((li) => li.textContent);
        expect(items).toEqual([
            'SAMA — Sample Fund A · its symbol is not public, so Yahoo Finance was not asked',
            'SAMB — Sample Fund B · Yahoo Finance was limiting requests; repair again later',
            'SAMC — Sample Fund C · A reason with no label yet.',
        ]);
        expect(within(box).getByRole('link', { name: 'SAMA — Sample Fund A' })).toHaveAttribute(
            'href', `/ledgers/${LEDGER_ID}/securities/sec-a`,
        );

        // The next check takes it away: it lists what still disagrees itself.
        check.mockResolvedValue({ ...QUOTE_REPORT, healthy: true, projections: [{ ...QUOTE_REPORT.projections[0]!, healthy: true, mismatchedCount: 0, mismatches: [] }] });
        await user.click(screen.getByRole('button', { name: /check consistency/i }));
        await waitFor(() => expect(screen.queryByText(/not repaired$/)).toBeNull());
        expect(await screen.findByText(/^Fetched price currencies — healthy/)).toBeInTheDocument();
    });

    it('shows a viewer the finding and its explanation, with no repair', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(QUOTE_REPORT);
        mockLedger('viewer');
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        // Settled: the viewer's report, with its rows, is drawn.
        expect(await screen.findByText(/^Fetched price currencies —/)).toHaveTextContent('2 of 3 securities out of step');
        expect(screen.getByText(/^Not confirmed:/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^Repair/ })).toBeNull();
        expect(screen.queryByText(/^Repair asks Yahoo Finance/)).toBeNull();
    });
});
