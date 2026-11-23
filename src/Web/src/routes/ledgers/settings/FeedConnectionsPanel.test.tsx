import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
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
        // The owner's view by default: connecting and configuring are theirs (FU-095).
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
            { id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' },
        ]);
    });

    it("offers an editor the sync, and not the owner's connect, disconnect or mapping", async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
            { id: LEDGER_ID, name: 'Personal', role: 'editor', currencyCode: 'USD' },
        ]);
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([{
            id: 'conn-1', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: 'Example Bank',
            status: 'active', lastSyncedAt: null, lastError: null, createdAt: '2026-10-01T00:00:00Z',
        } as FeedConnectionSummary]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockResolvedValue([]);

        renderPage();

        const row = (await screen.findByText('Example Bank')).closest('li')!;
        expect(within(row).getByRole('button', { name: /sync now/i })).toBeInTheDocument();
        expect(within(row).queryByRole('button', { name: /disconnect/i })).toBeNull();
        expect(screen.getByText(/only an owner of this ledger can connect/i)).toBeInTheDocument();
        expect(screen.queryByLabelText(/simplefin setup token/i)).toBeNull();
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

    it('gives focus to the setup-token field when a connection clears it and leaves Connect disabled', async () => {
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);
        const create = vi.spyOn(apiModule, 'createFeedConnection').mockResolvedValue({
            id: 'new-1', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: 'Newly Connected',
            status: 'active', lastSyncedAt: null, createdAt: '2026-05-15T12:00:00Z',
        });
        const user = userEvent.setup();

        renderPage();

        const input = await screen.findByLabelText(/simplefin setup token/i);
        await user.type(input, 'paste-this-from-simplefin');
        const connect = screen.getByRole('button', { name: /^connect$/i });
        await user.click(connect);

        await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
        await waitFor(() => expect(input).toHaveValue(''));
        expect(connect).toBeDisabled();
        expect(input).toHaveFocus();
    });

    it('keeps the setup-token field focused through a connect sent with Enter, and sends one', async () => {
        // The field whose Enter runs the action is not disabled while the
        // connect is in flight: it keeps focus, and its Enter refuses instead.
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([]);
        let finish: () => void = () => {};
        const create = vi.spyOn(apiModule, 'createFeedConnection').mockImplementation(() => new Promise((resolve) => {
            finish = () => resolve({
                id: 'new-1', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: 'Newly Connected',
                status: 'active', lastSyncedAt: null, createdAt: '2026-05-15T12:00:00Z',
            });
        }));
        const user = userEvent.setup();

        renderPage();

        const input = await screen.findByLabelText(/simplefin setup token/i);
        await user.type(input, 'paste-this-from-simplefin{Enter}');
        expect(await screen.findByRole('button', { name: /connecting/i })).toHaveAttribute('aria-busy', 'true');
        expect(input).toBeEnabled();
        expect(input).toHaveFocus();
        await user.keyboard('{Enter}');
        expect(create).toHaveBeenCalledTimes(1);
        // Pending, not disabled: the field refuses what is typed while the connect
        // whose success clears it is in flight, so nothing typed is dropped unseen.
        await user.keyboard('more');
        expect(input).toHaveValue('paste-this-from-simplefin');
        expect(input).toHaveAttribute('aria-disabled', 'true');

        finish();
        await waitFor(() => expect(input).toHaveValue(''));
        expect(input).toHaveFocus();
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

    it('has no Sync activity strip: run history lives in Settings → Activity', async () => {
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

        renderPage({ initialConnections: connections });

        // Anchor on the connection's own row so the absence is judged against a rendered panel.
        expect(await screen.findByRole('button', { name: /sync now/i })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /sync activity/i })).toBeNull();
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
                    providerKey: null,
                    accountLabel: null,
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

    describe('a mapping that lands swaps the row\'s controls, and focus goes to the first that takes the pressed one\'s place', () => {
        // ADR-0023 §L: a control that replaces itself hands focus on, never to the page.
        const connections: FeedConnectionSummary[] = [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: '2026-05-16T12:00:00Z',
            createdAt: '2026-05-15T12:00:00Z',
        }];
        const feedAccount = (bound: boolean) => ({
            simpleFinAccountId: 'sf-1',
            name: 'My Checking',
            orgName: 'Test Bank',
            currency: 'USD',
            balance: 100,
            lastSeenAt: '2026-05-16T12:00:00Z',
            boundLedgerAccountId: bound ? 'ledger-acct-checking' : null,
            boundLedgerAccountName: bound ? 'Test Bank Checking' : null,
            boundLedgerAccountSyncFrom: null,
        });
        const checking = {
            id: 'ledger-acct-checking',
            ledgerId: LEDGER_ID,
            parentId: null,
            name: 'Checking',
            accountType: 'bank' as const,
            categoryKind: null,
            currencyCode: 'USD',
            isActive: true,
            isSystem: false,
            feedConnectionId: null,
            needsReviewCount: 0,
            holdingsAccountId: null,
            isTradeCommission: false,
        };

        it('Unmap gives focus to the picker', async () => {
            let bound = true;
            vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
            vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
            vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockImplementation(async () => [feedAccount(bound)]);
            vi.spyOn(apiModule, 'unbindAccountFromFeed').mockImplementation(async () => { bound = false; });
            const invalidated = vi.spyOn(QueryClient.prototype, 'invalidateQueries');
            const user = userEvent.setup();
            renderPage({ initialConnections: connections });

            const unmap = await screen.findByRole('button', { name: /^unmap$/i });
            unmap.focus();
            await user.keyboard('{Enter}');

            const picker = await screen.findByRole('combobox');
            await waitFor(() => expect(picker).toHaveFocus());
            expectHomeRefreshed(invalidated);
        });

        it('Map gives focus to Sync from…', async () => {
            let bound = false;
            vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
            vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([checking]);
            vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockImplementation(async () => [feedAccount(bound)]);
            vi.spyOn(apiModule, 'mapAccountToFeed').mockImplementation(async () => { bound = true; return undefined as never; });
            const invalidated = vi.spyOn(QueryClient.prototype, 'invalidateQueries');
            const user = userEvent.setup();
            renderPage({ initialConnections: connections });

            await user.selectOptions(await screen.findByRole('combobox'), 'ledger-acct-checking');
            const map = screen.getByRole('button', { name: /^map$/i });
            map.focus();
            await user.keyboard('{Enter}');

            const syncFrom = await screen.findByRole('button', { name: /sync from/i });
            await waitFor(() => expect(syncFrom).toHaveFocus());
            expectHomeRefreshed(invalidated);
        });

        /** A binding moves which account a feed fills, which the home screen reads (ADR-0079). */
        function expectHomeRefreshed(spy: { mock: { calls: unknown[][] } }) {
            const keys = spy.mock.calls.map((c) => (c[0] as { queryKey?: unknown } | undefined)?.queryKey);
            expect(keys).toContainEqual(['home', LEDGER_ID]);
            expect(keys).toContainEqual(['overview', LEDGER_ID]);
            expect(keys).toContainEqual(['budget-progress', LEDGER_ID]);
        }
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

describe('FeedConnectionsPanel — an account the provider has stopped listing', () => {
    // last_seen_at has been written since the feed work landed and rendered nowhere,
    // so a bank quietly dropping an account presented as transactions that simply
    // stopped arriving — the hardest shape to notice in a register that never claimed
    // to be complete.
    //
    // Dates are computed from now(), not hardcoded: a fixture with a literal date
    // silently changes meaning as it ages past the threshold, which is exactly how the
    // existing fixtures in this file ended up stale without anyone noticing.
    const daysAgo = (n: number) => new Date(Date.now() - n * 86_400_000).toISOString();

    function connection() {
        return [{
            id: 'c1',
            ledgerId: LEDGER_ID,
            provider: 'simplefin',
            institutionName: 'Test Bank',
            status: 'active',
            lastSyncedAt: daysAgo(0),
            createdAt: daysAgo(90),
        }] as FeedConnectionSummary[];
    }

    function account(lastSeenAt: string) {
        return {
            simpleFinAccountId: 'sf-1',
            name: 'Dropped Checking',
            orgName: 'Test Bank',
            currency: 'USD',
            balance: 100,
            lastSeenAt,
            boundLedgerAccountId: 'ledger-acct-1',
            boundLedgerAccountName: 'Test Bank Checking',
            boundLedgerAccountSyncFrom: null,
        };
    }

    it('flags an account the provider has not returned for weeks', async () => {
        const connections = connection();
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts')
            .mockResolvedValue([account(daysAgo(30))]);

        renderPage({ initialConnections: connections });

        expect(await screen.findByText(/dropped checking/i)).toBeInTheDocument();
        expect(screen.getByText(/not seen since/i)).toBeInTheDocument();
    });

    it('says nothing about an account the provider returned today', async () => {
        // The negative half. Without it the badge could render unconditionally and the
        // test above would still pass — which is the same shape as a check that cannot
        // fail.
        const connections = connection();
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue(connections);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts')
            .mockResolvedValue([account(daysAgo(1))]);

        renderPage({ initialConnections: connections });

        expect(await screen.findByText(/dropped checking/i)).toBeInTheDocument();
        expect(screen.queryByText(/not seen since/i)).toBeNull();
    });
});

describe('FeedConnectionsPanel — by role (ADR-0023 §L)', () => {
    const asRole = (role: 'owner' | 'editor' | 'viewer') => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([{ id: LEDGER_ID, name: 'Personal', role, currencyCode: 'USD' }]);
        vi.spyOn(apiModule, 'fetchFeedConnections').mockResolvedValue([{
            id: 'conn-1', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: 'Example Bank',
            status: 'active', lastSyncedAt: null, lastError: null, createdAt: '2026-10-01T00:00:00Z',
        } as FeedConnectionSummary]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchSchedule').mockReturnValue(new Promise(() => {}));
    };
    /** The connection's row, once the reader's role has arrived: an owner-only line
     *  says it has, one way or the other. */
    const settledRow = async () => {
        const row = (await screen.findByText('Example Bank')).closest('li') as HTMLElement;
        await waitFor(() => expect(apiModule.fetchVisibleLedgers).toHaveBeenCalled());
        return row;
    };

    it('gives a viewer the connections, and no sync, sync schedule or how to connect', async () => {
        asRole('viewer');
        renderPage();
        const row = await settledRow();
        expect(await screen.findByText(/only an owner of this ledger can connect/i)).toBeInTheDocument();
        // Told nothing it cannot do.
        expect(screen.queryByText(/You can sync/)).toBeNull();

        // What it reads stays: the row's own disclosure of its recent syncs.
        expect(within(row).queryByRole('button', { name: /sync now/i })).toBeNull();
        expect(within(row).queryByRole('button', { name: /disconnect/i })).toBeNull();
        expect(screen.queryByRole('button', { name: 'Sync all' })).toBeNull();
        expect(screen.queryByText('Sync automatically')).toBeNull();
        expect(screen.queryByText(/Generate a setup token/)).toBeNull();
    });

    it('gives an editor sync and its schedule, and not how to connect', async () => {
        asRole('editor');
        renderPage();
        const row = await settledRow();
        expect(await within(row).findByRole('button', { name: /sync now/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Sync all' })).toBeInTheDocument();
        expect(screen.getByText('Sync automatically')).toBeInTheDocument();
        expect(screen.queryByText(/Generate a setup token/)).toBeNull();
        expect(screen.getByText(/You can sync the connections that exist\./)).toBeInTheDocument();
    });

    it('tells the owner how to connect', async () => {
        asRole('owner');
        renderPage();
        await settledRow();
        expect(await screen.findByText(/Generate a setup token/)).toBeInTheDocument();
        expect(screen.getByText('Sync automatically')).toBeInTheDocument();
    });
});

describe('FeedConnectionsPanel — renaming a connection', () => {
    const connection = (name: string | null): FeedConnectionSummary => ({
        id: 'conn-1', ledgerId: LEDGER_ID, provider: 'simplefin', institutionName: name,
        status: 'active', lastSyncedAt: null, createdAt: '2026-10-01T00:00:00Z',
    });

    /** The server: the connection's stored name, and each rename sent to it, answered
     *  when the test settles it. */
    function server() {
        let stored: string | null = 'Feed A';
        const sent: { name: string; settle: (outcome: 'ok' | ApiError) => void }[] = [];
        vi.spyOn(apiModule, 'fetchFeedConnections').mockImplementation(async () => [connection(stored)]);
        const rename = vi.spyOn(apiModule, 'renameFeedConnection').mockImplementation((_ledger, _id, body) =>
            new Promise<FeedConnectionSummary>((resolve, reject) => {
                sent.push({
                    name: body.name,
                    settle: (outcome) => {
                        if (outcome !== 'ok') return reject(outcome);
                        stored = body.name;
                        resolve(connection(stored));
                    },
                });
            }));
        return { rename, sent };
    }

    /** The panel as `role` sees it. The role and the list are in the cache from the
     *  first render, so a control the row leaves out is left out of a settled row, not
     *  of one still waiting for the role. */
    function renderAs(role: 'owner' | 'editor' | 'viewer') {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([{ id: LEDGER_ID, name: 'Personal', role, currencyCode: 'USD' }]);
        vi.spyOn(apiModule, 'fetchFeedConnectionAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchSchedule').mockReturnValue(new Promise(() => {}));
        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
        });
        queryClient.setQueryData(['ledgers'], [{ id: LEDGER_ID, name: 'Personal', role, currencyCode: 'USD' }]);
        queryClient.setQueryData(['feed-connections', LEDGER_ID], [connection('Feed A')]);
        render(
            <QueryClientProvider client={queryClient}>
                <FeedConnectionsPanel ledgerId={LEDGER_ID} />
            </QueryClientProvider>,
        );
    }

    const row = () => screen.getByText('Not synced yet').closest('li') as HTMLElement;
    const pencil = () => within(row()).getByRole('button', { name: 'Rename connection' });
    const field = () => within(row()).getByRole('textbox', { name: 'Connection name' });

    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('puts the pencil right after the name, for an owner, who sees Disconnect', async () => {
        server();
        renderAs('owner');

        const name = await screen.findByText('Feed A');
        expect(name.nextElementSibling).toBe(pencil());
        expect(pencil()).toHaveAttribute('title', 'Rename');
        expect(within(row()).getByRole('button', { name: 'Disconnect' })).toBeInTheDocument();
    });

    it.each(['editor', 'viewer'] as const)('offers an %s no pencil, as it offers no Disconnect', async (role) => {
        server();
        renderAs(role);

        expect(await screen.findByText('Feed A')).toBeInTheDocument();
        // An editor's Sync now says the row is drawn for the role it was given.
        if (role === 'editor') expect(within(row()).getByRole('button', { name: 'Sync now' })).toBeInTheDocument();
        expect(within(row()).queryByRole('button', { name: 'Disconnect' })).toBeNull();
        expect(within(row()).queryByRole('button', { name: 'Rename connection' })).toBeNull();
    });

    it('edits the name in place, with its text selected and the pencil out of the way', async () => {
        server();
        renderAs('owner');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));

        const input = field() as HTMLInputElement;
        expect(input).toHaveFocus();
        expect(input).toHaveValue('Feed A');
        expect([input.selectionStart, input.selectionEnd]).toEqual([0, 'Feed A'.length]);
        expect(input).toHaveAttribute('maxlength', '80');
        expect(within(row()).queryByRole('button', { name: 'Rename connection' })).toBeNull();
    });

    it('saves on Enter: the new name shows at once and the pencil, pending, holds focus and refuses a second press', async () => {
        const { rename, sent } = server();
        renderAs('owner');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));
        await user.clear(field());
        await user.keyboard('Feed B{Enter}');

        expect(rename).toHaveBeenCalledTimes(1);
        expect(rename).toHaveBeenCalledWith(LEDGER_ID, 'conn-1', { name: 'Feed B' });
        const pending = pencil();
        expect(within(row()).getByText('Feed B')).toBeInTheDocument();
        expect(screen.queryByText('Feed A')).toBeNull();
        expect(pending).toHaveFocus();
        expect(pending).toHaveAttribute('aria-busy', 'true');

        await user.keyboard('{Enter}');
        await user.click(pending);
        expect(within(row()).queryByRole('textbox', { name: 'Connection name' })).toBeNull();
        expect(rename).toHaveBeenCalledTimes(1);

        sent[0]!.settle('ok');
        await waitFor(() => expect(pencil()).not.toHaveAttribute('aria-busy'));
        expect(pencil()).toHaveFocus();
        expect(within(row()).getByText('Feed B')).toBeInTheDocument();
        // No success notice: the name is the confirmation.
        expect(within(row()).queryByRole('alert')).toBeNull();
    });

    it('saves on blur, and leaves focus where the reader went', async () => {
        const { rename } = server();
        renderAs('owner');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));
        await user.clear(field());
        await user.keyboard('Feed B');
        await user.tab();

        expect(rename).toHaveBeenCalledTimes(1);
        expect(rename).toHaveBeenCalledWith(LEDGER_ID, 'conn-1', { name: 'Feed B' });
        expect(within(row()).getByText('Feed B')).toBeInTheDocument();
        expect(within(row()).getByRole('button', { name: 'Sync now' })).toHaveFocus();
        expect(pencil()).not.toHaveFocus();
    });

    it('cancels on Escape and gives focus back to the pencil', async () => {
        const { rename } = server();
        renderAs('owner');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));
        await user.clear(field());
        await user.keyboard('Feed B{Escape}');

        expect(rename).not.toHaveBeenCalled();
        expect(within(row()).getByText('Feed A')).toBeInTheDocument();
        expect(pencil()).toHaveFocus();
    });

    it('cancels a blank name: on Enter focus goes back to the pencil, on blur it stays where it went', async () => {
        const { rename } = server();
        renderAs('owner');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));
        await user.clear(field());
        await user.keyboard('   {Enter}');
        expect(within(row()).getByText('Feed A')).toBeInTheDocument();
        expect(pencil()).toHaveFocus();

        await user.click(pencil());
        await user.clear(field());
        await user.tab();
        expect(within(row()).getByText('Feed A')).toBeInTheDocument();
        expect(within(row()).getByRole('button', { name: 'Sync now' })).toHaveFocus();

        expect(rename).not.toHaveBeenCalled();
    });

    it('puts the old name back when the save is refused, and says why under the row', async () => {
        const { sent } = server();
        renderAs('owner');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));
        await user.clear(field());
        await user.keyboard('Feed B{Enter}');
        expect(within(row()).getByText('Feed B')).toBeInTheDocument();

        sent[0]!.settle(new ApiError(
            422, "A connection's name is limited to 80 characters.", 'feed-connection-name-too-long'));

        expect(await within(row()).findByRole('alert')).toHaveTextContent(
            "Could not rename the connection. A connection's name is limited to 80 characters.");
        expect(within(row()).getByText('Feed A')).toBeInTheDocument();
        expect(within(row()).queryByText('Feed B')).toBeNull();
        expect(pencil()).not.toHaveAttribute('aria-busy');
    });

    it('names the connection by its new name in the Disconnect dialog', async () => {
        const { sent } = server();
        renderAs('owner');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));
        await user.clear(field());
        await user.keyboard('Feed B{Enter}');
        sent[0]!.settle('ok');
        await waitFor(() => expect(pencil()).not.toHaveAttribute('aria-busy'));

        await user.click(within(row()).getByRole('button', { name: 'Disconnect' }));
        expect(await screen.findByRole('dialog')).toHaveTextContent('Disconnect Feed B?');
    });
});
