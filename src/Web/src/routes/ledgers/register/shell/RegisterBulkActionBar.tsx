import type { ReactNode } from 'react';

import type { RateUnavailable, ReconStatus } from '@/lib/types';

import { Button } from '@/components/ui/Button';
import { BulkActionFooter } from '@/components/ui/BulkActionFooter';
import { formatSignedAmount } from '@/lib/money';
import { selectionErrorMessage } from '@/lib/useSelection';

import { NoRate } from '../../home/Money';

import type { StatusFilter } from './registerStatus';

interface RegisterBulkActionBarProps {
    /** Every selected row, read-only rows included (`selectionFooterCounts`). */
    selectedCount: number;
    /** How many of `selectedCount` are read-only, named beside it. */
    readOnlyCount: number;
    /** Signed Σ of the selection on this account, in `currency`; null when it
     *  needs a missing rate (ADR-0114 D9), which `sumRateUnavailable` names. */
    selectedSum: number | null;
    /** Every missing rate a null `selectedSum` needed, named on hover. */
    sumRateUnavailable?: readonly RateUnavailable[];
    /** The selection summary's last request failed (`useSelection`'s
     *  `summaryError`). The bar names the failure in place of the Σ, rather
     *  than drawing a figure it does not have. Null when it succeeded. */
    summaryError: unknown;
    /** The currency the Σ is in: the selection summary's (ADR-0114 D7), which on a
     *  category's register in several currencies is the reporting one, else the
     *  account's. Null while the account is still loading, when no Σ is drawn. */
    currency: string | null;
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
    /** Active status filter — Unhide shows only in the Hidden view. (There
     *  are no Categorize / Tag buttons here: bulk categorise and tag are
     *  blocked on write endpoints that do not exist.) */
    statusFilter: StatusFilter;
    /** ADR-0072 D2 — Unhide the selection (Hidden view only). */
    onBulkUnhide: () => void;
    bulkUnhidePending: boolean;
    /** Unhide is disabled for read-only selections, as Delete and Move are:
     *  un-hiding changes the transaction, and a read-only row's is changed from
     *  the register it belongs to (the server refuses it too). */
    unhideDisabled: boolean;
    unhideDisabledTitle: string;
    /** ADR-0072 D3 — open the move-to-account dialog. */
    onOpenMoveDialog: () => void;
    /** Move is disabled for read-only selections (a row whose canonical owner
     *  is elsewhere can't be moved from here). */
    moveDisabled: boolean;
    moveDisabledTitle: string;
    /** True on a CATEGORY's register. The move re-points the leg sitting on
     *  the register's own account, so on a category that is a RECATEGORISE
     *  and the button has to say so — "Move to account" there would name an
     *  operation that, without the server guard, silently destroys the
     *  category leg. */
    moveIsCategory?: boolean;
    /** Any further domain-specific actions rendered after the shared set. */
    extraActions?: ReactNode;
    /** The ledger is read-only to this user (a viewer): the selection's count
     *  and Σ only, with no action that writes. */
    readOnly?: boolean;
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
    readOnlyCount,
    selectedSum,
    sumRateUnavailable = [],
    summaryError,
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
    unhideDisabled,
    unhideDisabledTitle,
    onOpenMoveDialog,
    moveDisabled,
    moveDisabledTitle,
    moveIsCategory = false,
    extraActions,
    readOnly = false,
}: RegisterBulkActionBarProps) {
    const failed = summaryError !== null && summaryError !== undefined;
    // A failed summary draws no Σ: its figure would be the previous selection's,
    // or 0. The failure is named in the trailing slot instead, which shows even
    // when a select-all has no count yet. Nor is one drawn before the account's
    // currency is known.
    // A Σ that needs a missing rate is no figure: it reads "no rate", with the pairs
    // and days on hover, never a partial sum (ADR-0114 D9).
    const sum = failed || currency === null ? null : (
        <span className="font-mono tabular-nums text-text-muted">
            Σ {selectedSum === null
                ? <NoRate missing={sumRateUnavailable} />
                : formatSignedAmount(selectedSum, currency)}
        </span>
    );
    const trailing = failed || loading ? (
        <>
            {failed ? (
                <span role="alert" className="text-state-danger">
                    {selectionErrorMessage(summaryError)}
                </span>
            ) : null}
            {loading ? <span className="text-text-subtle">Loading…</span> : null}
        </>
    ) : null;
    if (readOnly) {
        return (
            <BulkActionFooter
                selectedCount={selectedCount}
                readOnlyCount={readOnlyCount}
                onClear={onClearSelection}
                alwaysVisible={false}
                actions={sum}
                trailing={trailing}
            />
        );
    }
    return (
        <BulkActionFooter
            selectedCount={selectedCount}
            readOnlyCount={readOnlyCount}
            // The "N selected ✕" count IS the clear affordance (plus Esc); no
            // separate Clear button competing with the bulk actions.
            onClear={onClearSelection}
            // Idle registers don't pin the strip — it surfaces only for an
            // active selection or while older/newer rows are loading.
            alwaysVisible={false}
            actions={
                <>
                    {sum}
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
                            disabled={unhideDisabled}
                            pending={bulkUnhidePending}
                            title={unhideDisabled ? unhideDisabledTitle : 'Unhide the selection'}
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
                                : moveIsCategory
                                    ? 'Move the selection to another category'
                                    : 'Move the selection to another account'
                        }
                    >
                        {moveIsCategory ? 'Move to category…' : 'Move to account…'}
                    </Button>
                    {extraActions}
                </>
            }
            trailing={trailing}
        />
    );
}
