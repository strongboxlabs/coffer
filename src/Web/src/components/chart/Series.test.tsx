import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';

import { Arc, Area, Band, Bar, Line, pointsAttr } from './Series';

const svg = (node: React.ReactNode) =>
    render(<svg viewBox="0 0 600 180">{node}</svg>).container;

const PTS = [{ x: 0, y: 180 }, { x: 300, y: 90 }, { x: 600, y: 0 }];

describe('Line', () => {
    it('draws through every point in order', () => {
        const c = svg(<Line points={PTS} className="stroke-accent" />);
        expect(c.querySelector('polyline')!.getAttribute('points'))
            .toBe('0.0,180.0 300.0,90.0 600.0,0.0');
    });

    it('draws NOTHING for fewer than two points', () => {
        // A one-point polyline is invisible anyway, and rendering it would
        // leave an empty element for a test to find and believe in. This is
        // also the behaviour SpendCurve already had, discovered by
        // characterising it: a projection that is over the ceiling from its
        // first step has a single under-ceiling point and draws no accent line.
        expect(svg(<Line points={[PTS[0]!]} className="stroke-accent" />)
            .querySelector('polyline')).toBeNull();
        expect(svg(<Line points={[]} className="stroke-accent" />)
            .querySelector('polyline')).toBeNull();
    });

    it('takes its colour as a class, never as a hex', () => {
        // Both themes follow the tokens, and the contrast audit can see it.
        const c = svg(<Line points={PTS} className="stroke-state-danger" />);
        const el = c.querySelector('polyline')!;
        expect(el.getAttribute('class')).toContain('stroke-state-danger');
        expect(el.getAttribute('stroke')).toBeNull();
    });

    it('leaves a dashed line butt-ended, so the dashes keep their gaps', () => {
        // Round caps round every DASH, lengthening each and closing the gaps
        // — a different texture, not a nicety. The dash carries a meaning
        // ("projected, not measured"), so it must stay legible as a dash.
        expect(svg(<Line points={PTS} className="stroke-accent" />)
            .querySelector('polyline')!.getAttribute('stroke-linecap')).toBe('round');
        expect(svg(<Line points={PTS} className="stroke-accent" dashed />)
            .querySelector('polyline')!.getAttribute('stroke-linecap')).toBe('butt');
    });

    it('dashes only when asked', () => {
        expect(svg(<Line points={PTS} className="stroke-accent" />)
            .querySelector('polyline')!.getAttribute('stroke-dasharray')).toBeNull();
        expect(svg(<Line points={PTS} className="stroke-accent" dashed />)
            .querySelector('polyline')!.getAttribute('stroke-dasharray')).toBe('4 4');
    });
});

describe('Band', () => {
    it('closes the shape by running back along the lower edge', () => {
        const upper = [{ x: 0, y: 10 }, { x: 100, y: 20 }];
        const lower = [{ x: 0, y: 50 }, { x: 100, y: 60 }];
        const c = svg(<Band upper={upper} lower={lower} className="fill-surface-hover/40" />);

        // Out along upper, back along lower REVERSED — otherwise the polygon
        // crosses itself into a bow tie.
        expect(c.querySelector('polygon')!.getAttribute('points'))
            .toBe('0.0,10.0 100.0,20.0 100.0,60.0 0.0,50.0');
    });
});

describe('Area', () => {
    it('fills down to the baseline, not to the floor of the chart', () => {
        // The baseline is normally the ZERO line. Filling to the floor would
        // shade the region below zero as though it were value.
        const c = svg(<Area points={PTS} baselineY={90} className="fill-accent/20" />);
        const pts = c.querySelector('polygon')!.getAttribute('points')!;
        expect(pts).toContain('600.0,90.0');
        expect(pts).toContain('0.0,90.0');
    });
});

describe('Bar', () => {
    it('hangs a NEGATIVE bar below the baseline instead of vanishing', () => {
        // THE ONE THAT MATTERS. Given a height rather than a value position, a
        // negative bar is a rectangle with negative height, which renders as
        // nothing — and that is exactly how a refund disappears from a chart.
        const c = svg(<Bar x={10} width={40} baselineY={90} valueY={140} className="fill-state-danger" />);
        const r = c.querySelector('rect')!;
        expect(r.getAttribute('y')).toBe('90.0');
        expect(r.getAttribute('height')).toBe('50.0');
    });

    it('draws a positive bar upward from the same baseline', () => {
        const c = svg(<Bar x={10} width={40} baselineY={90} valueY={30} className="fill-accent" />);
        const r = c.querySelector('rect')!;
        expect(r.getAttribute('y')).toBe('30.0');
        expect(r.getAttribute('height')).toBe('60.0');
    });

    it('gives a zero bar a hairline, so nothing is not mistaken for absent', () => {
        // A category that netted to zero is PRESENT in the report. Drawing it
        // at height 0 makes it indistinguishable from one with no activity.
        const c = svg(<Bar x={10} width={40} baselineY={90} valueY={90} className="fill-accent" />);
        expect(c.querySelector('rect')!.getAttribute('height')).toBe('1.0');
    });
});

describe('Arc', () => {
    it('draws a wedge with both radii, so a donut has a hole', () => {
        const c = svg(<Arc cx={100} cy={100} outerRadius={80} innerRadius={50}
            startTurn={0} endTurn={0.25} className="fill-accent" />);
        const d = c.querySelector('path')!.getAttribute('d')!;
        expect(d).toContain('A 80.0 80.0');
        expect(d).toContain('A 50.0 50.0');
        expect(d.endsWith('Z')).toBe(true);
    });

    it('starts at twelve o’clock, because a share of a whole reads from the top', () => {
        const c = svg(<Arc cx={100} cy={100} outerRadius={80} innerRadius={0}
            startTurn={0} endTurn={0.25} className="fill-accent" />);
        // First point is directly above the centre.
        expect(c.querySelector('path')!.getAttribute('d')!.startsWith('M 100.0 20.0')).toBe(true);
    });

    it('sets the large-arc flag past a half turn', () => {
        const small = svg(<Arc cx={100} cy={100} outerRadius={80} innerRadius={50}
            startTurn={0} endTurn={0.4} className="fill-accent" />);
        const big = svg(<Arc cx={100} cy={100} outerRadius={80} innerRadius={50}
            startTurn={0} endTurn={0.7} className="fill-accent" />);
        expect(small.querySelector('path')!.getAttribute('d')).toContain('0 0 1');
        expect(big.querySelector('path')!.getAttribute('d')).toContain('0 1 1');
    });

    it('draws a FULL turn as two halves rather than as nothing', () => {
        // A single arc from 0 to 1 starts and ends on the same point, and the
        // renderer draws nothing at all — so a portfolio in one asset class
        // would show an empty chart.
        const c = svg(<Arc cx={100} cy={100} outerRadius={80} innerRadius={50}
            startTurn={0} endTurn={1} className="fill-accent" />);
        expect(c.querySelectorAll('path')).toHaveLength(2);
    });

    it('draws nothing for an empty or inverted sweep', () => {
        expect(svg(<Arc cx={100} cy={100} outerRadius={80} innerRadius={50}
            startTurn={0.5} endTurn={0.5} className="fill-accent" />)
            .querySelector('path')).toBeNull();
    });
});

describe('pointsAttr', () => {
    it('rounds to one decimal, which is plenty at these sizes', () => {
        expect(pointsAttr([{ x: 1.26, y: 2.34 }])).toBe('1.3,2.3');
    });
});
