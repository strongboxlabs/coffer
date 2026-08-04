import type { ReactNode } from 'react';

import type { BankRow } from '@/lib/types';
import { Chip } from '@/components/ui/Chip';
import { TagChip } from '@/components/tags/TagChip';
import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';
import { categoryChipVariant } from '@/lib/categoryChip';
import { displayAccountPath } from '@/lib/accountPath';
import { formatLedgerDate } from '@/lib/dates';
import { formatCurrency, formatSignedAmount } from '@/lib/money';

import type {
    RegisterRowBodyCtx,
    RegisterRowContainerCtx,
    RegisterRowStrategy,
} from '../shell/RegisterRow';
import { isInvestmentOwnedRow, resolveRowStatus, taxDateSubLabel } from '../bank/columns';
import {
    BANK_COLS,
    renderBankSlot4,
    renderBankSlot5,
    renderBankSlot6,
} from './bankStrategy';

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
    const { currency, today } = ctx;
    const status = resolveRowStatus(txn, today);
    const scheduled = status === 'scheduled';
    // Read-only row guards (mirror the former BankTxnRow): an
    // investment-owned cash leg (canonical owner is the brokerage
    // register) or a split counter-side (source side owns edit / delete).
    const isInvestmentOwned = isInvestmentOwnedRow(txn);
    const isReadOnly = ctx.isTargetSplit;
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
            <span
                role="cell"
                className="truncate font-mono tabular-nums text-text-muted"
            >
                {renderBankSlot4(txn)}
            </span>
            <span role="cell" className="min-w-0">
                {isReadOnly ? (
                    <span className="flex min-w-0 items-baseline gap-1">
                        <span
                            className="inline-flex shrink-0 items-center rounded bg-accent-soft px-1 py-px text-[0.625rem] font-medium uppercase tracking-wide text-accent"
                            title={
                                isInvestmentOwned
                                    ? 'Row owned by an investment transaction · view + edit in the brokerage register'
                                    : 'Counter-side of a split transaction · edit + delete from the source-side register'
                            }
                        >
                            {isInvestmentOwned ? '↗ Investment' : '↗ Split'}
                        </span>
                        <span className="min-w-0">
                            {renderBankSlot5(txn)}
                        </span>
                    </span>
                ) : (
                    renderBankSlot5(txn)
                )}
            </span>
            <span role="cell" className="min-w-0 space-y-1">
                {renderBankSlot6(txn, ctx.accountPaths)}
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
                    {formatSignedAmount(txn.amount, currency)}
                </span>
            </span>
            <span
                role="cell"
                className="text-right font-mono tabular-nums"
            >
                {formatCurrency(txn.balanceAfter, currency)}
            </span>
        </>
    );
}

function renderSplitParentBody(row: BankRow, ctx: RegisterRowBodyCtx): ReactNode {
    // The page synthesizes the representative parent row as the canonical
    // leg with the group's amount + balance-after-last-leg, so the cells
    // read off `row` directly (same fields the former SplitParentRowCells
    // computed from canonical / groupAmount / groupBalanceAfter).
    const { currency, today, expand } = ctx;
    const status = resolveRowStatus(row, today);
    const scheduled = status === 'scheduled';
    const taxDateLabel = taxDateSubLabel(row);
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
                {/* Parent row shows payee + raw header memo
                    (migration 032, ADR-0025). Mig 107: leading
                    provenance icon. */}
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
                {row.headerMemo ? (
                    <span className="block truncate text-[0.6875rem] text-text-muted">
                        {row.headerMemo}
                    </span>
                ) : null}
            </span>
            {/* Combined category · tags column. For a split parent the
                category slot shows the "— N splits —" expand affordance;
                tags belong to the header so they wrap beneath. */}
            <span role="cell" className="min-w-0 space-y-1">
                <button
                    type="button"
                    aria-expanded={expand?.expanded ?? false}
                    aria-controls={`split-group-${expand?.groupId ?? ''}`}
                    onClick={() => expand?.onToggle()}
                    className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[0.6875rem] font-medium text-text-muted hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                >
                    <span aria-hidden>{expand?.expanded ? '▾' : '▸'}</span>
                    — {expand?.count ?? 0} splits —
                </button>
                {row.tags.length > 0 ? (
                    <span className="flex flex-wrap gap-1">
                        {row.tags.map((tag) => (
                            <TagChip key={tag} name={tag} />
                        ))}
                    </span>
                ) : null}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (scheduled
                        ? ''
                        : row.amount < 0
                            ? 'text-state-danger'
                            : 'text-text')
                }
            >
                {formatSignedAmount(row.amount, currency)}
            </span>
            <span role="cell" className="text-right font-mono tabular-nums">
                {formatCurrency(row.balanceAfter, currency)}
            </span>
        </>
    );
}

function renderSplitLegBody(leg: BankRow, ctx: RegisterRowBodyCtx): ReactNode {
    const { currency } = ctx;
    const chipVariant = categoryChipVariant(
        leg.counterpartyAccountName,
        leg.counterpartyAccountType,
        leg.counterpartyAccountId,
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
                {/* Tree-prefix glyph + raw leg memo (migration 032,
                    ADR-0025). Blank when this leg has no per-leg memo. */}
                <span className="block truncate">
                    <span aria-hidden className="mr-1 text-text-subtle">└</span>
                    {leg.legMemo ?? <span className="text-text-subtle">—</span>}
                </span>
            </span>
            {/* Combined category · tags. Leg rows are per-posting, so tags
                (header-level) are intentionally NOT rendered here — they
                sit on the parent row. Just the category chip. */}
            <span role="cell" className="min-w-0">
                {leg.counterpartyAccountName ? (
                    <Chip
                        variant={chipVariant}
                        className="max-w-full truncate"
                        title={categoryLabel ?? undefined}
                    >
                        <span className="truncate">
                            {categoryLabel}
                        </span>
                    </Chip>
                ) : (
                    <span className="text-text-subtle">—</span>
                )}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (leg.amount < 0 ? 'text-state-danger/70' : 'text-text/70')
                }
            >
                {formatSignedAmount(leg.amount, currency)}
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
function bankContainerAttrs(_row: BankRow, ctx: RegisterRowContainerCtx) {
    return {
        dataAttrs: {
            'aria-rowindex':
                ctx.rowIndex !== undefined ? ctx.rowIndex + 1 : undefined,
        },
    };
}

export const bankRowStrategy: RegisterRowStrategy<BankRow> = {
    cols: BANK_COLS,
    rowClassName: 'min-h-9 items-center',
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
