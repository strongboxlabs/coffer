import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { ChartFrame, VIEWBOX } from '@/components/chart/ChartFrame';
import { ExchangeRatesLink, MissingRateNote } from '@/components/ConversionNote';
import { Bar } from '@/components/chart/Series';
import { band, extent, vertical } from '@/components/chart/scale';
import { Skeleton } from '@/components/ui/Skeleton';
import { fetchReportSummary } from '@/lib/api';
import { cn } from '@/lib/cn';
import { formatCurrency } from '@/lib/money';
import type { ReportResult, ReportSpec } from '@/types/report';

import { useHome, useWidgetFrame } from '../homeContext';
import { reportRateUnavailable } from '../../reports/reportCurrency';
import { Money } from '../Money';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** The cash-flow question as Reports asks it, so the head link opens the same numbers. */
function cashFlowSpec(months: number): ReportSpec {
    return {
        measure: 'Net',
        groupBy: 'CategoryKind',
        bucket: 'Month',
        dateMode: 'Sliding',
        windowCount: months,
        windowUnit: 'Month',
        windowAlign: true,
    };
}

/**
 * Income against expenses, month by month. Income stands above zero and expenses
 * hang below it, both neutral, told apart by side; a tick marks each month's net,
 * `mark-danger` below zero. Every figure is a server row or a server total: the
 * chart never sums two rows into a net.
 */
export function CashFlowWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const months = Number(frame.settings.months ?? 12);
    const showTable = frame.settings.showTable !== false;
    const spec = cashFlowSpec(months);

    const query = useQuery({
        queryKey: ['home', home.ledgerId, 'cash-flow', months, home.today],
        queryFn: () => fetchReportSummary(home.ledgerId, spec),
    });

    const link = (
        <Link
            to="/ledgers/$ledgerId/reports/view"
            params={{ ledgerId: home.ledgerId }}
            search={spec}
            className="text-xs font-medium text-accent hover:underline"
        >
            Report →
        </Link>
    );
    const title = <>Cash flow <span className="font-normal text-text-muted">· last {months} months</span></>;

    if (query.isError) {
        return <WidgetPanel title={title} link={link}><WidgetError what="cash flow" error={query.error} onRetry={() => void query.refetch()} /></WidgetPanel>;
    }
    if (!query.data) {
        return (
            <WidgetPanel title={title} link={link}>
                <div aria-busy="true" aria-label="Loading cash flow" className="p-4"><Skeleton className="h-fixed-160px" /></div>
            </WidgetPanel>
        );
    }
    const result = query.data;
    if (result.rows.length === 0) {
        return (
            <WidgetPanel title={title} link={link}>
                <p className="p-4 text-sm text-text-muted">
                    No income or spending categorised yet ·{' '}
                    <Link to="/ledgers/$ledgerId/categories" params={{ ledgerId: home.ledgerId }} className="font-medium text-accent hover:underline">
                        Categories →
                    </Link>
                </p>
            </WidgetPanel>
        );
    }

    return (
        <WidgetPanel title={title} link={link}>
            <div className="space-y-3 p-4">
                {/* No bar is drawn while a figure needs a missing rate (ADR-0114 D9). */}
                {reportRateUnavailable(result).length > 0
                    ? <MissingRateNote missing={reportRateUnavailable(result)} ratesLink={<ExchangeRatesLink ledgerId={home.ledgerId} />} />
                    : <CashFlowChart result={result} thisMonth={home.today.slice(0, 7)} />}
                <p className="text-xs text-text-muted">Income and expense categories; transfers excluded.</p>
                {showTable ? <CashFlowTable result={result} months={months} today={home.today} /> : null}
            </div>
        </WidgetPanel>
    );
}

/** A month's figure for a kind: zero with no row, null when the row needs a missing rate. */
const cell = (result: ReportResult, period: string, kind: string): number | null => {
    const row = result.rows.find((r) => r.period === period && r.groupName === kind);
    return row === undefined ? 0 : row.amount;
};

/** Whether every figure is present, which is when a chart may draw them. */
const allValued = (xs: readonly (number | null)[]): xs is number[] => xs.every((x) => x !== null);

function CashFlowChart({ result, thisMonth }: { result: ReportResult; thisMonth: string }) {
    const home = useHome();
    const periods = result.periodTotals.map((p) => p.period);
    const currency = result.currencyCode;
    const income = periods.map((p) => cell(result, p, 'income'));
    const expense = periods.map((p) => cell(result, p, 'expense'));
    const net = result.periodTotals.map((p) => p.amount);
    if (!allValued(income) || !allValued(expense) || !allValued(net)) return null;
    const [lo, hi] = extent([...income, ...expense, ...net]);
    const y = vertical([lo, hi], VIEWBOX.height);
    const b = band(periods.length, VIEWBOX.width);
    const barWidth = b.width * 0.5;
    const pct = (v: number) => (y(v) / VIEWBOX.height) * 100;
    const below = periods.filter((_, i) => net[i]! < 0);
    const aria = `Income and expenses by month over ${periods.length} months`
        + (below.length > 0 ? `; net below zero in ${below.map(label).join(', ')}` : '; net at or above zero every month');

    return (
        <div>
            <ChartFrame
                heightClass="h-fixed-160px"
                ariaLabel={aria}
                overlay={
                    <>
                        <div aria-hidden className="pointer-events-none absolute inset-x-0 border-t border-text-subtle" style={{ top: `${pct(0)}%` }} />
                        {net.map((v, i) => (
                            <span
                                key={periods[i]}
                                aria-hidden
                                data-net-tick={periods[i]}
                                className={cn('pointer-events-none absolute h-fixed-2px -translate-y-1/2', v < 0 ? 'bg-mark-danger' : 'bg-text')}
                                style={{
                                    left: `${((b.center(i) - barWidth * 0.7) / VIEWBOX.width) * 100}%`,
                                    width: `${((barWidth * 1.4) / VIEWBOX.width) * 100}%`,
                                    top: `${pct(v)}%`,
                                }}
                            />
                        ))}
                        {/* Each month opens the same report for that month, by category. */}
                        {periods.map((p, i) => (
                            <Link
                                key={p}
                                to="/ledgers/$ledgerId/reports/view"
                                params={{ ledgerId: home.ledgerId }}
                                search={{
                                    measure: 'Net', groupBy: 'Category', bucket: 'None', dateMode: 'Static',
                                    from: `${p}-01T00:00:00Z`, to: `${nextMonth(p)}-01T00:00:00Z`, rollup: true,
                                }}
                                aria-label={`${label(p)}: income ${formatCurrency(income[i], currency)}, expenses ${formatCurrency(expense[i], currency)}, net ${formatCurrency(net[i], currency)}. Open by category`}
                                className="absolute inset-y-0 rounded-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
                                style={{ left: `${(i / periods.length) * 100}%`, width: `${100 / periods.length}%` }}
                            />
                        ))}
                    </>
                }
            >
                {periods.map((p, i) => {
                    const x = b.center(i) - barWidth / 2;
                    const current = p === thisMonth;
                    return (
                        <g key={p}>
                            <Bar x={x} width={barWidth} baselineY={y(0)} valueY={y(income[i]!)} className="fill-chip-neutral-text/60" hatched={current} />
                            <Bar x={x} width={barWidth} baselineY={y(0)} valueY={y(expense[i]!)} className="fill-text-subtle/40" hatched={current} />
                        </g>
                    );
                })}
            </ChartFrame>
            <div aria-hidden className="mt-1 flex justify-between text-[0.625rem] text-text-subtle">
                <span>{label(periods[0]!)}</span>
                <span>{periods.at(-1) === thisMonth ? `${MONTHS[Number(thisMonth.slice(5, 7)) - 1]} (MTD)` : label(periods.at(-1)!)}</span>
            </div>
        </div>
    );
}

function CashFlowTable({ result, months, today }: { result: ReportResult; months: number; today: string }) {
    const thisMonth = today.slice(0, 7);
    const lastMonth = previousMonth(thisMonth);
    const currency = result.currencyCode;
    const missing = reportRateUnavailable(result);
    const netFor = (p: string) => {
        const t = result.periodTotals.find((x) => x.period === p);
        return t === undefined ? 0 : t.amount;
    };
    const group = (kind: string) => {
        const g = result.groupTotals.find((x) => x.groupName === kind);
        return g === undefined ? 0 : g.amount;
    };
    const head = 'px-2 py-1 text-right text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle';
    const num = 'px-2 py-1 text-right';
    // "Last month" drops first on a narrow widget; This month and the window stay.
    const narrowHide = 'hidden @xs/panel:table-cell';

    return (
        // Two figure columns of a long amount each need the smaller size to fit a compact panel.
        <table className="w-full border-collapse text-xs @xs/panel:text-sm">
            <thead>
                <tr>
                    <th className="sr-only">Row</th>
                    <th className={head}>This month</th>
                    <th className={cn(head, narrowHide)}>Last month</th>
                    <th className={head}>{months} months</th>
                </tr>
            </thead>
            <tbody>
                <tr className="border-t border-border/60">
                    <th scope="row" className="py-1 pr-2 text-left font-normal">Income</th>
                    <td className={num}><Money amount={cell(result, thisMonth, 'income')} currency={currency} tone="fixed" rateUnavailable={missing} className="font-normal" /></td>
                    <td className={cn(num, narrowHide)}><Money amount={cell(result, lastMonth, 'income')} currency={currency} tone="fixed" rateUnavailable={missing} className="font-normal" /></td>
                    <td className={num}><Money amount={group('income')} currency={currency} tone="fixed" rateUnavailable={missing} className="font-normal" /></td>
                </tr>
                <tr className="border-t border-border/60">
                    <th scope="row" className="py-1 pr-2 text-left font-normal">Expenses</th>
                    <td className={num}><Money amount={cell(result, thisMonth, 'expense')} currency={currency} tone="fixed" rateUnavailable={missing} className="font-normal" /></td>
                    <td className={cn(num, narrowHide)}><Money amount={cell(result, lastMonth, 'expense')} currency={currency} tone="fixed" rateUnavailable={missing} className="font-normal" /></td>
                    <td className={num}><Money amount={group('expense')} currency={currency} tone="fixed" rateUnavailable={missing} className="font-normal" /></td>
                </tr>
                <tr className="border-t border-border">
                    <th scope="row" className="py-1 pr-2 text-left font-medium">Net</th>
                    <td className={num}><Money amount={netFor(thisMonth)} currency={currency} tone="news" rateUnavailable={missing} /></td>
                    <td className={cn(num, narrowHide)}><Money amount={netFor(lastMonth)} currency={currency} tone="news" rateUnavailable={missing} /></td>
                    <td className={num}><Money amount={result.total} currency={currency} tone="news" rateUnavailable={missing} /></td>
                </tr>
            </tbody>
        </table>
    );
}

/** `2026-10` → `Oct '26`. */
function label(period: string): string {
    return `${MONTHS[Number(period.slice(5, 7)) - 1]} '${period.slice(2, 4)}`;
}

function nextMonth(period: string): string {
    const [y, m] = period.split('-').map(Number);
    return m === 12 ? `${y! + 1}-01` : `${y}-${String(m! + 1).padStart(2, '0')}`;
}

function previousMonth(period: string): string {
    const [y, m] = period.split('-').map(Number);
    return m === 1 ? `${y! - 1}-12` : `${y}-${String(m! - 1).padStart(2, '0')}`;
}
