import type { ReportGroupBy } from '@/types/report';

/**
 * The group-by names a reader would use. Only CategoryKind needs one: the enum
 * value is a type name and "Income vs expense" is the question people actually
 * ask of it — which is the whole of what a cash-flow report is.
 */
export const GROUP_LABELS: Readonly<Partial<Record<ReportGroupBy, string>>> = {
    CategoryKind: 'Income vs expense',
};

/**
 * A group's display name, as the report prints it everywhere: the table's
 * rows, the chart's legend, a ranked bar's name, and the accessible text built
 * from them. One function, so the table and the chart cannot name the same
 * group two ways.
 *
 * Only category kinds need one. A kind's row name is the words the Categories
 * screen uses, not the stored value: "Expenses" rather than "expense", for the
 * same reason the measure reads Expenses.
 */
export function groupLabel(groupBy: ReportGroupBy, name: string): string {
    if (groupBy !== 'CategoryKind') return name;
    switch (name) {
        case 'income': return 'Income';
        case 'expense': return 'Expenses';
        case 'adjustment': return 'Adjustments';
        default: return name;
    }
}

/** What joins a category's path when it is shown whole: the register's separator. */
export const PATH_SEPARATOR = '/';

/** A group's name as the report shows it: the categories above it, then its own. */
export interface GroupNameParts {
    readonly parents: readonly string[];
    readonly leaf: string;
}

/**
 * A group's name with the categories above it (ADR-0104 amendment 2026-11-20).
 *
 * The server sends every group's whole path; what to show is the view's call. A
 * subcategory alone is ambiguous ("Base" of what?), so wherever nothing beside it
 * names its parent, it is shown under its whole path. Only a category has parents:
 * every other dimension's path is its one name.
 */
export function groupNameParts(groupBy: ReportGroupBy, name: string, path: readonly string[]): GroupNameParts {
    const leaf = groupLabel(groupBy, name);
    if (groupBy !== 'Category' || path.length < 2) return { parents: [], leaf };
    return { parents: path.slice(0, -1), leaf };
}

/** Only the group's own name: for a row its parent's row already names. */
export function leafOnly(groupBy: ReportGroupBy, name: string): GroupNameParts {
    return { parents: [], leaf: groupLabel(groupBy, name) };
}

/** The whole name as one string, for a tooltip, a title or accessible text. */
export function fullGroupName(parts: GroupNameParts): string {
    return [...parts.parents, parts.leaf].join(PATH_SEPARATOR);
}
