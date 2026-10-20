import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';

import { ApiError, restoreSnapshot } from '@/lib/api';
import type { SnapshotSummary } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';

/**
 * Restore confirmation dialog (ADR-0037 slice 2). Destructive
 * action — replaces the live ledger state with whatever the
 * snapshot captured. Two safety gates:
 *
 *   1. The checkbox the user must explicitly tick — the "Restore"
 *      button stays disabled until then.
 *   2. The server itself transactionally rolls back if anything in
 *      the restore plpgsql function throws (ADR-0037 §"Restore
 *      atomicity").
 *
 * Errors surfaced inline:
 *   - `snapshot-schema-version-mismatch` — Phase 1 refuses cross-
 *     version restore (forward-migration deferred to its own ADR).
 *   - `snapshot-payload-corrupt` — payload didn't decode (envelope
 *     format mismatch or storage corruption).
 *   - `snapshot-not-found` — race with delete or wrong ledger.
 */
export function RestoreSnapshotDialog({
    ledgerId,
    snapshot,
    onClose,
    onRestored,
}: {
    ledgerId: string;
    snapshot: SnapshotSummary;
    onClose: () => void;
    onRestored: () => void;
}) {
    const [confirmed, setConfirmed] = useState(false);

    const restoreMutation = useMutation({
        mutationFn: () => restoreSnapshot(ledgerId, snapshot.id),
    });

    const errorCode = restoreMutation.error instanceof ApiError
        ? restoreMutation.error.code
        : undefined;

    // Three view states:
    //   idle    — pre-action: warning + metadata + checkbox + Restore
    //   success — post-action: success summary + Done
    //   failure — post-action: error summary + Done (or Retry)
    // The user dismisses via Done in the post-action states — we
    // don't auto-close because a destructive action this big
    // deserves an explicit acknowledgment of what just happened.
    const phase: 'idle' | 'success' | 'failure' =
        restoreMutation.isSuccess ? 'success'
        : restoreMutation.isError ? 'failure'
        : 'idle';

    function handleClose() {
        // On success, propagate to the parent so it invalidates
        // queries + closes. On failure or pre-action, just close.
        if (phase === 'success') onRestored();
        else onClose();
    }

    return (
        <Modal
            open
            onClose={handleClose}
            titleId="restore-snapshot-title"
            className="max-w-lg"
            // No backdrop/Esc dismiss during the in-flight mutation —
            // the user might think clicking outside aborts the
            // restore, which it doesn't (the server is running
            // the transaction regardless). Once we're back at
            // a settled phase, dismiss is fine.
            dismissOnBackdrop={!restoreMutation.isPending}
            dismissOnEsc={!restoreMutation.isPending}
        >
            <div>
                <header className="border-b border-border px-4 py-3">
                    <h2 id="restore-snapshot-title" className="text-base font-semibold">
                        {phase === 'success' ? 'Restore complete'
                            : phase === 'failure' ? 'Restore failed'
                            : 'Restore from snapshot?'}
                    </h2>
                </header>

                {phase === 'idle' ? (
                    <PreActionBody
                        snapshot={snapshot}
                        confirmed={confirmed}
                        onToggleConfirmed={setConfirmed}
                        pending={restoreMutation.isPending}
                    />
                ) : phase === 'success' ? (
                    <SuccessBody snapshot={snapshot} />
                ) : (
                    <FailureBody
                        errorCode={errorCode}
                        error={restoreMutation.error}
                    />
                )}

                <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                    {phase === 'idle' ? (
                        <>
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                onClick={onClose}
                                disabled={restoreMutation.isPending}
                            >
                                Cancel
                            </Button>
                            <Button
                                type="button"
                                variant="danger"
                                size="sm"
                                onClick={() => restoreMutation.mutate()}
                                disabled={!confirmed || restoreMutation.isPending}
                            >
                                {restoreMutation.isPending ? 'Restoring…' : 'Restore'}
                            </Button>
                        </>
                    ) : phase === 'failure' ? (
                        <>
                            <Button
                                type="button"
                                variant="ghost"
                                size="sm"
                                onClick={onClose}
                            >
                                Close
                            </Button>
                            {/* Retry is allowed unless the error is a
                                permanent one (schema-mismatch, payload-
                                corrupt) — those won't fix themselves. */}
                            {errorCode !== 'snapshot-schema-version-mismatch'
                                && errorCode !== 'snapshot-payload-corrupt' ? (
                                <Button
                                    type="button"
                                    variant="primary"
                                    size="sm"
                                    onClick={() => restoreMutation.mutate()}
                                >
                                    Try again
                                </Button>
                            ) : null}
                        </>
                    ) : (
                        <Button
                            type="button"
                            variant="primary"
                            size="sm"
                            onClick={handleClose}
                            autoFocus
                        >
                            Done
                        </Button>
                    )}
                </footer>
            </div>
        </Modal>
    );
}

function PreActionBody({
    snapshot, confirmed, onToggleConfirmed, pending,
}: {
    snapshot: SnapshotSummary;
    confirmed: boolean;
    onToggleConfirmed: (next: boolean) => void;
    pending: boolean;
}) {
    return (
        <div className="space-y-3 p-4">
            <div className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs">
                <strong>⚠ This replaces your current ledger state</strong>{' '}
                with the state at the time the snapshot was taken.
                Transactions, accounts, securities, and tags added since
                this snapshot will be lost.
            </div>

            <SnapshotMetadata snapshot={snapshot} />

            <label className="flex items-center gap-2 text-xs">
                <input
                    type="checkbox"
                    checked={confirmed}
                    onChange={(e) => onToggleConfirmed(e.target.checked)}
                    disabled={pending}
                />
                <span>
                    I understand this will replace the current ledger data.
                </span>
            </label>
        </div>
    );
}

function SuccessBody({ snapshot }: { snapshot: SnapshotSummary }) {
    return (
        <div className="space-y-3 p-4">
            <div className="flex items-start gap-3 rounded border border-state-success/40 bg-state-success-soft px-3 py-2 text-sm">
                <span aria-hidden className="text-lg text-state-success">✓</span>
                <div className="space-y-1">
                    <p className="font-medium">Ledger restored from snapshot.</p>
                    <p className="text-xs text-text-muted">
                        All transactions, accounts, securities, tags, and
                        provider mappings now match the snapshot. Balances
                        were re-derived from the restored legs.
                    </p>
                </div>
            </div>
            <SnapshotMetadata snapshot={snapshot} />
            <p className="text-xs text-text-subtle">
                Navigate to a register to see the restored state.
            </p>
        </div>
    );
}

function FailureBody({
    errorCode, error,
}: {
    errorCode: string | undefined;
    error: unknown;
}) {
    const message =
        errorCode === 'snapshot-schema-version-mismatch'
            ? 'This snapshot was taken on a different schema version; restore is not supported in this release.'
        : errorCode === 'snapshot-payload-corrupt'
            ? "The snapshot's stored payload could not be decoded."
        : errorCode === 'snapshot-not-found'
            ? 'Snapshot not found — it may have been deleted in another session.'
        : (error instanceof Error ? error.message : 'Restore failed.');
    return (
        <div className="space-y-3 p-4">
            <div className="flex items-start gap-3 rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm">
                <span aria-hidden className="text-lg text-state-danger">✗</span>
                <div className="space-y-1">
                    <p className="font-medium">Restore did not complete.</p>
                    <p className="text-xs text-text-muted" role="alert">
                        {message}
                    </p>
                    <p className="text-xs text-text-subtle">
                        Your ledger data was not changed — the server rolls
                        the restore back in one transaction if any step
                        fails.
                    </p>
                </div>
            </div>
        </div>
    );
}

function SnapshotMetadata({ snapshot }: { snapshot: SnapshotSummary }) {
    return (
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
            <dt className="font-semibold text-text-muted">Taken</dt>
            <dd>
                {formatDateTime(snapshot.createdAt)}{' · '}
                <span className="uppercase tracking-wider text-text-muted">
                    {snapshot.kind}
                </span>
            </dd>
            {snapshot.description !== null ? (
                <>
                    <dt className="font-semibold text-text-muted">Note</dt>
                    <dd>"{snapshot.description}"</dd>
                </>
            ) : null}
            <dt className="font-semibold text-text-muted">Schema</dt>
            <dd className="break-all font-mono text-[0.6875rem]">
                {snapshot.schemaVersion}
            </dd>
        </dl>
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
