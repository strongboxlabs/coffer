import { formatBytes } from '@/lib/format';
import { useCallback, useMemo, useRef, useState, type Ref } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import {
    deleteSnapshot,
    fetchSchedule,
    fetchSnapshots,
    saveSchedule,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import type { SnapshotSummary } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { EmptyState } from '@/components/ui/EmptyState';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import { ScheduleControl } from '@/components/ScheduleControl';

import { CreateSnapshotDialog } from './components/CreateSnapshotDialog';
import { RestoreSnapshotDialog } from './components/RestoreSnapshotDialog';

/**
 * Snapshots panel (ADR-0037 slice 2). Lists up to 5 snapshots per
 * the cap, surfaces the "auto-snap paused" banner when the pool is
 * full of manual snaps, and provides create / restore / delete
 * affordances.
 */
export function SnapshotsPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    // Creating and deleting a snapshot, and its schedule, are an owner's or an
    // editor's; restoring one replaces the ledger's data, which is the owner's alone.
    // A viewer reads the list. The API refuses each the same way.
    const { canEdit, isOwner } = useLedgerRole(ledgerId);

    const query = useQuery({
        queryKey: ['snapshots', ledgerId],
        queryFn: () => fetchSnapshots(ledgerId),
    });

    const [createOpen, setCreateOpen] = useState(false);
    // The snapshot just made, and its row's first control. A create that
    // brings the ledger to its cap disables '+ Create snapshot', which the
    // dialog gives focus back to; focus goes to the new row instead of the
    // page (ADR-0023 §L).
    const [createdId, setCreatedId] = useState<string | null>(null);
    const createdRowRef = useRef<HTMLButtonElement | null>(null);
    // A deleted snapshot takes its row's Delete with it: focus goes to the next
    // row's Delete, the previous row's, or '+ Create snapshot' (ADR-0023 §L).
    const createRef = useRef<HTMLButtonElement | null>(null);
    const snapshotIds = useMemo(() => (query.data ?? []).map((s) => s.id), [query.data]);
    const deleteControl = useRowRemovalFocus(snapshotIds, createRef);
    const [restoreTarget, setRestoreTarget] = useState<SnapshotSummary | null>(null);
    const [deleteTarget, setDeleteTarget] = useState<SnapshotSummary | null>(null);
    const [deleteError, setDeleteError] = useState<string | null>(null);

    const deleteMutation = useMutation({
        mutationFn: (snapshotId: string) => deleteSnapshot(ledgerId, snapshotId),
        onSuccess: () => {
            setDeleteError(null);
            queryClient.invalidateQueries({ queryKey: ['snapshots', ledgerId] });
        },
        onError: (err) => {
            setDeleteError(errorMessage(err, 'Delete failed.'));
        },
    });

    const snapshots = query.data ?? [];
    // Banner condition: 5 entries in the pool AND all of them are
    // manual. The scheduler logs SkippedDueToFullPool in that state
    // (slice 1); we surface it here without needing a server-side
    // notification channel.
    const autoPaused = snapshots.length === 5
        && snapshots.every((s) => s.kind === 'manual');

    return (
        <section className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Snapshots</h2>
                <p className="text-sm text-text-muted">
                    {canEdit ? (
                        <>
                            Server-side checkpoints of this ledger's data. Create one
                            before a bulk edit or an import you might want to undo, or
                            schedule a daily auto-snapshot below. Up to 5 per ledger.
                        </>
                    ) : "Server-side checkpoints of this ledger's data. Up to 5 per ledger."}
                </p>
                {/* Said plainly, because the honest version of this limit is
                    unguessable and "create one before a risky change" points
                    straight at it, while a restore refuses
                    outright when the snapshot's schema version differs from live
                    (ADR-0037 Phase 1). Upgrading the app is the riskiest change
                    there is, and it is precisely the one a snapshot cannot carry
                    you back through. A backup can. */}
                {canEdit ? (
                    <p className="text-sm text-text-muted">
                        A snapshot can only be restored on the same app version that
                        made it — upgrading the app retires the snapshots taken
                        before it. To protect against an upgrade, take a{' '}
                        <strong className="font-medium text-text">backup</strong>{' '}
                        instead.
                    </p>
                ) : null}
            </header>

            {autoPaused ? (
                <div
                    role="status"
                    className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs text-text"
                >
                    <strong>Auto-snap paused.</strong> 5 manual snapshots fill the
                    pool{canEdit ? ' — delete one to resume scheduled auto-coverage.' : '.'}
                </div>
            ) : null}

            {canEdit ? (
            <div className="flex justify-end">
                <Button
                    ref={createRef}
                    type="button"
                    variant="primary"
                    size="sm"
                    onClick={() => setCreateOpen(true)}
                    disabled={snapshots.length >= 5}
                    title={snapshots.length >= 5
                        ? 'Delete a snapshot first — this ledger has 5 already.'
                        : undefined}
                >
                    + Create snapshot
                </Button>
            </div>
            ) : null}

            {query.isError ? (
                <Panel className="border-state-danger/40 bg-state-danger-soft">
                    <PanelBody>
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(query.error, 'Could not load snapshots.')}
                        </p>
                    </PanelBody>
                </Panel>
            ) : null}

            {deleteError !== null ? (
                <p role="alert" className="text-xs text-state-danger">
                    {deleteError}
                </p>
            ) : null}

            {query.isPending ? (
                <Panel>
                    <PanelBody>
                        <p className="text-sm text-text-subtle">Loading…</p>
                    </PanelBody>
                </Panel>
            ) : snapshots.length === 0 ? (
                <EmptyState
                    message="No snapshots yet."
                    hint={canEdit ? (
                        <>
                            Click <strong>+ Create snapshot</strong> to take one,
                            or enable a daily auto-snapshot below.
                        </>
                    ) : undefined}
                />
            ) : (
                <ul className="space-y-2">
                    {snapshots.map((snap) => (
                        <SnapshotRow
                            key={snap.id}
                            snapshot={snap}
                            firstControlRef={snap.id === createdId ? createdRowRef : undefined}
                            deleteRef={deleteControl(snap.id)}
                            pending={deleteMutation.isPending}
                            canRestore={isOwner}
                            canDelete={canEdit}
                            onRestore={() => setRestoreTarget(snap)}
                            onDelete={() => setDeleteTarget(snap)}
                        />
                    ))}
                </ul>
            )}

            {canEdit ? (
                <Panel>
                    <PanelBody>
                        <ScheduleControl
                            queryKey={['schedule', ledgerId, 'snapshot']}
                            load={() => fetchSchedule(ledgerId, 'snapshot')}
                            save={(body) => saveSchedule(ledgerId, 'snapshot', body)}
                            label="Take an automatic snapshot each day"
                            note="oldest auto-snapshot rotates out at the 5 cap"
                        />
                    </PanelBody>
                </Panel>
            ) : null}

            {createOpen ? (
                <CreateSnapshotDialog
                    ledgerId={ledgerId}
                    onClose={() => setCreateOpen(false)}
                    fallbackFocusRef={createdRowRef}
                    onCreated={async (snapshot) => {
                        // The list is refetched before the dialog closes, so the
                        // render that closes it draws the new row, and at the cap
                        // the disabled trigger, and the dialog's fallback finds it.
                        setCreatedId(snapshot.id);
                        await queryClient.invalidateQueries({ queryKey: ['snapshots', ledgerId] });
                        setCreateOpen(false);
                    }}
                />
            ) : null}

            {restoreTarget !== null ? (
                <RestoreSnapshotDialog
                    ledgerId={ledgerId}
                    snapshot={restoreTarget}
                    onClose={() => setRestoreTarget(null)}
                    onRestored={() => {
                        setRestoreTarget(null);
                        // Restore replaces the ledger's data graph — refresh the
                        // register surface (rows via the ADR-0079 canonical key,
                        // plus buckets / accounts / holdings / the sidebar's
                        // folders) so a mounted register and the rail reload, plus
                        // the snapshots list + securities catalog.
                        invalidateLedgerRegister(queryClient, ledgerId);
                        queryClient.invalidateQueries({ queryKey: ['snapshots', ledgerId] });
                        queryClient.invalidateQueries({ queryKey: ['securities', ledgerId] });
                    }}
                />
            ) : null}

            <ConfirmDialog
                open={deleteTarget !== null}
                variant="danger"
                title="Delete snapshot?"
                body={
                    deleteTarget !== null
                        ? `Delete this ${deleteTarget.kind} snapshot? You can't undo this.`
                        : ''
                }
                confirmLabel="Delete"
                isConfirming={deleteMutation.isPending}
                onConfirm={() => {
                    if (deleteTarget === null) return;
                    deleteMutation.mutate(deleteTarget.id, {
                        onSettled: () => setDeleteTarget(null),
                    });
                }}
                onCancel={() => setDeleteTarget(null)}
            />
        </section>
    );
}

function SnapshotRow({
    snapshot, firstControlRef, deleteRef, pending, canRestore, canDelete, onRestore, onDelete,
}: {
    snapshot: SnapshotSummary;
    /** Given the row's first control (Restore, else Delete). */
    firstControlRef?: Ref<HTMLButtonElement>;
    /** Given the row's Delete, which takes the row away (useRowRemovalFocus). */
    deleteRef: (el: HTMLElement | null) => void;
    /** A delete is in flight: the row's buttons keep focus and refuse to act (pending.ts). */
    pending: boolean;
    /** The owner: Restore is offered where the snapshot can be restored. */
    canRestore: boolean;
    /** An owner or editor: Delete is offered. */
    canDelete: boolean;
    onRestore: () => void;
    onDelete: () => void;
}) {
    // Delete is the row's removal control, and its first control too when there
    // is no Restore.
    const deleteButtonRef = useCallback((el: HTMLButtonElement | null) => {
        deleteRef(el);
        if ((snapshot.restorable && canRestore) || firstControlRef == null) return;
        if (typeof firstControlRef === 'function') firstControlRef(el);
        else (firstControlRef as { current: HTMLButtonElement | null }).current = el;
    }, [deleteRef, firstControlRef, snapshot.restorable, canRestore]);
    return (
        <li className="rounded border border-border bg-surface px-3 py-2.5">
            <div className="flex items-start justify-between gap-3">
                <div className="flex min-w-0 flex-1 items-start gap-2.5">
                    <KindIcon kind={snapshot.kind} />
                    <div className="min-w-0 flex-1 space-y-0.5">
                        <p className="text-sm font-medium">
                            {formatDateTime(snapshot.createdAt)}
                            <span className="ml-2 text-[0.6875rem] uppercase tracking-wider text-text-muted">
                                {snapshot.kind}
                            </span>
                        </p>
                        {snapshot.description !== null ? (
                            <p className="text-sm text-text-muted">
                                "{snapshot.description}"
                            </p>
                        ) : snapshot.kind === 'auto' ? (
                            <p className="text-xs text-text-subtle">
                                System-generated
                            </p>
                        ) : null}
                        <p className="text-xs text-text-subtle">
                            {formatBytes(snapshot.contentSizeStored)} stored · schema {snapshot.schemaVersion}
                        </p>
                        {!snapshot.restorable ? (
                            <p className="text-xs text-text-muted">
                                Retired by an app upgrade — it can no longer be
                                restored.{canDelete ? ' Delete is still available.' : null}
                            </p>
                        ) : null}
                    </div>
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                    {/* Restore is OMITTED, not disabled, when the schema has moved
                        on. A disabled control still advertises an action, and the
                        row already says why in words; a greyed button would just
                        invite hovering it to find out. Offering Restore on every
                        row and letting the server refuse AFTER the confirm would
                        make a pre-upgrade snapshot, taken precisely because an
                        upgrade is risky, announce its uselessness at the moment
                        it is needed. */}
                    {snapshot.restorable && canRestore ? (
                        <Button
                            ref={firstControlRef}
                            type="button"
                            variant="ghost"
                            size="sm"
                            onClick={onRestore}
                            pending={pending}
                        >
                            Restore
                        </Button>
                    ) : null}
                    {canDelete ? (
                        <Button
                            ref={deleteButtonRef}
                            type="button"
                            variant="ghost"
                            size="sm"
                            onClick={onDelete}
                            pending={pending}
                            className="text-state-danger hover:bg-state-danger-soft"
                        >
                            Delete
                        </Button>
                    ) : null}
                </div>
            </div>
        </li>
    );
}

function KindIcon({ kind }: { kind: 'auto' | 'manual' }) {
    // Inline SVGs keep the dependency footprint tight and match
    // the rest of the SPA's icon-as-text pattern.
    return kind === 'auto' ? (
        <span
            title="Auto-snapshot (weekly)"
            className="mt-0.5 inline-flex size-icon-lg items-center justify-center rounded-full bg-surface-muted text-accent/80"
            aria-hidden
        >
            ◷
        </span>
    ) : (
        <span
            title="Manual snapshot"
            className="mt-0.5 inline-flex size-icon-lg items-center justify-center rounded-full bg-accent-soft text-accent"
            aria-hidden
        >
            ✱
        </span>
    );
}

function formatDateTime(iso: string): string {
    const d = new Date(iso);
    return new Intl.DateTimeFormat(undefined, {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
    }).format(d);
}

