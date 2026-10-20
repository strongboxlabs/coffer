import type { ReportSpec } from '../../../types/report';

/**
 * The canned reports (FU-008, ADR-0104 D1) — each one a preset `ReportSpec`
 * rendered by the same view, never its own query code.
 *
 * THERE IS CURRENTLY ONE, AND SAYING SO IS THE POINT.
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
 * So: one report, named for what it covers. Discovery happens in the controls,
 * which is where the options actually live.
 *
 * ONLY the transaction-aggregation family is here. Net worth, account
 * balances, asset allocation, cost basis, capital gains and performance are
 * real reports and are NOT listed, because they are served by
 * `AccountsReportingRepository` and `InvestmentReportingRepository`, which have
 * no HTTP surface yet. A card that 404s is worse than a card that is absent:
 * it promises the feature exists and blames the reader's ledger when it does
 * not appear. They arrive with their endpoints — and they ARE different
 * reports, different repositories and different shapes, not different dropdown
 * values, so the list grows for real then.
 *
 * "Cash flow" is gone for a third reason and should not come back as a preset.
 * It was Net grouped by CATEGORY, which is net-per-category — not money in
 * versus money out. The honest version needs grouping by category KIND, and
 * `ReportGroupBy` has no such member; the server computes the kind but
 * `ReportRow` does not carry it. A familiar name over the wrong numbers is the
 * kind of thing nobody checks.
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
];

export function findCatalogReport(key: string): CatalogReport | undefined {
    return CATALOG.find((r) => r.key === key);
}
