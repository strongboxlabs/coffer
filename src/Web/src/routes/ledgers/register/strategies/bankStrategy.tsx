import type { ReactNode } from 'react';

import type { BankRow } from '@/lib/types';
import { Chip, type ChipProps } from '@/components/ui/Chip';
import { categoryChipVariant } from '@/lib/categoryChip';
import { displayAccountPath } from '@/lib/accountPath';
import { formatCurrency } from '@/lib/money';
import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';

import { renderRowMemo, renderRowTags } from './rowNotes';

/**
 * An account chip, and on a transfer between currencies the other leg's own amount after
 * it (ADR-0114 D14, ADR-0023's amendment, board RG-1a): a muted "·" and the figure in the
 * app's formatter, the symbol and no plus, never red, titled with the way the money moved
 * for that account. A long name truncates and the figure stays whole. With no other
 * amount (a category, a transfer in one currency) it is the chip alone, as before.
 *
 * `otherAmount` is signed, in `otherCurrency`: positive when the money arrived in the
 * other account. A zero (a mirrored zero pair that a move left across currencies) takes
 * its direction from this side's `amount`, which it opposes.
 */
export function renderAccountChip({ variant, label, chipClassName, otherAmount, otherCurrency, amount }: {
    variant: ChipProps['variant'];
    label: string | null | undefined;
    /** The chip's own width classes when it stands alone. */
    chipClassName: string;
    otherAmount: number | null | undefined;
    otherCurrency: string | null | undefined;
    /** This side's signed amount. */
    amount: number;
}): ReactNode {
    if (otherAmount === null || otherAmount === undefined || !otherCurrency) {
        return (
            <Chip variant={variant} className={chipClassName} title={label ?? undefined}>
                <span className="truncate">{label}</span>
            </Chip>
        );
    }
    const received = otherAmount > 0 || (otherAmount === 0 && amount <= 0);
    const figure = formatCurrency(otherAmount, otherCurrency);
    return (
        <span className="flex min-w-0 items-center gap-1">
            <Chip variant={variant} className="min-w-0 truncate" title={label ?? undefined}>
                <span className="truncate">{label}</span>
            </Chip>
            <span aria-hidden="true" className="shrink-0 text-text-subtle">·</span>
            <span
                className="shrink-0 font-mono tabular-nums text-[0.6875rem] text-text-muted"
                title={`${received ? 'Received in' : 'Sent from'} ${label ?? ''}: ${figure}`}
                data-other-leg
            >
                {figure}
            </span>
        </span>
    );
}

/**
 * Bank-register cell renderers + grid template. Used directly by
 * <c>BankRegisterPage</c> (bank, credit card, cash, asset, liability
 * accounts — RegisterRouter dispatches investment accounts elsewhere).
 *
 * Direct function imports rather than a polymorphic dispatch: this
 * page has exactly one concrete bank path.
 */

// Bank uses the 8-column grid. Leading two tracks are the shared
// register row-lead (checkbox-first, then status — see
// shell/RegisterRowLead); the same first two tracks/order appear in
// INVESTMENT_REGISTER_COLS so both registers' lead columns align.
// checkbox (1.75rem) + status (2.25rem) + date (6.5rem) + check# (6rem)
// + payee/memo (flex) + category/tags (19.5rem) + amount (6rem) +
// balance (6.5rem).
//
// The check# track is 6rem, not the 3.5rem a check number needs, because a
// read-only row's badge sits in it above the number: "↗ Investment"
// renders about 87px wide, and a track narrower than the badge would cut it.
export const BANK_COLS =
    '1.75rem 2.25rem 6.5rem 6rem minmax(8rem,1fr) 19.5rem 6rem 6.5rem';

/** Slot 4 — check number, mono-tabular. Empty cell when null. A read-only
 *  row's badge sits above it (bankRowStrategy). */
export function renderBankSlot4(txn: BankRow): ReactNode {
    return txn.checkNumber ?? '';
}

/** Slot 5 — payee (bold, with provenance icon) + memo subtitle. The memo is
 *  the one this register shows, with the inherited marker when it is the
 *  other side's. */
export function renderBankSlot5(
    txn: BankRow,
    accountPaths?: ReadonlyMap<string, string>,
): ReactNode {
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
            {renderRowMemo(txn, accountPaths)}
        </>
    );
}

/** Slot 6 — counterparty (category) chip + tags chips. The chip shows
 *  the category's full parent→child path (`Food/Groceries`) when
 *  `accountPaths` is supplied; bare leaf name otherwise. */
export function renderBankSlot6(
    txn: BankRow,
    accountPaths: ReadonlyMap<string, string> | undefined,
    rollupRootId: string | null | undefined,
    /** The ledger's system category ids: their chip is neutral. */
    systemCategoryIds: ReadonlySet<string>,
): ReactNode {
    const isSystem = (id: string | null) => id !== null && systemCategoryIds.has(id);
    // Subtree rollup only: the row's OWN category, when it isn't the one in
    // the breadcrumb. Full parent/child path, like every other category shown
    // outside a tree. Leads the cell because it answers "what is this row"
    // before the counterparty answers "against what".
    const ownPath = rollupRootId != null && txn.accountId !== rollupRootId
        ? displayAccountPath(accountPaths, txn.accountId, null)
        : null;
    const chipVariant = categoryChipVariant(
        txn.counterpartyAccountName,
        txn.counterpartyAccountType,
        txn.counterpartyAccountId,
        isSystem(txn.counterpartyAccountId),
    );
    const label = displayAccountPath(
        accountPaths, txn.counterpartyAccountId, txn.counterpartyAccountName,
    );
    return (
        <>
            {ownPath ? (
                <Chip
                    variant={categoryChipVariant(ownPath, 'category', txn.accountId, isSystem(txn.accountId))}
                    className="max-w-full truncate"
                    title={`Filed under ${ownPath}`}
                >
                    <span className="truncate">{ownPath}</span>
                </Chip>
            ) : null}
            {txn.counterpartyAccountName ? (
                renderAccountChip({
                    variant: chipVariant,
                    label,
                    chipClassName: 'max-w-full truncate',
                    otherAmount: txn.counterpartyAmount,
                    otherCurrency: txn.counterpartyCurrencyCode,
                    amount: txn.amount,
                })
            ) : (
                <span className="text-text-subtle">—</span>
            )}
            {renderRowTags(txn, accountPaths)}
        </>
    );
}
