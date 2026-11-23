import type { ReportSpec } from '../../../types/report';

/**
 * The canned reports (FU-008, ADR-0104 D1) — each one a preset `ReportSpec`
 * rendered by the same view, never its own query code.
 *
 * ONE AGGREGATION REPORT, AND SAYING SO IS THE POINT.
 *
 * This listed six, then four: Expenses, Income, Cash flow, by payee, by
 * account, Tag summary. Every one of them was the same aggregation with one
 * control set differently — measure, or group-by. Six cards for one report
 * made the catalog look richer than the feature is, and the second card taught
 * nothing the first had not. The intermediate position (one card per
 * DIMENSION, on the grounds that group-by is less discoverable than measure)
 * did not survive either: the dimension is a dropdown in the report, visible
 * on arrival, so the cards were still advertising the control's own options.
 *
 * So: one aggregation report, named for what it covers. Discovery happens in the controls,
 * which is where the options actually live.
 *
 * THE AGGREGATION FAMILY IS ONE ENTRY; a different report is its own entry.
 * Net worth and asset allocation are different reports — different
 * repositories, different shapes, their own endpoints
 * (`/reports/net-worth-history`, `/allocation`) — not different dropdown
 * values, so each is listed when it has a view of its own. Returns has one: its
 * spec carries `kind: 'returns'`, which the one `/reports/view` route sends to
 * the Returns view. A spec with no `kind` is the aggregation, which is what
 * every saved row from before a second report existed is.
 *
 * Cash flow is not a preset either: it is this report grouped by category KIND
 * ("Income vs expense"), reached from the Group by control and from the home
 * screen's Cash flow widget. Net grouped by CATEGORY is net per category, not
 * money in against money out, so no preset carries the name.
 *
 * Four more from the Moneydance parity list are deliberately absent for good:
 * Missing Checks, Transfers, Reconciliation (a register question — FU-058) and
 * Budget (its own surface, ADR-0099). See FU-008.
 */
export interface CatalogReport {
    readonly key: string;
    readonly name: string;
    readonly blurb: string;
    readonly spec: ReportSpec;
}

/** The default range: twelve whole months, sliding (ADR-0104 D5). */
const LAST_12_MONTHS = {
    dateMode: 'Sliding',
    windowCount: 12,
    windowUnit: 'Month',
    windowAlign: true,
} as const;

/**
 * What the view calls itself when no saved report is loaded. The report keeps
 * this name however its controls are set — it is the report's identity, not a
 * description of the current question, which the subtitle carries instead.
 */
export const AGGREGATION_REPORT_NAME = 'Income and Expenses';

/** What the Returns view calls itself when no saved report is loaded. */
export const RETURNS_REPORT_NAME = 'Returns';

export const CATALOG: readonly CatalogReport[] = [
    {
        key: 'income-and-expenses',
        name: AGGREGATION_REPORT_NAME,
        blurb: 'by category, payee, account or tag — over any range',
        spec: {
            measure: 'Spending',
            groupBy: 'Category',
            bucket: 'Month',
            rollup: true,
            ...LAST_12_MONTHS,
        },
    },
    {
        key: 'returns',
        name: RETURNS_REPORT_NAME,
        blurb: 'time-weighted index and gain, for the ledger and each account',
        // `measure` and `groupBy` are the type's required fields and mean nothing
        // here; `kind` is what routes the spec to its own view. No dates and
        // static: since inception, the whole history.
        spec: {
            kind: 'returns',
            measure: 'Net',
            groupBy: 'Account',
            bucket: 'Month',
            dateMode: 'Static',
            returnsShow: 'index',
            includeAccounts: true,
            view: 'chart',
        },
    },
];

export function findCatalogReport(key: string): CatalogReport | undefined {
    return CATALOG.find((r) => r.key === key);
}
