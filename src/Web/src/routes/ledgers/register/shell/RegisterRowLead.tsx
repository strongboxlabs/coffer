import type { ReactNode, Ref } from 'react';

// Shared leading two columns for every register row (ADR-0030 reuse).
//
// Both the bank and investment registers open each row with the same
// two cells — a selection checkbox, then the status badge — and the
// header band opens with the matching select-all checkbox + an empty
// status header. Previously each page hand-rolled these, and they had
// drifted: bank rendered `[checkbox][status]`, investment rendered
// `[status][checkbox]`, so the investment header's select-all sat over
// the wrong column. Standardizing on checkbox-first here means the two
// registers can't diverge again — the order lives in one place.
//
// The leading two grid TRACKS (checkbox width, then status width) are
// kept identical in `BANK_COLS` and `INVESTMENT_REGISTER_COLS` so these
// shared cells line up the same way on both pages.

interface RegisterLeadHeaderCellsProps {
    /** Ref for the select-all input (the page wires `indeterminate`). */
    selectAllRef: Ref<HTMLInputElement>;
    /** Whether every visible row is selected (drives `checked`). */
    allVisibleSelected: boolean;
    /** Toggle select-all for the visible/filtered set. */
    onToggleAll: () => void;
    /** Disable when there are no rows to select. */
    disabled: boolean;
    /** Accessible label for the select-all checkbox — bank scopes it
     *  to "matching the current filter"; both pages pass their own. */
    selectAllLabel: string;
}

/**
 * The header band's first two `role="columnheader"` cells: the
 * select-all checkbox (column 1) + an empty Status header (column 2).
 * Checkbox-first, matching `RegisterRowLead`.
 */
export function RegisterLeadHeaderCells({
    selectAllRef,
    allVisibleSelected,
    onToggleAll,
    disabled,
    selectAllLabel,
}: RegisterLeadHeaderCellsProps) {
    return (
        <>
            <span role="columnheader">
                <input
                    ref={selectAllRef}
                    type="checkbox"
                    aria-label={selectAllLabel}
                    checked={allVisibleSelected}
                    onChange={onToggleAll}
                    disabled={disabled}
                    className="h-3 w-3 accent-accent"
                />
            </span>
            <span role="columnheader" aria-label="Status" />
        </>
    );
}

interface RegisterRowLeadProps {
    /** Whether this row's header is in the active selection. */
    selected: boolean;
    /** Toggle this row's header in the selection. Receives whether Shift was
     *  held — shift-click extends the selection as a range from the anchor. */
    onToggleSelected: (shiftKey: boolean) => void;
    /** Accessible label for the per-row checkbox (callers embed the
     *  row id so each is unique). */
    selectLabel: string;
    /** The status badge / clickable status button for column 2. The
     *  page owns the status node because the cycle-vs-static treatment
     *  is row-shape specific (scheduled / pending rows are static). */
    statusNode: ReactNode;
}

/**
 * A data row's first two cells: the selection checkbox (column 1) +
 * the status node (column 2). Checkbox-first across both registers.
 * The checkbox stops click-propagation so toggling it doesn't also
 * move the focus cursor.
 */
export function RegisterRowLead({
    selected,
    onToggleSelected,
    selectLabel,
    statusNode,
}: RegisterRowLeadProps) {
    return (
        <>
            <span role="cell">
                <input
                    type="checkbox"
                    aria-label={selectLabel}
                    checked={selected}
                    // onClick carries shiftKey (range-select) and fires for both
                    // mouse and keyboard (Space dispatches a click). The checkbox
                    // is controlled, so onChange is a noop — onClick drives the
                    // toggle; stopPropagation keeps it from moving the row cursor.
                    onClick={(e) => {
                        e.stopPropagation();
                        onToggleSelected(e.shiftKey);
                    }}
                    onChange={() => {}}
                    className="h-3 w-3 accent-accent"
                />
            </span>
            <span role="cell">{statusNode}</span>
        </>
    );
}
