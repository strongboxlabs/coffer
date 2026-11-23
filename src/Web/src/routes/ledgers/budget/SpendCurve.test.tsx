import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';

import type { ScheduledDays } from './cumulative';
import { SpendCurve } from './SpendCurve';

// CHARACTERISATION for a refactor onto the shared chart kit (ADR-0104 D6):
// these tell a refactor that changes the picture from one that does not.
//
// These assert GEOMETRY, not implementation: the points a reader can see, at
// coordinates derived here independently of the component. If the kit version
// draws the same curve, every one of these still passes unchanged.

const VB_W = 600;
const VB_H = 180;

const SERIES = [10, 25, 40, 55, 70, 85, 100];   // cumulative, 7 days elapsed
const NORMAL = [12, 24, 36, 48, 60, 72, 84, 96, 108, 120];
const DAYS = 10;
const ELAPSED = 7;
const SPENT = SERIES[ELAPSED - 1]!;
/** What the baseline usually adds from the day after ELAPSED through `day`. */
const usualThrough = (day: number) => NORMAL[day - 1]! - NORMAL[ELAPSED - 1]!;
/** With nothing scheduled, the projection's last point: spent plus the usual rest of the month. */
const PROJECTED_END = SPENT + usualThrough(DAYS);

/** The component's own scale, re-derived rather than imported. */
function scales(series: readonly number[], normal: readonly number[],
    daysInMonth: number, elapsedDays: number, ceiling: number | null, projectedHigh = PROJECTED_END) {
    const spent = elapsedDays > 0 ? (series[Math.min(elapsedDays, series.length) - 1] ?? 0) : 0;
    // Every point drawn: each day so far, the ceiling, the projection and the band's upper edge.
    const top = Math.max(
        ...series.slice(0, Math.min(elapsedDays, daysInMonth)),
        ceiling ?? 0, projectedHigh, ...normal.map((v) => v * 1.14), 1,
    ) * 1.18;
    return {
        x: (day: number) => ((day - 1) / Math.max(daysInMonth - 1, 1)) * VB_W,
        y: (v: number) => VB_H - (v / top) * VB_H,
        spent, top,
    };
}

function draw(ceiling: number | null = 110, elapsed = ELAPSED) {
    return render(
        <SpendCurve
            series={SERIES}
            normal={NORMAL}
            daysInMonth={DAYS}
            elapsedDays={elapsed}
            ceiling={ceiling}
            currency="USD"
            heightClass="h-fixed-176px"
            ariaLabel="spend so far"
        />,
    );
}

const polylines = (c: HTMLElement) => [...c.querySelectorAll('polyline')];
const first = (pts: string) => pts.trim().split(' ')[0]!;
const last = (pts: string) => pts.trim().split(' ').at(-1)!;

describe('SpendCurve draws the month to scale', () => {
    it('plots the solid line over the elapsed days only', () => {
        const { container } = draw();
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 110);

        // The solid line is the one that is not dashed.
        const solid = polylines(container).find((p) => !p.getAttribute('stroke-dasharray'));
        expect(solid).toBeDefined();

        const pts = solid!.getAttribute('points')!;
        expect(pts.trim().split(' ')).toHaveLength(ELAPSED);
        expect(first(pts)).toBe(`${s.x(1).toFixed(1)},${s.y(SERIES[0]!).toFixed(1)}`);
        expect(last(pts)).toBe(
            `${s.x(ELAPSED).toFixed(1)},${s.y(SERIES[ELAPSED - 1]!).toFixed(1)}`);
    });

    it('stops the solid line dead at today and projects the rest dashed', () => {
        // The projection carries on from what is spent, so it must START where
        // the real data stops — a gap or an overlap would both misread.
        const { container } = draw();
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 110);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed.length).toBeGreaterThan(0);

        const projStart = first(dashed[0]!.getAttribute('points')!);
        expect(projStart).toBe(`${s.x(ELAPSED).toFixed(1)},${s.y(SPENT).toFixed(1)}`);
    });

    it('projects what the baseline usually adds after today, day by day', () => {
        const { container } = draw(10_000);
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 10_000);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        const points = dashed[0]!.getAttribute('points')!.trim().split(' ');
        expect(points).toHaveLength(DAYS - ELAPSED + 1);
        for (let d = ELAPSED; d <= DAYS; d++) {
            expect(points[d - ELAPSED]).toBe(`${s.x(d).toFixed(1)},${s.y(SPENT + usualThrough(d)).toFixed(1)}`);
        }
    });

    it('splits the projection where it crosses the ceiling, so only the problem is red', () => {
        // The projection adds to SPENT what NORMAL adds on each day after ELAPSED.
        // The ceiling passed here sits between the second-last and the last
        // projected values, so it is crossed on the LAST step, which leaves a real
        // segment either side.
        expect(SPENT + usualThrough(DAYS - 1)).toBeLessThan(130);
        expect(PROJECTED_END).toBeGreaterThan(130);
        const { container } = draw(130);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed).toHaveLength(2);
        expect(dashed[0]!.getAttribute('class')).toContain('stroke-accent');
        expect(dashed[1]!.getAttribute('class')).toContain('stroke-mark-danger');

        // The two segments MEET: the red one starts at the last under-ceiling
        // point, so the line is continuous rather than jumping the crossing.
        expect(first(dashed[1]!.getAttribute('points')!))
            .toBe(last(dashed[0]!.getAttribute('points')!));
    });

    it('draws the projection entirely red when it is over from the first step', () => {
        // Found by writing these: with the default ceiling, which sits between
        // the spend so far and the first projected step, the projection is over
        // on the day after ELAPSED, so the under-ceiling part is a SINGLE point — and a
        // one-point polyline is not drawn at all. The result is one red dashed
        // line, which is the honest picture, but it is behaviour nobody had
        // stated and a refactor could silently change.
        const { container } = draw(110);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed).toHaveLength(1);
        expect(dashed[0]!.getAttribute('class')).toContain('stroke-mark-danger');
    });

    it('leaves the projection one colour when it never reaches the ceiling', () => {
        // The discriminating pair for the test above: same data, a ceiling the
        // projection cannot reach, so nothing is red.
        const { container } = draw(10_000);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed).toHaveLength(1);
        expect(dashed[0]!.getAttribute('class')).toContain('stroke-accent');
    });

    it('draws nothing red for a projection past the ceiling by less than the last printed digit', () => {
        // The projection ends at PROJECTED_END, which prints as the same figure as
        // this ceiling: at it, not past it.
        const { container } = draw(PROJECTED_END - 0.0001);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed).toHaveLength(1);
        expect(dashed[0]!.getAttribute('class')).toContain('stroke-accent');
    });

    it('draws a projection one minor unit past the ceiling red where it passes', () => {
        const { container } = draw(PROJECTED_END - 0.01);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed).toHaveLength(2);
        expect(dashed[1]!.getAttribute('class')).toContain('stroke-mark-danger');
    });

    it('draws the normal band as one closed polygon, out and back', () => {
        const { container } = draw();
        const band = container.querySelector('polygon');
        expect(band).not.toBeNull();
        // One point per day on the way out and one on the way back.
        expect(band!.getAttribute('points')!.trim().split(' ')).toHaveLength(DAYS * 2);
    });

    it('places the ceiling rule at the ceiling’s own height', () => {
        const { container } = draw(110);
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 110);

        const rule = container.querySelector<HTMLElement>('.border-dashed');
        expect(rule).not.toBeNull();
        expect(rule!.style.top).toMatch(/%$/);
        expect(Number.parseFloat(rule!.style.top)).toBeCloseTo((s.y(110) / VB_H) * 100, 3);
    });

    it('omits the ceiling rule when there is no ceiling', () => {
        const { container } = draw(null);
        expect(container.querySelector('.border-dashed')).toBeNull();
    });

    it('hides the today marker once the month is complete', () => {
        // Anchored against the mid-month case, so this cannot pass on a
        // component that never draws the marker at all.
        const mid = draw(110, ELAPSED);
        expect(mid.container.querySelector('.w-px')).not.toBeNull();
        mid.unmount();

        const done = draw(110, DAYS);
        expect(done.container.querySelector('.w-px')).toBeNull();
    });

    /** The curve with one row scheduled on BILL_DAY, under a ceiling it never reaches:
     *  a bill when `amount` is positive, a credit when it is negative. */
    const BILL_DAY = 9;
    function drawBill(amount: number) {
        const debits = new Array<number>(DAYS).fill(0);
        const credits = new Array<number>(DAYS).fill(0);
        (amount < 0 ? credits : debits)[BILL_DAY - 1] = amount;
        return render(
            <SpendCurve
                series={SERIES} normal={NORMAL} daysInMonth={DAYS} elapsedDays={ELAPSED}
                ceiling={10_000} currency="USD" scheduled={{ debits, credits }} heightClass="h-fixed-160px" ariaLabel="with a bill"
            />,
        );
    }

    it('steps the projection up on a bill\'s date when the bill is larger than the usual days so far', () => {
        // The bill is more than the baseline adds through its date, so from that
        // date the line is spent plus the bill, until the usual days pass it.
        const BILL = usualThrough(BILL_DAY) + 6;
        const { container } = drawBill(BILL);
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 10_000, Math.max(SPENT + BILL, PROJECTED_END));

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        const points = dashed[0]!.getAttribute('points')!.trim().split(' ');
        // Today, the day after, the bill's day twice (before and after it), the last day.
        expect(points).toHaveLength(5);
        expect(points[2]).toBe(`${s.x(BILL_DAY).toFixed(1)},${s.y(SPENT + usualThrough(BILL_DAY)).toFixed(1)}`);
        expect(points[3]).toBe(`${s.x(BILL_DAY).toFixed(1)},${s.y(SPENT + BILL).toFixed(1)}`);
        expect(points[4]).toBe(`${s.x(DAYS).toFixed(1)},${s.y(Math.max(SPENT + BILL, PROJECTED_END)).toFixed(1)}`);

        const circles = container.querySelectorAll<HTMLElement>('[data-scheduled-step]');
        expect(circles).toHaveLength(1);
        expect(circles[0]!.getAttribute('data-scheduled-step')).toBe(String(BILL_DAY));
        expect(circles[0]!.style.top).toMatch(/%$/);
        expect(Number.parseFloat(circles[0]!.style.top)).toBeCloseTo((s.y(SPENT + BILL) / VB_H) * 100, 3);
    });

    it('marks a bill the usual days already hold on the line, without a step', () => {
        // Counting it on top of the usual days would count it twice.
        const BILL = usualThrough(BILL_DAY) - 6;
        const { container } = drawBill(BILL);
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 10_000);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        const points = dashed[0]!.getAttribute('points')!.trim().split(' ');
        const onTheLine = `${s.x(BILL_DAY).toFixed(1)},${s.y(SPENT + usualThrough(BILL_DAY)).toFixed(1)}`;
        expect(points[2]).toBe(onTheLine);
        expect(points[3]).toBe(onTheLine);
        expect(last(dashed[0]!.getAttribute('points')!)).toBe(`${s.x(DAYS).toFixed(1)},${s.y(PROJECTED_END).toFixed(1)}`);

        const circle = container.querySelector<HTMLElement>('[data-scheduled-step]');
        expect(circle).not.toBeNull();
        expect(circle!.style.top).toMatch(/%$/);
        expect(Number.parseFloat(circle!.style.top)).toBeCloseTo((s.y(SPENT + usualThrough(BILL_DAY)) / VB_H) * 100, 3);
    });

    it('steps the projection down by all of a scheduled credit on its date, where the usual days are larger', () => {
        // Nothing else is scheduled, so the usual days carry the path, and the credit
        // comes off it in full from its date.
        const CREDIT = 5;
        const { container } = drawBill(-CREDIT);
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 10_000, PROJECTED_END - CREDIT);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        const points = dashed[0]!.getAttribute('points')!.trim().split(' ');
        // Today, the day after, the credit's day twice (before and after it), the last day.
        expect(points).toHaveLength(5);
        expect(points[2]).toBe(`${s.x(BILL_DAY).toFixed(1)},${s.y(SPENT + usualThrough(BILL_DAY)).toFixed(1)}`);
        expect(points[3]).toBe(`${s.x(BILL_DAY).toFixed(1)},${s.y(SPENT + usualThrough(BILL_DAY) - CREDIT).toFixed(1)}`);
        expect(points[4]).toBe(`${s.x(DAYS).toFixed(1)},${s.y(PROJECTED_END - CREDIT).toFixed(1)}`);

        const circles = container.querySelectorAll<HTMLElement>('[data-scheduled-step]');
        expect(circles).toHaveLength(1);
        expect(circles[0]!.getAttribute('data-scheduled-step')).toBe(String(BILL_DAY));
        expect(circles[0]!.style.top).toMatch(/%$/);
        expect(Number.parseFloat(circles[0]!.style.top)).toBeCloseTo((s.y(SPENT + usualThrough(BILL_DAY) - CREDIT) / VB_H) * 100, 3);
    });

    describe('a projection that passes the ceiling and comes back under it', () => {
        // Spent on day 1 and nothing since. After today the path rises by RISE on
        // RISE_DAY and falls by BACK on BACK_DAY; the ceiling sits between the level
        // it rises to and the level it falls back to.
        const D = 10;
        const TODAY_D = 4;
        const SPENT_D = 50;
        const RISE_DAY = 6;
        const BACK_DAY = 8;
        const RISE = 40;
        const BACK = 25;
        const CEILING_D = SPENT_D + RISE - BACK / 2;
        const spent = new Array<number>(TODAY_D).fill(SPENT_D);
        const x = (day: number) => ((day - 1) / (D - 1)) * VB_W;
        const xs = (line: Element) => line.getAttribute('points')!.trim().split(' ').map((p) => Number(p.split(',')[0]));

        const none = () => new Array<number>(D).fill(0);

        function drawRuns(normal: readonly number[], scheduled: ScheduledDays) {
            return render(
                <SpendCurve
                    series={spent} normal={normal} daysInMonth={D} elapsedDays={TODAY_D}
                    ceiling={CEILING_D} currency="USD" scheduled={scheduled} heightClass="h-fixed-160px" ariaLabel="runs"
                />,
            );
        }

        function expectThreeRuns(container: HTMLElement) {
            const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
            expect(dashed).toHaveLength(3);
            expect(dashed.map((p) => p.getAttribute('class'))).toEqual([
                expect.stringContaining('stroke-accent'),
                expect.stringContaining('stroke-mark-danger'),
                expect.stringContaining('stroke-accent'),
            ]);
            // Each run starts where the one before ended, so the line is continuous.
            expect(first(dashed[1]!.getAttribute('points')!)).toBe(last(dashed[0]!.getAttribute('points')!));
            expect(first(dashed[2]!.getAttribute('points')!)).toBe(last(dashed[1]!.getAttribute('points')!));
            // No under-the-ceiling run spans the stretch past it.
            expect(Math.max(...xs(dashed[0]!))).toBeLessThanOrEqual(x(RISE_DAY));
            expect(Math.min(...xs(dashed[2]!))).toBeGreaterThanOrEqual(x(BACK_DAY - 1));
        }

        it('when the usual days fall back, as a day that is net a refund does', () => {
            const normal = Array.from({ length: D }, (_, i) =>
                (i + 1 >= BACK_DAY ? RISE - BACK : i + 1 >= RISE_DAY ? RISE : 0));
            expectThreeRuns(drawRuns(normal, { debits: none(), credits: none() }).container);
        });

        it('when a scheduled credit follows a scheduled bill, with no history', () => {
            const debits = none();
            const credits = none();
            debits[RISE_DAY - 1] = RISE;
            credits[BACK_DAY - 1] = -BACK;
            expectThreeRuns(drawRuns(none(), { debits, credits }).container);
        });
    });

    describe('keeps everything it draws inside the frame', () => {
        // No history, so the projection is what is spent plus what is scheduled.
        const D = 10;
        const TODAY_D = 3;
        const CREDIT_DAY = 8;
        const SPENT_D = 40;
        const none = () => new Array<number>(D).fill(0);
        const creditOn = (day: number, amount: number): ScheduledDays => {
            const credits = none();
            credits[day - 1] = -amount;
            return { debits: none(), credits };
        };

        function drawWith(series: readonly number[], normal: readonly number[], scheduled: ScheduledDays) {
            return render(
                <SpendCurve
                    series={series} normal={normal} daysInMonth={D} elapsedDays={TODAY_D}
                    ceiling={null} currency="USD" scheduled={scheduled} heightClass="h-fixed-160px" ariaLabel="in frame"
                />,
            );
        }

        /** Every vertex of every line and of the band, and every circle, within the frame. */
        function expectInFrame(container: HTMLElement) {
            const ys = [...container.querySelectorAll('polyline, polygon')].flatMap((el) =>
                el.getAttribute('points')!.trim().split(' ').map((p) => Number(p.split(',')[1])));
            expect(ys.length).toBeGreaterThan(0);
            for (const y of ys) {
                expect(y).toBeGreaterThanOrEqual(0);
                expect(y).toBeLessThanOrEqual(VB_H);
            }
            for (const circle of container.querySelectorAll<HTMLElement>('[data-scheduled-step]')) {
                const top = Number.parseFloat(circle.style.top);
                expect(top).toBeGreaterThanOrEqual(0);
                expect(top).toBeLessThanOrEqual(100);
            }
        }

        const dashedOf = (c: HTMLElement) => polylines(c).filter((p) => p.getAttribute('stroke-dasharray'));

        it('a scheduled credit larger than the spend so far, which takes the projection below zero', () => {
            const REFUND = SPENT_D * 2;
            const { container } = drawWith(new Array<number>(TODAY_D).fill(SPENT_D), none(), creditOn(CREDIT_DAY, REFUND));
            expect(dashedOf(container).length).toBeGreaterThan(0);
            expect(container.querySelector(`[data-scheduled-step="${CREDIT_DAY}"]`)).not.toBeNull();
            expectInFrame(container);
        });

        it('a month with nothing spent and only a credit scheduled, which still draws its step and circle', () => {
            const REFUND = SPENT_D;
            const { container } = drawWith(none().slice(0, TODAY_D), none(), creditOn(CREDIT_DAY, REFUND));
            expect(dashedOf(container).length).toBeGreaterThan(0);
            expect(container.querySelector(`[data-scheduled-step="${CREDIT_DAY}"]`)).not.toBeNull();
            expectInFrame(container);
        });

        it('a credit on the last day, where the step starts above every point the path lands on', () => {
            // The usual month adds everything on its last day, and a credit of the
            // same size lands that day: the path ends where it stood, and the step
            // down starts a whole usual month above it.
            const USUAL = SPENT_D;
            const normal = [...new Array<number>(D - 1).fill(0), USUAL];
            const { container } = drawWith(new Array<number>(TODAY_D).fill(SPENT_D), normal, creditOn(D, USUAL));
            expect(container.querySelector(`[data-scheduled-step="${D}"]`)).not.toBeNull();
            expectInFrame(container);
        });

        it('a refund already in, which leaves the running total below zero on a day gone', () => {
            const REFUND = SPENT_D;
            const series = [-REFUND, -REFUND, SPENT_D];
            expect(series).toHaveLength(TODAY_D);
            expectInFrame(drawWith(series, none(), { debits: none(), credits: none() }).container);
        });

        it('a baseline below zero, as a window whose early days are net a refund draws', () => {
            const REFUND = SPENT_D;
            const normal = [...new Array<number>(D - 1).fill(-REFUND), 0];
            expectInFrame(drawWith(none().slice(0, TODAY_D), normal, { debits: none(), credits: none() }).container);
        });

        it('a running total that peaked before today, as a refund after a large spend leaves it', () => {
            const PEAK = SPENT_D * 5;
            const series = [PEAK, PEAK, SPENT_D];
            expect(series).toHaveLength(TODAY_D);
            expectInFrame(drawWith(series, none(), { debits: none(), credits: none() }).container);
        });

        it('a baseline that peaks before the month ends, as a window whose last days are net a refund draws', () => {
            const PEAK = SPENT_D * 5;
            const normal = Array.from({ length: D }, (_, i) => (i + 1 <= D / 2 ? PEAK : SPENT_D));
            expectInFrame(
                drawWith(new Array<number>(TODAY_D).fill(SPENT_D), normal, { debits: none(), credits: none() }).container,
            );
        });
    });

    it('scales without measuring, so it renders in a DOM with no layout', () => {
        // The property the whole kit decision rests on: one viewBox with
        // preserveAspectRatio="none" and no ResizeObserver, so the chart is
        // real in jsdom instead of 0×0 with the tests passing anyway.
        const { container } = draw();
        const svg = container.querySelector('svg')!;
        expect(svg.getAttribute('viewBox')).toBe(`0 0 ${VB_W} ${VB_H}`);
        expect(svg.getAttribute('preserveAspectRatio')).toBe('none');
        expect(svg.getAttribute('role')).toBe('img');
        expect(svg.getAttribute('aria-label')).toBe('spend so far');
    });
});
