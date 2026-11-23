import { useState, type RefObject } from 'react';
import { useMutation } from '@/lib/mutation';

import { ApiError, createSnapshot } from '@/lib/api';
import type { SnapshotSummary } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Modal } from '@/components/ui/Modal';

const DESCRIPTION_MAX_LENGTH = 200;

/**
 * Manual snapshot create dialog (ADR-0037 slice 2). One field —
 * optional description — and a Create button. 422
 * `snapshot-manual-at-cap` surfaces inline (it shouldn't happen
 * because the panel disables the trigger button when count >= 5,
 * but the server gate is authoritative).
 */
export function CreateSnapshotDialog({
    ledgerId,
    onClose,
    onCreated,
    fallbackFocusRef,
}: {
    ledgerId: string;
    onClose: () => void;
    /** The snapshot was made. The create stays pending until what this
     *  returns has settled: the host refetches its list there, then closes. */
    onCreated: (snapshot: SnapshotSummary) => Promise<void> | void;
    /** Where focus goes on close when the control that opened the dialog can
     *  no longer take it (Modal): at the cap, '+ Create snapshot' is disabled
     *  by the snapshot just made. */
    fallbackFocusRef?: RefObject<HTMLElement | null>;
}) {
    const [description, setDescription] = useState('');

    const createMutation = useMutation({
        mutationFn: () =>
            createSnapshot(ledgerId, {
                description: description.trim().length === 0
                    ? null
                    : description.trim(),
            }),
        // Returned, so the create stays pending while the host refetches.
        onSuccess: (response) => onCreated(response.snapshot),
    });

    function handleSubmit(e: React.FormEvent) {
        e.preventDefault();
        if (createMutation.isPending) return;
        createMutation.mutate();
    }

    const errorCode = createMutation.error instanceof ApiError
        ? createMutation.error.code
        : undefined;

    return (
        <Modal
            open
            onClose={onClose}
            titleId="create-snapshot-title"
            className="max-w-md"
            fallbackFocusRef={fallbackFocusRef}
        >
            <form onSubmit={handleSubmit}>
                <header className="border-b border-border px-4 py-3">
                    <h2 id="create-snapshot-title" className="text-base font-semibold">Create snapshot</h2>
                </header>
                <div className="space-y-3 p-4">
                    <div className="flex flex-col gap-1 text-xs">
                        <FieldLabel htmlFor="snapshot-description">
                            Description (optional)
                        </FieldLabel>
                        <input
                            id="snapshot-description"
                            type="text"
                            value={description}
                            onChange={(e) => setDescription(e.target.value)}
                            maxLength={DESCRIPTION_MAX_LENGTH}
                            autoFocus
                            placeholder="e.g. before MD-import"
                            className="rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                        <span className="text-[0.6875rem] text-text-subtle">
                            Captures the current state of all transactions,
                            accounts, securities, and tags. Up to{' '}
                            {DESCRIPTION_MAX_LENGTH} chars.
                        </span>
                    </div>

                    {errorCode === 'snapshot-manual-at-cap' ? (
                        <p role="alert" className="text-xs text-state-danger">
                            Delete a snapshot first — this ledger has 5 already.
                        </p>
                    ) : createMutation.isError ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {(createMutation.error as Error).message}
                        </p>
                    ) : null}
                </div>
                <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                    <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={onClose}
                        pending={createMutation.isPending}
                    >
                        Cancel
                    </Button>
                    <Button
                        type="submit"
                        variant="primary"
                        size="sm"
                        pending={createMutation.isPending}
                        pendingLabel="Creating…"
                    >
                        Create
                    </Button>
                </footer>
            </form>
        </Modal>
    );
}
