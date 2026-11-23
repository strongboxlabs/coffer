import { describe, it, expect, beforeEach, vi } from 'vitest';
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

import { LandingPage } from './LandingPage';
import { ApiError } from '@/lib/api';
import * as apiModule from '@/lib/api';
import type { DemoJob } from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';

// Smoke tests for the post-auth landing. Behaviour we lock down:
//
//   * the populated list renders one entry per ledger with the
//     ledger's name + role
//   * the empty-state copy appears when the API returns []
//   * an API failure surfaces the alert region
//   * the Demo ledger action and its build rows (the second describe)

function renderLanding() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    const root = createRootRoute();
    const landingRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: LandingPage,
    });
    // Stub destination route so the Link's typed `to` resolves.
    // We don't render its component in these tests (we only check the
    // <a href>); the route just needs to exist in the tree.
    const ledgerDetailRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>detail</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([landingRoute, ledgerDetailRoute]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('LandingPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        sessionStorage.clear();
        // No Demo build unless a test says otherwise.
        vi.spyOn(apiModule, 'fetchDemoJob').mockResolvedValue(null);
    });

    it('renders one entry per ledger with name + role, each as a link to the detail route', async () => {
        const ledgers: LedgerSummary[] = [
            { id: '00000000-0000-0000-0000-000000000010', name: 'Personal', role: 'owner', currencyCode: 'USD' },
            { id: '00000000-0000-0000-0000-000000000011', name: 'Household', role: 'owner', currencyCode: 'USD' },
        ];
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);

        renderLanding();

        // Each ledger name appears.
        expect(await screen.findByText('Personal')).toBeInTheDocument();
        expect(screen.getByText('Household')).toBeInTheDocument();

        // Role appears next to the name (twice — once per ledger).
        expect(screen.getAllByText(/owner/i).length).toBe(2);

        // Each entry is a link to its detail page. The href is what
        // we lock down — actually navigating is a router concern
        // covered by the home screen's own tests.
        const personalLink = screen.getByRole('link', { name: /personal/i });
        expect(personalLink).toHaveAttribute(
            'href',
            '/ledgers/00000000-0000-0000-0000-000000000010',
        );
        const householdLink = screen.getByRole('link', { name: /household/i });
        expect(householdLink).toHaveAttribute(
            'href',
            '/ledgers/00000000-0000-0000-0000-000000000011',
        );

        // The empty-state copy is absent.
        expect(
            screen.queryByText(/you don't have any ledgers yet/i),
        ).not.toBeInTheDocument();
    });

    it('renders the empty-state copy when the user has no ledgers', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);

        renderLanding();

        expect(
            await screen.findByText(/you don't have any ledgers yet/i),
        ).toBeInTheDocument();
    });

    it('surfaces an alert when the API fails', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockRejectedValue(
            new ApiError(500, 'Internal error'),
        );

        renderLanding();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/could not load your ledgers/i);
    });

    // ADR-0114 D7: a new ledger's reporting currency is chosen as it is created, USD
    // unless changed.
    it('creates a ledger in USD unless another reporting currency is picked', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);
        const create = vi.spyOn(apiModule, 'createLedger').mockResolvedValue({
            id: '00000000-0000-0000-0000-0000000000c1', name: 'Sample', role: 'owner', currencyCode: 'USD',
        });
        renderLanding();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'New ledger' }));
        const dialog = within(await screen.findByRole('dialog', { name: 'Create a ledger' }));
        const currency = dialog.getByLabelText('Reporting currency');
        expect(currency).toHaveValue('USD');
        expect(dialog.getByText(/Totals that combine accounts are shown in it\. Each account keeps its own currency/)).toBeInTheDocument();
        await user.type(dialog.getByLabelText('Name'), 'Sample');
        await user.click(dialog.getByRole('button', { name: 'Create' }));
        await waitFor(() => expect(create).toHaveBeenCalledWith('Sample', true, 'USD'));
    });

    // ADR-0023 §L: the request has taken the currency, so the field holds until it answers.
    it('holds the reporting currency while Create is pending', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);
        vi.spyOn(apiModule, 'createLedger').mockReturnValue(new Promise(() => {}));
        renderLanding();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'New ledger' }));
        const dialog = within(await screen.findByRole('dialog', { name: 'Create a ledger' }));
        const currency = dialog.getByLabelText('Reporting currency');
        await user.type(dialog.getByLabelText('Name'), 'Sample');
        await user.selectOptions(currency, 'CAD');
        await user.click(dialog.getByRole('button', { name: 'Create' }));

        await waitFor(() => expect(currency).toHaveAttribute('aria-disabled', 'true'));
        await user.selectOptions(currency, 'EUR');
        expect(currency).toHaveValue('CAD');
    });

    it('sends the reporting currency picked, and says a refusal', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);
        const create = vi.spyOn(apiModule, 'createLedger').mockRejectedValue(
            new ApiError(422, 'The reporting currency must be a 3-letter ISO code.', 'ledger-currency-invalid'),
        );
        renderLanding();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'New ledger' }));
        const dialog = within(await screen.findByRole('dialog', { name: 'Create a ledger' }));
        await user.type(dialog.getByLabelText('Name'), 'Sample');
        await user.selectOptions(dialog.getByLabelText('Reporting currency'), 'EUR');
        await user.click(dialog.getByRole('button', { name: 'Create' }));
        await waitFor(() => expect(create).toHaveBeenCalledWith('Sample', true, 'EUR'));
        expect(await dialog.findByRole('alert')).toHaveTextContent('The reporting currency must be a 3-letter ISO code.');
    });
});

// The hub's Demo ledger (ADR-0115 D2, D9): the header action, the empty hub's two
// cards, and the build's rows, as the approved boards draw them.
describe('LandingPage Demo ledger', () => {
    const HOUSEHOLD: LedgerSummary = { id: '00000000-0000-0000-0000-0000000000a1', name: 'Household', role: 'owner', currencyCode: 'USD', demoExamplesMissing: null };
    const DEMO: LedgerSummary = { id: '00000000-0000-0000-0000-0000000000a2', name: 'Demo', role: 'owner', currencyCode: 'USD', demoExamplesMissing: null };
    const DEMO_2_ID = '00000000-0000-0000-0000-0000000000a3';
    const NOTE_KEY = 'coffer.demoJob';

    function demoJob(overrides: Partial<DemoJob> = {}): DemoJob {
        return {
            jobId: 'job-1', state: 'running', completed: 3, total: 21, step: 'transactions',
            ledgerId: null, error: null, warnings: [], skippedTransactions: [], skippedTransactionsNotListed: 0,
            ledgerName: 'Demo 2', phase: 'import', stepNumber: 4, stepLabel: 'importing transactions',
            ...overrides,
        };
    }

    function ledgerList() {
        return screen.getByRole('list', { name: 'Ledgers' });
    }

    beforeEach(() => {
        vi.restoreAllMocks();
        sessionStorage.clear();
        vi.spyOn(apiModule, 'fetchDemoJob').mockResolvedValue(null);
    });

    it('offers Import, Demo ledger and New ledger in the header, in that order, with the flask icon', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO]);
        renderLanding();
        await screen.findByText('Household');

        const header = screen.getByRole('heading', { level: 1, name: 'Manage ledgers' }).closest('header')!;
        const buttons = within(header).getAllByRole('button');
        expect(buttons.map((b) => b.textContent)).toEqual(['Import from Moneydance', 'Demo ledger', 'New ledger']);
        expect(buttons[1].querySelector('svg.lucide-flask-conical')).not.toBeNull();
        expect(buttons[1].querySelector('svg')).toHaveClass('mr-1', 'size-icon-md');
    });

    it('shows the empty hub’s two cards, and each does what its header button does', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);
        const start = vi.spyOn(apiModule, 'startDemoLedger').mockReturnValue(new Promise(() => {}));
        renderLanding();
        const user = userEvent.setup();

        expect(await screen.findByText("You don't have any ledgers yet.")).toBeInTheDocument();
        expect(screen.getByText('Start your own, or add a Demo to look around first.')).toBeInTheDocument();
        const newCard = screen.getByRole('button', { name: /^New ledger\s*An empty ledger for your own books, with the default categories\.$/ });
        const demoCard = screen.getByRole('button', { name: /^Add a Demo ledger\s*Five years of made-up household history, ending today\. Safe to explore and delete\.$/ });
        expect(demoCard.querySelector('svg.lucide-flask-conical')).toHaveClass('size-icon-md', 'text-accent');

        await user.click(newCard);
        expect(await screen.findByRole('dialog', { name: 'Create a ledger' })).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: 'Cancel' }));

        await user.click(demoCard);
        await waitFor(() => expect(start).toHaveBeenCalledOnce());
    });

    it('shows a running build as a preparing row: the step in progress, its label and the bar', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO]);
        vi.spyOn(apiModule, 'fetchDemoJob').mockResolvedValue(demoJob());
        renderLanding();

        const status = await screen.findByRole('status');
        expect(status).toHaveTextContent('Step 4 of 21 — importing transactions');
        const row = status.closest('li')!;
        expect(within(row).getByText('Demo 2')).toBeInTheDocument();
        expect(within(row).getByText('Preparing')).toBeInTheDocument();
        expect(within(row).queryByRole('link')).not.toBeInTheDocument();
        expect(row.firstElementChild).toHaveAttribute('aria-disabled', 'true');
        // The board's bar: step 4 of 21 is 19%.
        expect(row.querySelector('.bg-accent')).toHaveStyle({ width: '19%' });
        // The panel counts the ledgers that can be opened, not the build.
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('2 ledgers');
        expect(within(ledgerList()).getAllByRole('listitem')).toHaveLength(3);
    });

    it('lists a Demo still adding its examples once, as its preparing row', async () => {
        const demo2: LedgerSummary = { id: DEMO_2_ID, name: 'Demo 2', role: 'owner', currencyCode: 'USD', demoExamplesMissing: null };
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO, demo2]);
        vi.spyOn(apiModule, 'fetchDemoJob').mockResolvedValue(demoJob({
            phase: 'examples', ledgerId: DEMO_2_ID, completed: 13, stepNumber: 14, stepLabel: 'adding budget targets',
        }));
        renderLanding();

        expect(await screen.findByRole('status')).toHaveTextContent('Step 14 of 21 — adding budget targets');
        expect(screen.getAllByText('Demo 2')).toHaveLength(1);
        expect(screen.queryByRole('link', { name: /demo 2/i })).not.toBeInTheDocument();
        expect(screen.getByRole('link', { name: /household/i })).toBeInTheDocument();
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('2 ledgers');
    });

    it('shows a failed build with the server’s text and Try again, which starts it again', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO]);
        vi.spyOn(apiModule, 'fetchDemoJob').mockResolvedValue(demoJob({
            state: 'failed', stepNumber: 21, stepLabel: null,
            error: 'Building Demo 2 failed and nothing was kept. Try again, or check the server logs.',
        }));
        const start = vi.spyOn(apiModule, 'startDemoLedger').mockResolvedValue(demoJob({ jobId: 'job-2', completed: 0, stepNumber: 1, stepLabel: 'importing securities' }));
        renderLanding();
        const user = userEvent.setup();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('Building Demo 2 failed and nothing was kept. Try again, or check the server logs.');
        expect(alert.querySelector('svg')).toHaveClass('size-icon-lg');
        const row = alert.closest('li')!;
        expect(within(row).getByText('Demo 2')).toBeInTheDocument();
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('2 ledgers');

        await user.click(within(row).getByRole('button', { name: 'Try again' }));
        await waitFor(() => expect(start).toHaveBeenCalledOnce());
        expect(await screen.findByRole('status')).toHaveTextContent('Step 1 of 21 — importing securities');
        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('shows a start that failed after naming the Demo as that Demo’s failed row, in the failed row’s own sentence', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO]);
        // A refusal whose detail is the upload's memory sentence: the row still says what
        // every failed build says, and the reason stays in the server log.
        vi.spyOn(apiModule, 'startDemoLedger').mockRejectedValue(new ApiError(
            422, 'This export needs more memory to import than the server has free.',
            'import-parse-failed', undefined, [], [], 'Demo 2',
        ));
        renderLanding();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Demo ledger' }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('Building Demo 2 failed and nothing was kept. Try again, or check the server logs.');
        expect(screen.queryByText(/needs more memory/)).not.toBeInTheDocument();
        const row = alert.closest('li')!;
        expect(within(row).getByText('Demo 2')).toBeInTheDocument();
        expect(within(row).getByRole('button', { name: 'Try again' })).toBeInTheDocument();
    });

    it('says the examples a Demo is missing under its row, which opens and counts as any other', async () => {
        const demo2: LedgerSummary = { id: DEMO_2_ID, name: 'Demo 2', role: 'owner', currencyCode: 'USD', demoExamplesMissing: ['budget targets'] };
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO, demo2]);
        renderLanding();

        const warning = await screen.findByRole('status');
        expect(warning).toHaveTextContent('The history is complete, but some examples could not be added: budget targets.');
        expect(warning).toHaveClass('text-state-warning');
        expect(warning.closest('a')).toHaveAttribute('href', `/ledgers/${DEMO_2_ID}`);
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('3 ledgers');
    });

    it('joins several missing examples with commas', async () => {
        const demo2: LedgerSummary = { id: DEMO_2_ID, name: 'Demo 2', role: 'owner', currencyCode: 'USD', demoExamplesMissing: ['saved reports', 'the Home layout'] };
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([demo2]);
        renderLanding();

        expect(await screen.findByRole('status')).toHaveTextContent(
            'The history is complete, but some examples could not be added: saved reports, the Home layout.');
    });

    it('names a build it saw running that the server lost, with Try again', async () => {
        sessionStorage.setItem(NOTE_KEY, JSON.stringify({
            jobId: 'job-1', ledgerName: 'Demo 2', ledgerIdsAtStart: [HOUSEHOLD.id, DEMO.id], lastState: 'running',
        }));
        const list = vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO]);
        const start = vi.spyOn(apiModule, 'startDemoLedger').mockReturnValue(new Promise(() => {}));
        renderLanding();
        const user = userEvent.setup();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('Building Demo 2 stopped because the server restarted. Nothing was kept.');
        // The list was read again after the build was found lost.
        expect(list.mock.calls.length).toBeGreaterThanOrEqual(2);
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('2 ledgers');

        await user.click(within(alert.closest('li')!).getByRole('button', { name: 'Try again' }));
        await waitFor(() => expect(start).toHaveBeenCalledOnce());
        expect(sessionStorage.getItem(NOTE_KEY)).toBeNull();
        expect(screen.queryByText(/stopped because the server restarted/)).not.toBeInTheDocument();
    });

    it('does not call a build interrupted when its ledger has appeared since it started', async () => {
        sessionStorage.setItem(NOTE_KEY, JSON.stringify({
            jobId: 'job-1', ledgerName: 'Demo 2', ledgerIdsAtStart: [HOUSEHOLD.id, DEMO.id], lastState: 'running',
        }));
        const demo2: LedgerSummary = { id: DEMO_2_ID, name: 'Demo 2', role: 'owner', currencyCode: 'USD', demoExamplesMissing: null };
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD, DEMO, demo2]);
        renderLanding();

        expect(await screen.findByRole('link', { name: /demo 2/i })).toBeInTheDocument();
        await waitFor(() => expect(sessionStorage.getItem(NOTE_KEY)).toBeNull());
        expect(screen.queryByText(/stopped because the server restarted/)).not.toBeInTheDocument();
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('3 ledgers');
    });

    it('says a busy refusal under the header, with no row', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD]);
        vi.spyOn(apiModule, 'startDemoLedger').mockRejectedValue(new ApiError(
            422, 'An import is already running. Wait for it to finish before starting another.', 'import-already-running',
        ));
        renderLanding();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Demo ledger' }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('An import is already running. Wait for it to finish before starting another.');
        expect(alert.closest('li')).toBeNull();
        expect(within(ledgerList()).getAllByRole('listitem')).toHaveLength(1);
        expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
    });

    it('polls a running build and reloads the ledgers when it succeeds', async () => {
        const demo2: LedgerSummary = { id: DEMO_2_ID, name: 'Demo 2', role: 'owner', currencyCode: 'USD', demoExamplesMissing: null };
        const list = vi.spyOn(apiModule, 'fetchVisibleLedgers')
            .mockResolvedValueOnce([HOUSEHOLD, DEMO])
            .mockResolvedValue([HOUSEHOLD, DEMO, demo2]);
        vi.spyOn(apiModule, 'startDemoLedger').mockResolvedValue(demoJob({ completed: 0, stepNumber: 1, stepLabel: 'importing securities' }));
        const poll = vi.spyOn(apiModule, 'fetchDemoJob')
            .mockResolvedValueOnce(null)
            .mockResolvedValue(demoJob({ state: 'succeeded', completed: 21, stepNumber: 21, stepLabel: null, ledgerId: DEMO_2_ID, phase: 'examples' }));
        renderLanding();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Demo ledger' }));
        expect(await screen.findByRole('status')).toHaveTextContent('Step 1 of 21 — importing securities');
        // The POST's job is remembered with the ledgers that were there before it.
        expect(JSON.parse(sessionStorage.getItem(NOTE_KEY)!)).toEqual({
            jobId: 'job-1', ledgerName: 'Demo 2', ledgerIdsAtStart: [HOUSEHOLD.id, DEMO.id], lastState: 'running',
        });

        expect(await screen.findByRole('link', { name: /demo 2/i }, { timeout: 3000 })).toBeInTheDocument();
        expect(poll.mock.calls.length).toBeGreaterThanOrEqual(2);
        expect(list.mock.calls.length).toBeGreaterThanOrEqual(2);
        expect(screen.queryByRole('status')).not.toBeInTheDocument();
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('3 ledgers');
        // Seen finished, the build is forgotten.
        expect(sessionStorage.getItem(NOTE_KEY)).toBeNull();

        // And no longer polled: a finished job is not asked about again.
        const polls = poll.mock.calls.length;
        await new Promise((resolve) => setTimeout(resolve, 1500));
        expect(poll.mock.calls.length).toBe(polls);
    });

    it('keeps the finished build’s row until the reloaded list shows its ledger, so an empty hub never flashes its empty state', async () => {
        const demo2: LedgerSummary = { id: DEMO_2_ID, name: 'Demo 2', role: 'owner', currencyCode: 'USD', demoExamplesMissing: null };
        let answerReload: (ledgers: LedgerSummary[]) => void = () => {};
        const list = vi.spyOn(apiModule, 'fetchVisibleLedgers')
            .mockResolvedValueOnce([])
            .mockImplementation(() => new Promise<LedgerSummary[]>((resolve) => { answerReload = resolve; }));
        vi.spyOn(apiModule, 'startDemoLedger').mockResolvedValue(demoJob({ completed: 0, stepNumber: 1, stepLabel: 'importing securities' }));
        const poll = vi.spyOn(apiModule, 'fetchDemoJob')
            .mockResolvedValueOnce(null)
            .mockResolvedValue(demoJob({ state: 'succeeded', completed: 21, stepNumber: 21, stepLabel: null, ledgerId: DEMO_2_ID, phase: 'examples' }));
        renderLanding();
        const user = userEvent.setup();

        expect(await screen.findByText("You don't have any ledgers yet.")).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: 'Demo ledger' }));
        expect(await screen.findByRole('status')).toHaveTextContent('Step 1 of 21 — importing securities');

        // The job has succeeded and the list is being read again, but has not answered.
        await waitFor(() => expect(poll.mock.calls.length).toBeGreaterThanOrEqual(2), { timeout: 3000 });
        await waitFor(() => expect(list.mock.calls.length).toBeGreaterThanOrEqual(2));
        expect(await screen.findByText('Step 21 of 21')).toBeInTheDocument();
        expect(screen.queryByText("You don't have any ledgers yet.")).not.toBeInTheDocument();

        answerReload([demo2]);
        expect(await screen.findByRole('link', { name: /demo 2/i })).toBeInTheDocument();
        expect(screen.queryByRole('status')).not.toBeInTheDocument();
        expect(screen.queryByText("You don't have any ledgers yet.")).not.toBeInTheDocument();
        expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('1 ledger');
    });

    it('still builds and shows the row when session storage is unavailable', async () => {
        vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied'); });
        vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('denied'); });
        vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new Error('denied'); });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([HOUSEHOLD]);
        const start = vi.spyOn(apiModule, 'startDemoLedger').mockResolvedValue(demoJob({ ledgerName: 'Demo', completed: 0, stepNumber: 1, stepLabel: 'importing securities' }));
        renderLanding();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Demo ledger' }));
        await waitFor(() => expect(start).toHaveBeenCalledOnce());
        expect(await screen.findByRole('status')).toHaveTextContent('Step 1 of 21 — importing securities');
    });
});
