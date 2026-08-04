import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    deleteSnapshot,
    fetchSchedule,
    fetchSnapshots,
    saveSchedule,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import type { SnapshotSummary } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { EmptyState } from '@/components/ui/EmptyState';
import { Panel, PanelBody } from '@/components/ui/Panel';
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

    const query = useQuery({
        queryKey: ['snapshots', ledgerId],
        queryFn: () => fetchSnapshots(ledgerId),
    });

    const [createOpen, setCreateOpen] = useState(false);
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
                    Server-side checkpoints of this ledger's data. Create one
                    manually before a risky change, or schedule a daily
                    auto-snapshot below. Up to 5 per ledger.
                </p>
            </header>

            {autoPaused ? (
                <div
                    role="status"
                    className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs text-text"
                >
                    <strong>Auto-snap paused.</strong> 5 manual snapshots fill the
                    pool — delete one to resume scheduled auto-coverage.
                </div>
            ) : null}

            <div className="flex justify-end">
                <Button
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
                    hint={
                        <>
                            Click <strong>+ Create snapshot</strong> to take one,
                            or enable a daily auto-snapshot below.
                        </>
                    }
                />
            ) : (
                <ul className="space-y-2">
                    {snapshots.map((snap) => (
                        <SnapshotRow
                            key={snap.id}
                            snapshot={snap}
                            disabled={deleteMutation.isPending}
                            onRestore={() => setRestoreTarget(snap)}
                            onDelete={() => setDeleteTarget(snap)}
                        />
                    ))}
                </ul>
            )}

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

            {createOpen ? (
                <CreateSnapshotDialog
                    ledgerId={ledgerId}
                    onClose={() => setCreateOpen(false)}
                    onCreated={() => {
                        setCreateOpen(false);
                        queryClient.invalidateQueries({ queryKey: ['snapshots', ledgerId] });
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
                        // plus buckets / accounts / holdings) so a mounted register
                        // reloads, plus the snapshots list + securities catalog.
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
    snapshot, disabled, onRestore, onDelete,
}: {
    snapshot: SnapshotSummary;
    disabled: boolean;
    onRestore: () => void;
    onDelete: () => void;
}) {
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
                            {formatBytes(snapshot.contentSizeUncompressed)} · schema {snapshot.schemaVersion}
                        </p>
                    </div>
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={onRestore}
                        disabled={disabled}
                    >
                        Restore
                    </Button>
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={onDelete}
                        disabled={disabled}
                        className="text-state-danger hover:bg-state-danger-soft"
                    >
                        Delete
                    </Button>
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
            className="mt-0.5 inline-flex h-5 w-5 items-center justify-center rounded-full bg-surface-muted text-accent/80"
            aria-hidden
        >
            ◷
        </span>
    ) : (
        <span
            title="Manual snapshot"
            className="mt-0.5 inline-flex h-5 w-5 items-center justify-center rounded-full bg-accent-soft text-accent"
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

function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
