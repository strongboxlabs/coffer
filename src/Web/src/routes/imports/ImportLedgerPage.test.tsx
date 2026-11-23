import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
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

import { ImportLedgerPage } from './ImportLedgerPage';
import * as importApi from '@/lib/api/import';
import { ApiError } from '@/lib/api';

// ImportLedgerPage (ADR-0071 D2): the new-ledger-from-Moneydance wizard.
// Locked down: analyze → preview, run → complete, failure surfaces the error.

function renderImport() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    const root = createRootRoute();
    const importRoute = createRoute({
        getParentRoute: () => root,
        path: '/imports/moneydance',
        component: ImportLedgerPage,
    });
    const ledgerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>ledger</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([importRoute, ledgerRoute]),
        history: createMemoryHistory({ initialEntries: ['/imports/moneydance'] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

const PREVIEW: importApi.ImportPreview = {
    exporter: 'Moneydance 2023',
    build: 5000,
    exportDate: 20260101,
    totalItems: 1234,
    baseCurrencyCode: 'USD',
    counts: [
        { objType: 'txn', count: 1000 },
        { objType: 'acct', count: 42 },
    ],
};

function makeFile() {
    return new File(['{"metadata":{},"all_items":[]}'], 'export.json', {
        type: 'application/json',
    });
}

const NO_OUTCOME = { warnings: [], skippedTransactions: [], skippedTransactionsNotListed: 0 };

const LEDGER_ID = '00000000-0000-0000-0000-0000000000bb';

function succeeded(outcome: Partial<importApi.ImportJob> = {}): importApi.ImportJob {
    return {
        jobId: 'job-3', state: 'succeeded', completed: 10, total: 10, step: null,
        ledgerId: LEDGER_ID, error: null, ...NO_OUTCOME, ...outcome,
    };
}

/**
 * Mocked at FETCH, not at the api module, so the job the page renders is the
 * JSON the status endpoint would send: preview, start, then the settled status.
 */
function stubImportFetch(settled: importApi.ImportJob, statusGate?: Promise<void>) {
    const json = (body: unknown) =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    const mock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url.endsWith('/api/imports/moneydance/preview')) return json(PREVIEW);
        if (url.endsWith('/api/imports/moneydance') && init?.method === 'POST')
            return json({ ...settled, state: 'running', completed: 0, ledgerId: null, ...NO_OUTCOME });
        if (url.endsWith(`/api/imports/moneydance/${settled.jobId}`)) {
            await statusGate;
            return json(settled);
        }
        return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/json' } });
    });
    vi.stubGlobal('fetch', mock);
    return mock;
}

async function runImport() {
    const user = userEvent.setup();
    renderImport();
    await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
    await user.click(screen.getByRole('button', { name: /analyze export/i }));
    await user.type(await screen.findByLabelText(/new ledger name/i), 'Imported');
    await user.click(screen.getByRole('button', { name: /create ledger/i }));
}

describe('ImportLedgerPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });
    afterEach(() => vi.unstubAllGlobals());

    it('a clean import keeps the last step as it was: no warning line, boxes or hint', async () => {
        stubImportFetch(succeeded());
        await runImport();

        // Settled: this line renders only once the status has arrived as succeeded with
        // nothing to report, so the absences below are read off the finished frame.
        expect(await screen.findByText('Import complete.')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Open ledger' })).toBeEnabled();
        expect(screen.queryByRole('region', { name: 'Import warnings' })).toBeNull();
        expect(screen.getByRole('status')).toHaveTextContent(/^Import complete\.$/);
        expect(screen.queryByText(/written to the server log/)).toBeNull();
    });

    it('one live region carries the run and its result, and takes focus when the import starts', async () => {
        let release!: () => void;
        const gate = new Promise<void>((resolve) => { release = resolve; });
        stubImportFetch(succeeded({
            warnings: [{ check: 'header-has-legs', message: '2 transactions were imported with no lines, so they have no amount.' }],
        }), gate);
        await runImport();

        // Held on the running frame: the status fetch has not answered yet.
        expect(await screen.findByText(/Importing… starting/)).toBeInTheDocument();
        const live = screen.getByRole('status');
        expect(live).toHaveTextContent('Importing… starting');
        // Focus lands on the run's panel, not on the page body the started button left it on.
        expect(document.activeElement).not.toBe(document.body);
        expect(document.activeElement).toContainElement(live);

        release();
        expect(await screen.findByText('Import complete, with 1 warning.')).toBeInTheDocument();
        // The same node, so a screen reader announces the change instead of meeting a new region.
        expect(screen.getByRole('status')).toBe(live);
        expect(live).toHaveTextContent(/^Import complete, with 1 warning\.$/);
        expect(document.activeElement).toContainElement(live);
    });

    it('an import with warnings says so and gives each failed check its own box', async () => {
        stubImportFetch(succeeded({
            warnings: [
                {
                    check: 'header-balance',
                    message: '1 transaction has lines that do not add up to zero, so the balances of the accounts it touches may be off.',
                },
                { check: 'no-dropped-transactions', message: '5 transactions were not imported.' },
                { check: 'a-check-added-later', message: 'Something the server found.' },
            ],
            skippedTransactions: [
                { date: '2026-01-02', reason: 'UnknownSecurity', security: 'Example Fund', ticker: 'EXF', shares: 12.5 },
                { date: null, reason: 'SomeFutureReason', security: null, ticker: null, shares: null },
            ],
            skippedTransactionsNotListed: 3,
        }));
        await runImport();

        expect(await screen.findByText('Import complete, with 3 warnings.')).toBeInTheDocument();
        expect(screen.getByRole('status')).toHaveTextContent(/^Import complete, with 3 warnings\.$/);
        expect(screen.queryByText('Import complete.')).toBeNull();
        const region = screen.getByRole('region', { name: 'Import warnings' });

        // One box per failed check, each headed by its plain title at the level under the
        // page's h1; the not-imported box leads whatever order the server sent.
        const titles = within(region).getAllByRole('heading', { level: 2 }).map((h) => h.textContent);
        expect(titles).toEqual([
            '5 transactions were not imported',
            'Transactions that do not balance',
            'a-check-added-later',
        ]);
        expect(within(region).queryAllByRole('heading').length).toBe(titles.length);
        const balance = within(region).getByRole('heading', { name: 'Transactions that do not balance' }).parentElement!;
        expect(balance).toHaveTextContent(
            '1 transaction has lines that do not add up to zero, so the balances of the accounts it touches may be off.',
        );
        const unknown = within(region).getByRole('heading', { name: 'a-check-added-later' }).parentElement!;
        expect(unknown).toHaveTextContent('Something the server found.');

        // The not-imported box: what to do, the table, and the count of rows not listed.
        const dropped = within(region).getByRole('heading', { name: '5 transactions were not imported' }).parentElement!;
        expect(dropped).toHaveTextContent(
            'They are still in your Moneydance file. Fix them there, then import the export again into a new ledger. You can delete this ledger afterwards.',
        );
        // Named by its box's title, so table navigation announces what it lists.
        const table = within(dropped).getByRole('table', { name: '5 transactions were not imported' });
        const rows = within(table).getAllByRole('row').map((r) =>
            [...within(r).queryAllByRole('columnheader'), ...within(r).queryAllByRole('cell')]
                .map((c) => c.textContent));
        expect(rows).toEqual([
            ['Date', 'Security', 'Shares', 'Why'],
            ['2026-01-02', 'EXF · Example Fund', '12.5', 'Security not in the export'],
            ['—', '—', '—', 'SomeFutureReason'],
        ]);
        // Shares in the register's number face, as every other shares column.
        expect(within(table).getByRole('cell', { name: '12.5' })).toHaveClass('font-mono', 'tabular-nums', 'text-right');
        // A capped list of hundreds of rows scrolls inside the box instead of pushing the
        // hint and the Open ledger button down the page.
        expect(table.parentElement).toHaveClass('max-h-fixed-344px', 'overflow-auto');
        expect(within(dropped).getByText('3 more transactions not listed.')).toBeInTheDocument();

        expect(screen.getByText('Warnings stay on this screen only. They are also written to the server log.'))
            .toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Open ledger' })).toBeEnabled();
    });

    it('each known check gets its plain title, counted in the singular when there is one', async () => {
        stubImportFetch(succeeded({
            warnings: [
                'account-name-uniqueness',
                'investment-action-conformance',
                'header-balance',
                'header-has-legs',
                'originating-account',
                'lots-reference-existing-legs',
                'account-count-parity',
                'security-types-recognised',
                'no-dropped-transactions',
            ].map((check) => ({ check, message: `message of ${check}` })),
            skippedTransactions: [
                { date: '2026-01-02', reason: 'UnknownShape', security: null, ticker: 'EXF', shares: 1 },
            ],
        }));
        await runImport();

        expect(await screen.findByText('Import complete, with 9 warnings.')).toBeInTheDocument();
        const region = screen.getByRole('region', { name: 'Import warnings' });
        expect(within(region).getAllByRole('heading').map((h) => h.textContent)).toEqual([
            '1 transaction was not imported',
            'Duplicate account names',
            'Investment transactions with an action Coffer could not use',
            'Transactions that do not balance',
            'Transactions with no lines',
            'Transactions without an owning account',
            'Tax lots pointing at missing lines',
            'Accounts missing after the import',
            'Securities with a type Coffer does not recognise',
        ]);
        const row = within(within(region).getByRole('table')).getAllByRole('row')[1]!;
        expect(within(row).getAllByRole('cell').map((c) => c.textContent))
            .toEqual(['2026-01-02', 'EXF', '1', 'Action Coffer cannot place']);
        expect(within(region).queryByText(/not listed/)).toBeNull();
    });

    it('a bank transaction not imported gets the plain label of its reason', async () => {
        stubImportFetch(succeeded({
            warnings: [{ check: 'no-dropped-transactions', message: '4 transactions were not imported.' }],
            skippedTransactions: ['UnknownPrimaryAccount', 'UnknownSplitAccount', 'NoSplits', 'DuplicateFitid']
                .map((reason) => ({ date: '2026-01-02', reason, security: null, ticker: null, shares: null })),
        }));
        await runImport();

        const region = await screen.findByRole('region', { name: 'Import warnings' });
        const rows = within(within(region).getByRole('table')).getAllByRole('row').slice(1);
        expect(rows.map((r) => within(r).getAllByRole('cell').map((c) => c.textContent))).toEqual([
            ['2026-01-02', '—', '—', 'Account not in the export'],
            ['2026-01-02', '—', '—', 'Category or account not in the export'],
            ['2026-01-02', '—', '—', 'No lines'],
            ['2026-01-02', '—', '—', 'Same bank download as another transaction'],
        ]);
    });

    it('a single warning is counted in the singular', async () => {
        stubImportFetch(succeeded({
            warnings: [{ check: 'header-has-legs', message: '2 transactions were imported with no lines, so they have no amount.' }],
        }));
        await runImport();

        expect(await screen.findByText('Import complete, with 1 warning.')).toBeInTheDocument();
    });

    it('shows the per-type preview after analyzing a file', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue(PREVIEW);
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));

        expect(await screen.findByText('txn')).toBeInTheDocument();
        expect(screen.getByText('1,000')).toBeInTheDocument();
        expect(screen.getByLabelText(/new ledger name/i)).toBeInTheDocument();
    });

    it('runs the import and shows completion with an Open ledger action', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue(PREVIEW);
        vi.spyOn(importApi, 'startMoneydanceImport').mockResolvedValue({
            jobId: 'job-1', state: 'running', completed: 0, total: 9, step: null, ledgerId: null, error: null,
            ...NO_OUTCOME,
        });
        vi.spyOn(importApi, 'fetchImportJob').mockResolvedValue({
            jobId: 'job-1', state: 'succeeded', completed: 9, total: 9, step: null,
            ledgerId: '00000000-0000-0000-0000-0000000000aa', error: null,
            ...NO_OUTCOME,
        });
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        await user.type(await screen.findByLabelText(/new ledger name/i), 'Imported');
        await user.click(screen.getByRole('button', { name: /create ledger/i }));

        expect(await screen.findByText(/import complete/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /open ledger/i })).toBeEnabled();
    });

    it('surfaces a failed import with a Start over action', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue(PREVIEW);
        vi.spyOn(importApi, 'startMoneydanceImport').mockResolvedValue({
            jobId: 'job-2', state: 'running', completed: 0, total: 9, step: null, ledgerId: null, error: null,
            ...NO_OUTCOME,
        });
        vi.spyOn(importApi, 'fetchImportJob').mockResolvedValue({
            jobId: 'job-2', state: 'failed', completed: 3, total: 9, step: null,
            ledgerId: null, error: 'Ledger name already taken.',
            ...NO_OUTCOME,
        });
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        await user.type(await screen.findByLabelText(/new ledger name/i), 'Dupe');
        await user.click(screen.getByRole('button', { name: /create ledger/i }));

        expect(await screen.findByText(/ledger name already taken/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /start over/i })).toBeInTheDocument();
        // The alert speaks for a failed run; the run's status region is out of the way.
        expect(screen.queryByRole('status')).toBeNull();
        expect(screen.queryByText(/Importing…/)).toBeNull();
    });

    // ADR-0114 D5, D7: the new ledger's reporting currency starts as the export's base
    // currency, and an export whose currencies cannot be told is refused by name.
    it("offers the export's base currency as the reporting currency, and sends the one picked", async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue({ ...PREVIEW, baseCurrencyCode: 'CAD' });
        const start = vi.spyOn(importApi, 'startMoneydanceImport').mockResolvedValue({
            jobId: 'job-3', state: 'running', completed: 0, total: 9, step: null, ledgerId: null, error: null,
            ...NO_OUTCOME,
        });
        vi.spyOn(importApi, 'fetchImportJob').mockReturnValue(new Promise(() => {}));
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        const currency = await screen.findByLabelText('Reporting currency');
        expect(currency).toHaveValue('CAD');
        expect(screen.getByText(/It starts as the export's base currency\. Each imported account keeps the currency it has in Moneydance/))
            .toBeInTheDocument();

        await user.type(screen.getByLabelText(/new ledger name/i), 'Imported');
        await user.click(screen.getByRole('button', { name: /create ledger/i }));
        expect(start).toHaveBeenCalledWith(expect.any(File), 'Imported', 'CAD');
    });

    it('sends a reporting currency changed from the base one', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue(PREVIEW);
        const start = vi.spyOn(importApi, 'startMoneydanceImport').mockReturnValue(new Promise(() => {}));
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        const currency = await screen.findByLabelText('Reporting currency');
        await user.selectOptions(currency, 'GBP');
        await user.type(screen.getByLabelText(/new ledger name/i), 'Imported');
        await user.click(screen.getByRole('button', { name: /create ledger/i }));
        expect(start).toHaveBeenCalledWith(expect.any(File), 'Imported', 'GBP');

        // ADR-0023 §L: the start has taken the currency, so the field holds until it answers.
        await waitFor(() => expect(currency).toHaveAttribute('aria-disabled', 'true'));
        await user.selectOptions(currency, 'EUR');
        expect(currency).toHaveValue('GBP');
    });

    it('offers a base currency the list does not hold', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue({ ...PREVIEW, baseCurrencyCode: 'HKD' });
        const user = userEvent.setup();
        renderImport();
        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        expect(await screen.findByLabelText('Reporting currency')).toHaveValue('HKD');
    });

    it('says an export whose currencies cannot be told is refused, with the accounts it names', async () => {
        const detail = 'This export cannot be imported, because Coffer cannot tell which currency these are in: '
            + 'Account A has no currency. Account B is in a security, not a currency.';
        vi.spyOn(importApi, 'previewMoneydanceImport').mockRejectedValue(
            new ApiError(422, detail, 'import-currency-unresolved'),
        );
        const user = userEvent.setup();
        renderImport();
        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(detail);
        expect(alert).toHaveTextContent('Give each of these accounts its currency in Moneydance, export again, and choose the new file.');
        // Still on the first step: nothing was previewed.
        expect(screen.queryByLabelText('Reporting currency')).toBeNull();
    });

    it('says another refusal in its own words, with no Moneydance hint', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockRejectedValue(
            new ApiError(422, 'The reporting currency must be a 3-letter ISO code.', 'ledger-currency-invalid'),
        );
        const user = userEvent.setup();
        renderImport();
        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('The reporting currency must be a 3-letter ISO code.');
        expect(alert).not.toHaveTextContent(/export again/);
    });
});
