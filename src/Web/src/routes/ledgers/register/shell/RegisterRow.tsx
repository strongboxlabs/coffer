import type { MouseEvent, ReactNode } from 'react';
import type { ReconStatus, RegisterRow as RegisterRowUnion } from '@/lib/types';

import { StatusBadge } from '@/components/ui/StatusBadge';

import { registerRowChrome } from './registerRowChrome';
import { RegisterRowLead } from './RegisterRowLead';
import type { RowStatus } from './registerStatus';

// ---------------------------------------------------------------------------
// Shared, strategy-driven register-row shell (ADR-0030 reuse).
//
// This ONE component replaced six near-duplicate row components that each
// re-declared the same scaffolding and differed only in (a) their domain
// cells and (b) a handful of per-register variant tweaks:
//
//   * bank:        BankTxnRow, SplitParentRowCells, SplitLegRowCells
//                  (was bank/BankRegisterRow.tsx)
//   * investment:  InvestmentTxnRow, InvestmentSplitParentRow,
//                  InvestmentSplitLegRow + the InvestmentRow dispatcher
//                  (was investment/InvestmentRow.tsx)
//
// The split is now shell vs. strategy:
//
//   * The SHELL (this file) owns everything common to every register
//     row of every variant: the `role="row"` container + its grid
//     template / state chrome / interaction wiring, the shared
//     RegisterRowLead (checkbox + status-cycle button) for txn /
//     split-parent rows, the two blank lead cells for split-leg rows,
//     and the data-* / aria-* attributes the tests + CSS depend on.
//   * The STRATEGY (strategies/bankRowStrategy + investmentRowStrategy)
//     owns the cells AFTER the lead (date ... amount ... balance) for
//     all three variants via one `renderBody` switch, plus the per-
//     register grid template + container layout classes that genuinely
//     differ (bank rows are `min-h-9 items-center`, investment rows are
//     `items-start py-1.5`).
//
// Rendering is byte-for-byte identical to the six components this
// replaced: the per-variant JSX was relocated verbatim into each
// strategy's `renderBody`, and every className / chip / tree-glyph /
// expand affordance / data-attr is preserved exactly. The shell is
// generic over the row shape (`R extends RegisterRow`) so bank
// instantiates it with `BankRow` and investment with `InvestmentRow`,
// with no coercion at the call sites.
// ---------------------------------------------------------------------------

export type RegisterRowVariant = 'txn' | 'split-parent' | 'split-leg';

/** Context handed to a strategy's `renderBody`. Carries the discriminant
 *  the strategy switches on plus the formatting context + (for a
 *  split-parent) the expand control the strategy injects into its own
 *  category / description slot, exactly where each register currently
 *  puts it. */
export interface RegisterRowBodyCtx {
    variant: RegisterRowVariant;
    currency: string;
    today: Date;
    /** True when this row is the read-only TARGET side of a multi-posting
     *  header (ADR-0036). Drives the "↗ Split" / "↗ Investment" chip
     *  treatment inside the strategy's description slot. */
    isTargetSplit: boolean;
    /** Ledger account-id → full slash path (`Food/Groceries`), built once
     *  per page from the accounts list. Strategies use it to render a
     *  category's full chain instead of its bare leaf name. Optional /
     *  may be absent while accounts load — strategies fall back to the
     *  leaf name via {@link displayAccountPath}. */
    accountPaths?: ReadonlyMap<string, string>;
    /** Present only for split-parent rows — the expand affordance the
     *  strategy renders in its category / description slot. */
    expand?: {
        expanded: boolean;
        onToggle: () => void;
        count: number;
        groupId: string;
    };
}

/** Context for a strategy's `containerAttrs` — the bits a per-register
 *  container attribute might key on. `rowIndex` is the virtuoso render
 *  index (a render param, not row-derivable); the rest are row/variant
 *  facts. */
export interface RegisterRowContainerCtx {
    variant: RegisterRowVariant;
    today: Date;
    focused: boolean;
    rowIndex?: number;
    isTargetSplit: boolean;
}

/** Per-register rendering strategy. The objects live in non-component
 *  modules (strategies/*RowStrategy) so they don't trip
 *  react-refresh/only-export-components. */
export interface RegisterRowStrategy<R extends RegisterRowUnion> {
    /** Grid template (BANK_COLS / INVESTMENT_REGISTER_COLS). */
    cols: string;
    /** Container layout classes that differ per register — bank uses
     *  `min-h-9 items-center`, investment uses `items-start py-1.5`. */
    rowClassName: string;
    /** Cursor affordance class for the container — bank rows are
     *  `cursor-pointer`, investment rows are `cursor-default`. */
    cursorClassName: string;
    /** Per-register container `data-*` / `aria-*` attributes + any extra
     *  className. These genuinely differ per register (bank emits
     *  `aria-rowindex` / `data-scheduled` / `data-needs-review`;
     *  investment emits `data-headerid` / `data-focused`), so each
     *  strategy owns its own set rather than the shared shell carrying
     *  register-specific knowledge. The shell still owns the
     *  VARIANT-universal attrs (aria-selected, data-split-parent /
     *  -expanded / -split-leg). Optional — return `{}` for none. */
    containerAttrs?(
        row: R,
        ctx: RegisterRowContainerCtx,
    ): { dataAttrs?: Record<string, string | number | boolean | undefined>; className?: string };
    /** Everything AFTER the shared lead: date ... amount ... balance,
     *  for all three variants via one internal switch. */
    renderBody(row: R, ctx: RegisterRowBodyCtx): ReactNode;
}

interface RegisterRowProps<R extends RegisterRowUnion> {
    strategy: RegisterRowStrategy<R>;
    variant: RegisterRowVariant;
    /** The row to render. For split-parent the page synthesizes a
     *  representative row (bank: canonical + group amount/balance;
     *  investment: the aggregated cluster row). */
    row: R;
    currency: string;
    today: Date;

    focused: boolean;
    onFocus: () => void;

    // -- Selection + status (txn / split-parent only) --
    selected: boolean;
    /** Toggle this row's selection; `shiftKey` requests a range-select from the
     *  anchor (checkbox shift-click). Cmd/Ctrl-click toggles a single row. */
    onToggleSelected: (shiftKey?: boolean) => void;
    /** Accessible label for the per-row checkbox. */
    selectLabel?: string;
    /** Resolved status the badge shows in column 2. Bank passes
     *  `resolveRowStatus(...)` (may be `scheduled` / `pending`);
     *  investment passes the persisted `ReconStatus` directly. */
    status: RowStatus;
    /** The persisted reconciliation status the cycle button reports +
     *  cycles. In the cycle case this equals `status` (a non-scheduled,
     *  non-pending row), so the badge + the cycle stay in sync. */
    cycleStatus: ReconStatus;
    onCycleReconStatus: (headerId: string, current: ReconStatus) => void;
    /** Header id the status cycle operates on (canonical leg's header for
     *  split-parent rows). */
    statusHeaderId?: string;
    /** When true the status node renders a STATIC badge (no cycle button)
     *  — bank's scheduled / pending rows. Defaults false. */
    statusStatic?: boolean;

    // -- Interaction --
    onDoubleClickEdit?: () => void;
    onContextMenu?: (anchor: { x: number; y: number }) => void;
    /** Cmd/Ctrl-click toggles selection instead of focusing (bank-txn +
     *  every investment row). Bank split-parent does NOT toggle on
     *  cmd-click — it leaves this false. */
    cmdClickToggles?: boolean;
    /** Suppress double-click-to-edit (read-only target-split rows). */
    readOnly?: boolean;

    // -- Chrome --
    needsReview?: boolean;

    /** The expand affordance for a split-parent row, handed through to
     *  the strategy's `renderBody` via `ctx.expand` so the strategy can
     *  place it in its own category / description slot. The shell also
     *  derives the variant-universal `data-split-parent` / `data-expanded`
     *  attributes from it. */
    expand?: {
        expanded: boolean;
        onToggle: () => void;
        count: number;
        groupId: string;
    };

    /** Virtuoso render index — forwarded to the strategy's
     *  `containerAttrs` (bank turns it into `aria-rowindex`). */
    rowIndex?: number;
    /** The container `title`. */
    title?: string;

    /** Ledger account-id → full slash path, handed through to the
     *  strategy's `renderBody` via `ctx.accountPaths` so category chips
     *  render their full parent→child chain. */
    accountPaths?: ReadonlyMap<string, string>;
}

/**
 * The shared register-row shell. Owns the `role="row"` container, the
 * state chrome, the interaction wiring, and the lead region; delegates
 * the domain cells to `strategy.renderBody`.
 */
export function RegisterRow<R extends RegisterRowUnion>(
    props: RegisterRowProps<R>,
) {
    const {
        strategy,
        variant,
        row,
        currency,
        today,
        focused,
        onFocus,
        selected,
        onToggleSelected,
        selectLabel,
        status,
        cycleStatus,
        onCycleReconStatus,
        statusHeaderId,
        statusStatic = false,
        onDoubleClickEdit,
        onContextMenu,
        cmdClickToggles = false,
        readOnly = false,
        needsReview = false,
        rowIndex,
        title,
        expand,
        accountPaths,
    } = props;

    const isLeg = variant === 'split-leg';

    // Shared row-state chrome (ADR-0030 reuse; ADR-0021 revision). Leg
    // rows rest on the muted plane (nested), otherwise selected / focus /
    // needs-review drive the treatment.
    const { bgClass, boxShadow } = registerRowChrome(
        isLeg
            ? { focused, nested: true }
            : { focused, selected, needsReview },
    );

    const handleClick = (e: MouseEvent) => {
        if (!isLeg && cmdClickToggles && (e.metaKey || e.ctrlKey)) {
            onToggleSelected(false);
            return;
        }
        onFocus();
    };
    const handleDoubleClick = isLeg
        ? undefined
        : () => {
              if (readOnly) return;
              onDoubleClickEdit?.();
          };
    const handleContextMenu = isLeg || onContextMenu === undefined
        ? undefined
        : (e: MouseEvent) => {
              e.preventDefault();
              onFocus();
              onContextMenu({ x: e.clientX, y: e.clientY });
          };

    // Per-register container attrs (data-* / aria-* / extra className)
    // live in the strategy — the shell stays free of register-specific
    // attribute knowledge. The shell keeps only the VARIANT-universal
    // attrs below.
    const container = strategy.containerAttrs?.(row, {
        variant,
        today,
        focused,
        rowIndex,
        isTargetSplit: readOnly,
    }) ?? {};

    // Shared row-STATE treatment so the needs-review ("to be accepted"),
    // scheduled, and hidden looks are IDENTICAL across bank + investment.
    // This used to live in the bank strategy only (investment's differed);
    // computed here from the row's own flags so both registers can't drift.
    const scheduled = status === 'scheduled';
    const isParentOrTxn = variant === 'txn' || variant === 'split-parent';
    const stateClassName = isLeg
        ? 'text-text-muted'
        : row.isHidden && isParentOrTxn
            ? 'text-text-subtle opacity-60'
            : scheduled && isParentOrTxn
                ? 'italic text-text-subtle'
                : undefined;
    const stateDataAttrs = {
        'data-scheduled': variant === 'txn' && scheduled ? true : undefined,
        'data-needs-review': variant === 'txn' ? needsReview || undefined : undefined,
        'data-hidden': variant === 'txn' && row.isHidden ? true : undefined,
    };

    const baseClassName =
        'grid gap-2 border-b border-border px-3 text-xs ' +
        strategy.cursorClassName + ' ' + strategy.rowClassName + ' ' +
        (stateClassName ? stateClassName + ' ' : '') +
        (container.className ? container.className + ' ' : '') + bgClass;

    const ctx: RegisterRowBodyCtx = {
        variant,
        currency,
        today,
        isTargetSplit: readOnly,
        accountPaths,
        expand,
    };

    return (
        <div
            role="row"
            aria-selected={isLeg ? undefined : selected}
            data-split-parent={
                variant === 'split-parent' ? expand?.groupId : undefined
            }
            data-expanded={
                variant === 'split-parent' ? expand?.expanded || undefined : undefined
            }
            data-split-leg={isLeg ? 'true' : undefined}
            {...stateDataAttrs}
            {...container.dataAttrs}
            style={{ gridTemplateColumns: strategy.cols, boxShadow }}
            onClick={handleClick}
            onDoubleClick={handleDoubleClick}
            onContextMenu={handleContextMenu}
            title={title}
            className={baseClassName}
        >
            {isLeg ? (
                <>
                    <span role="cell" />
                    <span role="cell" />
                </>
            ) : (
                <RegisterRowLead
                    selected={selected}
                    onToggleSelected={onToggleSelected}
                    selectLabel={selectLabel ?? ''}
                    statusNode={
                        statusStatic ? (
                            <StatusBadge status={status} />
                        ) : (
                            <button
                                type="button"
                                onClick={(e) => {
                                    e.stopPropagation();
                                    onCycleReconStatus(
                                        statusHeaderId ?? '',
                                        cycleStatus,
                                    );
                                }}
                                // Cycling two states = two quick clicks, which
                                // the browser also reports as a dblclick. Stop it
                                // bubbling to the row's onDoubleClick (→ edit), so
                                // repeat-clicking the badge cycles instead of
                                // opening the editor.
                                onDoubleClick={(e) => e.stopPropagation()}
                                title="Cycle reconciliation status"
                                className="cursor-pointer rounded-full hover:opacity-80"
                                aria-label={`Cycle reconciliation status (currently ${cycleStatus})`}
                            >
                                <StatusBadge status={status} />
                            </button>
                        )
                    }
                />
            )}
            {strategy.renderBody(row, ctx)}
        </div>
    );
}
