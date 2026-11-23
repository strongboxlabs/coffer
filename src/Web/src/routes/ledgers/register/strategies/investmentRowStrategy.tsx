import type { ReactNode } from 'react';

import type { InvestmentRow } from '@/lib/types';
import { readOnlyBadge } from '@/lib/annotations/notes';
import { formatLedgerDateLong } from '@/lib/dates';

import type {
    RegisterRowBodyCtx,
    RegisterRowContainerCtx,
    RegisterRowStrategy,
} from '../shell/RegisterRow';
import { resolveRowStatus } from '../shell/registerStatus';
import { INVESTMENT_REGISTER_COLS } from '../investment/columns';
import { investmentStrategy } from './investmentStrategy';
import { rowAmountText, rowBalance } from './registerMoney';

// ---------------------------------------------------------------------------
// Investment register row STRATEGY (ADR-0030 reuse). Implements
// `RegisterRowStrategy<InvestmentRow>`: the cells AFTER the shared lead. The
// shell (shell/RegisterRow) owns the container + lead.
//
// Only the flat row variant exists here. The server collapses every entry on
// an investment account into one event row (ADR-0080), and a split's lines on
// their other side are each their own read-only row, so this register has no
// expandable split parent and no leg rows.
// ---------------------------------------------------------------------------

function renderTxnBody(
    txn: InvestmentRow,
    ctx: RegisterRowBodyCtx,
): ReactNode {
    const { currency, today } = ctx;
    const scheduled = resolveRowStatus(txn, today) === 'scheduled';
    // Each in the currency the row names; on a brokerage both are its own (ADR-0114 D7).
    const amountText = rowAmountText(txn);
    const balanceText = rowBalance(txn, '—');
    const dateText = formatLedgerDateLong(txn.postedAt);
    const dateSubLabel = investmentStrategy.renderDateSubLabel?.(txn) ?? null;
    const actionSubLabel = investmentStrategy.renderActionSubLabel?.(txn) ?? null;
    const amountSubtitle = investmentStrategy.renderAmountSubtitle(txn, currency);
    // A read-only row names where it is edited: the split's own register, or
    // the brokerage the event belongs to.
    const badge = ctx.isReadOnly ? readOnlyBadge(txn.notesKind) : null;
    return (
        <>
            {/* Slot 3: date + tax-date subtitle */}
            <span className="font-mono tabular-nums text-text">
                <span className="block">{dateText}</span>
                {dateSubLabel ? (
                    <span className="block text-[0.6875rem] text-text-muted">
                        {dateSubLabel}
                    </span>
                ) : null}
            </span>
            {/* Slot 4: action chip + check# subtitle. The check number is an MD
                marker qualifying the ACTION (Auto / EXfr / Xfr), so it stacks
                here; slot 3's second line is the tax date.

                A read-only row puts its badge here too, on a line of its own
                above the chip: it sat in front of the payee, where it
                left the payee and memo a letter each. Every row in this
                register has a chip (`markUnset`), so a badged row always has
                both, and "↗ Investment" beside the longer chips (BuyXfr,
                DividendXfr) is wider than the track; stacked, neither is cut
                and the chip keeps the left edge every other row's chip has. */}
            <span className="min-w-0">
                {badge !== null ? (
                    <span className="mb-0.5 block">
                        <span
                            className="inline-flex shrink-0 items-center rounded bg-accent-soft px-1 py-px text-[0.625rem] font-medium uppercase tracking-wide text-accent"
                            title={
                                badge === 'investment'
                                    ? 'Part of an investment transaction in another account · view + edit it there'
                                    : 'One line of a split transaction · edit it in the split’s own register'
                            }
                        >
                            {badge === 'investment' ? '↗ Investment' : '↗ Split'}
                        </span>
                    </span>
                ) : null}
                <span className="block">{investmentStrategy.renderSlot4(txn, { markUnset: true })}</span>
                {actionSubLabel ? (
                    <span className="block font-mono text-[0.6875rem] text-text-muted">
                        {actionSubLabel}
                    </span>
                ) : null}
            </span>
            {/* Slot 5: payee + memo, the cell's full width. */}
            <span className="min-w-0">
                {investmentStrategy.renderSlot5(txn, ctx.accountPaths)}
            </span>
            {/* Slot 6: category | transfer + fee, and the tags */}
            <span className="flex min-w-0 flex-col gap-0.5">
                {investmentStrategy.renderSlot6(txn, ctx.accountPaths, ctx.systemCategoryIds)}
            </span>
            {/* Slot 7: security + shares @ price */}
            <span className="min-w-0">
                {investmentStrategy.renderSlot7(txn, currency)}
            </span>
            {/* Slot 8: signed amount + fee subtitle. ADR-0023 §H, as in the
                bank register: a debit is red, a credit default, and a
                Scheduled row drops the colour to the row's own muting. */}
            <span className="text-right font-mono tabular-nums">
                <span
                    className={
                        'block ' +
                        (scheduled
                            ? ''
                            : txn.amount < 0
                                ? 'text-state-danger'
                                : 'text-text')
                    }
                >
                    {amountText}
                </span>
                {amountSubtitle}
            </span>
            {/* Slot 9: balance after */}
            <span className="text-right font-mono tabular-nums text-text">
                {balanceText}
            </span>
        </>
    );
}

// Investment-specific container attributes: `data-headerid` and
// `data-focused` on every row. `aria-rowindex` is emitted here too: it was
// bank-only, so a screen reader moving through a brokerage register was told
// nothing about where it was. `data-scheduled` comes from the shared
// RegisterRow, as it does for the bank.
function investmentContainerAttrs(
    row: InvestmentRow,
    ctx: RegisterRowContainerCtx,
) {
    return {
        dataAttrs: {
            'aria-rowindex':
                ctx.rowIndex !== undefined ? ctx.rowIndex + 1 : undefined,
            'data-headerid': row.headerId,
            'data-focused': ctx.focused ? 'true' : 'false',
        },
        // Row-state styling lives in the shared RegisterRow.
    };
}

export const investmentRowStrategy: RegisterRowStrategy<InvestmentRow> = {
    cols: INVESTMENT_REGISTER_COLS,
    rowClassName: 'items-start py-1.5',
    // These rows open the editor on double-click (the page wires
    // onDoubleClickEdit), so an arrow cursor said the opposite of what is true.
    // Bank has always used the pointer for the same affordance.
    cursorClassName: 'cursor-pointer',
    containerAttrs: investmentContainerAttrs,
    renderBody(row, ctx) {
        return renderTxnBody(row, ctx);
    },
};
