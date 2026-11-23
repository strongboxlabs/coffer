import type { ReactNode } from 'react';

import type { BankRow } from '@/lib/types';
import { InheritedText } from '@/components/register/InheritedMarker';
import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';
import { readOnlyBadge } from '@/lib/annotations/notes';
import { categoryChipVariant } from '@/lib/categoryChip';
import { displayAccountPath } from '@/lib/accountPath';
import { formatLedgerDate } from '@/lib/dates';
import { groupCurrency } from '@/lib/splitCollapse';

import type {
    RegisterRowBodyCtx,
    RegisterRowContainerCtx,
    RegisterRowStrategy,
} from '../shell/RegisterRow';
import { resolveRowStatus, taxDateSubLabel } from '../bank/columns';
import {
    BANK_COLS,
    renderAccountChip,
    renderBankSlot4,
    renderBankSlot5,
    renderBankSlot6,
} from './bankStrategy';
import { renderRowMemo, renderRowTags } from './rowNotes';
import { Mixed } from '../../home/Money';
import { rowAmountText, rowBalance } from './registerMoney';

// ---------------------------------------------------------------------------
// Bank register row STRATEGY (ADR-0030 reuse). Implements
// `RegisterRowStrategy<BankRow>`: the cells AFTER the shared lead for all
// three row variants (txn / split-parent / split-leg) via one
// `renderBody` switch. This is where the per-variant JSX from the three
// former bank row components (BankTxnRow, SplitParentRowCells,
// SplitLegRowCells) was relocated verbatim — same class strings, same
// slot renderers from `./bankStrategy`, same tree glyph / expand control
// / read-only "↗" treatment. The shell (shell/RegisterRow) owns the
// container + lead.
// ---------------------------------------------------------------------------

function renderTxnBody(txn: BankRow, ctx: RegisterRowBodyCtx): ReactNode {
    const { today } = ctx;
    const status = resolveRowStatus(txn, today);
    const scheduled = status === 'scheduled';
    // A read-only row names where it is edited: an investment event's
    // counterpart (the brokerage register) or a split line seen from its other
    // side (the split's own register). The row's kind says which.
    const badge = ctx.isReadOnly ? readOnlyBadge(txn.notesKind) : null;
    const taxDateLabel = taxDateSubLabel(txn);
    const dateSubLabel = taxDateLabel ? `tax ${taxDateLabel}` : null;
    return (
        <>
            <span role="cell" className="font-mono tabular-nums">
                <span className="block leading-tight">
                    {formatLedgerDate(txn.postedAt)}
                </span>
                {dateSubLabel ? (
                    <span className="block truncate text-[0.625rem] leading-tight text-text-subtle">
                        {dateSubLabel}
                    </span>
                ) : null}
            </span>
            {/* Slot 4: the check number, and on a read-only row the badge
                above it. The badge sat in front of the payee, where on a
                narrow cell it left the payee and memo a letter each; here, in
                the slot the investment register gives its action chip, the
                payee and memo keep the whole cell. A row without a badge renders
                this cell exactly as before, so nothing in the column moves. */}
            {badge !== null ? (
                <span role="cell" className="flex min-w-0 flex-col items-start gap-0.5">
                    <span
                        className="inline-flex shrink-0 items-center rounded bg-accent-soft px-1 py-px text-[0.625rem] font-medium uppercase tracking-wide text-accent"
                        title={
                            badge === 'investment'
                                ? 'Row owned by an investment transaction · view + edit in the brokerage register'
                                : 'One line of a split transaction · edit it in the split’s own register'
                        }
                    >
                        {badge === 'investment' ? '↗ Investment' : '↗ Split'}
                    </span>
                    {txn.checkNumber ? (
                        <span className="block max-w-full truncate font-mono tabular-nums text-text-muted">
                            {renderBankSlot4(txn)}
                        </span>
                    ) : null}
                </span>
            ) : (
                <span
                    role="cell"
                    className="truncate font-mono tabular-nums text-text-muted"
                >
                    {renderBankSlot4(txn)}
                </span>
            )}
            <span role="cell" className="min-w-0">
                {renderBankSlot5(txn, ctx.accountPaths)}
            </span>
            <span role="cell" className="min-w-0 space-y-1.5">
                {renderBankSlot6(txn, ctx.accountPaths, ctx.rollupRootId, ctx.systemCategoryIds)}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (scheduled
                        ? ''
                        : txn.amount < 0
                            ? 'text-state-danger'
                            : 'text-text')
                }
            >
                <span className="block">
                    {rowAmountText(txn)}
                </span>
            </span>
            <span
                role="cell"
                className="text-right font-mono tabular-nums"
            >
                {rowBalance(txn)}
            </span>
        </>
    );
}

function renderSplitParentBody(row: BankRow, ctx: RegisterRowBodyCtx<BankRow>): ReactNode {
    // The page synthesizes the representative parent row as the canonical
    // leg with the group's amount + balance-after-last-leg, and with the
    // TRANSACTION's memo and tags in `memo` / `tags` (a line's own fields are
    // its own), so the cells read off `row` directly.
    const { today, expand } = ctx;
    const status = resolveRowStatus(row, today);
    const scheduled = status === 'scheduled';
    const taxDateLabel = taxDateSubLabel(row);
    // A category's split paid from accounts in different currencies has no net, as no
    // figure adds currencies (ADR-0114 D7): its Amount reads the mixed marker.
    const mixed = ctx.legs !== undefined && groupCurrency(ctx.legs) === null;
    return (
        <>
            <span role="cell" className="font-mono tabular-nums">
                <span className="block leading-tight">
                    {formatLedgerDate(row.postedAt)}
                </span>
                {taxDateLabel ? (
                    <span className="block text-[0.625rem] leading-tight text-text-subtle">
                        tax {taxDateLabel}
                    </span>
                ) : null}
            </span>
            <span
                role="cell"
                className="truncate font-mono tabular-nums text-text-muted"
            >
                {row.checkNumber ?? ''}
            </span>
            <span role="cell" className="min-w-0">
                {/* Parent row shows payee + the transaction's memo. Mig 107:
                    leading provenance icon. */}
                <span className="flex items-center gap-1.5 truncate font-medium">
                    <ProvenanceIcon
                        origin={row.origin}
                        providerKey={row.providerKey}
                        isMergeWinner={row.isMergeWinner}
                    />
                    <span className="truncate">
                        {row.payee ?? (
                            <span className="text-text-subtle">—</span>
                        )}
                    </span>
                </span>
                {renderRowMemo(row, ctx.accountPaths)}
            </span>
            {/* Combined category · tags column, holding the expand toggle.
                The transaction's tags wrap beneath; each line's own tags show
                on its expanded row.

                NO LEAD CATEGORY HERE, deliberately: no category at the cell's
                left edge that "stands for the group" (the filter-matched leg or
                else the largest by absolute summed amount). Sitting at the same
                left edge as a flat row's chip, it would make the column read
                straight down — which makes a heuristic PICK look like the
                definite category a flat row states. A split has N categories;
                naming one of them there asserts something untrue, and any
                tie-break between legs is a guess.

                If a collapsed split should ever answer "which leg matched the
                active filter, and for how much", that is a different and
                narrower feature. */}
            <span role="cell" className="min-w-0 space-y-1.5">
                <span className="flex min-w-0 items-center gap-1">
                    <button
                        type="button"
                        aria-expanded={expand?.expanded ?? false}
                        onClick={(e) => {
                            // The row underneath handles click (focus).
                            e.stopPropagation();
                            expand?.onToggle();
                        }}
                        // BOTH handlers are needed, and that is the whole
                        // trap. `dblclick` is its own DOM event, not something
                        // derived from the two clicks, so stopping click
                        // propagation does nothing for it — without this an
                        // impatient double-click on the toggle would expand the
                        // split AND drop the editor on top of it.
                        //
                        // The investment strategy stops only the click and
                        // looks fine, but that is luck: its split parents are
                        // readOnly, so RegisterRow never wires a double-click
                        // handler for the event to reach.
                        onDoubleClick={(e) => e.stopPropagation()}
                        title={
                            expand?.expanded === true
                                ? 'Hide this split’s categories'
                                : 'Show this split’s categories'
                        }
                        // FU-061: NO `ml-auto`. It would push this to the far
                        // right of the 19.5rem cell — about 300px from where
                        // every flat row's category chip sits, hard against the
                        // amount — so on a register with a few split rows it
                        // would read as a sparse extra column between CATEGORY
                        // and AMOUNT rather than as anything to do with a
                        // category. The investment strategy renders the same
                        // affordance in the same slot without it.
                        //
                        // What must NOT go in the space this frees is a lead
                        // category chip (see the comment above this cell). A
                        // toggle at the left edge
                        // states the row's SHAPE, which is true of a split; a
                        // chip there would name one leg of N as though it were
                        // the row's category.
                        className="inline-flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 text-[0.6875rem] font-medium text-text-muted hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                    >
                        {/* FU-062: sized independently of the caption beside it.
                            A triangle fills far less of its em box than a letter
                            does, so at the button's 11px it read smaller still —
                            and it is the one thing on the row whose whole job is
                            to be clicked. `leading-none` keeps the taller glyph
                            from growing the line box: this cell is `space-y-1.5`
                            stacked over a tag line, and a row that gains height
                            here falls out of step with its neighbours, which is
                            the only reading pattern a register has. */}
                        {/* `not-italic` because a SCHEDULED row is italic
                            (RegisterRow's `italic text-text-subtle`), and the
                            glyph inherited it — a slanted triangle, which reads
                            as a rendering fault rather than as a control. It
                            was always inherited; at 11px the skew was small
                            enough to pass for noise, and sizing the glyph up is
                            what made it legible. Text may lean; a geometric
                            glyph is a shape and does not. */}
                        <span aria-hidden className="text-[0.875rem] not-italic leading-none">
                            {expand?.expanded ? '▾' : '▸'}
                        </span>
                        {/* The count stays in the TEXT, not in an aria-label:
                            an aria-label here would be the button's whole
                            accessible name and the category chip beside it
                            would be all a screen reader lost. */}
                        {expand?.count ?? 0} splits
                    </button>
                </span>
                {renderRowTags(row, ctx.accountPaths)}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (scheduled || mixed
                        ? ''
                        : row.amount < 0
                            ? 'text-state-danger'
                            : 'text-text')
                }
            >
                {mixed ? <Mixed /> : rowAmountText(row)}
            </span>
            <span role="cell" className="text-right font-mono tabular-nums">
                {rowBalance(row)}
            </span>
        </>
    );
}

function renderSplitLegBody(leg: BankRow, ctx: RegisterRowBodyCtx): ReactNode {
    const { today } = ctx;
    // A line carries its transaction's date, so a future-dated split's lines
    // are Scheduled too and drop the colour like their parent (ADR-0023 §H).
    const scheduled = resolveRowStatus(leg, today) === 'scheduled';
    const chipVariant = categoryChipVariant(
        leg.counterpartyAccountName,
        leg.counterpartyAccountType,
        leg.counterpartyAccountId,
        leg.counterpartyAccountId !== null && ctx.systemCategoryIds.has(leg.counterpartyAccountId),
    );
    const categoryLabel = displayAccountPath(
        ctx.accountPaths, leg.counterpartyAccountId, leg.counterpartyAccountName,
    );
    // Leg body: the two blank lead cells are rendered by the shell; this
    // covers the four remaining blank cells + memo / category / amount /
    // blank-balance (mirrors the former SplitLegRowCells).
    return (
        <>
            <span role="cell" />
            <span role="cell" />
            <span role="cell" className="min-w-0 pl-4">
                {/* Tree-prefix glyph + the line's memo as this register shows
                    it. A line with no memo of its own shows "—": it does not
                    repeat the transaction's, which sits on the parent row. */}
                <span className="flex min-w-0 items-baseline">
                    <span aria-hidden className="mr-1 shrink-0 text-text-subtle">└</span>
                    {leg.memo === null ? (
                        <span className="text-text-subtle">—</span>
                    ) : leg.memoInherited ? (
                        <InheritedText
                            inherited
                            from={displayAccountPath(ctx.accountPaths, leg.inheritFromAccountId, null)}
                        >
                            {leg.memo}
                        </InheritedText>
                    ) : (
                        <span className="min-w-0 truncate">{leg.memo}</span>
                    )}
                </span>
            </span>
            {/* Combined category · tags: the line's category and, under it,
                the line's own tags. A line to an account in another currency
                shows its own other amount after the chip (ADR-0114 D14). */}
            <span role="cell" className="min-w-0 space-y-1.5">
                {leg.counterpartyAccountName ? (
                    renderAccountChip({
                        variant: chipVariant,
                        label: categoryLabel,
                        chipClassName: 'max-w-full truncate',
                        otherAmount: leg.counterpartyAmount,
                        otherCurrency: leg.counterpartyCurrencyCode,
                        amount: leg.amount,
                    })
                ) : (
                    <span className="text-text-subtle">—</span>
                )}
                {renderRowTags(leg, ctx.accountPaths)}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (scheduled
                        ? ''
                        : leg.amount < 0
                            ? 'text-state-danger'
                            : 'text-text')
                }
            >
                {rowAmountText(leg)}
            </span>
            {/* Balance intentionally blank on leg rows — only the group's
                final balance is meaningful in the running-total flow. */}
            <span role="cell" />
        </>
    );
}

// Bank-specific container attributes (preserved exactly as the three
// former bank components emitted them): `aria-rowindex` on every
// variant; `data-scheduled` + `data-needs-review` on the txn row only;
// the `italic text-text-subtle` scheduled treatment on txn + parent.
// Bank-specific container attribute: `aria-rowindex` on every variant. The
// row-STATE styling (scheduled / needs-review / hidden + leg muting) now lives
// in the shared RegisterRow so bank + investment render it identically.
function bankContainerAttrs(row: BankRow, ctx: RegisterRowContainerCtx) {
    return {
        dataAttrs: {
            'aria-rowindex':
                ctx.rowIndex !== undefined ? ctx.rowIndex + 1 : undefined,
            // Mirrors the investment strategy. Focus visible only as styling
            // leaves "which row has the keyboard cursor" unassertable, so a test
            // cannot see focus land on the wrong row; these attributes make it
            // checkable. `data-headerid` is omitted on legs, as investment omits it,
            // because a leg is not the header's row.
            'data-headerid': ctx.variant === 'split-leg' ? undefined : row.headerId,
            'data-focused': ctx.focused ? 'true' : 'false',
        },
    };
}

export const bankRowStrategy: RegisterRowStrategy<BankRow> = {
    cols: BANK_COLS,
    // py-1 so a two-line cell does not sit on the row's border. Without vertical
    // padding, one line of content never reaches the edge, but the category cell
    // can carry a splits indicator AND a tag line, and 34px of content in a 36px row touches top and bottom — most visible on the
    // selected row, where there is an outline against it. min-h still holds the
    // single-line rows at 36px, so only the rows that need the room take it.
    rowClassName: 'min-h-control-36px items-center py-1',
    cursorClassName: 'cursor-pointer',
    containerAttrs: bankContainerAttrs,
    renderBody(row, ctx) {
        switch (ctx.variant) {
            case 'split-parent':
                return renderSplitParentBody(row, ctx);
            case 'split-leg':
                return renderSplitLegBody(row, ctx);
            default:
                return renderTxnBody(row, ctx);
        }
    },
};
