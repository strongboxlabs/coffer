import { cn } from '@/lib/cn';
import { formatCurrency } from '@/lib/money';
import type { ReportResult, ReportSpec } from '@/types/report';

import type { RateUnavailable } from '@/lib/types';

import { NoRate } from '../home/Money';
import { GroupName } from './GroupName';
import { GROUP_LABELS, groupNameParts, leafOnly } from './groupLabel';
import { currencyRules } from './reportCurrency';
import type { PivotRow } from './ReportView';

/** Indent per depth. A lookup, because Tailwind cannot see a computed class. */
const DEPTH_PAD = ['', 'pl-3', 'pl-6', 'pl-8', 'pl-10'] as const;

function padFor(depth: number): string {
    return DEPTH_PAD[Math.min(depth, DEPTH_PAD.length - 1)];
}

/**
 * A report as a table: one row per group, one column per period, the server's
 * totals underneath, and the notes that say what the rows do and do not add up to.
 * The table scrolls inside `scrollClassName`; the notes stay under it.
 */
export function ReportTable({ spec, result, rows, periods, scrollClassName }: {
    spec: ReportSpec;
    result: ReportResult;
    /** The groups, as `pivot` orders them, with no direct rows. */
    rows: readonly PivotRow[];
    periods: readonly string[];
    scrollClassName: string;
}) {
    // The totals combine accounts, so they are in the reporting currency; each row is in
    // its own, an account's own currency when grouped by account (ADR-0114 D7).
    const reporting = result.currencyCode;
    const periodTotals = new Map(result.periodTotals.map((t) => [t.period, t]));
    const { missing } = currencyRules(result, spec);
    const figure = (
        amount: number | null, rateUnavailable: readonly RateUnavailable[] | undefined, currency: string,
    ) => (amount === null
        ? <NoRate missing={rateUnavailable && rateUnavailable.length > 0 ? rateUnavailable : missing} />
        : formatCurrency(amount, currency));

    return (
        <>
            <div className={scrollClassName}>
                <table className="w-full border-collapse text-xs">
                    <thead>
                        <tr>
                            {/* The corner: sticky both ways, so it
                                never slides under either neighbour. */}
                            <th className="sticky top-0 left-0 z-20 border-b border-border bg-surface-muted py-1 pr-2 text-left text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                                {GROUP_LABELS[spec.groupBy] ?? spec.groupBy}
                            </th>
                            {periods.map((p) => (
                                <th key={p} className="sticky top-0 z-10 border-b border-border bg-surface-muted px-2 py-1 text-right text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                                    {p}
                                </th>
                            ))}
                            <th className="sticky top-0 z-10 border-b border-border bg-surface-muted px-2 py-1 text-right text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                                Total
                            </th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map((row) => (
                            <tr key={row.key}>
                                <td className={cn(
                                    'sticky left-0 z-10 border-b border-border bg-surface-muted py-1 pr-2',
                                    padFor(row.depth),
                                    row.depth === 0 ? 'font-medium text-text' : 'text-text-muted',
                                )}>
                                    {/* Indented, a row's parent is the row above it,
                                        which names it. At the top level nothing
                                        does, so a subcategory whose parent has no
                                        row here is shown under its whole path. */}
                                    <GroupName wrap parts={row.depth === 0
                                        ? groupNameParts(spec.groupBy, row.name, row.path)
                                        : leafOnly(spec.groupBy, row.name)} />

                                </td>
                                {periods.map((p) => (
                                    <td key={p} className="border-b border-border px-2 py-1 text-right font-mono tabular-nums text-text">
                                        {row.byPeriod.has(p) ? figure(row.byPeriod.get(p)!, row.rateUnavailable, row.currencyCode) : '—'}
                                    </td>
                                ))}
                                <td className="border-b border-border px-2 py-1 text-right font-mono tabular-nums text-text">
                                    {figure(row.total, row.rateUnavailable, row.currencyCode)}
                                </td>
                            </tr>
                        ))}
                        <tr>
                            <td className="sticky left-0 z-10 border-t border-border-strong bg-surface-muted py-1 pr-2 font-semibold text-text">Total</td>
                            {/* The server's per-period totals, never a sum of the
                                rows above: under roll-up those overlap, and under
                                top-N some are not listed. */}
                            {periods.map((p) => {
                                const periodTotal = periodTotals.get(p);
                                return (
                                    <td key={p} className="border-t border-border-strong px-2 py-1 text-right font-mono tabular-nums text-text">
                                        {periodTotal === undefined ? '' : figure(periodTotal.amount, periodTotal.rateUnavailable, reporting)}
                                    </td>
                                );
                            })}
                            <td className="border-t border-border-strong px-2 py-1 text-right font-mono font-semibold tabular-nums text-text">
                                {figure(result.total, result.rateUnavailable, reporting)}
                            </td>
                        </tr>
                    </tbody>
                </table>
            </div>

            <div className="shrink-0">
                {spec.topN !== undefined ? (
                    <p className="mt-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                        Showing the top {spec.topN} by size. The total is every row in
                        range, including the ones not listed — so these rows add up to
                        less than it.
                    </p>
                ) : null}
                {spec.rollup && spec.groupBy === 'Category' ? (
                    <p className="mt-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                        Roll-up is on: a parent row already includes its children, so the rows
                        are subtotals. The total is the true sum of postings, not the sum of
                        the rows above it.
                    </p>
                ) : null}
                {spec.groupBy === 'Tag' ? (
                    <p className="mt-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                        A transaction tagged more than once is counted under each of its tags,
                        so these rows and their total can come to more than the same range&apos;s
                        expenses.
                    </p>
                ) : null}
            </div>
        </>
    );
}
