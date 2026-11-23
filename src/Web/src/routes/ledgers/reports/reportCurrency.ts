import type { RateUnavailable } from '@/lib/types';
import type { ReportResult, ReportSpec } from '@/types/report';

/**
 * Every missing rate a figure of the result needed (ADR-0114 D9): its total's, its
 * rows', its period and group totals' and the rest's. Empty when every figure
 * converted.
 */
export function reportRateUnavailable(result: ReportResult): RateUnavailable[] {
    return [
        ...(result.rateUnavailable ?? []),
        ...result.rows.flatMap((r) => r.rateUnavailable ?? []),
        ...result.periodTotals.flatMap((t) => t.rateUnavailable ?? []),
        ...result.groupTotals.flatMap((t) => t.rateUnavailable ?? []),
        ...(result.rest?.rateUnavailable ?? []),
    ];
}

/**
 * The currencies a chart of the result would draw on one axis, when there is more than
 * one; null when every row is in the reporting currency (ADR-0114 D7).
 *
 * A row grouped by account stays in its account's own currency, while the totals and
 * the rest a chart stacks it with are converted. The server sends no converted figure
 * for such a row, so a chart would put different currencies on one scale and add them
 * in a stack. The reporting currency is in the list because the totals are in it.
 */
export function chartCurrencies(result: ReportResult): string[] | null {
    const currencies = new Set(result.rows.map((r) => r.currencyCode));
    if ([...currencies].every((c) => c === result.currencyCode)) return null;
    currencies.add(result.currencyCode);
    return [...currencies].sort();
}

/**
 * What the page says where the chart of rows in several currencies would be:
 * `currencies` from {@link chartCurrencies}, `reporting` the totals' currency.
 */
export function crossCurrencyChartText(currencies: readonly string[], reporting: string): string {
    return `No chart: each account here is in its own currency and the totals are in ${reporting}, `
        + `so a chart would draw ${listOf(currencies)} on one scale. `
        + 'The table shows each figure in its currency.';
}

function listOf(items: readonly string[]): string {
    if (items.length <= 1) return items.join('');
    return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}

/**
 * How a report reads when its figures are in more than one currency or need a missing
 * rate (ADR-0114 D7, D9). A converted figure that needs a missing rate reads "no rate"
 * where it stands, and the chart is not drawn, since a bar or a stack without it would
 * be a partial sum. Rows of accounts in currencies other than the totals' are not
 * charted either: nothing converts them, and one axis cannot hold two currencies.
 * Reports and a home report tile read the same rules here.
 */
export function currencyRules(result: ReportResult | undefined, spec: ReportSpec): {
    /** Every missing rate the result's figures needed. */
    missing: RateUnavailable[];
    /** The rows are category kinds, each a ledger-wide total. */
    kindRows: boolean;
    /** The currencies a chart would mix, when the rows are not all in the reporting currency. */
    crossCurrency: string[] | null;
    /** A chart is not drawn: it would mix currencies, or a figure it draws needs a missing rate. */
    chartWithheld: boolean;
} {
    const missing = result ? reportRateUnavailable(result) : [];
    const crossCurrency = result ? chartCurrencies(result) : null;
    return {
        missing,
        kindRows: spec.groupBy === 'CategoryKind',
        crossCurrency,
        chartWithheld: missing.length > 0 || crossCurrency !== null,
    };
}
