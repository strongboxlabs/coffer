import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';

import { SpendCurve } from './SpendCurve';

// CHARACTERISATION, written before this component is refactored onto the
// shared chart kit (ADR-0104 D6). It had no tests at all, so there was nothing
// to tell a refactor that changed the picture from one that did not.
//
// These assert GEOMETRY, not implementation: the points a reader can see, at
// coordinates derived here independently of the component. If the kit version
// draws the same curve, every one of these still passes unchanged.

const VB_W = 600;
const VB_H = 180;

/** The component's own scale, re-derived rather than imported. */
function scales(series: readonly number[], normal: readonly number[],
    daysInMonth: number, elapsedDays: number, ceiling: number | null) {
    const spent = elapsedDays > 0 ? (series[Math.min(elapsedDays, series.length) - 1] ?? 0) : 0;
    const rate = elapsedDays > 0 ? spent / elapsedDays : 0;
    const top = Math.max(spent, ceiling ?? 0, rate * daysInMonth, normal.at(-1) ?? 0, 1) * 1.18;
    return {
        x: (day: number) => ((day - 1) / Math.max(daysInMonth - 1, 1)) * VB_W,
        y: (v: number) => VB_H - (v / top) * VB_H,
        spent, rate, top,
    };
}

const SERIES = [10, 25, 40, 55, 70, 85, 100];   // cumulative, 7 days elapsed
const NORMAL = [12, 24, 36, 48, 60, 72, 84, 96, 108, 120];
const DAYS = 10;
const ELAPSED = 7;

function draw(ceiling: number | null = 110, elapsed = ELAPSED) {
    return render(
        <SpendCurve
            series={SERIES}
            normal={NORMAL}
            daysInMonth={DAYS}
            elapsedDays={elapsed}
            ceiling={ceiling}
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
        // The projection is the claim "at this rate", so it must START where
        // the real data stops — a gap or an overlap would both misread.
        const { container } = draw();
        const s = scales(SERIES, NORMAL, DAYS, ELAPSED, 110);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed.length).toBeGreaterThan(0);

        const projStart = first(dashed[0]!.getAttribute('points')!);
        expect(projStart).toBe(`${s.x(ELAPSED).toFixed(1)},${s.y(s.rate * ELAPSED).toFixed(1)}`);
    });

    it('splits the projection where it crosses the ceiling, so only the problem is red', () => {
        // The rate is the last SERIES value over ELAPSED, and the projection
        // adds one rate per day from ELAPSED to DAYS. The ceiling passed here
        // sits between the second-last and the last projected values, so it is
        // crossed on the LAST step, which leaves a real segment either side.
        const { container } = draw(130);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed).toHaveLength(2);
        expect(dashed[0]!.getAttribute('class')).toContain('stroke-accent');
        expect(dashed[1]!.getAttribute('class')).toContain('stroke-state-danger');

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
        expect(dashed[0]!.getAttribute('class')).toContain('stroke-state-danger');
    });

    it('leaves the projection one colour when it never reaches the ceiling', () => {
        // The discriminating pair for the test above: same data, a ceiling the
        // projection cannot reach, so nothing is red.
        const { container } = draw(10_000);

        const dashed = polylines(container).filter((p) => p.getAttribute('stroke-dasharray'));
        expect(dashed).toHaveLength(1);
        expect(dashed[0]!.getAttribute('class')).toContain('stroke-accent');
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
        expect(rule!.style.top).toBe(`${(s.y(110) / VB_H) * 100}%`);
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
