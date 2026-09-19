import { formatCurrency } from '@/lib/money';
import { cn } from '@/lib/cn';

import { crossingDay, projectTo } from './cumulative';
import { SpendCurve } from './SpendCurve';

/**
 * The page hero: the whole month, always. It does NOT change to show a single
 * category — an earlier cut did, and losing the overall picture at the moment
 * you start investigating a part of it is exactly backwards. A category's own
 * curve opens inside its table row instead.
 *
 * WHY A CEILING AND NOT A PACE LINE. A target is a horizontal rule, because the
 * shaded band already does the pace job and does it better than any straight
 * line: it knows rent leaves on the 1st, so it does not accuse you of
 * overspending every month between the 1st and the 5th. What the ceiling adds
 * is the one genuinely actionable fact here — not "12% over" but the DATE the
 * projection crosses it.
 */

interface BudgetHeroProps {
    series: readonly number[];
    normal: readonly number[];
    daysInMonth: number;
    elapsedDays: number;
    /** Sum of the table's marks, or null when nothing has usable history. */
    ceiling: number | null;
    currency: string;
}

export function BudgetHero({
    series,
    normal,
    daysInMonth,
    elapsedDays,
    ceiling,
    currency,
}: BudgetHeroProps) {
    const spent = elapsedDays > 0 ? (series[Math.min(elapsedDays, series.length) - 1] ?? 0) : 0;
    const projected = projectTo(series, elapsedDays, daysInMonth);
    const crosses = ceiling === null ? null : crossingDay(series, elapsedDays, daysInMonth, ceiling);
    const overBy = ceiling === null || projected === null ? null : projected - ceiling;
    const monthIsOver = elapsedDays >= daysInMonth;

    return (
        <div className="grid gap-0 lg:grid-cols-[1fr_15rem]">
            <div className="p-4">
                <SpendCurve
                    series={series}
                    normal={normal}
                    daysInMonth={daysInMonth}
                    elapsedDays={elapsedDays}
                    ceiling={ceiling}
                    heightClass="h-fixed-176px"
                    ceilingLabel={`ceiling ${formatCurrency(ceiling ?? 0, currency)}`}
                    ariaLabel={summary(spent, ceiling, projected, crosses, currency)}
                />
                <div aria-hidden className="mt-1 flex justify-between text-[0.625rem] text-text-subtle">
                    <span>1</span>
                    <span>{Math.round(daysInMonth / 2)}</span>
                    <span>{daysInMonth}</span>
                </div>
            </div>

            <div className="flex flex-col gap-3 border-t border-border p-4 lg:border-l lg:border-t-0">
                <div>
                    <div className="font-mono text-2xl font-bold tabular-nums">
                        {formatCurrency(spent, currency)}
                    </div>
                    <div className="mt-1 text-[0.6875rem] text-text-muted">
                        {ceiling === null ? 'spent so far' : `of ${formatCurrency(ceiling, currency)}`}
                    </div>
                </div>

                {/* A projection for a month that has already ended is just the
                    total again, printed twice. Suppress it. */}
                {projected !== null && !monthIsOver ? (
                    <div>
                        <div
                            className={cn(
                                'font-mono text-base font-semibold tabular-nums',
                                overBy !== null && overBy > 0 ? 'text-state-danger' : 'text-text',
                            )}
                        >
                            {formatCurrency(projected, currency)}
                        </div>
                        <div className="mt-0.5 text-[0.6875rem] text-text-muted">
                            on track for, by month end
                        </div>
                    </div>
                ) : null}

                {crosses !== null ? (
                    <p className="border-t border-border pt-3 text-[0.6875rem] leading-relaxed text-state-danger">
                        {crosses <= elapsedDays
                            ? `Past the ceiling since the ${ordinal(crosses)}.`
                            : `At this rate you cross it on the ${ordinal(crosses)}.`}
                    </p>
                ) : ceiling !== null ? (
                    <p className="border-t border-border pt-3 text-[0.6875rem] leading-relaxed text-text-muted">
                        {monthIsOver ? 'Finished inside it.' : 'On track to stay inside it.'}
                    </p>
                ) : null}
            </div>
        </div>
    );
}

/** The chart's only route to a screen reader, so it carries the conclusion. */
function summary(
    spent: number,
    ceiling: number | null,
    projected: number | null,
    crosses: number | null,
    currency: string,
): string {
    const parts = [`${formatCurrency(spent, currency)} spent so far`];
    if (ceiling !== null) parts.push(`of ${formatCurrency(ceiling, currency)}`);
    if (projected !== null) parts.push(`on track for ${formatCurrency(projected, currency)}`);
    if (crosses !== null) parts.push(`crossing on the ${ordinal(crosses)}`);
    return `${parts.join(', ')}.`;
}

function ordinal(n: number): string {
    const rem100 = n % 100;
    if (rem100 >= 11 && rem100 <= 13) return `${n}th`;
    switch (n % 10) {
        case 1: return `${n}st`;
        case 2: return `${n}nd`;
        case 3: return `${n}rd`;
        default: return `${n}th`;
    }
}
