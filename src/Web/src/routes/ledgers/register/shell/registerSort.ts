// Register column-sort vocabulary (mig 166). Shared by both registers so the
// options + labels can't drift. Sort is DISPLAY-ORDER only — it never changes
// which entries match (that's the filter), only the order the windowed read
// returns them in. Pure module (no React) so the controls + pages import it
// without tripping the react-refresh "only export components" rule.

import { counterpartyWords, type CounterpartyKind } from '@/lib/counterparty';

export type RegisterSortColumn =
    | 'date'
    | 'amount'
    | 'payee'
    | 'category'
    | 'security'
    | 'shares'
    | 'price'
    | 'action';

export type RegisterSortDir = 'asc' | 'desc';

export interface RegisterSortState {
    column: RegisterSortColumn;
    dir: RegisterSortDir;
}

/** The register's out-of-the-box ordering — newest posted first. Matches the
 *  server's own default when no sort is sent. */
export const DEFAULT_SORT: RegisterSortState = { column: 'date', dir: 'desc' };

/** Sort columns in menu order, each with its label + whether it applies only to
 *  investment registers. The investment-only columns (Security / Shares /
 *  Price / Action) read off the security leg, so a bank register would sort
 *  them all-equal — the menu hides them there (the API tolerates them as a
 *  harmless no-op; the SPA just doesn't offer them). */
export const REGISTER_SORT_COLUMNS: ReadonlyArray<{
    value: RegisterSortColumn;
    label: string;
    investmentOnly: boolean;
}> = [
    { value: 'date', label: 'Date', investmentOnly: false },
    { value: 'amount', label: 'Amount', investmentOnly: false },
    { value: 'payee', label: 'Payee', investmentOnly: false },
    // `category` is the wire value and sorts by the COUNTERPARTY's name, which
    // is an account on a category's register. The label here is only the
    // default — render through `sortColumnLabel`, which names it for the
    // register it is on.
    { value: 'category', label: 'Category', investmentOnly: false },
    { value: 'security', label: 'Security', investmentOnly: true },
    { value: 'shares', label: 'Shares', investmentOnly: true },
    { value: 'price', label: 'Price', investmentOnly: true },
    { value: 'action', label: 'Action', investmentOnly: true },
];

/** The direction a freshly-picked column starts in: descending for date +
 *  numeric columns (newest / largest first), ascending for text/label columns
 *  (A→Z, natural). Re-picking the already-active column toggles instead (the
 *  menu owns that). */
export function defaultDirFor(column: RegisterSortColumn): RegisterSortDir {
    return column === 'date'
        || column === 'amount'
        || column === 'shares'
        || column === 'price'
        ? 'desc'
        : 'asc';
}

/**
 * A column's label ON THIS REGISTER — for the dropdown trigger and every option.
 *
 * The counterparty column is the one whose name depends on the register: it
 * read "Category" on a category's register, sorting by accounts, which made
 * this the third surface to ship that defect.
 */
export function sortColumnLabel(
    column: RegisterSortColumn,
    counterpartyKind: CounterpartyKind = 'category',
): string {
    if (column === 'category') return counterpartyWords(counterpartyKind).noun;
    return REGISTER_SORT_COLUMNS.find((c) => c.value === column)?.label ?? 'Date';
}
