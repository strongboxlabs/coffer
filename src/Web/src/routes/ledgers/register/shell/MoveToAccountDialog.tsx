import { useEffect, useState } from 'react';

import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { TwoAmountDone } from '@/components/TwoAmountDone';
import type { AccountSummary } from '@/lib/types/account';

// Move dialog (ADR-0072 D3). Picks the destination for a register
// selection, then hands the id back to the caller's bulk-move mutation.
// Reuses the register's global account picker (ADR-0043) — typeahead,
// grouped by type, active accounts. The API enforces the per-row guards
// (investment events, splits crossing between banking and investment, a
// self-transfer collision) and its rejection message renders inline here.
//
// It offers only targets on the register's own side of the CATEGORY
// boundary — categories on a category's register, money accounts (banking
// and investment alike) everywhere else. The move re-points the leg sitting
// on the register's own account, and crossing that boundary would turn a
// category leg into a money-account leg and leave the transaction with no
// category at all.
//
// It does NOT split banking from investment: a split here would be a THIRD
// copy of a rule the single-row path does not have. Banking ↔ investment is allowed (a
// mis-filed deposit, or a contribution filed against the wrong side), and
// whether a particular row may cross is a per-row question only the server
// can answer: a split may not, and an investment event may not unless it is
// a cash-only transfer. Offering the account and letting the server explain
// a refusal is right for a rule that depends on the rows, not the target.
//
// The server refuses a category crossing whatever the client sends, so this
// is not the guard — it is the half that stops the picker offering a choice
// that can only fail. On a category register it also puts the useful
// operation, recategorise, on screen.

export interface MoveToAccountDialogProps {
    open: boolean;
    /** Full account universe for the ledger (the picker builds parent
     *  paths from it; eligibility is applied via isEligible). */
    accounts: readonly AccountSummary[];
    /** The account the selected rows currently live on (excluded from
     *  the target list). */
    sourceAccountId: string;
    /** The register's own account type. Targets are filtered to its side of
     *  the category boundary, and the copy follows — a category's register
     *  moves to a category. REQUIRED, not defaulted: a page that forgot it
     *  would silently offer money accounts, which on a category register is
     *  the exact bug this prop exists to stop. */
    sourceAccountType: string;
    /** Number of selected transactions — shown in the heading. */
    count: number;
    /** True while the move mutation is in flight. */
    pending: boolean;
    /** Server rejection message (guard code) to surface inline, or null. */
    error: string | null;
    /** How many transfers the landed move left keeping two amounts (ADR-0114 D13). Above
     *  0, the dialog shows that line and Done in place of the picker. REQUIRED, like
     *  sourceAccountType: a page that forgot it would show the picker again after the
     *  move, over a selection already cleared. */
    becameTwoAmount: number;
    onConfirm: (targetAccountId: string) => void;
    onCancel: () => void;
}

/**
 * The account-level half of the server's move rule
 * (`BulkTransactionsRepository.CrossesCategoryBoundary`): a category and an
 * account are not interchangeable. The client decides what to OFFER, the
 * server decides what is legal, and a picker wider than this half just
 * produces a rejection the user cannot act on.
 */
const isCategory = (accountType: string) => accountType === 'category';

export function MoveToAccountDialog({
    open,
    accounts,
    sourceAccountId,
    sourceAccountType,
    count,
    pending,
    error,
    becameTwoAmount,
    onConfirm,
    onCancel,
}: MoveToAccountDialogProps) {
    const sourceIsCategory = isCategory(sourceAccountType);
    const [targetId, setTargetId] = useState<string | null>(null);
    // The count the title read when Move was pressed. A landed move clears the
    // selection, so `count` falls to 0 while the dialog stays open on the two-amount
    // line; the title keeps what it said.
    const [movedCount, setMovedCount] = useState(count);
    const done = becameTwoAmount > 0;
    const shownCount = done ? movedCount : count;

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
                    Move {shownCount} transaction{shownCount === 1 ? '' : 's'} to…
                </h2>
                {done ? (
                    <TwoAmountDone count={becameTwoAmount} onDone={onCancel} />
                ) : (
                    <>
                        {/* Active, non-system, not the source, same side of the
                            category boundary. The picker's other tab auto-hides,
                            because nothing in it is eligible. */}
                        <AccountCategoryPicker
                            accounts={accounts}
                            isEligible={(a) =>
                                a.isActive &&
                                !a.isSystem &&
                                isCategory(a.accountType) === sourceIsCategory &&
                                a.id !== sourceAccountId
                            }
                            valueId={targetId}
                            onChangeId={setTargetId}
                            label={sourceIsCategory ? 'Target category' : 'Target account'}
                            placeholder={sourceIsCategory ? 'Category…' : 'Account…'}
                            pending={pending}
                            ariaLabel={sourceIsCategory
                                ? 'Target category for move'
                                : 'Target account for move'}
                        />
                        {error ? (
                            <p className="text-sm text-danger" role="alert">
                                {error}
                            </p>
                        ) : null}
                        <div className="flex justify-end gap-2">
                            <Button variant="ghost" onClick={onCancel} pending={pending}>
                                Cancel
                            </Button>
                            <Button
                                onClick={() => {
                                    if (targetId === null) return;
                                    setMovedCount(count);
                                    onConfirm(targetId);
                                }}
                                disabled={targetId === null}
                                pending={pending}
                            >
                                Move
                            </Button>
                        </div>
                    </>
                )}
            </div>
        </Modal>
    );
}
