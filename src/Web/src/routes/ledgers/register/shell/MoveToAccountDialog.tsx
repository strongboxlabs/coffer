import { useEffect, useState } from 'react';

import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import type { AccountSummary } from '@/lib/types/account';

// Move-to-account dialog (ADR-0072 D3). Picks the destination account
// for a register selection, then hands the id back to the caller's
// bulk-move mutation. Reuses the register's global account picker
// (ADR-0043) — typeahead, grouped by type, active accounts — with an
// accounts-only eligibility, so it matches the counterparty picker in
// the transaction editor. The API enforces the remaining guards
// (self-transfer collision, split → investment) and its rejection
// message renders inline here.

export interface MoveToAccountDialogProps {
    open: boolean;
    /** Full account universe for the ledger (the picker builds parent
     *  paths from it; eligibility is applied via isEligible). */
    accounts: readonly AccountSummary[];
    /** The account the selected rows currently live on (excluded from
     *  the target list). */
    sourceAccountId: string;
    /** Number of selected transactions — shown in the heading. */
    count: number;
    /** True while the move mutation is in flight. */
    pending: boolean;
    /** Server rejection message (guard code) to surface inline, or null. */
    error: string | null;
    onConfirm: (targetAccountId: string) => void;
    onCancel: () => void;
}

export function MoveToAccountDialog({
    open,
    accounts,
    sourceAccountId,
    count,
    pending,
    error,
    onConfirm,
    onCancel,
}: MoveToAccountDialogProps) {
    const [targetId, setTargetId] = useState<string | null>(null);

    // Reset the picked target each time the dialog opens.
    useEffect(() => {
        if (open) setTargetId(null);
    }, [open]);

    return (
        <Modal open={open} onClose={onCancel} titleId="move-dialog-title">
            <div className="flex flex-col gap-4 p-6">
                <h2
                    id="move-dialog-title"
                    className="text-base font-semibold text-text"
                >
                    Move {count} transaction{count === 1 ? '' : 's'} to…
                </h2>
                {/* Accounts-only eligibility: real (non-category), active,
                    non-system, and not the source. The picker's Categories
                    tab auto-hides because no category is eligible. */}
                <AccountCategoryPicker
                    accounts={accounts}
                    isEligible={(a) =>
                        a.isActive &&
                        !a.isSystem &&
                        a.accountType !== 'category' &&
                        a.id !== sourceAccountId
                    }
                    valueId={targetId}
                    onChangeId={setTargetId}
                    label="Target account"
                    placeholder="Account…"
                    disabled={pending}
                    ariaLabel="Target account for move"
                />
                {error ? (
                    <p className="text-sm text-danger" role="alert">
                        {error}
                    </p>
                ) : null}
                <div className="flex justify-end gap-2">
                    <Button variant="ghost" onClick={onCancel} disabled={pending}>
                        Cancel
                    </Button>
                    <Button
                        onClick={() => {
                            if (targetId !== null) onConfirm(targetId);
                        }}
                        disabled={pending || targetId === null}
                    >
                        Move
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
