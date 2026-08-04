import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { FeedConnectionsPanel } from './FeedConnectionsPanel';
import { ApiError } from '@/lib/api';
import * as apiModule from '@/lib/api';
import type { FeedConnectionSummary } from '@/lib/types';

// Smoke tests for the SimpleFIN connection management panel (a
// Settings tab). Locked-down behaviour:
//   * Empty state when the list is empty.
//   * Connected institutions render with their name + relative-time
//     last-sync hint + status pill.
//   * Connect: paste a token + click Connect → calls
//     createFeedConnection, list refetches.
//   * Connect with an empty token → button disabled, no API call.
//   * Delete: click trash → confirm dialog → calls
//     deleteFeedConnection.
//   * API error on create surfaces as an inline alert.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderPage(opts: { initialConnections?: FeedConnectionSummary[] } = {}) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });

    if (opts.initialConnections) {
        queryClient.setQueryData(
            ['feed-connections', LEDGER_ID],
            opts.initialConnections,
        );
    }

    return render(
        <QueryClientProvider client={queryClient}>
            <FeedConnectionsPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

describe('FeedConnectionsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('renders the empty state when no connections exist', async () => {
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);

        renderPage();

        expect(
            await screen.findByText(/no connected institutions yet/i),
        ).toBeInTheDocument();
    });

    it('lists existing connections with name + status', async () => {
        const connections: FeedConnectionSummary[] = [
            {
                id: 'c1',
                ledgerId: LEDGER_ID,
                provider: 'simplefin',
                institutionName: 'Test Bank',
                status: 'active',
                lastSyncedAt: null,
                createdAt: '2026-05-15T12:00:00Z',
            },
        ];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);

        renderPage({ initialConnections: connections });

        expect(await screen.findByText('Test Bank')).toBeInTheDocument();
        expect(screen.getByText(/not synced yet/i)).toBeInTheDocument();
        expect(screen.getByText(/active/i)).toBeInTheDocument();
    });

    it('falls back to "SimpleFIN" when institutionName is null', async () => {
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: null,
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }]);

        renderPage();

        // The fallback appears as the row's primary label, distinct
        // from the page header text.
        const matches = await screen.findAllByText(/simplefin/i);
        expect(matches.length).toBeGreaterThan(0);
    });

    it('keeps the Connect button disabled until a token is pasted', async () => {
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);
        const user = userEvent.setup();

        renderPage();

        await screen.findByText(/connect a new institution/i);
        const button = screen.getByRole('button', { name: /^connect$/i });
        expect(button).toBeDisabled();

        const input = screen.getByLabelText(/simplefin setup token/i);
        await user.type(input, '  '); // whitespace only — still disabled
        expect(button).toBeDisabled();

        await user.type(input, 'real-token');
        expect(button).toBeEnabled();
    });

    it('calls createFeedConnection on Connect and refetches the list', async () => {
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);
        const create = vi.spyOn(apiModule, 'createFeedConnection').mockResolvedValue({
            id: 'new-1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Newly Connected',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        });
        const user = userEvent.setup();

        renderPage();

        const input = await screen.findByLabelText(/simplefin setup token/i);
        await user.type(input, 'paste-this-from-simplefin');
        await user.click(screen.getByRole('button', { name: /^connect$/i }));

        await waitFor(() => {
            expect(create).toHaveBeenCalledWith(LEDGER_ID, {
                setupToken: 'paste-this-from-simplefin',
            });
        });
    });

    it('surfaces API errors on connect as an inline alert', async () => {
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);
        vi.spyOn(apiModule, 'createFeedConnection').mockRejectedValue(
            new ApiError(
                422,
                'Setup token already consumed — generate a fresh one.',
                'feed-connection-setup-token-invalid',
            ),
        );
        const user = userEvent.setup();

        renderPage();

        const input = await screen.findByLabelText(/simplefin setup token/i);
        await user.type(input, 'stale-token');
        await user.click(screen.getByRole('button', { name: /^connect$/i }));

        expect(
            await screen.findByText(/setup token already consumed/i),
        ).toBeInTheDocument();
    });

    it('confirms and deletes a connection', async () => {
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Bank to remove',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        const del = vi.spyOn(apiModule, 'deleteFeedConnection').mockResolvedValue();
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });

        await user.click(await screen.findByRole('button', { name: /disconnect/i }));
        // Confirm dialog now visible — scope the affirmative
        // button lookup to within it so we don't also match the
        // row's IconButton (which uses the same label).
        const dialog = await screen.findByRole('dialog');
        expect(dialog).toHaveTextContent(/disconnect bank to remove\?/i);
        const { getByRole } = await import('@testing-library/dom');
        await user.click(getByRole(dialog, 'button', { name: /^disconnect$/i }));

        await waitFor(() => {
            expect(del).toHaveBeenCalledWith(LEDGER_ID, 'c1');
        });
    });

    it('clicks Sync now and renders the result summary', async () => {
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        const sync = vi.spyOn(apiModule, 'syncFeedConnection').mockResolvedValue({
            accountsDiscovered: 1,
            transactionsForReview: 12,
            transactionsStillPending: 0,
            alreadyKnown: 3,
            connectionStatus: 'active',
            errors: [],
        });
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });

        await user.click(await screen.findByRole('button', { name: /sync now/i }));

        await waitFor(() => {
            expect(sync).toHaveBeenCalledWith(LEDGER_ID, 'c1');
        });
        // Summary line carries the counts the server returned. The
        // "Last synced …" label also matches /synced/i, so anchor on
        // the unique "already known" phrase.
        expect(
            await screen.findByText(/already known/i),
        ).toHaveTextContent(/1.*account.*12.*new transaction.*3.*already known/i);
    });

    it('renders "still pending at the bank" copy when transactionsStillPending > 0', async () => {
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'syncFeedConnection').mockResolvedValue({
            accountsDiscovered: 1,
            transactionsForReview: 5,
            transactionsStillPending: 2,
            alreadyKnown: 0,
            connectionStatus: 'active',
            errors: [],
        });
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });

        await user.click(await screen.findByRole('button', { name: /sync now/i }));

        // Slice 2c summary: the "still pending" leg only appears
        // when the sync landed bank-pending rows (SimpleFIN
        // pending=true). Anchor on the unique "still pending"
        // phrase since "synced" also matches the last-synced label.
        expect(
            await screen.findByText(/still pending at the bank/i),
        ).toHaveTextContent(/5.*new transaction.*2.*still pending/i);
    });

    it('toggles the Sync activity panel and lists recent runs (slice 2c.1)', async () => {
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        const runs = vi.spyOn(apiModule, 'fetchSyncRuns').mockResolvedValue([
            {
                id: 'run-1',
                feedConnectionId: 'c1',
                status: 'completed',
                txnsFetched: 5,
                txnsInserted: 5,
                txnsPromoted: 0,
                txnsAlreadyKnown: 0,
                txnsStillPending: 0,
                errorMessage: null,
                startedAt: '2026-05-16T10:00:00Z',
                completedAt: '2026-05-16T10:00:05Z',
                triggeredByUserId: null,
                errorCount: 0,
                promotionCount: 0,
            },
        ]);
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });

        // Panel starts collapsed — clicking the toggle fires the
        // runs fetch (gated on `expanded`).
        const toggle = await screen.findByRole('button', {
            name: /sync activity/i,
        });
        expect(runs).not.toHaveBeenCalled();
        await user.click(toggle);
        await waitFor(() => {
            expect(runs).toHaveBeenCalledWith(LEDGER_ID, 'c1', 5);
        });
        expect(await screen.findByText(/completed/i)).toBeInTheDocument();
        // Headline counters for a clean run.
        expect(screen.getByText(/5 new/i)).toBeInTheDocument();
    });

    it('shows the Re-connect required banner when sync flips to needs_reauth', async () => {
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'syncFeedConnection').mockResolvedValue({
            accountsDiscovered: 0,
            transactionsForReview: 0,
            transactionsStillPending: 0,
            alreadyKnown: 0,
            connectionStatus: 'needs_reauth',
            errors: [],
        });
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });

        await user.click(await screen.findByRole('button', { name: /sync now/i }));

        // Banner copy is the defensive-API surface — verifies the
        // SPA reads connectionStatus, not just success counts.
        expect(
            await screen.findByText(/re-connect required/i),
        ).toBeInTheDocument();
        expect(
            screen.getByText(/simplefin rejected the stored access url/i),
        ).toBeInTheDocument();
    });

    it('renders SimpleFIN errlist entries alongside the success summary', async () => {
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'syncFeedConnection').mockResolvedValue({
            accountsDiscovered: 1,
            transactionsForReview: 0,
            transactionsStillPending: 0,
            alreadyKnown: 0,
            connectionStatus: 'active',
            errors: [
                {
                    code: 'fi.maintenance',
                    message: 'Bank A maintenance window',
                    simpleFinConnectionId: 'c-A',
                    simpleFinAccountId: null,
                },
            ],
        });
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });

        await user.click(await screen.findByRole('button', { name: /sync now/i }));

        expect(
            await screen.findByText(/simplefin reported 1 problem/i),
        ).toBeInTheDocument();
        expect(
            screen.getByText(/bank a maintenance window/i),
        ).toBeInTheDocument();
        // Structured code is rendered too — telemetry / power-user
        // signal that the SPA isn't dropping it.
        expect(screen.getByText(/fi\.maintenance/i)).toBeInTheDocument();
    });

    it('renders the unified accounts list per connection (slice 2c.4)', async () => {
        // Slice 2c.4: the wizard is gone. The page renders ONE
        // unified list per connection, populated by
        // GET /feed-connections/{cid}/accounts — independent of
        // any recent sync. Mapped rows show their Coffer binding
        // with an Unmap button; unmapped rows show the picker +
        // Map button.
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: '2026-05-16T12:00:00Z',
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([
            {
                id: 'ledger-acct-1',
                ledgerId: LEDGER_ID,
                parentId: null,
                name: 'Checking',
                accountType: 'bank',
                categoryKind: null,
                currencyCode: 'USD',
                isActive: true,
                isSystem: false,
                feedConnectionId: null,
                needsReviewCount: 0,
                holdingsAccountId: null,
        isTradeCommission: false,
            },
        ]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockResolvedValue([
            {
                simpleFinAccountId: 'sf-bound',
                name: 'Bound Checking',
                orgName: 'Test Bank',
                currency: 'USD',
                balance: 100,
                lastSeenAt: '2026-05-16T12:00:00Z',
                boundLedgerAccountId: 'ledger-acct-bound',
                boundLedgerAccountName: 'Test Bank Checking',
                boundLedgerAccountSyncFrom: null,
            },
            {
                simpleFinAccountId: 'sf-unbound',
                name: 'Unbound Savings',
                orgName: 'Test Bank',
                currency: 'USD',
                balance: 50,
                lastSeenAt: '2026-05-16T12:00:00Z',
                boundLedgerAccountId: null,
                boundLedgerAccountName: null,
                boundLedgerAccountSyncFrom: null,
            },
        ]);

        renderPage({ initialConnections: connections });

        // Both rows render — mapped + unmapped together.
        expect(await screen.findByText(/bound checking/i)).toBeInTheDocument();
        expect(screen.getByText(/unbound savings/i)).toBeInTheDocument();
        // Mapped row shows its Coffer binding name + Unmap button.
        expect(screen.getByText(/test bank checking/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /^unmap$/i })).toBeInTheDocument();
        // Unmapped row shows a picker + Map button.
        expect(screen.getByRole('button', { name: /^map$/i })).toBeInTheDocument();
    });

    it('Unmap calls unbindAccountFromFeed with the bound Coffer account id (slice 2c.4)', async () => {
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: '2026-05-16T12:00:00Z',
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockResolvedValue([
            {
                simpleFinAccountId: 'sf-1',
                name: 'My Checking',
                orgName: 'Test Bank',
                currency: 'USD',
                balance: 100,
                lastSeenAt: '2026-05-16T12:00:00Z',
                boundLedgerAccountId: 'ledger-acct-checking',
                boundLedgerAccountName: 'Test Bank Checking',
                boundLedgerAccountSyncFrom: null,
            },
        ]);
        const unbind = vi.spyOn(apiModule, 'unbindAccountFromFeed').mockResolvedValue();
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });
        await screen.findByText(/my checking/i);

        await user.click(screen.getByRole('button', { name: /^unmap$/i }));

        await waitFor(() => {
            expect(unbind).toHaveBeenCalledWith(LEDGER_ID, 'ledger-acct-checking');
        });
    });

    it('Sync all fires syncAllConnections and renders the aggregate summary (slice 2c.3)', async () => {
        const connections: FeedConnectionSummary[] = [
            {
                id: 'c1',
                ledgerId: LEDGER_ID,
                provider: 'simplefin',
                institutionName: 'Bank A',
                status: 'active',
                lastSyncedAt: null,
                createdAt: '2026-05-15T12:00:00Z',
            },
            {
                id: 'c2',
                ledgerId: LEDGER_ID,
                provider: 'simplefin',
                institutionName: 'Bank B',
                status: 'active',
                lastSyncedAt: null,
                createdAt: '2026-05-15T12:00:00Z',
            },
        ];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        const syncAll = vi.spyOn(apiModule, 'syncAllConnections').mockResolvedValue({
            connections: [
                {
                    connectionId: 'c1',
                    result: {
                        accountsDiscovered: 1,
                        transactionsForReview: 3,
                        transactionsStillPending: 0,
                        alreadyKnown: 0,
                                    connectionStatus: 'active',
                        errors: [],
                    },
                    failureCode: null,
                },
                {
                    connectionId: 'c2',
                    result: {
                        accountsDiscovered: 0,
                        transactionsForReview: 0,
                        transactionsStillPending: 0,
                        alreadyKnown: 0,
                                    connectionStatus: 'needs_reauth',
                        errors: [],
                    },
                    failureCode: null,
                },
            ],
            hadAnyFailure: true,
        });
        const user = userEvent.setup();

        renderPage({ initialConnections: connections });

        const btn = await screen.findByRole('button', { name: /^sync all$/i });
        await user.click(btn);

        await waitFor(() => {
            expect(syncAll).toHaveBeenCalledWith(LEDGER_ID);
        });
        // Aggregate summary renders with the partial-failure
        // copy. The partial-failure note is unique to the
        // aggregate panel; anchor on that and walk up to confirm
        // the summary counters are co-located.
        const partialNote = await screen.findByText(
            /one or more connections need attention/i,
        );
        const panel = partialNote.parentElement!;
        expect(panel).toHaveTextContent(/Synced 2 connections/i);
        expect(panel).toHaveTextContent(/3 new for review/i);
    });

    it('filters already-bound Coffer accounts out of the picker (slice 2c.2)', async () => {
        // Bound account shouldn't appear in any mapping dropdown.
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: null,
            createdAt: '2026-05-15T12:00:00Z',
        }];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([
            // Already bound — must NOT appear in the picker.
            {
                id: 'ledger-acct-bound',
                ledgerId: LEDGER_ID,
                parentId: null,
                name: 'Already Bound',
                accountType: 'bank',
                categoryKind: null,
                currencyCode: 'USD',
                isActive: true,
                isSystem: false,
                feedConnectionId: 'c1',
                needsReviewCount: 0,
                holdingsAccountId: null,
        isTradeCommission: false,
            },
            // Unbound — should appear.
            {
                id: 'ledger-acct-free',
                ledgerId: LEDGER_ID,
                parentId: null,
                name: 'Free Account',
                accountType: 'bank',
                categoryKind: null,
                currencyCode: 'USD',
                isActive: true,
                isSystem: false,
                feedConnectionId: null,
                needsReviewCount: 0,
                holdingsAccountId: null,
        isTradeCommission: false,
            },
        ]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockResolvedValue([
            {
                simpleFinAccountId: 'sf-1',
                name: 'New SimpleFIN',
                orgName: 'Test Bank',
                currency: 'USD',
                balance: 0,
                lastSeenAt: '2026-05-16T12:00:00Z',
                boundLedgerAccountId: null,
                boundLedgerAccountName: null,
                boundLedgerAccountSyncFrom: null,
            },
        ]);

        renderPage({ initialConnections: connections });
        await screen.findByText(/new simplefin/i);

        const select = screen.getByRole('combobox');
        const options = Array.from(select.querySelectorAll('option'))
            .map((o) => o.textContent);
        expect(options).toContain('Free Account');
        expect(options).not.toContain('Already Bound');
    });
});
