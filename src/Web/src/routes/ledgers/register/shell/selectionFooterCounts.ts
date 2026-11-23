import type { DisplayRow } from '@/lib/splitCollapse';
import type { RegisterRow, SelectionRequest, SelectionSummary } from '@/lib/types';

/** What the bulk-action footer names: "N selected · M read-only". */
export interface SelectionFooterCounts {
    /** Every selected row, read-only rows included. */
    selected: number;
    /** How many of `selected` the register shows read-only. */
    readOnly: number;
}

/**
 * The footer's counts for the active selection.
 *
 * A select-all's rows are not all loaded, so its counts are the server's
 * (`totalCount`, `readOnlyCount`); 0 until the summary arrives.
 *
 * An explicit selection's count is the ids it holds. Its read-only part is the
 * server's `readOnlyCount` once the summary for this selection has arrived
 * (`summaryIsCurrent`): a selected row can sit on a page the window has evicted,
 * or never loaded, and only the server sees those. Until then it is counted from
 * the loaded rows' `isReadOnly`, so the footer names a read-only row the moment it
 * is checked. A header counts as read-only when no loaded row of it is editable,
 * as the server judges it.
 *
 * The counts describe the selection, not what an action reaches: what each bulk
 * action acts on is unchanged (summary `count`, the confirmations).
 */
export function selectionFooterCounts<R extends RegisterRow>(
    selection: SelectionRequest,
    summary: SelectionSummary | null,
    summaryIsCurrent: boolean,
    rows: readonly DisplayRow<R>[],
): SelectionFooterCounts {
    if (selection.kind === 'all') {
        return {
            selected: summary?.totalCount ?? 0,
            readOnly: summary?.readOnlyCount ?? 0,
        };
    }
    const selectedIds = new Set(selection.headerIds);
    if (summaryIsCurrent && summary !== null) {
        return { selected: selectedIds.size, readOnly: summary.readOnlyCount };
    }
    return { selected: selectedIds.size, readOnly: readOnlyAmongLoaded(selectedIds, rows) };
}

/** The selected headers whose loaded rows are all read-only. */
function readOnlyAmongLoaded<R extends RegisterRow>(
    selectedIds: ReadonlySet<string>,
    rows: readonly DisplayRow<R>[],
): number {
    const readOnlyHeaders = new Set<string>();
    const editableHeaders = new Set<string>();
    for (const row of rows) {
        const legs =
            row.kind === 'txn' ? [row.txn] : row.kind === 'split-leg' ? [row.leg] : row.legs;
        for (const leg of legs) {
            if (!selectedIds.has(leg.headerId)) continue;
            (leg.isReadOnly ? readOnlyHeaders : editableHeaders).add(leg.headerId);
        }
    }
    let readOnly = 0;
    for (const headerId of readOnlyHeaders) {
        if (!editableHeaders.has(headerId)) readOnly += 1;
    }
    return readOnly;
}
