import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import { formatCurrency } from '@/lib/money';

import { BudgetHero } from './BudgetHero';
import { cumulative, PROJECTION_LEGEND, scheduledByDay, type ValuedCell } from './cumulative';

// The hero's projection against its ceiling, rendered directly. Every figure is
// invented: a payment on the 1st that the trailing window paid too, and an amount
// the window usually spends on a later day, so the month projects to PROJECTED
// from the band's own baseline, and each ceiling below is set against it.

const DAYS = 30;
const ELAPSED = 10;
const PAID = 100;
const LATER_DAY = 20;
const LATER = 150;
const PROJECTED = PAID + LATER;
const zeros = () => new Array<number>(DAYS).fill(0);

function hero(ceiling: number, elapsedDays = ELAPSED) {
    return render(
        <BudgetHero
            series={cumulative([{ categoryId: 'a', day: 1, amount: PAID }], DAYS)}
            normal={cumulative([
                { categoryId: 'a', day: 1, amount: PAID },
                { categoryId: 'a', day: LATER_DAY, amount: LATER },
            ], DAYS)}
            scheduled={{ debits: zeros(), credits: zeros() }}
            actualTotal={PAID}
            scheduledTotal={0}
            daysInMonth={DAYS}
            elapsedDays={elapsedDays}
            ceiling={ceiling}
            currency="USD"
            rateUnavailable={[]}
        />,
    );
}

describe('BudgetHero: over is over, at the precision the figures print', () => {
    const projected = () => screen.getByText(formatCurrency(PROJECTED, 'USD'));

    it('does not call a projection landing exactly on the ceiling past it', () => {
        hero(PROJECTED);
        expect(screen.getByText('On track to finish inside it.')).toBeInTheDocument();
        expect(projected()).not.toHaveClass('text-state-danger');
        expect(screen.queryByText(/cross it/)).toBeNull();
    });

    it('does not call a projection past the ceiling by less than the last printed digit past it', () => {
        // The ceiling prints as the same figure as the projection.
        hero(PROJECTED - 0.004);
        expect(screen.getByText('On track to finish inside it.')).toBeInTheDocument();
        expect(projected()).not.toHaveClass('text-state-danger');
        expect(screen.queryByText(/cross it/)).toBeNull();
    });

    it('calls a projection one minor unit past the ceiling past it, in danger, with its day', () => {
        hero(PROJECTED - 0.01);
        expect(screen.getByText(`If the rest of the month goes as usual, you cross it on the ${LATER_DAY}th.`)).toBeInTheDocument();
        expect(projected()).toHaveClass('text-state-danger');
        expect(screen.queryByText('On track to finish inside it.')).toBeNull();
    });

    it('calls spending past a ceiling of zero past it, from the day it began, never inside it', () => {
        // Every mark at zero: the spending on the 1st is past the ceiling from then on.
        hero(0);
        expect(screen.getByText('Past the ceiling since the 1st.')).toBeInTheDocument();
        expect(projected()).toHaveClass('text-state-danger');
        expect(screen.queryByText(/inside it/)).toBeNull();
    });

    it('says the rule its figure rests on to a screen reader', () => {
        hero(PROJECTED);
        expect(screen.getByRole('img', {
            name: new RegExp(`on track for \\${formatCurrency(PROJECTED, 'USD')} if the rest of the month goes as usual`),
        })).toBeInTheDocument();
    });

    it('states no projection to a screen reader once the month is over, as it shows none', () => {
        // A past month, or this one on its last day: no rest of the month is left.
        hero(PROJECTED, DAYS);
        expect(screen.getByText('Finished inside it.')).toBeInTheDocument();
        const chart = screen.getByRole('img', {
            name: (name) => name.startsWith(`${formatCurrency(PAID, 'USD')} spent, of ${formatCurrency(PROJECTED, 'USD')}`),
        });
        expect(chart).not.toHaveAccessibleName(/on track for/);
        expect(chart).not.toHaveAccessibleName(/if the rest of the month goes as usual/);
    });
});

describe('BudgetHero: a refund scheduled after today', () => {
    it('comes off the projection in full, so with no history the month ends at its Spent', () => {
        // Spent so far, and a refund in another category dated later in the month,
        // which the month's Spent already counts.
        const SPENT = 400;
        const REFUND = 150;
        const REFUND_DAY = 20;
        const credits = zeros();
        credits[REFUND_DAY - 1] = -REFUND;
        render(
            <BudgetHero
                series={cumulative([
                    { categoryId: 'a', day: 2, amount: SPENT },
                    { categoryId: 'b', day: REFUND_DAY, amount: -REFUND },
                ], DAYS)}
                normal={zeros()}
                scheduled={{ debits: zeros(), credits }}
                actualTotal={SPENT - REFUND}
                scheduledTotal={-REFUND}
                daysInMonth={DAYS}
                elapsedDays={ELAPSED}
                ceiling={SPENT * 2}
                currency="USD"
                rateUnavailable={[]}
            />,
        );
        const label = screen.getByText('on track for, by month end');
        expect(label.previousElementSibling).toHaveTextContent(formatCurrency(SPENT - REFUND, 'USD'));
        // The refund's date is marked, and the line under the curve says why.
        expect(document.querySelector(`[data-scheduled-step="${REFUND_DAY}"]`)).not.toBeNull();
        expect(screen.getByText(PROJECTION_LEGEND)).toBeInTheDocument();
    });
});

describe('BudgetHero: a month that has not started', () => {
    it('gives no outlook and no legend, even with its scheduled rows already past the ceiling', () => {
        // Every row of a month that has not started is scheduled, and these add up to
        // more than the ceiling. There is no projection to judge or draw.
        const BILL = 300;
        const BILL_DAY = 5;
        const LATER_BILL_DAY = 18;
        const CEILING = BILL;
        const cells: ValuedCell[] = [
            { categoryId: 'a', day: BILL_DAY, amount: BILL },
            { categoryId: 'b', day: LATER_BILL_DAY, amount: BILL },
        ];
        const series = cumulative(cells, DAYS);
        const total = series.at(-1)!;
        expect(total).toBeGreaterThan(CEILING);
        render(
            <BudgetHero
                series={series}
                normal={zeros()}
                scheduled={scheduledByDay(cells, DAYS, 0)}
                actualTotal={total}
                scheduledTotal={total}
                daysInMonth={DAYS}
                elapsedDays={0}
                ceiling={CEILING}
                currency="USD"
                rateUnavailable={[]}
            />,
        );
        // The figures under the total, which this month does show.
        expect(screen.getByText(
            `of ${formatCurrency(CEILING, 'USD')} · incl. ${formatCurrency(total, 'USD')} scheduled`,
        )).toBeInTheDocument();
        expect(screen.queryByText(/inside it/)).toBeNull();
        expect(screen.queryByText(/Past the ceiling since|you cross it on the/)).toBeNull();
        expect(screen.queryByText('on track for, by month end')).toBeNull();
        expect(screen.queryByText(PROJECTION_LEGEND)).toBeNull();
    });
});

describe('BudgetHero: the crossing agrees with the month end', () => {
    // No history, spent so far on the 2nd, and rows scheduled after today: a bill
    // that takes the path past the ceiling, then a refund that brings it back under.
    const SPENT = 400;
    const BILL = 300;
    const BILL_DAY = 15;
    const REFUND = 200;
    const REFUND_DAY = 20;
    const LATE_DAY = 25;
    const PAST_DAY = 3;
    const CEILING = SPENT + BILL - REFUND / 2;
    const cell = (day: number, amount: number, categoryId = 'a'): ValuedCell => ({ categoryId, day, amount });

    function heroOf(cells: ValuedCell[]) {
        const series = cumulative(cells, DAYS);
        const scheduled = scheduledByDay(cells, DAYS, ELAPSED);
        const total = series.at(-1)!;
        return render(
            <BudgetHero
                series={series}
                normal={zeros()}
                scheduled={scheduled}
                actualTotal={total}
                scheduledTotal={total - series[ELAPSED - 1]!}
                daysInMonth={DAYS}
                elapsedDays={ELAPSED}
                ceiling={CEILING}
                currency="USD"
                rateUnavailable={[]}
            />,
        );
    }

    const chart = () => screen.getByRole('img', { name: /spent/ });

    it('names no crossing, in words, colour or to a screen reader, for a path that passes the ceiling and ends under it', () => {
        heroOf([cell(2, SPENT), cell(BILL_DAY, BILL), cell(REFUND_DAY, -REFUND, 'b')]);
        expect(screen.getByText('On track to finish inside it.')).toBeInTheDocument();
        expect(screen.queryByText(/cross it/)).toBeNull();
        expect(screen.getByText('on track for, by month end').previousElementSibling).not.toHaveClass('text-state-danger');
        expect(chart()).not.toHaveAccessibleName(/crossing on/);
    });

    it('names the day it goes past for good, for a path that passes, comes back under, and ends past', () => {
        heroOf([cell(2, SPENT), cell(BILL_DAY, BILL), cell(REFUND_DAY, -REFUND, 'b'), cell(LATE_DAY, REFUND)]);
        expect(screen.getByText(`If the rest of the month goes as usual, you cross it on the ${LATE_DAY}th.`)).toBeInTheDocument();
        expect(chart()).toHaveAccessibleName(new RegExp(`crossing on the ${LATE_DAY}th\\.$`));
    });

    it('names the day already gone for a breach that stays past', () => {
        heroOf([cell(2, SPENT), cell(PAST_DAY, CEILING), cell(REFUND_DAY, -REFUND, 'b')]);
        expect(screen.getByText(`Past the ceiling since the ${PAST_DAY}rd.`)).toBeInTheDocument();
        expect(chart()).toHaveAccessibleName(new RegExp(`crossing on the ${PAST_DAY}rd\\.$`));
    });

    it('names no crossing for a breach already gone that a scheduled refund undoes, the month ending under', () => {
        heroOf([cell(2, SPENT), cell(PAST_DAY, CEILING - SPENT + REFUND / 4), cell(REFUND_DAY, -REFUND, 'b')]);
        expect(screen.queryByText(/Past the ceiling since/)).toBeNull();
        expect(screen.getByText('On track to finish inside it.')).toBeInTheDocument();
        expect(chart()).not.toHaveAccessibleName(/crossing on/);
    });
});
