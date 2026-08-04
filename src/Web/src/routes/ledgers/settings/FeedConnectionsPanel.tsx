import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ChevronDown, ChevronRight, RefreshCw, Trash2 } from 'lucide-react';

import {
    createFeedConnection,
    deleteFeedConnection,
    fetchAccounts,
    fetchFeedConnectionAccounts,
    fetchFeedConnections,
    fetchSyncRunDetail,
    fetchSyncRuns,
    mapAccountToFeed,
    syncAllConnections,
    syncFeedConnection,
    unbindAccountFromFeed,
    setAccountSyncFromDate,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import type {
    AccountSummary,
    FeedConnectionAccountDto,
    FeedConnectionSummary,
    SyncAllResultDto,
    SyncErrorDto,
    SyncResultDto,
    SyncRunSummary,
} from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { IconButton } from '@/components/ui/IconButton';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';

/**
 * Per-ledger SimpleFIN connection management.
 *
 * Slice 2a shipped connect + list + disconnect.
 * Slice 2b adds Sync now + the mapping wizard for SimpleFIN
 * accounts the user hasn't yet bound to a Coffer account.
 *
 * Daily flow:
 *   1. Click Sync on a connected institution.
 *   2. Server pulls the latest accounts + transactions from
 *      SimpleFIN. FITID-dedup against existing rows; unmatched
 *      land directly in txn_headers with needs_review=true so
 *      the register's needs-review flow can surface them.
 *   3. If SimpleFIN returns accounts not yet mapped to a Coffer
 *      account, the wizard appears below the connection — pick
 *      the matching Coffer account, click Map; on success re-sync
 *      to pull that account's transactions.
 */
export function FeedConnectionsPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();

    const connectionsQuery = useQuery({
        queryKey: ['feed-connections', ledgerId],
        queryFn: () => fetchFeedConnections(ledgerId),
    });
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
    });

    const invalidateConnections = () =>
        queryClient.invalidateQueries({ queryKey: ['feed-connections', ledgerId] });

    const createMutation = useMutation({
        mutationFn: (setupToken: string) =>
            createFeedConnection(ledgerId, { setupToken }),
        onSuccess: invalidateConnections,
    });
    const deleteMutation = useMutation({
        mutationFn: (connectionId: string) =>
            deleteFeedConnection(ledgerId, connectionId),
        onSuccess: invalidateConnections,
    });
    const syncMutation = useMutation({
        mutationFn: (connectionId: string) =>
            syncFeedConnection(ledgerId, connectionId),
        onSuccess: (result, connectionId) => {
            invalidateConnections();
            // A sync landed new / changed transactions — refresh the register
            // surface (rows via the ADR-0079 canonical key) so a mounted register
            // on any of this ledger's accounts reflects them.
            invalidateLedgerRegister(queryClient, ledgerId);
            // Sync activity panel: a fresh run just landed in
            // sync_runs; invalidate so the panel re-fetches when
            // visible (slice 2c.1).
            queryClient.invalidateQueries({
                queryKey: ['sync-runs', ledgerId, connectionId],
            });
            // Slice 2c.4: the sync just upserted feed_connection_accounts
            // server-side. Invalidate the directory so the unified
            // accounts panel re-fetches and shows the bank's account
            // list (including any new accounts the bank surfaced).
            queryClient.invalidateQueries({
                queryKey: ['feed-connection-accounts', ledgerId, connectionId],
            });
            // Park the result on local state keyed by connection
            // so the row below this connection renders the summary.
            setLastSync((prior) => {
                const next = new Map(prior);
                next.set(connectionId, result);
                return next;
            });
        },
    });
    // Slice 2c.3: ledger-wide "Sync all" — walks every connection
    // server-side, sequentially. The server respects per-connection
    // serialization (DB UNIQUE + in-process semaphore), so even if
    // an individual connection has a sync mid-flight from another
    // tab, sync-all skips it cleanly via the failureCode path.
    const syncAllMutation = useMutation({
        mutationFn: () => syncAllConnections(ledgerId),
        onSuccess: (aggregate) => {
            invalidateConnections();
            invalidateLedgerRegister(queryClient, ledgerId);
            // Fan the aggregate result back into per-connection
            // lastSync state so each connection's inline summary
            // updates the same way it does for a per-connection
            // sync. Connections that hit a pre-flight failure
            // (failureCode set) are skipped — their status pill
            // already reflects the post-sync state.
            setLastSync((prior) => {
                const next = new Map(prior);
                for (const entry of aggregate.connections) {
                    if (entry.result) next.set(entry.connectionId, entry.result);
                    queryClient.invalidateQueries({
                        queryKey: ['sync-runs', ledgerId, entry.connectionId],
                    });
                    // Slice 2c.4: same directory invalidation as the
                    // per-connection sync — every connection in the
                    // aggregate just upserted its feed_connection_accounts
                    // rows, so the unified accounts panel must re-fetch.
                    queryClient.invalidateQueries({
                        queryKey: ['feed-connection-accounts', ledgerId, entry.connectionId],
                    });
                }
                return next;
            });
        },
    });

    // Per-row in-flight tracking (slice 2c.2). The mutation is
    // shared across rows but `mappingInFlight` records which
    // simpleFinAccountId is currently being PATCHed, so only that
    // row's Map button + dropdown disable. Map clicks on other rows
    // queue independently. The mutation no longer auto-syncs on
    // success — concurrent syncs against the same connection were
    // the race source. Server-side enforcement layered on top via
    // the UNIQUE partial index from migration 040; the SPA changes
    // here are UX clarity only (project memory:
    // feedback_server_side_concurrency).
    const [mappingInFlight, setMappingInFlight] = useState<Set<string>>(
        () => new Set(),
    );

    const mapMutation = useMutation({
        mutationFn: (args: {
            accountId: string;
            connectionId: string;
            simpleFinAccountId: string;
        }) =>
            mapAccountToFeed(ledgerId, args.accountId, {
                feedConnectionId: args.connectionId,
                simpleFinAccountId: args.simpleFinAccountId,
            }),
        onMutate: (args) => {
            setMappingInFlight((s) => new Set(s).add(args.simpleFinAccountId));
        },
        onSettled: (_data, _err, args) => {
            setMappingInFlight((s) => {
                const next = new Set(s);
                next.delete(args.simpleFinAccountId);
                return next;
            });
        },
        onSuccess: (_, args) => {
            // Slice 2c.4: the unified accounts list is the source of
            // truth; invalidate it + the accounts list so both
            // re-fetch and the per-row binding flips in place.
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({
                queryKey: ['feed-connection-accounts', ledgerId, args.connectionId],
            });
        },
    });

    // Slice 2c.4: unmap (DELETE feed-mapping) shares the same
    // per-row in-flight tracking as map. The simpleFinAccountId is
    // the row identity in the UI; we look up the bound Coffer
    // account by it and DELETE its mapping. Optimistic via the
    // same invalidations as map.
    const unmapMutation = useMutation({
        mutationFn: (args: {
            connectionId: string;
            ledgerAccountId: string;
            simpleFinAccountId: string;
        }) => unbindAccountFromFeed(ledgerId, args.ledgerAccountId),
        onMutate: (args) => {
            setMappingInFlight((s) => new Set(s).add(args.simpleFinAccountId));
        },
        onSettled: (_data, _err, args) => {
            setMappingInFlight((s) => {
                const next = new Set(s);
                next.delete(args.simpleFinAccountId);
                return next;
            });
        },
        onSuccess: (_, args) => {
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({
                queryKey: ['feed-connection-accounts', ledgerId, args.connectionId],
            });
        },
    });

    // Slice 2c.5: user-resettable per-account sync watermark. The
    // PATCH writes accounts.last_simplefin_sync_at; the next sync
    // against this account's connection asks SimpleFIN for
    // transactions from (this − 7d) forward. Null clears the
    // watermark — next sync requests the full 90-day window.
    const setSyncFromMutation = useMutation({
        mutationFn: (args: {
            connectionId: string;
            ledgerAccountId: string;
            syncFromDate: string | null;
        }) =>
            setAccountSyncFromDate(ledgerId, args.ledgerAccountId, {
                syncFromDate: args.syncFromDate,
            }),
        onSuccess: (_, args) => {
            queryClient.invalidateQueries({
                queryKey: ['feed-connection-accounts', ledgerId, args.connectionId],
            });
        },
    });

    const [setupToken, setSetupToken] = useState('');
    const [pendingDelete, setPendingDelete] =
        useState<FeedConnectionSummary | null>(null);
    // Slice 2c.3: keyed by connectionId so the per-connection sync,
    // per-account sync, AND sync-all flows all populate the same
    // inline-summary surface independently.
    const [lastSync, setLastSync] = useState<ReadonlyMap<string, SyncResultDto>>(
        () => new Map(),
    );

    function handleConnect() {
        const trimmed = setupToken.trim();
        if (trimmed.length === 0) return;
        createMutation.mutate(trimmed, {
            onSuccess: () => setSetupToken(''),
        });
    }

    const connections = connectionsQuery.data ?? [];
    // Accounts that are real (not categories, not system, active)
    // and not yet bound to ANY feed connection — the candidates the
    // mapping wizard's dropdown offers. Mig 106 collapsed the old
    // is_hidden flag into is_active; inactive accounts shouldn't
    // surface as map candidates.
    const mappableAccounts = useMemo(
        () => (accountsQuery.data ?? [])
            .filter((a) =>
                a.isActive
                && !a.isSystem
                && a.accountType !== 'category'
                // Slice 2c.2: hide accounts already bound to a
                // SimpleFIN connection so the user can't double-map.
                && a.feedConnectionId === null),
        [accountsQuery.data],
    );

    return (
        <>
            <header className="mb-5 flex items-start justify-between gap-4">
                <div className="min-w-0 flex-1">
                    <h2 className="text-base font-semibold">Bank feeds</h2>
                    <p className="mt-1 text-sm text-text-muted">
                        SimpleFIN connections for this ledger. Generate a
                        setup token at{' '}
                        <a
                            href="https://bridge.simplefin.org/"
                            target="_blank"
                            rel="noreferrer"
                            className="font-medium text-accent hover:underline"
                        >
                            bridge.simplefin.org
                        </a>{' '}
                        and paste it below to link an institution.
                    </p>
                </div>
                {connections.length > 0 ? (
                    <Button
                        type="button"
                        variant="secondary"
                        className="shrink-0"
                        onClick={() => syncAllMutation.mutate()}
                        disabled={syncAllMutation.isPending}
                    >
                        {syncAllMutation.isPending ? 'Syncing all…' : 'Sync all'}
                    </Button>
                ) : null}
            </header>
            {syncAllMutation.isError ? (
                <p
                    role="alert"
                    className="mb-3 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-xs text-state-danger"
                >
                    {errorMessage(syncAllMutation.error, 'Something went wrong.')}
                </p>
            ) : null}
            {syncAllMutation.data ? (
                <SyncAllSummary aggregate={syncAllMutation.data} />
            ) : null}

            <Panel className="mb-6">
                <PanelHead>
                    <span className="text-sm font-semibold">
                        Connect a new institution
                    </span>
                </PanelHead>
                <PanelBody>
                    <label
                        htmlFor="simplefin-setup-token"
                        className="mb-1 block text-xs font-medium text-text-muted"
                    >
                        SimpleFIN setup token
                    </label>
                    <div className="flex gap-2">
                        <Input
                            id="simplefin-setup-token"
                            autoComplete="off"
                            spellCheck={false}
                            value={setupToken}
                            onChange={(e) => setSetupToken(e.target.value)}
                            onKeyDown={(e) => {
                                if (e.key === 'Enter') {
                                    e.preventDefault();
                                    handleConnect();
                                }
                            }}
                            disabled={createMutation.isPending}
                            placeholder="Paste the base64 token from simplefin.org/setup…"
                            className="font-mono text-xs"
                        />
                        <Button
                            type="button"
                            variant="primary"
                            onClick={handleConnect}
                            disabled={
                                setupToken.trim().length === 0 ||
                                createMutation.isPending
                            }
                        >
                            {createMutation.isPending ? 'Connecting…' : 'Connect'}
                        </Button>
                    </div>
                    {createMutation.isError ? (
                        <p
                            role="alert"
                            className="mt-2 text-xs text-state-danger"
                        >
                            {errorMessage(createMutation.error, 'Something went wrong.')}
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            <Panel>
                <PanelHead>
                    <span className="text-sm font-semibold">
                        {connections.length === 0
                            ? 'No connected institutions yet'
                            : `Connected (${connections.length})`}
                    </span>
                </PanelHead>
                <PanelBody>
                    {connectionsQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : connectionsQuery.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(connectionsQuery.error, 'Something went wrong.')}
                        </p>
                    ) : connections.length === 0 ? (
                        <p className="text-sm text-text-subtle">
                            Connect an institution above to start syncing
                            transactions. Existing transactions you've
                            already imported from Moneydance will be
                            matched automatically when the first sync
                            runs.
                        </p>
                    ) : (
                        <ul className="divide-y divide-border">
                            {connections.map((c) => (
                                <ConnectionRow
                                    key={c.id}
                                    connection={c}
                                    mappableAccounts={mappableAccounts}
                                    isSyncing={
                                        syncMutation.isPending &&
                                        syncMutation.variables === c.id
                                    }
                                    isDeleting={
                                        deleteMutation.isPending &&
                                        deleteMutation.variables === c.id
                                    }
                                    lastSync={lastSync.get(c.id) ?? null}
                                    syncError={
                                        syncMutation.isError &&
                                        syncMutation.variables === c.id
                                            ? errorMessage(syncMutation.error, 'Something went wrong.')
                                            : null
                                    }
                                    onSync={() => {
                                        setLastSync((prior) => {
                                            const next = new Map(prior);
                                            next.delete(c.id);
                                            return next;
                                        });
                                        syncMutation.mutate(c.id);
                                    }}
                                    onDelete={() => setPendingDelete(c)}
                                    onMap={(accountId, simpleFinAccountId) =>
                                        mapMutation.mutate({
                                            accountId,
                                            connectionId: c.id,
                                            simpleFinAccountId,
                                        })
                                    }
                                    onUnmap={(ledgerAccountId, simpleFinAccountId) =>
                                        unmapMutation.mutate({
                                            connectionId: c.id,
                                            ledgerAccountId,
                                            simpleFinAccountId,
                                        })
                                    }
                                    onSetSyncFrom={(ledgerAccountId, syncFromDate) =>
                                        setSyncFromMutation.mutate({
                                            connectionId: c.id,
                                            ledgerAccountId,
                                            syncFromDate,
                                        })
                                    }
                                    mappingInFlight={mappingInFlight}
                                />
                            ))}
                        </ul>
                    )}
                </PanelBody>
            </Panel>

            <ConfirmDialog
                open={pendingDelete !== null}
                variant="danger"
                title={`Disconnect ${pendingDelete?.institutionName ?? 'SimpleFIN'}?`}
                body="The connection will be removed and no further syncs will run. Already-imported transactions stay; you can reconnect later."
                confirmLabel="Disconnect"
                onCancel={() => setPendingDelete(null)}
                onConfirm={() => {
                    if (pendingDelete === null) return;
                    deleteMutation.mutate(pendingDelete.id, {
                        onSettled: () => setPendingDelete(null),
                    });
                }}
                confirmDisabled={deleteMutation.isPending}
            />
        </>
    );
}

// --------------------------------------------------------------------
// One connection row + (optional) sync-result panel + mapping wizard
// --------------------------------------------------------------------

function ConnectionRow({
    connection,
    mappableAccounts,
    isSyncing,
    isDeleting,
    lastSync,
    syncError,
    mappingInFlight,
    onSync,
    onDelete,
    onMap,
    onUnmap,
    onSetSyncFrom,
}: {
    connection: FeedConnectionSummary;
    mappableAccounts: readonly AccountSummary[];
    isSyncing: boolean;
    isDeleting: boolean;
    lastSync: SyncResultDto | null;
    syncError: string | null;
    mappingInFlight: ReadonlySet<string>;
    onSync: () => void;
    onDelete: () => void;
    onMap: (accountId: string, simpleFinAccountId: string) => void;
    onUnmap: (ledgerAccountId: string, simpleFinAccountId: string) => void;
    onSetSyncFrom: (ledgerAccountId: string, syncFromDate: string | null) => void;
}) {
    return (
        <li className="py-2">
            <div className="flex items-center gap-3">
                <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">
                        {connection.institutionName ?? 'SimpleFIN'}
                    </p>
                    <p className="text-[0.6875rem] text-text-subtle">
                        {connection.lastSyncedAt
                            ? `Last synced ${formatRelative(connection.lastSyncedAt)}`
                            : 'Not synced yet'}
                    </p>
                </div>
                <span
                    className={
                        'rounded px-1.5 py-0.5 text-[0.625rem] font-medium uppercase tracking-wider ' +
                        statusClass(connection.status)
                    }
                >
                    {connection.status.replace(/_/g, ' ')}
                </span>
                <IconButton
                    aria-label="Sync now"
                    title="Sync now"
                    onClick={onSync}
                    disabled={isSyncing || isDeleting}
                >
                    <RefreshCw
                        className={
                            'h-3.5 w-3.5 ' + (isSyncing ? 'animate-spin' : '')
                        }
                        aria-hidden
                    />
                </IconButton>
                <IconButton
                    aria-label="Disconnect"
                    title="Disconnect"
                    onClick={onDelete}
                    disabled={isDeleting}
                >
                    <Trash2 className="h-3.5 w-3.5" aria-hidden />
                </IconButton>
            </div>

            {syncError ? (
                <p
                    role="alert"
                    className="mt-2 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-[0.6875rem] text-state-danger"
                >
                    {syncError}
                </p>
            ) : null}

            {lastSync ? <SyncSummary result={lastSync} /> : null}

            <ConnectionAccountsList
                ledgerId={connection.ledgerId}
                connectionId={connection.id}
                mappableAccounts={mappableAccounts}
                mappingInFlight={mappingInFlight}
                onMap={onMap}
                onUnmap={onUnmap}
                onSetSyncFrom={onSetSyncFrom}
            />

            <SyncActivityPanel
                ledgerId={connection.ledgerId}
                connectionId={connection.id}
            />
        </li>
    );
}

function SyncSummary({
    result,
}: {
    result: SyncResultDto;
}) {
    return (
        <div className="mt-2 space-y-2 rounded border border-border/60 bg-surface-muted/30 p-2">
            {result.connectionStatus === 'needs_reauth' ? (
                <div
                    role="alert"
                    className="rounded border border-state-warning/40 bg-state-warning-soft p-2 text-[0.6875rem] text-state-warning"
                >
                    <p className="font-medium">Re-connect required.</p>
                    <p className="mt-1">
                        SimpleFIN rejected the stored access URL (403).
                        Disconnect this institution and reconnect with a
                        fresh setup token from{' '}
                        <a
                            href="https://bridge.simplefin.org/"
                            target="_blank"
                            rel="noreferrer"
                            className="font-medium underline"
                        >
                            bridge.simplefin.org
                        </a>{' '}
                        to resume syncing.
                    </p>
                </div>
            ) : (
                <p className="text-[0.6875rem] text-text-muted">
                    Synced <strong>{result.accountsDiscovered}</strong>{' '}
                    {result.accountsDiscovered === 1 ? 'account' : 'accounts'} —{' '}
                    <strong>{result.transactionsForReview}</strong> new
                    transaction
                    {result.transactionsForReview === 1 ? '' : 's'} for review
                    {result.transactionsStillPending > 0 ? (
                        <>
                            {', '}
                            <strong>{result.transactionsStillPending}</strong>{' '}
                            still pending at the bank
                        </>
                    ) : null}
                    , <strong>{result.alreadyKnown}</strong> already known.
                </p>
            )}
            {result.errors.length > 0 ? (
                <FeedErrorList errors={result.errors} />
            ) : null}
        </div>
    );
}

function SyncAllSummary({ aggregate }: { aggregate: SyncAllResultDto }) {
    // Slice 2c.3 ledger-wide sync summary. Aggregates counters
    // across all connections + flags whether any connection had
    // a partial/needs_reauth/pre-flight failure so the SPA can
    // direct the user to the affected row's inline detail.
    const completed = aggregate.connections.filter((c) => c.result !== null).length;
    const skipped = aggregate.connections.length - completed;
    const totalForReview = aggregate.connections.reduce(
        (sum, c) => sum + (c.result?.transactionsForReview ?? 0),
        0,
    );
    const totalStillPending = aggregate.connections.reduce(
        (sum, c) => sum + (c.result?.transactionsStillPending ?? 0),
        0,
    );
    return (
        <div
            className={
                'mb-4 rounded border p-3 text-sm ' +
                (aggregate.hadAnyFailure
                    ? 'border-state-warning/40 bg-state-warning-soft text-state-warning'
                    : 'border-state-success/40 bg-state-success-soft text-state-success')
            }
        >
            <p className="font-medium">
                Synced <strong>{completed}</strong>{' '}
                {completed === 1 ? 'connection' : 'connections'}
                {skipped > 0 ? ` (${skipped} skipped)` : ''} —{' '}
                <strong>{totalForReview}</strong> new for review
                {totalStillPending > 0 ? (
                    <>
                        {', '}
                        <strong>{totalStillPending}</strong> still pending at the bank
                    </>
                ) : null}
                .
            </p>
            {aggregate.hadAnyFailure ? (
                <p className="mt-1 text-xs">
                    One or more connections need attention. Expand the
                    affected institutions below for details.
                </p>
            ) : null}
        </div>
    );
}

function FeedErrorList({ errors }: { errors: readonly SyncErrorDto[] }) {
    return (
        <div className="rounded border border-state-danger/40 bg-state-danger-soft p-2">
            <p className="mb-1 text-[0.6875rem] font-medium text-state-danger">
                SimpleFIN reported {errors.length}{' '}
                {errors.length === 1 ? 'problem' : 'problems'}:
            </p>
            <ul className="space-y-0.5 text-[0.6875rem] text-state-danger">
                {errors.map((e, idx) => (
                    <li key={`${e.code}-${idx}`}>
                        <span className="font-mono">{e.code}</span> — {e.message}
                    </li>
                ))}
            </ul>
        </div>
    );
}

// ---------------------------------------------------------------------------
// Unified per-connection accounts list (slice 2c.4)
// ---------------------------------------------------------------------------
// MD+ concept-parity (per ADR-0021): show ALL bank-side accounts in one
// list — mapped + unmapped together — with the binding state surfaced
// per-row. Picking from the dropdown re-binds; clicking Unmap clears.
// Backed by GET /feed-connections/{cid}/accounts (independent of any
// recent sync) so the list is visible the moment the user opens the
// page, not gated behind a fresh sync response.

function ConnectionAccountsList({
    ledgerId,
    connectionId,
    mappableAccounts,
    mappingInFlight,
    onMap,
    onUnmap,
    onSetSyncFrom,
}: {
    ledgerId: string;
    connectionId: string;
    mappableAccounts: readonly AccountSummary[];
    mappingInFlight: ReadonlySet<string>;
    onMap: (accountId: string, simpleFinAccountId: string) => void;
    onUnmap: (ledgerAccountId: string, simpleFinAccountId: string) => void;
    onSetSyncFrom: (ledgerAccountId: string, syncFromDate: string | null) => void;
}) {
    const accountsQuery = useQuery({
        queryKey: ['feed-connection-accounts', ledgerId, connectionId],
        queryFn: () => fetchFeedConnectionAccounts(ledgerId, connectionId),
    });

    if (accountsQuery.isPending) {
        return (
            <p className="mt-2 text-[0.6875rem] text-text-subtle">
                Loading accounts…
            </p>
        );
    }
    if (accountsQuery.isError) {
        return (
            <p
                role="alert"
                className="mt-2 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-[0.6875rem] text-state-danger"
            >
                {errorMessage(accountsQuery.error, 'Something went wrong.')}
            </p>
        );
    }
    const accounts = accountsQuery.data ?? [];
    if (accounts.length === 0) {
        return (
            <p className="mt-2 text-[0.6875rem] text-text-subtle">
                No accounts yet on this connection — hit Sync to populate.
            </p>
        );
    }
    return (
        <ul className="mt-2 divide-y divide-border rounded border border-border/60">
            {accounts.map((a) => (
                <ConnectionAccountRow
                    key={a.simpleFinAccountId}
                    account={a}
                    mappableAccounts={mappableAccounts}
                    mappingInFlight={mappingInFlight}
                    onMap={onMap}
                    onUnmap={onUnmap}
                    onSetSyncFrom={onSetSyncFrom}
                />
            ))}
        </ul>
    );
}

function ConnectionAccountRow({
    account,
    mappableAccounts,
    mappingInFlight,
    onMap,
    onUnmap,
    onSetSyncFrom,
}: {
    account: FeedConnectionAccountDto;
    mappableAccounts: readonly AccountSummary[];
    mappingInFlight: ReadonlySet<string>;
    onMap: (accountId: string, simpleFinAccountId: string) => void;
    onUnmap: (ledgerAccountId: string, simpleFinAccountId: string) => void;
    onSetSyncFrom: (ledgerAccountId: string, syncFromDate: string | null) => void;
}) {
    const inFlight = mappingInFlight.has(account.simpleFinAccountId);
    const bound = account.boundLedgerAccountId !== null;
    const [pickerValue, setPickerValue] = useState<string>('');
    const [syncFromOpen, setSyncFromOpen] = useState(false);

    // The picker shows every unbound Coffer account PLUS the currently-
    // bound one (so the row's own binding is visible even though
    // global `mappableAccounts` filters bound rows out for other rows
    // to prevent double-map).
    const options = useMemo(() => {
        if (bound) {
            // Show ONLY the current binding when bound — change via
            // unmap-then-remap to keep the model simple. Re-binding
            // in-place is a 2-step from the user POV.
            return [] as readonly AccountSummary[];
        }
        return mappableAccounts;
    }, [bound, mappableAccounts]);

    return (
        <li className="px-2 py-1.5 text-xs">
            <div className="flex flex-wrap items-center gap-2">
                <span className="min-w-0 flex-1 truncate">
                    <span className="font-medium">{account.name}</span>
                    {account.orgName ? (
                        <span className="text-text-subtle">
                            {' '}· {account.orgName}
                        </span>
                    ) : null}
                </span>
                {bound ? (
                    <>
                        <span className="text-text-muted">
                            →{' '}
                            <span className="font-medium text-text">
                                {account.boundLedgerAccountName ?? '(unknown)'}
                            </span>
                        </span>
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={() => setSyncFromOpen((v) => !v)}
                            disabled={inFlight}
                        >
                            Sync from…
                        </Button>
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={() =>
                                onUnmap(account.boundLedgerAccountId!, account.simpleFinAccountId)
                            }
                            disabled={inFlight}
                        >
                            {inFlight ? 'Unmapping…' : 'Unmap'}
                        </Button>
                    </>
                ) : (
                    <>
                        <select
                            value={pickerValue}
                            onChange={(e) => setPickerValue(e.target.value)}
                            disabled={inFlight}
                            className="h-7 rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        >
                            <option value="">Pick a Coffer account…</option>
                            {options.map((a) => (
                                <option key={a.id} value={a.id}>
                                    {a.name}
                                </option>
                            ))}
                        </select>
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={() => {
                                if (pickerValue.length === 0) return;
                                onMap(pickerValue, account.simpleFinAccountId);
                            }}
                            disabled={pickerValue.length === 0 || inFlight}
                        >
                            {inFlight ? 'Mapping…' : 'Map'}
                        </Button>
                    </>
                )}
            </div>

            {bound && syncFromOpen ? (
                <SyncFromEditor
                    currentSyncFrom={account.boundLedgerAccountSyncFrom}
                    onClose={() => setSyncFromOpen(false)}
                    onApply={(syncFromDate) => {
                        onSetSyncFrom(account.boundLedgerAccountId!, syncFromDate);
                        setSyncFromOpen(false);
                    }}
                />
            ) : null}
        </li>
    );
}

// ---------------------------------------------------------------------------
// Inline "Sync from…" editor (slice 2c.5)
// ---------------------------------------------------------------------------
// Lets the user reset the per-account SimpleFIN sync watermark. Two
// affordances:
//   * Pick an explicit date → next sync asks SimpleFIN for transactions
//     from that date forward (server adds the standard 7-day overlap).
//   * Clear → next sync asks for the full 90-day window.
//
// The applied watermark is just a hint; SimpleFIN itself caps history
// at 90 days, so dates older than that are capped at request time.

function SyncFromEditor({
    currentSyncFrom,
    onClose,
    onApply,
}: {
    currentSyncFrom: string | null;
    onClose: () => void;
    onApply: (syncFromDate: string | null) => void;
}) {
    // Default to 90 days ago — the widest window SimpleFIN exposes.
    // If we have a current watermark, prefer that as the starting
    // value so the user sees what's currently in effect.
    const defaultDate = useMemo(() => {
        if (currentSyncFrom) return currentSyncFrom.slice(0, 10);
        const d = new Date();
        d.setUTCDate(d.getUTCDate() - 90);
        return d.toISOString().slice(0, 10);
    }, [currentSyncFrom]);
    const [draft, setDraft] = useState(defaultDate);
    const todayIso = new Date().toISOString().slice(0, 10);

    function handleApply() {
        if (draft.length === 0) return;
        // Treat the date as UTC midnight so the server stores a
        // consistent value regardless of the browser's timezone.
        onApply(`${draft}T00:00:00Z`);
    }

    return (
        <div className="mt-2 rounded border border-border/60 bg-surface-muted/30 p-2 text-[0.6875rem]">
            <p className="mb-1 font-medium text-text">Sync this account from…</p>
            <p className="mb-2 text-text-subtle">
                Next sync will request transactions from this date forward.
                SimpleFIN caps history at 90 days — earlier dates will be
                capped server-side.
                {currentSyncFrom ? (
                    <>
                        {' '}Current: <strong>{currentSyncFrom.slice(0, 10)}</strong>.
                    </>
                ) : (
                    <> Currently: full 90-day window.</>
                )}
            </p>
            <div className="flex flex-wrap items-center gap-2">
                <input
                    type="date"
                    value={draft}
                    max={todayIso}
                    onChange={(e) => setDraft(e.target.value)}
                    className="h-7 rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                />
                <Button type="button" variant="secondary" size="sm" onClick={onClose}>
                    Cancel
                </Button>
                <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    onClick={() => onApply(null)}
                    title="Clear the watermark — next sync requests the full 90 days."
                >
                    Reset to 90 days
                </Button>
                <Button
                    type="button"
                    variant="primary"
                    size="sm"
                    onClick={handleApply}
                    disabled={draft.length === 0}
                >
                    Apply
                </Button>
            </div>
        </div>
    );
}

function statusClass(status: string): string {
    switch (status) {
        case 'active':
            return 'bg-state-success-soft text-state-success';
        case 'needs_reauth':
            return 'bg-state-warning-soft text-state-warning';
        case 'error':
            return 'bg-state-danger-soft text-state-danger';
        case 'disconnected':
        default:
            return 'bg-surface-muted text-text-subtle';
    }
}

function formatRelative(iso: string): string {
    const then = new Date(iso).getTime();
    const now = Date.now();
    const diffSec = Math.max(1, Math.round((now - then) / 1000));
    if (diffSec < 60) return `${diffSec}s ago`;
    const diffMin = Math.round(diffSec / 60);
    if (diffMin < 60) return `${diffMin}m ago`;
    const diffHr = Math.round(diffMin / 60);
    if (diffHr < 48) return `${diffHr}h ago`;
    const diffDay = Math.round(diffHr / 24);
    return `${diffDay}d ago`;
}

// ---------------------------------------------------------------------------
// Sync activity panel (slice 2c.1) — per-connection collapsed-by-default
// strip showing the most recent sync_runs entries. Click a row to expand
// the per-run errors + promotions detail. Fetches are gated on the
// `expanded` toggle so we don't burn a round-trip on every page render.
// ---------------------------------------------------------------------------

function SyncActivityPanel({
    ledgerId,
    connectionId,
}: {
    ledgerId: string;
    connectionId: string;
}) {
    const [expanded, setExpanded] = useState(false);
    const runsQuery = useQuery({
        queryKey: ['sync-runs', ledgerId, connectionId],
        queryFn: () => fetchSyncRuns(ledgerId, connectionId, 5),
        enabled: expanded,
    });

    return (
        <div className="mt-2 rounded border border-border/60 bg-surface-muted/20 text-[0.6875rem]">
            <button
                type="button"
                onClick={() => setExpanded((v) => !v)}
                className="flex w-full items-center gap-1.5 px-2 py-1 font-medium text-text-muted hover:text-text"
                aria-expanded={expanded}
            >
                {expanded ? (
                    <ChevronDown className="h-3 w-3" aria-hidden />
                ) : (
                    <ChevronRight className="h-3 w-3" aria-hidden />
                )}
                Sync activity
            </button>
            {expanded ? (
                <div className="border-t border-border/60 px-2 py-1.5">
                    {runsQuery.isPending ? (
                        <p className="text-text-subtle">Loading…</p>
                    ) : runsQuery.isError ? (
                        <p role="alert" className="text-state-danger">
                            {errorMessage(runsQuery.error, 'Something went wrong.')}
                        </p>
                    ) : (runsQuery.data ?? []).length === 0 ? (
                        <p className="text-text-subtle">
                            No sync activity yet for this institution.
                        </p>
                    ) : (
                        <ul className="space-y-1.5">
                            {runsQuery.data!.map((run) => (
                                <SyncRunListItem
                                    key={run.id}
                                    run={run}
                                    ledgerId={ledgerId}
                                />
                            ))}
                        </ul>
                    )}
                </div>
            ) : null}
        </div>
    );
}

function SyncRunListItem({
    run,
    ledgerId,
}: {
    run: SyncRunSummary;
    ledgerId: string;
}) {
    const [expanded, setExpanded] = useState(false);
    const hasDetail = run.errorCount > 0 || run.promotionCount > 0;
    const detailQuery = useQuery({
        queryKey: ['sync-run-detail', ledgerId, run.id],
        queryFn: () => fetchSyncRunDetail(ledgerId, run.id),
        enabled: expanded && hasDetail,
    });

    return (
        <li className="rounded border border-border/60 bg-surface px-2 py-1">
            <button
                type="button"
                onClick={() => hasDetail && setExpanded((v) => !v)}
                disabled={!hasDetail}
                className={
                    'flex w-full items-center gap-2 ' +
                    (hasDetail ? 'cursor-pointer' : 'cursor-default')
                }
                aria-expanded={hasDetail ? expanded : undefined}
            >
                <span
                    className={
                        'rounded px-1 py-0.5 text-[0.625rem] font-medium uppercase tracking-wider ' +
                        runStatusClass(run.status)
                    }
                >
                    {run.status.replace(/_/g, ' ')}
                </span>
                <span className="text-text-subtle">
                    {formatRelative(run.startedAt)}
                </span>
                <span className="flex-1 truncate text-text-muted">
                    {runHeadline(run)}
                </span>
                {hasDetail ? (
                    expanded ? (
                        <ChevronDown className="h-3 w-3" aria-hidden />
                    ) : (
                        <ChevronRight className="h-3 w-3" aria-hidden />
                    )
                ) : null}
            </button>

            {expanded && hasDetail ? (
                <div className="mt-1.5 space-y-1 border-t border-border/60 pt-1.5">
                    {detailQuery.isPending ? (
                        <p className="text-text-subtle">Loading…</p>
                    ) : detailQuery.isError ? (
                        <p role="alert" className="text-state-danger">
                            {errorMessage(detailQuery.error, 'Something went wrong.')}
                        </p>
                    ) : detailQuery.data ? (
                        <>
                            {detailQuery.data.errors.length > 0 ? (
                                <div>
                                    <p className="font-medium text-text-muted">
                                        Errors:
                                    </p>
                                    <ul className="ml-3 list-disc">
                                        {detailQuery.data.errors.map((e, idx) => (
                                            <li key={`${e.code}-${idx}`}>
                                                <span className="font-mono">
                                                    {e.code}
                                                </span>{' '}
                                                — {e.message}
                                            </li>
                                        ))}
                                    </ul>
                                </div>
                            ) : null}
                            {detailQuery.data.promotions.length > 0 ? (
                                <div>
                                    <p className="font-medium text-text-muted">
                                        Cleared at different amounts:
                                    </p>
                                    <ul className="ml-3 list-disc">
                                        {detailQuery.data.promotions.map((p) => (
                                            <li key={p.headerId}>
                                                Was <strong>{p.wasAmount.toFixed(2)}</strong>,{' '}
                                                cleared as{' '}
                                                <strong>{p.becameAmount.toFixed(2)}</strong>
                                            </li>
                                        ))}
                                    </ul>
                                </div>
                            ) : null}
                        </>
                    ) : null}
                </div>
            ) : null}
        </li>
    );
}

function runStatusClass(status: string): string {
    switch (status) {
        case 'completed':
            return 'bg-state-success-soft text-state-success';
        case 'partial':
            return 'bg-state-warning-soft text-state-warning';
        case 'needs_reauth':
            return 'bg-state-warning-soft text-state-warning';
        case 'failed':
            return 'bg-state-danger-soft text-state-danger';
        case 'running':
        default:
            return 'bg-surface-muted text-text-subtle';
    }
}

function runHeadline(run: SyncRunSummary): string {
    if (run.status === 'needs_reauth') return 'Re-connect required';
    if (run.status === 'failed') return run.errorMessage ?? 'Sync failed';
    if (run.status === 'running') return 'Sync in progress';
    const review = run.txnsInserted - run.txnsStillPending + run.txnsPromoted;
    const parts: string[] = [];
    parts.push(`${review} new`);
    if (run.txnsStillPending > 0) parts.push(`${run.txnsStillPending} pending`);
    if (run.txnsPromoted > 0) parts.push(`${run.txnsPromoted} promoted`);
    if (run.txnsAlreadyKnown > 0) parts.push(`${run.txnsAlreadyKnown} known`);
    if (run.errorCount > 0) parts.push(`${run.errorCount} error${run.errorCount === 1 ? '' : 's'}`);
    return parts.join(' · ');
}
