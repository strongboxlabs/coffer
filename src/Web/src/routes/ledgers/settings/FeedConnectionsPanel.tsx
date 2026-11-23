import { useCallback, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Pencil, RefreshCw, Trash2 } from 'lucide-react';

import {
    createFeedConnection,
    deleteFeedConnection,
    renameFeedConnection,
    fetchAccounts,
    fetchFeedConnectionAccounts,
    fetchFeedConnections,
    fetchSchedule,
    mapAccountToFeed,
    saveSchedule,
    syncAllConnections,
    syncFeedConnection,
    unbindAccountFromFeed,
    setAccountSyncFromDate,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { formatRelative } from '@/lib/ledgerOperationDisplay';
import { invalidateHomeReads, invalidateLedgerRegister } from '@/lib/registerInvalidation';
import { useLedgerRole } from '@/lib/useLedgerRole';
import type {
    AccountSummary,
    FeedConnectionAccountDto,
    FeedConnectionSummary,
    SyncAllResultDto,
    SyncErrorDto,
    SyncResultDto,
} from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { IconButton } from '@/components/ui/IconButton';
import { InlineNameInput, type InlineNameEnd } from '@/components/ui/InlineNameInput';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { pendingChoiceProps } from '@/components/ui/pending';
import { useFocusHandOff, useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import { ScheduleControl } from '@/components/ScheduleControl';

/**
 * Per-ledger SimpleFIN connection management.
 *
 * Connect + list + disconnect, Sync now, and the mapping wizard for
 * SimpleFIN accounts the user hasn't yet bound to a Coffer account.
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
    // Connecting a bank, renaming or disconnecting it and choosing what it feeds are the
    // owner's (FU-095): the connection holds credentials to their accounts at the bank. An editor
    // still syncs, and keeps the sync schedule (PUT /schedules is an owner's or an
    // editor's); a viewer reads the connections and syncs nothing. The API refuses
    // the rest, so the controls are not offered.
    const { isOwner, canEdit } = useLedgerRole(ledgerId);

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
            // The sync is a new run in Settings → Activity.
            queryClient.invalidateQueries({
                queryKey: ['ledger-operations', ledgerId],
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
                        queryKey: ['ledger-operations', ledgerId],
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
            // re-fetch and the per-row binding flips in place. The home
            // screen reads the same accounts.
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            invalidateHomeReads(queryClient, ledgerId);
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
            invalidateHomeReads(queryClient, ledgerId);
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
    // A connection made clears the setup token, which leaves Connect disabled
    // with focus on it; focus goes to the field it waits on (ADR-0023 §L).
    const setupTokenRef = useRef<HTMLInputElement>(null);
    const [pendingDelete, setPendingDelete] =
        useState<FeedConnectionSummary | null>(null);
    // A disconnected connection takes its row's Disconnect with it: focus goes to
    // the next row's Disconnect, the previous row's, or the setup token field
    // that adds one, as Connect is disabled until a token is pasted (ADR-0023 §L).
    const disconnectControl = useRowRemovalFocus(
        (connectionsQuery.data ?? []).map((c) => c.id),
        setupTokenRef,
    );
    // Slice 2c.3: keyed by connectionId so the per-connection sync,
    // per-account sync, AND sync-all flows all populate the same
    // inline-summary surface independently.
    const [lastSync, setLastSync] = useState<ReadonlyMap<string, SyncResultDto>>(
        () => new Map(),
    );

    // Enter in the setup-token field presses Connect, so the press holds it as
    // a click does, and Connect refuses it while unavailable or busy. The field
    // stays enabled while a connect is in flight, so the field it was pressed
    // in keeps focus, but it is pending (read-only): the connect's success
    // clears it, which would drop anything typed meanwhile (ADR-0023 §L).
    const connectRef = useRef<HTMLButtonElement | null>(null);
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
                        SimpleFIN connections for this ledger.
                        {/* Connecting is the owner's, so only the owner is told how. */}
                        {isOwner ? (
                            <>
                                {' '}Generate a setup token at{' '}
                                <a
                                    href="https://bridge.simplefin.org/"
                                    target="_blank"
                                    rel="noreferrer"
                                    className="font-medium text-accent hover:underline"
                                >
                                    bridge.simplefin.org
                                </a>{' '}
                                and paste it below to link an institution.
                            </>
                        ) : null}
                    </p>
                </div>
                {canEdit && connections.length > 0 ? (
                    <Button
                        type="button"
                        variant="secondary"
                        className="shrink-0"
                        onClick={() => syncAllMutation.mutate()}
                        pending={syncAllMutation.isPending}
                        pendingLabel="Syncing all…"
                    >
                        Sync all
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

            {canEdit ? (
            <Panel className="mb-6">
                <PanelHead>
                    <span className="text-sm font-semibold">Sync automatically</span>
                </PanelHead>
                <PanelBody className="space-y-2">
                    <ScheduleControl
                        queryKey={['schedule', ledgerId, 'feed-sync']}
                        load={() => fetchSchedule(ledgerId, 'feed-sync')}
                        save={(body) => saveSchedule(ledgerId, 'feed-sync', body)}
                        label="Sync every connection once a day"
                        note="skips connections that need reconnecting"
                    />
                    {/* Said, not implied. The scheduler stores an hour and a minute and
                        always advances by a day, so daily is genuinely all that can be
                        expressed — and someone expecting intra-day refreshes would
                        otherwise discover the gap by finding stale balances. */}
                    <p className="text-xs text-text-muted">
                        Once a day, at the time you pick. More frequent syncing is not
                        available yet. A connection that needs reconnecting is skipped
                        rather than retried, so it will not use up your bank&apos;s
                        patience — reconnect it here and the next run picks it up.
                    </p>
                </PanelBody>
            </Panel>
            ) : null}

            {isOwner ? (
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
                                ref={setupTokenRef}
                                id="simplefin-setup-token"
                                autoComplete="off"
                                spellCheck={false}
                                pending={createMutation.isPending}
                                value={setupToken}
                                onChange={(e) => setSetupToken(e.target.value)}
                                onKeyDown={(e) => {
                                    if (e.key === 'Enter') {
                                        e.preventDefault();
                                        connectRef.current?.click();
                                    }
                                }}
                                placeholder="Paste the base64 token from simplefin.org/setup…"
                                className="font-mono text-xs"
                            />
                            <Button
                                ref={connectRef}
                                type="button"
                                variant="primary"
                                onClick={handleConnect}
                                disabled={setupToken.trim().length === 0}
                                pending={createMutation.isPending}
                                pendingLabel="Connecting…"
                                fallbackFocusRef={setupTokenRef}
                            >
                                Connect
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
            ) : (
                <p className="mb-6 text-sm text-text-muted">
                    Only an owner of this ledger can connect or disconnect a bank, or choose
                    which accounts it feeds.
                    {/* Said only to whoever can: an editor syncs, a viewer does not. */}
                    {canEdit ? ' You can sync the connections that exist.' : null}
                </p>
            )}

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
                                    disconnectRef={disconnectControl(c.id)}
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
                                    canConfigure={isOwner}
                                    canSync={canEdit}
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
                isConfirming={deleteMutation.isPending}
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
    disconnectRef,
    onMap,
    onUnmap,
    onSetSyncFrom,
    canConfigure,
    canSync,
}: {
    connection: FeedConnectionSummary;
    /** Given the row's Disconnect, which takes the row away (useRowRemovalFocus). */
    disconnectRef: (el: HTMLElement | null) => void;
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
    /** An owner: Rename, Disconnect and the account mapping are offered. */
    canConfigure: boolean;
    /** An owner or editor: Sync now is offered. */
    canSync: boolean;
}) {
    const rename = useConnectionRename(connection);
    const [editingName, setEditingName] = useState(false);
    // Enter or Escape ends editing with focus in the field, which goes with it: focus goes
    // back to the pencil as it returns. A blur leaves focus where the reader put it.
    const focusPencilOnReturn = useRef(false);
    const pencilRef = useCallback((el: HTMLButtonElement | null) => {
        if (el !== null && focusPencilOnReturn.current) {
            focusPencilOnReturn.current = false;
            el.focus();
        }
    }, []);
    function stopEditing(how: InlineNameEnd) {
        if (how === 'key') focusPencilOnReturn.current = true;
        setEditingName(false);
    }

    return (
        <li className="py-2">
            <div className="flex items-center gap-3">
                <div className="min-w-0 flex-1">
                    {editingName ? (
                        <InlineNameInput
                            label="Connection name"
                            initial={rename.name}
                            maxLength={80}
                            className="w-full max-w-fixed-344px text-sm font-medium"
                            onCommit={(name, how) => {
                                if (name !== rename.name) rename.save(name);
                                stopEditing(how);
                            }}
                            onCancel={stopEditing}
                        />
                    ) : (
                        <div className="flex min-w-0 items-center gap-1">
                            <p className="min-w-0 truncate text-sm font-medium">
                                {rename.name}
                            </p>
                            {canConfigure ? (
                                <IconButton
                                    ref={pencilRef}
                                    size="sm"
                                    aria-label="Rename connection"
                                    title="Rename"
                                    className="shrink-0"
                                    onClick={() => setEditingName(true)}
                                    pending={rename.pending || isDeleting}
                                >
                                    <Pencil className="size-icon-xs" aria-hidden />
                                </IconButton>
                            ) : null}
                        </div>
                    )}
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
                {canSync ? (
                    <IconButton
                        aria-label="Sync now"
                        title="Sync now"
                        onClick={onSync}
                        pending={isSyncing || isDeleting}
                    >
                        <RefreshCw
                            className={
                                'size-icon-sm ' + (isSyncing ? 'animate-spin' : '')
                            }
                            aria-hidden
                        />
                    </IconButton>
                ) : null}
                {canConfigure ? (
                    <IconButton
                        ref={disconnectRef}
                        aria-label="Disconnect"
                        title="Disconnect"
                        onClick={onDelete}
                        pending={isDeleting}
                    >
                        <Trash2 className="size-icon-sm" aria-hidden />
                    </IconButton>
                ) : null}
            </div>

            {syncError ? (
                <p
                    role="alert"
                    className="mt-2 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-[0.6875rem] text-state-danger"
                >
                    {syncError}
                </p>
            ) : null}

            {rename.error !== null ? (
                <p
                    role="alert"
                    className="mt-2 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-[0.6875rem] text-state-danger"
                >
                    Could not rename the connection. {rename.error}
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
                canConfigure={canConfigure}
            />
        </li>
    );
}

/**
 * Renaming one connection: the name its row shows, a save, and the save's state.
 *
 * The new name goes into the list under ['feed-connections', ledgerId] at once. That
 * list is what this row, the Disconnect dialog, the sidebar and the Overview's Feeds &
 * checks read, so each shows the new name while the save is in flight. A refusal puts
 * the old name back there and is said under the row; the save's answer replaces the
 * connection, and the list is read again either way. There is no success notice: the
 * name is the confirmation.
 *
 * The list's own render reaches the row a moment after the press, so until it does the
 * row shows the name that was saved, and the old one never flashes back between the
 * field and the new name. That name is held against the connection it was saved from,
 * so the first newer connection the list passes in takes over from it.
 */
function useConnectionRename(connection: FeedConnectionSummary) {
    const queryClient = useQueryClient();
    const key = ['feed-connections', connection.ledgerId];
    const [sent, setSent] = useState<{ name: string; from: FeedConnectionSummary } | null>(null);
    const renameIn = (list: FeedConnectionSummary[] | undefined, name: string | null, only?: string) =>
        list?.map((c) => (c.id === connection.id && (only === undefined || c.institutionName === only)
            ? { ...c, institutionName: name }
            : c));
    const mutation = useMutation({
        mutationFn: (name: string) =>
            renameFeedConnection(connection.ledgerId, connection.id, { name }),
        onMutate: async (name) => {
            await queryClient.cancelQueries({ queryKey: key });
            const previous = queryClient.getQueryData<FeedConnectionSummary[]>(key)
                ?.find((c) => c.id === connection.id)?.institutionName ?? null;
            queryClient.setQueryData<FeedConnectionSummary[]>(key, (list) => renameIn(list, name));
            return { previous };
        },
        // Only while the list still holds this save's name: a newer read stands.
        onError: (_error, name, sentFrom) => {
            queryClient.setQueryData<FeedConnectionSummary[]>(key, (list) =>
                renameIn(list, sentFrom?.previous ?? null, name));
        },
        onSuccess: (updated) => {
            queryClient.setQueryData<FeedConnectionSummary[]>(key, (list) =>
                list?.map((c) => (c.id === updated.id ? updated : c)));
        },
        onSettled: () => queryClient.invalidateQueries({ queryKey: key }),
    });
    const held = sent !== null && sent.from === connection;
    return {
        name: held ? sent.name : (connection.institutionName ?? 'SimpleFIN'),
        pending: mutation.isPending || held,
        error: mutation.isError ? errorMessage(mutation.error, 'Something went wrong.') : null,
        save: (name: string) => {
            setSent({ name, from: connection });
            mutation.mutate(name);
        },
    };
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
    canConfigure,
}: {
    ledgerId: string;
    connectionId: string;
    mappableAccounts: readonly AccountSummary[];
    mappingInFlight: ReadonlySet<string>;
    onMap: (accountId: string, simpleFinAccountId: string) => void;
    onUnmap: (ledgerAccountId: string, simpleFinAccountId: string) => void;
    onSetSyncFrom: (ledgerAccountId: string, syncFromDate: string | null) => void;
    canConfigure: boolean;
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
                    canConfigure={canConfigure}
                />
            ))}
        </ul>
    );
}

/**
 * How long an account may go unseen in a provider pull before the row says so.
 *
 * Seven days rather than one or two: a pull happens daily at most, providers have bad
 * days, and a badge that cries wolf every time a bank has an outage gets ignored — at
 * which point it is worse than absent. A week is comfortably longer than any transient
 * failure and comfortably shorter than a person noticing missing transactions on their
 * own.
 */
const STALE_ACCOUNT_DAYS = 7;

/**
 * Says when the provider last returned this account.
 *
 * Unrendered, the failure the column exists to expose — a bank quietly dropping an
 * account from its listing — presents as transactions that simply stop arriving, which
 * is the hardest shape to notice in a register that never claims to be complete.
 *
 * DELIBERATELY NOT "this account was removed". A total connection failure returns no
 * accounts at all, so every row on that connection goes stale together and a per-row
 * message cannot tell the two apart. The connection's own status and last-synced time
 * (rendered one level up) are what disambiguate, so this says only what it knows: when
 * the provider last mentioned it.
 */
function isStale(lastSeenAt: string): boolean {
    // Outside the component on purpose. react-hooks/purity forbids reading the clock
    // in a render body, and rightly: the result changes without a state change, so
    // React cannot know when to re-render it. Same shape as formatRelative below, and
    // the same accepted limitation — a row that goes stale while the panel is open
    // updates on the next fetch or remount, not on a timer. For a seven-day threshold
    // that is not worth an interval.
    const ageMs = Date.now() - new Date(lastSeenAt).getTime();
    return Number.isFinite(ageMs) && ageMs >= STALE_ACCOUNT_DAYS * 24 * 60 * 60 * 1000;
}

function StaleAccountNote({ lastSeenAt }: { lastSeenAt: string }) {
    if (!isStale(lastSeenAt)) return null;
    return (
        <span
            role="status"
            className="ml-2 rounded bg-state-warning-soft px-1.5 py-0.5 text-[0.6875rem] text-state-warning"
        >
            Not seen since {formatRelative(lastSeenAt)}
        </span>
    );
}

function ConnectionAccountRow({
    account,
    mappableAccounts,
    mappingInFlight,
    onMap,
    onUnmap,
    onSetSyncFrom,
    canConfigure,
}: {
    account: FeedConnectionAccountDto;
    mappableAccounts: readonly AccountSummary[];
    mappingInFlight: ReadonlySet<string>;
    onMap: (accountId: string, simpleFinAccountId: string) => void;
    onUnmap: (ledgerAccountId: string, simpleFinAccountId: string) => void;
    onSetSyncFrom: (ledgerAccountId: string, syncFromDate: string | null) => void;
    canConfigure: boolean;
}) {
    const inFlight = mappingInFlight.has(account.simpleFinAccountId);
    const bound = account.boundLedgerAccountId !== null;
    const [pickerValue, setPickerValue] = useState<string>('');
    const [syncFromOpen, setSyncFromOpen] = useState(false);
    // A mapping that lands swaps the row's controls: Map gives way to Sync
    // from… and Unmap, and Unmap to the picker and Map. The first control of
    // the set that takes the pressed one's place takes its focus (ADR-0023 §L).
    const pickerEl = useRef<HTMLSelectElement | null>(null);
    const syncFromEl = useRef<HTMLButtonElement | null>(null);
    const fromMap = useFocusHandOff(() => syncFromEl.current);
    const fromUnmap = useFocusHandOff(() => pickerEl.current);

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
                    <StaleAccountNote lastSeenAt={account.lastSeenAt} />
                </span>
                {bound ? (
                    <>
                        <span className="text-text-muted">
                            →{' '}
                            <span className="font-medium text-text">
                                {account.boundLedgerAccountName ?? '(unknown)'}
                            </span>
                        </span>
                        {canConfigure ? (
                            <>
                                <Button
                                    ref={syncFromEl}
                                    type="button"
                                    variant="secondary"
                                    size="sm"
                                    onClick={() => setSyncFromOpen((v) => !v)}
                                    pending={inFlight}
                                >
                                    Sync from…
                                </Button>
                                <Button
                                    ref={fromUnmap}
                                    type="button"
                                    variant="secondary"
                                    size="sm"
                                    onClick={() =>
                                        onUnmap(account.boundLedgerAccountId!, account.simpleFinAccountId)
                                    }
                                    pending={inFlight}
                                    pendingLabel="Unmapping…"
                                >
                                    Unmap
                                </Button>
                            </>
                        ) : null}
                    </>
                ) : !canConfigure ? (
                    <span className="text-text-subtle">Not mapped</span>
                ) : (
                    <>
                        <select
                            ref={pickerEl}
                            value={pickerValue}
                            className="h-control-28px rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                            {...pendingChoiceProps<HTMLSelectElement>(inFlight, {
                                onChange: (e) => setPickerValue(e.target.value),
                            })}
                        >
                            <option value="">Pick a Coffer account…</option>
                            {options.map((a) => (
                                <option key={a.id} value={a.id}>
                                    {a.name}
                                </option>
                            ))}
                        </select>
                        <Button
                            ref={fromMap}
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={() => {
                                if (pickerValue.length === 0) return;
                                onMap(pickerValue, account.simpleFinAccountId);
                            }}
                            disabled={pickerValue.length === 0}
                            pending={inFlight}
                            pendingLabel="Mapping…"
                        >
                            Map
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
                    className="h-control-28px rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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

