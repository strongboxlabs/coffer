import type { ReactNode } from 'react';

import { formatCurrency } from '@/lib/money';
import { cn } from '@/lib/cn';
import type { RateUnavailable } from '@/lib/types';

import { Money } from '../home/Money';
import { crossingDay, hasScheduled, pastBy, PROJECTION_LEGEND, projectTo, type ScheduledDays } from './cumulative';
import { SpendCurve } from './SpendCurve';

/**
 * The page hero: the whole month, always. It does NOT change to show a single
 * category: losing the overall picture at the moment you start investigating a
 * part of it is exactly backwards. A category's own
 * curve opens inside its table row instead.
 *
 * WHY A CEILING AND NOT A PACE LINE. A target is a horizontal rule, because the
 * shaded band already does the pace job and does it better than any straight
 * line: it knows rent leaves on the 1st, so it does not accuse you of
 * overspending every month between the 1st and the 5th. What the ceiling adds
 * is the one genuinely actionable fact here — not "12% over" but the DATE the
 * projection crosses it.
 *
 * THE PROJECTION READS THE SAME BAND, for the same reason. "On track for" is what
 * is spent so far plus the larger of the spend still scheduled and what the band's
 * baseline usually adds over the rest of the month, plus every credit still
 * scheduled (`projectedPath`). A payment that leaves once a month, early in it, is
 * not a daily rate: carried forward as one, it would be counted many times over
 * until late in the month.
 */

interface BudgetHeroProps {
    series: readonly number[];
    /** The baseline the band is drawn around; the projection reads the same one. */
    normal: readonly number[];
    /** Rows dated after today, per day and split by sign, each on its own date: spend
     *  is a floor under the projection, not added on top of it; a credit comes off it in full. */
    scheduled: ScheduledDays;
    /** The month's whole spend, the rows dated after today included (ADR-0109 D10);
     *  null when a rate it needs is missing. */
    actualTotal: number | null;
    /** The part of `actualTotal` dated after today; null when a rate is missing. */
    scheduledTotal: number | null;
    daysInMonth: number;
    elapsedDays: number;
    /** Sum of the table's marks, or null when nothing has usable history. */
    ceiling: number | null;
    currency: string;
    /** Every missing rate a figure of the month needed (ADR-0114 D9). With any, the
     *  totals read "no rate", and nothing is drawn, projected or dated. */
    rateUnavailable: readonly RateUnavailable[];
    /** What stands where the curve would, when a rate is missing: the line naming it. */
    missingNote?: ReactNode;
}

export function BudgetHero({
    series,
    normal,
    scheduled,
    actualTotal,
    scheduledTotal,
    daysInMonth,
    elapsedDays,
    ceiling,
    currency,
    rateUnavailable,
    missingNote,
}: BudgetHeroProps) {
    // A figure that needs a missing rate is not a figure (ADR-0114 D9): the totals
    // read "no rate", and nothing is drawn, projected or dated from them.
    if (rateUnavailable.length > 0 || actualTotal === null || scheduledTotal === null) {
        return (
            <div className="grid gap-0 lg:grid-cols-[1fr_15rem]">
                <div className="p-4">{missingNote}</div>
                <div className="border-t border-border p-4 lg:border-l lg:border-t-0">
                    <Money amount={actualTotal} currency={currency} tone="fixed" rateUnavailable={rateUnavailable} className="block text-2xl font-bold" />
                    <div className="mt-1 text-[0.6875rem] text-text-muted">
                        {ceiling === null ? 'spent' : <>of <Money amount={ceiling} currency={currency} tone="fixed" className="font-normal" /></>}
                        {scheduledTotal !== 0 ? (
                            <>
                                {ceiling === null ? ' ' : ' · '}incl.{' '}
                                <Money amount={scheduledTotal} currency={currency} tone="fixed" rateUnavailable={rateUnavailable} className="font-normal" /> scheduled
                            </>
                        ) : null}
                    </div>
                </div>
            </div>
        );
    }

    const projected = projectTo(series, elapsedDays, daysInMonth, scheduled, normal);
    const crosses = ceiling === null ? null : crossingDay(series, elapsedDays, daysInMonth, ceiling, currency, scheduled, normal);
    // As the two figures print: a projection at the ceiling is not past it.
    const overBy = ceiling === null || projected === null ? null : pastBy(projected, ceiling, currency);
    const monthIsOver = elapsedDays >= daysInMonth;
    const incl = scheduledTotal !== 0 ? `incl. ${formatCurrency(scheduledTotal, currency)} scheduled` : null;

    return (
        <div className="grid gap-0 lg:grid-cols-[1fr_15rem]">
            <div className="p-4">
                <SpendCurve
                    series={series}
                    normal={normal}
                    daysInMonth={daysInMonth}
                    elapsedDays={elapsedDays}
                    ceiling={ceiling}
                    currency={currency}
                    scheduled={scheduled}
                    heightClass="h-fixed-176px"
                    ceilingLabel={`ceiling ${formatCurrency(ceiling ?? 0, currency)}`}
                    ariaLabel={summary(actualTotal, incl, ceiling, monthIsOver ? null : projected, crosses, currency)}
                />
                <div aria-hidden className="mt-1 flex justify-between text-[0.625rem] text-text-subtle">
                    <span>1</span>
                    <span>{Math.round(daysInMonth / 2)}</span>
                    <span>{daysInMonth}</span>
                </div>
                {/* Only where the curve draws a projection: a month that has not
                    started has none, so it has no circles or dashed line to explain. */}
                {hasScheduled(scheduled) && projected !== null && !monthIsOver ? (
                    <p className="mt-1 text-[0.6875rem] text-text-muted">
                        {PROJECTION_LEGEND}
                    </p>
                ) : null}
            </div>

            <div className="flex flex-col gap-3 border-t border-border p-4 lg:border-l lg:border-t-0">
                <div>
                    <div className="font-mono text-2xl font-bold tabular-nums">
                        {formatCurrency(actualTotal, currency)}
                    </div>
                    <div className="mt-1 text-[0.6875rem] text-text-muted">
                        {ceiling === null
                            ? (incl ? `spent ${incl}` : 'spent')
                            : `of ${formatCurrency(ceiling, currency)}${incl ? ` · ${incl}` : ''}`}
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
                            : `If the rest of the month goes as usual, you cross it on the ${ordinal(crosses)}.`}
                    </p>
                ) : ceiling !== null && (monthIsOver || projected !== null) ? (
                    <p className="border-t border-border pt-3 text-[0.6875rem] leading-relaxed text-text-muted">
                        {/* "Finish", not "stay": with no crossing, a month that has
                            values to judge ends inside the ceiling, though its path may
                            pass it on the way. A month that has not started has none, so
                            it gets no outlook at all. */}
                        {monthIsOver ? 'Finished inside it.' : 'On track to finish inside it.'}
                    </p>
                ) : null}
            </div>
        </div>
    );
}

/** The chart's only route to a screen reader, so it carries the conclusion. */
function summary(
    spent: number,
    incl: string | null,
    ceiling: number | null,
    /** Null once the month is over: there is no rest of the month to project, as
     *  the visible figure is hidden then too. */
    projected: number | null,
    crosses: number | null,
    currency: string,
): string {
    const parts = [`${formatCurrency(spent, currency)} spent${incl ? ` ${incl}` : ''}`];
    if (ceiling !== null) parts.push(`of ${formatCurrency(ceiling, currency)}`);
    if (projected !== null) {
        parts.push(`on track for ${formatCurrency(projected, currency)} if the rest of the month goes as usual`);
    }
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
