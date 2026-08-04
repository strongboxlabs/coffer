import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import type { RegisterRow } from '@/lib/types';

import { singleDeleteCopy } from './registerDeleteCopy';

/** Pending-delete state shared by both registers: a single target row, a bulk
 *  selection, or nothing. Generic over the row shape so the single-delete
 *  callback receives the caller's narrowed type back. */
export type PendingDelete<R extends RegisterRow = RegisterRow> =
    | { kind: 'single'; target: R }
    | { kind: 'bulk' }
    | null;

/**
 * The ONE delete confirmation for both registers (feedback: registers unified
 * by default). A single ConfirmDialog covers single- AND bulk-delete: the
 * single variant cites the row via the shared copy resolver (hard-delete vs
 * soft-hide by `external_id`); the bulk variant counts headers and requires a
 * typed confirmation above the ADR-0024 threshold. Bank had this combined
 * dialog inline; investment used a separate single dialog + bulk dialog — this
 * collapses both onto one shared pattern.
 */
export function RegisterDeleteConfirm<R extends RegisterRow = RegisterRow>({
    pending,
    selectedCount,
    allMode,
    isConfirming,
    onConfirmSingle,
    onConfirmBulk,
    onCancel,
}: {
    pending: PendingDelete<R>;
    /** Server-resolved bulk count (footer's source of truth). */
    selectedCount: number;
    /** True when the bulk selection is 'all'-mode — adds the split-owner note. */
    allMode: boolean;
    /** The relevant delete mutation is in flight (page decides single vs bulk). */
    isConfirming: boolean;
    onConfirmSingle: (target: R) => void;
    onConfirmBulk: () => void;
    onCancel: () => void;
}) {
    const single = pending?.kind === 'single' ? pending.target : null;

    const title = single
        ? singleDeleteCopy(single).title
        : pending?.kind === 'bulk'
            ? `Delete ${selectedCount} transaction${selectedCount === 1 ? '' : 's'}?`
            : '';
    const body = single
        ? singleDeleteCopy(single).body
        : pending?.kind === 'bulk'
            ? bulkBody(allMode)
            : '';
    const confirmLabel = single ? singleDeleteCopy(single).confirmLabel : 'Delete';

    return (
        <ConfirmDialog
            open={pending !== null}
            title={title}
            body={body}
            confirmLabel={confirmLabel}
            variant="danger"
            // Typed-confirmation (ADR-0024) for a bulk delete large enough that a
            // fat-finger would be catastrophic — the user types "delete N".
            requireTypedConfirmation={
                pending?.kind === 'bulk' && selectedCount > 100
                    ? `delete ${selectedCount}`
                    : undefined
            }
            isConfirming={isConfirming}
            onConfirm={() => {
                if (pending?.kind === 'single') onConfirmSingle(pending.target);
                else if (pending?.kind === 'bulk') onConfirmBulk();
                onCancel();
            }}
            onCancel={onCancel}
        />
    );
}

function bulkBody(allMode: boolean): string {
    const base =
        'Manual entries (no source id) will be permanently deleted. Feed / import-keyed rows will be soft-hidden — they stay in the database but disappear from the register.';
    // All-mode delete acts only on transactions this account originates
    // (server-side, ADR-0036); split rows owned by another account are left
    // untouched, so call that out.
    return allMode
        ? `${base} Split rows reflecting the other side of a transaction owned by another account won't be deleted — manage those from that account.`
        : base;
}
