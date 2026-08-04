import type { ReactNode } from 'react';

import type { BankRow } from '@/lib/types';
import { Chip } from '@/components/ui/Chip';
import { TagChip } from '@/components/tags/TagChip';
import { categoryChipVariant } from '@/lib/categoryChip';
import { displayAccountPath } from '@/lib/accountPath';
import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';

/**
 * Bank-register cell renderers + grid template. Used directly by
 * <c>BankRegisterPage</c> (bank, credit card, cash, asset, liability
 * accounts — RegisterRouter dispatches investment accounts elsewhere).
 *
 * These were previously exposed through a polymorphic
 * `RegisterStrategy` dispatch; that abstraction had exactly one
 * concrete bank path on this page, so it was collapsed to direct
 * function imports. Rendering is byte-for-byte the same.
 */

// Bank uses the 8-column grid. Leading two tracks are the shared
// register row-lead (checkbox-first, then status — see
// shell/RegisterRowLead); the same first two tracks/order appear in
// INVESTMENT_REGISTER_COLS so both registers' lead columns align.
// checkbox (1.75rem) + status (2.25rem) + date (6.5rem) + check# (3.5rem)
// + payee/memo (flex) + category/tags (19.5rem) + amount (6rem) +
// balance (6.5rem).
export const BANK_COLS =
    '1.75rem 2.25rem 6.5rem 3.5rem minmax(8rem,1fr) 19.5rem 6rem 6.5rem';

/** Slot 4 — check number, mono-tabular. Empty cell when null. */
export function renderBankSlot4(txn: BankRow): ReactNode {
    return txn.checkNumber ?? '';
}

/** Slot 5 — payee (bold, with provenance icon) + memo subtitle. */
export function renderBankSlot5(txn: BankRow): ReactNode {
    // Mig 107: leading provenance icon (online / file / manual +
    // merge-winner overlay) on every row, not just split-parents.
    // The bank-register split-parent branch renders its own
    // ProvenanceIcon inline; this slot covers the single-leg
    // branch used for the bulk of rows + split-leg counter-rows.
    return (
        <>
            <span className="flex items-center gap-1.5 truncate font-medium">
                <ProvenanceIcon
                    origin={txn.origin}
                    providerKey={txn.providerKey}
                    isMergeWinner={txn.isMergeWinner}
                />
                <span className="truncate">
                    {txn.payee ?? <span className="text-text-subtle">—</span>}
                </span>
            </span>
            {txn.memo ? (
                <span className="block truncate text-[0.6875rem] text-text-muted">
                    {txn.memo}
                </span>
            ) : null}
        </>
    );
}

/** Slot 6 — counterparty (category) chip + tags chips. The chip shows
 *  the category's full parent→child path (`Food/Groceries`) when
 *  `accountPaths` is supplied; bare leaf name otherwise. */
export function renderBankSlot6(
    txn: BankRow,
    accountPaths?: ReadonlyMap<string, string>,
): ReactNode {
    const chipVariant = categoryChipVariant(
        txn.counterpartyAccountName,
        txn.counterpartyAccountType,
        txn.counterpartyAccountId,
    );
    const label = displayAccountPath(
        accountPaths, txn.counterpartyAccountId, txn.counterpartyAccountName,
    );
    return (
        <>
            {txn.counterpartyAccountName ? (
                <Chip
                    variant={chipVariant}
                    className="max-w-full truncate"
                    title={label ?? undefined}
                >
                    <span className="truncate">
                        {label}
                    </span>
                </Chip>
            ) : (
                <span className="text-text-subtle">—</span>
            )}
            {txn.tags.length > 0 ? (
                <span className="flex flex-wrap gap-1">
                    {txn.tags.map((tag) => (
                        <TagChip key={tag} name={tag} />
                    ))}
                </span>
            ) : null}
        </>
    );
}
