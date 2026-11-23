import { describe, it, expect, beforeEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ActivityPanel } from './ActivityPanel';
import * as apiModule from '@/lib/api';
import type { LedgerOperationSummary, SyncErrorDto, SyncRunDetail } from '@/lib/types';

// The undo-import entry point.
//
// `undo-import` existed for releases with exactly one caller: the import
// dialog. The undo therefore lived only as long as that dialog stayed open —
// close it, or go and look at what actually landed, and the endpoint was
// unreachable from the app. An import you have not looked at is the one you
// are least likely to want to undo, so the affordance belonged where you go to
// look.
//
// These tests exist because "the button renders" is not the property that
// matters. What matters is that clicking it reaches the endpoint, that the
// confirm states what will be lost, and that it is absent where an undo would
// be a lie.

const LEDGER_ID = '00000000-0000-0000-0000-0000000000b1';

function op(overrides: Partial<LedgerOperationSummary>): LedgerOperationSummary {
    return {
        id: '00000000-0000-0000-0000-00000000f001',
        family: 'ingest',
        providerKey: 'csv-generic',
        triggeredVia: 'file-upload',
        status: 'completed',
        startedAt: '2026-09-20T12:00:00Z',
        completedAt: '2026-09-20T12:00:03Z',
        triggeredByUserId: null,
        details: { txns_inserted: 12 },
        errorCount: 0,
        feedConnectionId: null,
        ...overrides,
    };
}

function renderPanel() {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={client}>
            <ActivityPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

function syncError(over: Partial<SyncErrorDto> & Pick<SyncErrorDto, 'code' | 'message'>): SyncErrorDto {
    return {
        simpleFinConnectionId: null,
        simpleFinAccountId: null,
        providerKey: null,
        accountLabel: null,
        ...over,
    };
}

function detail(
    over: Partial<SyncRunDetail> & { errorMessage?: string | null; status?: string } = {},
): SyncRunDetail {
    const { errorMessage = null, status = 'partial', ...rest } = over;
    return {
        summary: {
            id: '00000000-0000-0000-0000-00000000f001', feedConnectionId: null, status,
            txnsFetched: 0, txnsInserted: 0, txnsPromoted: 0, txnsAlreadyKnown: 0, txnsStillPending: 0,
            errorMessage, startedAt: '2026-09-20T12:00:00Z', completedAt: '2026-09-20T12:00:03Z',
            triggeredByUserId: null, errorCount: 0, promotionCount: 0,
        },
        errors: [],
        promotions: [],
        ...rest,
    };
}

describe('ActivityPanel run details', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);
    });

    it('opens a needs-reauth run to say what happened and what to do, since the run records no message', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ providerKey: 'simplefin', status: 'needs_reauth', details: {} }),
        ]);
        // As IngestOrchestrator closes one: the status, and no message, errors or promotions.
        vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(detail({ status: 'needs_reauth' }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /simplefin/i }));

        expect(await screen.findByText(/refused this connection's stored access/)).toBeInTheDocument();
        expect(screen.getByText(/Re-connect it in Settings → Bank feeds/)).toBeInTheDocument();
        expect(screen.queryByText(/No further details/)).toBeNull();
    });

    it('lists only the charges whose amount moved as cleared at a different amount, and counts the rest', async () => {
        // Every pending charge that clears is a promotion. One that cleared at its held
        // figure must not be reported as the bank changing the amount.
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ providerKey: 'simplefin', details: { txns_promoted: 3 } }),
        ]);
        vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(detail({
            promotions: [
                { headerId: 'h1', wasAmount: -10, becameAmount: -10, promotedAt: '2026-09-20T12:00:01Z', currencyCode: 'USD' },
                { headerId: 'h2', wasAmount: -20, becameAmount: -20, promotedAt: '2026-09-20T12:00:02Z', currencyCode: 'USD' },
                { headerId: 'h3', wasAmount: -30, becameAmount: -35, promotedAt: '2026-09-20T12:00:03Z', currencyCode: 'USD' },
            ],
        }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /simplefin/i }));

        const heading = await screen.findByText('Cleared at different amounts:');
        expect(within(heading.parentElement!).getAllByRole('listitem')).toHaveLength(1);
        expect(screen.getByText('2 pending charges cleared at the same amount.')).toBeInTheDocument();
    });

    it('does not claim a different amount when every charge cleared at its held figure', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ providerKey: 'simplefin', details: { txns_promoted: 1 } }),
        ]);
        vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(detail({
            promotions: [
                { headerId: 'h1', wasAmount: -10, becameAmount: -10, promotedAt: '2026-09-20T12:00:01Z', currencyCode: 'USD' },
            ],
        }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /simplefin/i }));

        expect(await screen.findByText('1 pending charge cleared at the same amount.')).toBeInTheDocument();
        expect(screen.queryByText('Cleared at different amounts:')).toBeNull();
    });

    it('names the bank connection a sync pulled, so several connections\' runs can be told apart', async () => {
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([
            { id: 'c1', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: 'Example Bank', status: 'active', lastSyncedAt: null, createdAt: '2026-09-01T00:00:00Z' },
            { id: 'c2', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: 'Other Credit Union', status: 'active', lastSyncedAt: null, createdAt: '2026-09-01T00:00:00Z' },
            { id: 'c3', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: null, status: 'active', lastSyncedAt: null, createdAt: '2026-09-01T00:00:00Z' },
        ]);
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ id: 'r1', providerKey: 'simplefin', feedConnectionId: 'c1', details: { txns_inserted: 1 } }),
            op({ id: 'r2', providerKey: 'simplefin', feedConnectionId: 'c2', details: { txns_inserted: 2 } }),
            op({ id: 'r3', providerKey: 'simplefin', feedConnectionId: 'c3', details: { txns_inserted: 3 } }),
        ]);
        renderPanel();

        expect(await screen.findByText(/· Example Bank/)).toBeInTheDocument();
        expect(screen.getByText(/· Other Credit Union/)).toBeInTheDocument();
        // A connection with no name of its own adds nothing to "SimpleFIN".
        const first = screen.getByText(/1 new/).closest('li')!;
        expect(first).toHaveTextContent(/SimpleFIN · Example Bank/);
        const third = screen.getByText(/3 new/).closest('li')!;
        expect(third).not.toHaveTextContent(/SimpleFIN\s*·/);
    });

    it('opens a run with errors to show each error\'s code and message', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ providerKey: 'simplefin', triggeredVia: 'manual', status: 'partial', errorCount: 2 }),
        ]);
        const fetchDetail = vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(detail({
            errors: [
                syncError({ code: 'con.auth', message: 'Connection to Example Bank may need attention. Auth required' }),
                syncError({ code: 'gen.auth', message: 'Second problem' }),
            ],
        }));
        const user = userEvent.setup();
        renderPanel();

        // Nothing is fetched until the row is opened.
        const row = await screen.findByRole('button', { name: /2 errors/i });
        expect(fetchDetail).not.toHaveBeenCalled();
        await user.click(row);

        expect(await screen.findByText('con.auth')).toBeInTheDocument();
        expect(screen.getByText(/Auth required/)).toBeInTheDocument();
        expect(screen.getByText('gen.auth')).toBeInTheDocument();
        expect(fetchDetail).toHaveBeenCalledWith(LEDGER_ID, '00000000-0000-0000-0000-00000000f001');
        // A sync's errors name no source, so they list under no source heading.
        expect(screen.queryByText('SimpleFIN holdings')).toBeNull();
    });

    it('groups a quote run\'s errors under the source that raised each one', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ family: 'quote', providerKey: 'quote-refresh', triggeredVia: 'manual', status: 'partial', errorCount: 3, details: {} }),
        ]);
        vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(detail({
            errors: [
                syncError({ code: 'currency-missing', message: 'First holdings problem', providerKey: 'simplefin-holdings' }),
                syncError({ code: 'ticker-not-resolved', message: 'A Yahoo problem', providerKey: 'yahoo' }),
                syncError({ code: 'currency-unknown', message: 'Second holdings problem', providerKey: 'simplefin-holdings' }),
            ],
        }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /3 errors/i }));

        const holdings = await screen.findByText('SimpleFIN holdings');
        const holdingsGroup = within(holdings.parentElement!);
        expect(holdingsGroup.getByText(/First holdings problem/)).toBeInTheDocument();
        expect(holdingsGroup.getByText(/Second holdings problem/)).toBeInTheDocument();
        expect(holdingsGroup.queryByText(/A Yahoo problem/)).toBeNull();
        const yahooGroup = within(screen.getByText('Yahoo Finance').parentElement!);
        expect(yahooGroup.getByText(/A Yahoo problem/)).toBeInTheDocument();
        expect(yahooGroup.queryByText(/holdings problem/)).toBeNull();
    });

    it('names the account an error is about, by its label, else by the feed id the message does not carry', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ family: 'quote', providerKey: 'quote-refresh', triggeredVia: 'post-sync', status: 'partial', errorCount: 3, details: {} }),
        ]);
        vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(detail({
            errors: [
                syncError({
                    code: 'currency-missing', message: 'Labelled problem', providerKey: 'simplefin-holdings',
                    simpleFinAccountId: 'ACT-labelled', accountLabel: 'Brokerage 1 · Institution A',
                }),
                syncError({
                    code: 'currency-missing', message: 'Unlabelled problem', providerKey: 'simplefin-holdings',
                    simpleFinAccountId: 'ACT-unlabelled',
                }),
                // An older row carries the id in its message already: it is not repeated.
                syncError({
                    code: 'currency-missing', message: 'Account ACT-old: older problem', providerKey: null,
                    simpleFinAccountId: 'ACT-old',
                }),
            ],
        }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /3 errors/i }));

        expect(await screen.findByText(/after SimpleFIN sync/)).toBeInTheDocument();
        const labelled = screen.getByText(/Labelled problem/).closest('li')!;
        expect(labelled).toHaveTextContent('Brokerage 1 · Institution A: Labelled problem');
        expect(labelled).not.toHaveTextContent('ACT-labelled');
        expect(screen.getByText(/Unlabelled problem/).closest('li')!).toHaveTextContent('ACT-unlabelled: Unlabelled problem');
        const old = screen.getByText(/older problem/).closest('li')!;
        expect(old).toHaveTextContent('currency-missing — Account ACT-old: older problem');
    });

    it('shows a failed run\'s failure message, which only the detail carries', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ providerKey: 'simplefin', status: 'failed', details: {} }),
        ]);
        vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(
            detail({ errorMessage: 'SimpleFIN /accounts fetch timed out.' }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /simplefin/i }));

        expect(await screen.findByText('SimpleFIN /accounts fetch timed out.')).toBeInTheDocument();
    });

    it('offers no expander on a clean run', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);
        renderPanel();

        // An expandable row is a button named by its own line, so it is matched on the
        // fixture's summary, which renders only on a row, not on its provider label.
        expect(await screen.findByText(/12 new/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /12 new/i })).toBeNull();
    });

    it('prints a promotion in its account\'s currency, at that currency\'s places', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ providerKey: 'simplefin', details: { txns_promoted: 2 } }),
        ]);
        vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(detail({
            promotions: [
                { headerId: 'h1', wasAmount: -1234, becameAmount: -1250, promotedAt: '2026-09-20T12:00:01Z', currencyCode: 'JPY' },
                { headerId: 'h2', wasAmount: -10.5, becameAmount: -12.25, promotedAt: '2026-09-20T12:00:02Z', currencyCode: null },
            ],
        }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /simplefin/i }));

        const heading = await screen.findByText('Cleared at different amounts:');
        const [jpy, bare] = within(heading.parentElement!).getAllByRole('listitem');
        const jpyFigures = [...jpy!.querySelectorAll('strong')];
        expect(jpyFigures[0]).toHaveTextContent(/^-¥1,234$/);
        expect(jpyFigures[1]).toHaveTextContent(/^-¥1,250$/);
        // A row with no originating account prints as stored, with no symbol guessed.
        const bareFigures = [...bare!.querySelectorAll('strong')];
        expect(bareFigures[0]).toHaveTextContent(/^-10\.5$/);
        expect(bareFigures[1]).toHaveTextContent(/^-12\.25$/);
    });

    it('lists the rates and benchmarks backfill as a provider to filter by', async () => {
        const list = vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([]);
        const user = userEvent.setup();
        renderPanel();

        await user.selectOptions(
            await screen.findByRole('combobox', { name: /provider/i }),
            'Rates and benchmarks backfill',
        );

        await waitFor(() => {
            expect(list).toHaveBeenLastCalledWith(
                LEDGER_ID, expect.objectContaining({ provider: 'market-series-backfill' }));
        });
    });
});

describe('ActivityPanel is a log, not a control surface', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);
    });

    it('offers no undo — or any other action — on a listed operation', async () => {
        // No "Undo this import" here. A log reports what ran; putting an
        // irreversible hard-delete of transactions in a list of past events is a
        // category error, and the context is worst there — one summarised line
        // from days ago, versus the import modal where you have just seen what
        // landed.
        //
        // Anchored on "12 new" — the fixture's own txns_inserted summary, which
        // renders ONLY on a row. An anchor on /csv import/i would match
        // PROVIDER_OPTIONS' "CSV import" filter option, so the absences below
        // would be judged against a panel whose rows had not rendered, and would
        // pass with an undo button injected.
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);

        renderPanel();

        expect(await screen.findByText(/12 new/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /undo/i })).toBeNull();
        expect(screen.queryByText(/remove \d+ transaction/i)).toBeNull();
    });
});

describe('ActivityPanel filters a file import by its reader', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('asks for the CSV reader\'s runs when the CSV import option is chosen', async () => {
        // A file run is recorded under its reader's key and the server matches the key
        // exactly, so the option's value must be that key.
        const fetch = vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);

        renderPanel();
        expect(await screen.findByText(/12 new/i)).toBeInTheDocument();

        fireEvent.change(screen.getByDisplayValue('All operations'), { target: { value: 'csv-generic' } });

        await waitFor(() =>
            expect(fetch).toHaveBeenLastCalledWith(LEDGER_ID, expect.objectContaining({ provider: 'csv-generic' })),
        );
        expect(screen.getByRole('option', { name: 'CSV import' })).toHaveValue('csv-generic');
        expect(screen.getByRole('option', { name: 'Fidelity CSV import' })).toHaveValue('csv-fidelity');
    });
});

describe('ActivityPanel lists a Demo marker', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('offers the Demo examples as a filter, and shows a marker row by its label and count', async () => {
        const fetch = vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({
                providerKey: 'demo', triggeredVia: 'manual', status: 'partial',
                details: { resolve: 1, folders: 1, budgets: 0 },
            }),
        ]);

        renderPanel();

        // Anchored on the row's own summary, which renders only on a row.
        expect(await screen.findByText('2 of 11 example steps added')).toBeInTheDocument();
        expect(screen.getByRole('option', { name: 'Demo examples' })).toHaveValue('demo');
        expect(screen.queryByRole('button', { name: /undo/i })).toBeNull();
        expect(fetch).toHaveBeenCalled();
    });

    it('opens a marker that stopped partway to the examples it lacks, which only the detail carries', async () => {
        // A partial marker has no error rows: what it lacks, and why, is its message.
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({
                providerKey: 'demo', triggeredVia: 'manual', status: 'partial',
                details: { resolve: 1, folders: 1, budgets: 0 }, errorCount: 0,
            }),
        ]);
        const fetchDetail = vi.spyOn(apiModule, 'fetchSyncRunDetail').mockResolvedValue(
            detail({ errorMessage: 'Budget targets: the category was not found.' }));
        const user = userEvent.setup();
        renderPanel();

        const row = await screen.findByRole('button', { name: /2 of 11 example steps added/i });
        expect(row).toHaveAttribute('aria-expanded', 'false');
        await user.click(row);

        expect(await screen.findByText('Budget targets: the category was not found.')).toBeInTheDocument();
        expect(fetchDetail).toHaveBeenCalledWith(LEDGER_ID, '00000000-0000-0000-0000-00000000f001');
    });

    it('offers no expander on a marker whose examples all finished', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([
            op({ providerKey: 'demo', triggeredVia: 'manual', status: 'completed', details: { resolve: 1, folders: 1 } }),
        ]);
        renderPanel();

        expect(await screen.findByText('2 of 11 example steps added')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /example steps added/i })).toBeNull();
    });
});
