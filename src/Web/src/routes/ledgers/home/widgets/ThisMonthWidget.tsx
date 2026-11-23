import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { ExchangeRatesLink, MissingRateNote } from '@/components/ConversionNote';
import { Skeleton } from '@/components/ui/Skeleton';
import { fetchAccounts } from '@/lib/api';
import { buildAccountPathMap } from '@/lib/accountPath';
import { cn } from '@/lib/cn';
import { monthLabel } from '@/lib/calendar';
import { categoryDotClass, systemCategoryIds } from '@/lib/categoryChip';
import { formatCurrency } from '@/lib/money';
import type { BudgetProgress } from '@/lib/types';

import {
    budgetRateUnavailable,
    ceilingFromRows,
    crossingDay,
    cumulative,
    hasScheduled,
    markIsBudgeted,
    overspentRows,
    pastMark,
    PROJECTION_LEGEND,
    projectTo,
    scheduledByDay,
    usualByDay,
    valuedCells,
} from '../../budget/cumulative';
import { SpendCurve } from '../../budget/SpendCurve';
import { typicalBasis, usualSpan } from '../../budget/usualSpan';
import { useHome, useWidgetFrame } from '../homeContext';
import { Money } from '../Money';
import { useBudgetProgress } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * This month's spending against the mark (ADR-0099), at 160px. Spent is the whole
 * month, scheduled rows included and said so; the projection is what is spent plus
 * the larger of the spend still scheduled and what the band's baseline usually adds
 * over the rest of the month, plus every credit still scheduled (`projectedPath`),
 * turning `mark-danger` past the mark.
 * Below it, the categories already over their own mark.
 */
export function ThisMonthWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const windowMonths = Number(frame.settings.windowMonths ?? 3);
    const overRows = Number(frame.settings.overRows ?? 3);
    const month = home.today.slice(0, 7);

    const progress = useBudgetProgress(home.ledgerId, home.today, windowMonths);
    const accounts = useQuery({ queryKey: ['accounts', home.ledgerId], queryFn: () => fetchAccounts(home.ledgerId) });
    const paths = useMemo(() => buildAccountPathMap(accounts.data ?? []), [accounts.data]);
    // The ledger's Uncategorized, whose dot is always neutral.
    const systemIds = useMemo(() => systemCategoryIds(accounts.data), [accounts.data]);

    const data = progress.data;
    const link = (
        <Link to="/ledgers/$ledgerId/budget" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Budget →
        </Link>
    );
    const title = data
        ? <>This month <span className="font-normal text-text-muted">· day {data.elapsedDays} of {data.daysInMonth}</span></>
        : 'This month';

    if (progress.isError) {
        return <WidgetPanel title={title} link={link}><WidgetError what="this month's spending" error={progress.error} onRetry={() => void progress.refetch()} /></WidgetPanel>;
    }
    if (!data) {
        return (
            <WidgetPanel title={title} link={link}>
                <div aria-busy="true" aria-label="Loading this month" className="space-y-2 p-4">
                    <Skeleton className="h-control-20px w-fixed-320px max-w-full" />
                    <Skeleton className="h-fixed-160px" />
                </div>
            </WidgetPanel>
        );
    }
    if (data.rows.length === 0 || data.actualTotal === 0) {
        return (
            <WidgetPanel title={title} link={link}>
                <p className="p-4 text-sm text-text-muted">
                    No categorised spending this month
                    {data.latestMonthWithSpending ? (
                        <>
                            {' · '}
                            <Link
                                to="/ledgers/$ledgerId/budget"
                                params={{ ledgerId: home.ledgerId }}
                                search={{ month: data.latestMonthWithSpending }}
                                className="font-medium text-accent hover:underline"
                            >
                                Latest spending is in {monthName(data.latestMonthWithSpending)} →
                            </Link>
                        </>
                    ) : '.'}
                </p>
            </WidgetPanel>
        );
    }

    return (
        <WidgetPanel title={title} link={link}>
            <MonthBody data={data} month={month} overRows={overRows} paths={paths} systemIds={systemIds} />
        </WidgetPanel>
    );
}

function MonthBody({ data, month, overRows, paths, systemIds }: {
    data: BudgetProgress;
    month: string;
    overRows: number;
    paths: ReadonlyMap<string, string>;
    /** The ledger's system category ids (its Uncategorized): a neutral dot. */
    systemIds: ReadonlySet<string>;
}) {
    const home = useHome();
    const currency = data.currencyCode;
    // A month with a missing rate draws, projects and dates nothing (ADR-0114 D9).
    const missing = budgetRateUnavailable(data);
    const actualCells = valuedCells(data.dailyActual);
    const usualCells = valuedCells(data.dailyNormal);
    const unconverted = missing.length > 0 || actualCells === null || usualCells === null;
    const series = actualCells === null ? [] : cumulative(actualCells, data.daysInMonth);
    // The baseline the band is drawn around, which the projection reads too.
    const normal = usualCells === null ? [] : usualByDay(usualCells, data.daysInMonth);
    const scheduled = actualCells === null
        ? { debits: [], credits: [] }
        : scheduledByDay(actualCells, data.daysInMonth, data.elapsedDays);
    const mark = ceilingFromRows(data.rows);
    const projected = projectTo(series, data.elapsedDays, data.daysInMonth, scheduled, normal);
    const crosses = mark === null ? null
        : crossingDay(series, data.elapsedDays, data.daysInMonth, mark, currency, scheduled, normal);
    // A young ledger's typical averages fewer months, or the months since a day: said so.
    const basis = markIsBudgeted(data.rows) ? 'budgeted' : typicalBasis(usualSpan(data));
    const over = overRows > 0 ? overspentRows(data.rows, currency).slice(0, overRows) : [];
    // The month's last day leaves no rest of the month, as the Budget hero reads it.
    const monthIsOver = data.elapsedDays >= data.daysInMonth;
    const hasSteps = hasScheduled(scheduled) && !monthIsOver;
    const mon = MONTHS[Number(month.slice(5, 7)) - 1]!;
    // A date read off a figure with a missing rate means nothing, so none is given.
    const crossLabel = crosses === null || unconverted ? null
        : crosses <= data.elapsedDays ? `past the mark since ${mon} ${crosses}`
        : `projected past the mark on ${mon} ${crosses}`;
    // A projected conclusion says the rule it rests on; a crossing already past does
    // not need one, and nor does a month with none of it left to project.
    const asUsual = 'if the rest of the month goes as usual';
    const outlook = crossLabel !== null
        ? (crosses !== null && crosses > data.elapsedDays ? `${asUsual}, ${crossLabel}` : crossLabel)
        : projected !== null && mark !== null
            ? (monthIsOver ? 'finished under the mark' : `${asUsual}, projected to finish under the mark`)
            : 'no mark yet';
    const aria = `Spent ${formatCurrency(data.actualTotal, currency)}`
        + (data.scheduledTotal !== 0 ? ` including ${formatCurrency(data.scheduledTotal, currency)} scheduled` : '')
        + (mark !== null ? ` of a ${formatCurrency(mark, currency)} mark` : '')
        + ` by day ${data.elapsedDays}; `
        + outlook;

    return (
        <div className="space-y-2 p-4">
            {/* One run of text. Each separator is held to the phrase before it by a
                no-break space, so a line breaks after a "·" and never starts with one. */}
            <p className="text-sm text-text-muted">
                <span className="whitespace-nowrap">
                    Spent <Money amount={data.actualTotal} currency={currency} tone="fixed" rateUnavailable={data.rateUnavailable} />
                    {data.scheduledTotal === 0 ? ' ·' : null}
                </span>{' '}
                {data.scheduledTotal !== 0 ? (
                    <>
                        <span className="whitespace-nowrap">
                            incl. <Money amount={data.scheduledTotal} currency={currency} tone="fixed" rateUnavailable={data.rateUnavailable} className="font-normal" /> scheduled&nbsp;·
                        </span>{' '}
                    </>
                ) : null}
                {mark !== null ? (
                    <>
                        <span className="whitespace-nowrap">
                            mark <Money amount={mark} currency={currency} tone="fixed" className="font-normal" />&nbsp;·
                        </span>{' '}
                    </>
                ) : null}
                <span className="whitespace-nowrap">{basis}</span>
                {crossLabel ? <span className="block text-state-danger">{crossLabel}</span> : null}
            </p>
            {unconverted ? (
                <MissingRateNote missing={missing} ratesLink={<ExchangeRatesLink ledgerId={home.ledgerId} />} />
            ) : (
                <>
                    <SpendCurve
                        series={series}
                        normal={normal}
                        daysInMonth={data.daysInMonth}
                        elapsedDays={data.elapsedDays}
                        ceiling={mark}
                        currency={currency}
                        scheduled={scheduled}
                        heightClass="h-fixed-160px"
                        ceilingLabel="mark"
                        ariaLabel={aria}
                    />
                    <MonthAxis mon={mon} elapsedDays={data.elapsedDays} daysInMonth={data.daysInMonth} />
                    {hasSteps ? (
                        <p className="text-xs text-text-muted">{PROJECTION_LEGEND}</p>
                    ) : null}
                </>
            )}
            {over.length > 0 ? (
                <div className="flex flex-wrap items-center gap-x-4 gap-y-1 border-t border-border/60 pt-2 text-xs text-text-muted">
                    <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">Over the mark</span>
                    {over.map((r) => (
                        <Link
                            key={r.categoryId}
                            to="/ledgers/$ledgerId/accounts/$accountId"
                            params={{ ledgerId: home.ledgerId, accountId: r.categoryId }}
                            search={{ subcategories: true }}
                            className="text-text hover:underline"
                            title={paths.get(r.categoryId) ?? r.name}
                        >
                            {/* Inline text, so a narrow panel wraps between the pieces, never inside one. */}
                            <span aria-hidden className={`mr-1.5 inline-block size-dot-sm rounded-full align-middle ${categoryDotClass(r.name, r.categoryId, systemIds.has(r.categoryId))}`} />
                            {paths.get(r.categoryId) ?? r.name}{' '}
                            <span className="whitespace-nowrap">
                                <span className="font-mono tabular-nums text-state-danger">
                                    +{formatCurrency(pastMark(r, currency), currency)} over
                                </span>
                                <span className="text-text-muted">&nbsp;·</span>
                            </span>{' '}
                            <span className="whitespace-nowrap text-text-muted">mark {formatCurrency(r.mark ?? 0, currency)}</span>
                        </Link>
                    ))}
                </div>
            ) : null}
        </div>
    );
}

/**
 * The day axis: the month's first day, today at the marker, and its last day.
 * Today's label leans toward the middle, starting at the marker in the first half
 * and ending at it in the second, so it never runs off the chart. An end label
 * that today's would overlap gives way. How close is too close is a share of the
 * chart's width, which differs by panel size, so both versions are drawn and the
 * panel's container query shows one.
 */
function MonthAxis({ mon, elapsedDays, daysInMonth }: { mon: string; elapsedDays: number; daysInMonth: number }) {
    const showToday = elapsedDays > 1 && elapsedDays < daysInMonth;
    const at = (elapsedDays - 1) / Math.max(daysInMonth - 1, 1);
    // Room for one label and a gap: a share of the chart, compact panels and from @xl up.
    const gives = (distance: number) =>
        !showToday ? '' : distance < 0.08 ? 'hidden' : distance < 0.16 ? 'hidden @xl/panel:inline' : '';
    return (
        <div aria-hidden className="relative h-control-20px text-[0.625rem] text-text-subtle">
            <span className={cn('absolute left-0', gives(at))}>{mon} 1</span>
            {showToday ? (
                <span
                    className={cn('absolute whitespace-nowrap', at > 0.5 && '-translate-x-full')}
                    style={{ left: `${at * 100}%` }}
                >
                    {mon} {elapsedDays}
                </span>
            ) : null}
            <span className={cn('absolute right-0', gives(1 - at))}>{mon} {daysInMonth}</span>
        </div>
    );
}

function monthName(yyyyMm: string): string {
    const [y, m] = yyyyMm.split('-').map(Number);
    return monthLabel(y!, m!);
}
