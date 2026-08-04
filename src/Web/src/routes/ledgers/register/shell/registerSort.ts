// Register column-sort vocabulary (mig 166). Shared by both registers so the
// options + labels can't drift. Sort is DISPLAY-ORDER only — it never changes
// which entries match (that's the filter), only the order the windowed read
// returns them in. Pure module (no React) so the controls + pages import it
// without tripping the react-refresh "only export components" rule.

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

/** Label for the active column (for the dropdown trigger). */
export function sortColumnLabel(column: RegisterSortColumn): string {
    return REGISTER_SORT_COLUMNS.find((c) => c.value === column)?.label ?? 'Date';
}
