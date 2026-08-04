import type { ReactNode } from 'react';

import type { ReconStatus } from '@/lib/types';

import { Button } from '@/components/ui/Button';
import { BulkActionFooter } from '@/components/ui/BulkActionFooter';
import { formatSignedAmount } from '@/lib/money';

import type { StatusFilter } from './registerStatus';

interface RegisterBulkActionBarProps {
    /** Server-resolved selection count (footer's source of truth). */
    selectedCount: number;
    /** Signed Σ of the selection on this account. */
    selectedSum: number;
    currency: string;
    /** True while a load-older / load-newer fetch is in flight —
     *  drives the trailing "Loading…" hint. */
    loading: boolean;
    /** True when Bulk Delete must be disabled (all-mode selection, or
     *  a visible selected row is read-only). */
    bulkDeleteDisabled: boolean;
    /** Tooltip explaining the enabled / disabled Delete state. */
    bulkDeleteDisabledTitle: string;
    /** Apply a recon status to the active selection. */
    onBulkSetReconStatus: (status: ReconStatus) => void;
    /** Open the typed-confirm dialog for a bulk delete. */
    onRequestBulkDelete: () => void;
    /** Clear the active selection. */
    onClearSelection: () => void;
    /** Active status filter — Unhide shows only in the Hidden view; the
     *  Categorize / Tag placeholders show only outside it. */
    statusFilter: StatusFilter;
    /** ADR-0072 D2 — Unhide the selection (Hidden view only). */
    onBulkUnhide: () => void;
    bulkUnhidePending: boolean;
    /** ADR-0072 D3 — open the move-to-account dialog. */
    onOpenMoveDialog: () => void;
    /** Move is disabled for read-only selections (a row whose canonical owner
     *  is elsewhere can't be moved from here). */
    moveDisabled: boolean;
    moveDisabledTitle: string;
    /** Any further domain-specific actions rendered after the shared set. */
    extraActions?: ReactNode;
}

/**
 * Multi-select action bar (ADR-0024) shared by the bank and
 * investment registers. Renders the selection Σ, the bulk
 * recon-status buttons, the gated bulk Delete (which opens a
 * typed-confirm dialog owned by the page), Clear, and any
 * domain-specific `extraActions`. Lifted verbatim from the former
 * `BankBulkActionBar` — same wiring, same copy — so bank behavior
 * is unchanged and investment reuses it as-is.
 */
export function RegisterBulkActionBar({
    selectedCount,
    selectedSum,
    currency,
    loading,
    bulkDeleteDisabled,
    bulkDeleteDisabledTitle,
    onBulkSetReconStatus,
    onRequestBulkDelete,
    onClearSelection,
    statusFilter,
    onBulkUnhide,
    bulkUnhidePending,
    onOpenMoveDialog,
    moveDisabled,
    moveDisabledTitle,
    extraActions,
}: RegisterBulkActionBarProps) {
    return (
        <BulkActionFooter
            selectedCount={selectedCount}
            // The "N selected ✕" count IS the clear affordance (plus Esc); no
            // separate Clear button competing with the bulk actions.
            onClear={onClearSelection}
            // Idle registers don't pin the strip — it surfaces only for an
            // active selection or while older/newer rows are loading.
            alwaysVisible={false}
            actions={
                <>
                    <span className="font-mono tabular-nums text-text-muted">
                        Σ {formatSignedAmount(selectedSum, currency)}
                    </span>
                    {/* Status-change bulk actions preserve the
                        selection: the user may want to mark the
                        same batch reconciling then cleared, or
                        iterate through several states. The Delete
                        button below clears selection because the
                        rows themselves go away. Bulk action fires
                        one request per status — server resolves
                        the predicate and applies the new status
                        in a single UPDATE (ADR-0024). */}
                    <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => onBulkSetReconStatus('cleared')}
                        title="Mark all selected as cleared"
                    >
                        ✓ Cleared
                    </Button>
                    <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => onBulkSetReconStatus('reconciling')}
                        title="Mark all selected as reconciling (in-progress workflow flag)"
                    >
                        · Reconciling
                    </Button>
                    <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => onBulkSetReconStatus('uncleared')}
                        title="Mark all selected as uncleared"
                    >
                        ○ Uncleared
                    </Button>
                    <Button
                        variant="ghost"
                        size="sm"
                        onClick={onRequestBulkDelete}
                        disabled={bulkDeleteDisabled}
                        title={bulkDeleteDisabledTitle}
                        className="text-state-danger hover:text-state-danger"
                    >
                        Delete
                    </Button>
                    {/* ADR-0072 D2: Unhide only applies to hidden rows. */}
                    {statusFilter === 'hidden' ? (
                        <Button
                            variant="ghost"
                            size="sm"
                            onClick={onBulkUnhide}
                            disabled={bulkUnhidePending}
                        >
                            Unhide
                        </Button>
                    ) : null}
                    {/* ADR-0072 D3: Move re-files the selection into another
                        account, available in any view. Disabled for read-only
                        selections (canonical owner elsewhere). */}
                    <Button
                        variant="ghost"
                        size="sm"
                        onClick={onOpenMoveDialog}
                        disabled={moveDisabled}
                        title={
                            moveDisabled
                                ? moveDisabledTitle
                                : 'Move the selection to another account'
                        }
                    >
                        Move to account…
                    </Button>
                    {extraActions}
                </>
            }
            trailing={
                loading ? (
                    <span className="text-text-subtle">Loading…</span>
                ) : null
            }
        />
    );
}
