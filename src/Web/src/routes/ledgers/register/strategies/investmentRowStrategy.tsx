import type { ReactNode } from 'react';

import type { InvestmentRow } from '@/lib/types';
import { formatLedgerDateLong } from '@/lib/dates';
import { formatCurrency, formatSignedAmount } from '@/lib/money';

import type {
    RegisterRowBodyCtx,
    RegisterRowContainerCtx,
    RegisterRowStrategy,
} from '../shell/RegisterRow';
import { INVESTMENT_REGISTER_COLS } from '../investment/columns';
import { investmentStrategy } from './investmentStrategy';

// ---------------------------------------------------------------------------
// Investment register row STRATEGY (ADR-0030 reuse). Implements
// `RegisterRowStrategy<InvestmentRow>`: the cells AFTER the
// shared lead for all three row variants (txn / split-parent / split-leg)
// via one `renderBody` switch. This is where the per-variant JSX from the
// three former investment row components (InvestmentTxnRow,
// InvestmentSplitParentRow, InvestmentSplitLegRow) was relocated verbatim
// — same class strings, same cell renderers from `./investmentStrategy`,
// same "↗ Split" treatment / tree glyph / expand control. The shell
// (shell/RegisterRow) owns the container + lead.
// ---------------------------------------------------------------------------

function formatBalance(balance: number | null, currency: string): string {
    if (balance === null) return '—';
    return formatCurrency(balance, currency);
}

function renderTxnBody(
    txn: InvestmentRow,
    ctx: RegisterRowBodyCtx,
): ReactNode {
    const { currency, isTargetSplit } = ctx;
    const amountText = formatSignedAmount(txn.amount, currency);
    const balanceText = formatBalance(txn.balanceAfter, currency);
    const dateText = formatLedgerDateLong(txn.postedAt);
    const dateSubLabel = investmentStrategy.renderDateSubLabel?.(txn) ?? null;
    const actionSubLabel = investmentStrategy.renderActionSubLabel?.(txn) ?? null;
    const amountSubtitle = investmentStrategy.renderAmountSubtitle?.(txn) ?? null;
    return (
        <>
            {/* Slot 3: date + tax-date subtitle */}
            <span className="font-mono tabular-nums text-text-default">
                <span className="block">{dateText}</span>
                {dateSubLabel ? (
                    <span className="block text-[0.6875rem] text-text-muted">
                        {dateSubLabel}
                    </span>
                ) : null}
            </span>
            {/* Slot 4: action chip + check# subtitle. The check number is an MD
                marker qualifying the ACTION (Auto / EXfr / Xfr), so it stacks
                here; slot 3's second line is the tax date. */}
            <span className="min-w-0">
                <span className="block">{investmentStrategy.renderSlot4(txn)}</span>
                {actionSubLabel ? (
                    <span className="block font-mono text-[0.6875rem] text-text-muted">
                        {actionSubLabel}
                    </span>
                ) : null}
            </span>
            {/* Slot 5: payee + memo (with "↗ Split" prefix on
                target-side rows per ADR-0036). */}
            <span className="min-w-0">
                {isTargetSplit ? (
                    <span className="flex min-w-0 items-baseline gap-1">
                        <span
                            className="inline-flex shrink-0 items-center rounded bg-accent-soft px-1 py-px text-[0.625rem] font-medium uppercase tracking-wide text-accent"
                            title="Counter-side of a split transaction · edit + delete from the source-side register"
                        >
                            ↗ Split
                        </span>
                        <span className="min-w-0">
                            {investmentStrategy.renderSlot5(txn)}
                        </span>
                    </span>
                ) : (
                    investmentStrategy.renderSlot5(txn)
                )}
            </span>
            {/* Slot 6: category | transfer + fee */}
            <span className="flex min-w-0 flex-col gap-0.5">
                {investmentStrategy.renderSlot6(txn, ctx.accountPaths)}
            </span>
            {/* Slot 7: security + shares @ price */}
            <span className="min-w-0">
                {investmentStrategy.renderSlot7?.(txn) ?? null}
            </span>
            {/* Slot 8: signed amount + fee subtitle */}
            <span className="text-right font-mono tabular-nums">
                <span
                    className={
                        'block ' +
                        (txn.amount < 0 ? 'text-text-default' : 'text-state-success')
                    }
                >
                    {amountText}
                </span>
                {amountSubtitle}
            </span>
            {/* Slot 9: balance after */}
            <span className="text-right font-mono tabular-nums text-text-default">
                {balanceText}
            </span>
        </>
    );
}

function renderSplitParentBody(
    aggregate: InvestmentRow,
    ctx: RegisterRowBodyCtx,
): ReactNode {
    // The page passes the synthesized cluster aggregate as `row`, so the
    // cells read off it directly (mirrors the former
    // InvestmentSplitParentRow). The aggregate is built from
    // `canonicalLeg(legs)` (legs[0]) + the shared `groupAmount` /
    // `groupBalanceAfter` helpers (ADR-0080), so it carries legs[0]'s date.
    const { currency, expand } = ctx;
    const amountText = formatSignedAmount(aggregate.amount, currency);
    const balanceText = formatBalance(aggregate.balanceAfter, currency);
    const dateText = formatLedgerDateLong(aggregate.postedAt);
    const dateSubLabel = investmentStrategy.renderDateSubLabel?.(aggregate) ?? null;
    const actionSubLabel = investmentStrategy.renderActionSubLabel?.(aggregate) ?? null;
    return (
        <>
            {/* Slot 3: date + tax-date subtitle */}
            <span className="font-mono tabular-nums text-text-default">
                <span className="block">{dateText}</span>
                {dateSubLabel ? (
                    <span className="block text-[0.6875rem] text-text-muted">
                        {dateSubLabel}
                    </span>
                ) : null}
            </span>
            {/* Slot 4: action chip (derived 'Xfr' on target splits) + check#. */}
            <span className="min-w-0">
                <span className="block">{investmentStrategy.renderSlot4(aggregate)}</span>
                {actionSubLabel ? (
                    <span className="block font-mono text-[0.6875rem] text-text-muted">
                        {actionSubLabel}
                    </span>
                ) : null}
            </span>
            {/* Slot 5: "↗ Split" treatment + payee/memo — same chip the
                flat target rows carry (ADR-0036). */}
            <span className="min-w-0">
                <span className="flex min-w-0 items-baseline gap-1">
                    <span
                        className="inline-flex shrink-0 items-center rounded bg-accent-soft px-1 py-px text-[0.625rem] font-medium uppercase tracking-wide text-accent"
                        title="Counter-side of a split transaction · edit + delete from the source-side register"
                    >
                        ↗ Split
                    </span>
                    <span className="min-w-0">
                        {investmentStrategy.renderSlot5(aggregate)}
                    </span>
                </span>
            </span>
            {/* Slot 6: the expand affordance — "▸ N splits" toggling the
                leg rows. Reuses the same column the flat row uses for
                category / transfer / fee. */}
            <span className="flex min-w-0 flex-col gap-0.5">
                <button
                    type="button"
                    aria-expanded={expand?.expanded ?? false}
                    aria-controls={`split-group-${expand?.groupId ?? ''}`}
                    onClick={(e) => {
                        e.stopPropagation();
                        expand?.onToggle();
                    }}
                    className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[0.6875rem] font-medium text-text-muted hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                >
                    <span aria-hidden>{expand?.expanded ? '▾' : '▸'}</span>
                    {expand?.count ?? 0} splits
                </button>
            </span>
            {/* Slot 7: security — usually empty on Xfr target splits, but
                rendered through the same renderer for consistency. */}
            <span className="min-w-0">
                {investmentStrategy.renderSlot7?.(aggregate) ?? null}
            </span>
            {/* Slot 8: net signed amount across the cluster's legs. */}
            <span className="text-right font-mono tabular-nums">
                <span
                    className={
                        'block ' +
                        (aggregate.amount < 0 ? 'text-text-default' : 'text-state-success')
                    }
                >
                    {amountText}
                </span>
            </span>
            {/* Slot 9: REAL post-header balance (not a fabricated
                per-leg step). */}
            <span className="text-right font-mono tabular-nums text-text-default">
                {balanceText}
            </span>
        </>
    );
}

function renderSplitLegBody(
    leg: InvestmentRow,
    ctx: RegisterRowBodyCtx,
): ReactNode {
    const { currency } = ctx;
    // The shell renders the two blank lead cells (checkbox + status); this
    // body covers the remaining seven cells (mirrors the former
    // InvestmentSplitLegRow).
    return (
        <>
            {/* Slot 3: blank — date is the group's, on the parent row. */}
            <span role="cell" />
            {/* Slot 4: action chip (the leg's derived action). */}
            <span role="cell">{investmentStrategy.renderSlot4(leg)}</span>
            {/* Slot 5: tree-prefix glyph + per-leg memo (the
                differentiator); payee already shows on the parent. */}
            <span role="cell" className="min-w-0 pl-4">
                <span className="block truncate">
                    <span aria-hidden className="mr-1 text-text-subtle">└</span>
                    {leg.legMemo ?? <span className="text-text-subtle">—</span>}
                </span>
            </span>
            {/* Slot 6: category | transfer · fee for this leg. */}
            <span role="cell" className="flex min-w-0 flex-col gap-0.5">
                {investmentStrategy.renderSlot6(leg, ctx.accountPaths)}
            </span>
            {/* Slot 7: security for this leg (usually empty on Xfr). */}
            <span role="cell" className="min-w-0">
                {investmentStrategy.renderSlot7?.(leg) ?? null}
            </span>
            {/* Slot 8: the leg's OWN signed amount. */}
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (leg.amount < 0 ? 'text-text-default/70' : 'text-state-success/70')
                }
            >
                {formatSignedAmount(leg.amount, currency)}
            </span>
            {/* Slot 9: balance intentionally blank on leg rows — only the
                group's final balance is meaningful in the running flow.
                Truly blank (no glyph), matching the bank split-leg. */}
            <span role="cell" />
        </>
    );
}

// Investment-specific container attributes (preserved exactly as the
// three former investment components emitted them): `data-headerid` on
// the txn + split-parent rows (not legs), `data-focused` on every
// variant. No `aria-rowindex` / `data-scheduled` (those were bank-only).
function investmentContainerAttrs(
    row: InvestmentRow,
    ctx: RegisterRowContainerCtx,
) {
    return {
        dataAttrs: {
            'data-headerid': ctx.variant === 'split-leg' ? undefined : row.headerId,
            'data-focused': ctx.focused ? 'true' : 'false',
        },
        // Leg muting + row-state styling now live in the shared RegisterRow.
    };
}

export const investmentRowStrategy: RegisterRowStrategy<InvestmentRow> = {
    cols: INVESTMENT_REGISTER_COLS,
    rowClassName: 'items-start py-1.5',
    cursorClassName: 'cursor-default',
    containerAttrs: investmentContainerAttrs,
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
