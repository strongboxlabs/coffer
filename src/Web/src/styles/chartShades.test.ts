import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { contrastRatio } from '@/lib/contrast';
import { CHART_HUES } from '@/routes/ledgers/reports/chartSeries';

/**
 * Report chart shades (ADR-0021 Rule 5): in every theme, no two series fills
 * are closer than ΔE2000 13, Other's slate included, and every fill is 3:1 or
 * more on the theme's card.
 *
 * WHY A PAIR TEST. A contrast floor is per colour and cannot see two fills
 * that are each legible and the same to the eye. The chip text shades all
 * clear AA, and several of them are too close to tell apart as fills side by
 * side in a stack. Distance between every pair is the property a chart needs.
 *
 * The floor is the closest pair the light set reaches within the hue families:
 * the tightest theme, so it is what a change has to keep.
 *
 * ΔE2000 is implemented here rather than taken from a package, and checked
 * against the published reference pairs below, because a silent error in the
 * formula would pass every shade.
 */

const CSS = readFileSync(join(import.meta.dirname, '..', 'index.css'), 'utf8');
const THEMES = ['light', 'light-hc', 'dark', 'dark-hc'] as const;
type Theme = (typeof THEMES)[number];

const MIN_DELTA_E = 13;
const MIN_CONTRAST = 3;

/** One theme block's declarations. Blocks are flat, so the first lone `}` closes one. */
function tokensOf(theme: Theme): Map<string, string> {
    const opener = `[data-theme="${theme}"] {`;
    const start = CSS.indexOf(opener);
    if (start === -1) return new Map();
    const end = CSS.indexOf('\n}', start);
    const body = CSS.slice(start + opener.length, end);
    return new Map([...body.matchAll(/--color-([a-z0-9-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;/g)]
        .map((m) => [m[1]!, m[2]!.toLowerCase()]));
}

/** The fills one chart can hold: each family's shade, and Other's slate. */
function fillsOf(theme: Theme, shade: 'chart' | 'text' = 'chart'): Array<[string, string]> {
    const t = tokensOf(theme);
    return [
        ...CHART_HUES.map((h): [string, string] => [h, t.get(`cat-${h}-${shade}`) ?? '']),
        ['other', t.get('cat-xfer-text') ?? ''],
    ];
}

// --- CIEDE2000 ---------------------------------------------------------------

type Lab = readonly [number, number, number];

/** sRGB `#rrggbb` to CIELAB, under the CIE standard daylight white point. */
function labOf(hex: string): Lab {
    const lin = (c: number) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
    const [r, g, b] = [1, 3, 5].map((i) => lin(parseInt(hex.slice(i, i + 2), 16) / 255)) as [number, number, number];
    const x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
    const y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
    const z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
    const f = (t: number) => (t > 216 / 24389 ? Math.cbrt(t) : ((24389 / 27) * t + 16) / 116);
    const [fx, fy, fz] = [f(x), f(y), f(z)];
    return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];
}

const rad = (d: number) => (d * Math.PI) / 180;
const deg = (r: number) => (r * 180) / Math.PI;

/** CIEDE2000 colour difference, kL = kC = kH = 1. */
function deltaE2000([L1, a1, b1]: Lab, [L2, a2, b2]: Lab): number {
    const C1 = Math.hypot(a1, b1);
    const C2 = Math.hypot(a2, b2);
    const Cbar7 = ((C1 + C2) / 2) ** 7;
    const G = 0.5 * (1 - Math.sqrt(Cbar7 / (Cbar7 + 25 ** 7)));
    const a1p = (1 + G) * a1;
    const a2p = (1 + G) * a2;
    const C1p = Math.hypot(a1p, b1);
    const C2p = Math.hypot(a2p, b2);
    const hue = (a: number, b: number) => (a === 0 && b === 0 ? 0 : (deg(Math.atan2(b, a)) + 360) % 360);
    const h1p = hue(a1p, b1);
    const h2p = hue(a2p, b2);

    const dLp = L2 - L1;
    const dCp = C2p - C1p;
    let dhp = 0;
    if (C1p * C2p !== 0) {
        dhp = h2p - h1p;
        if (dhp > 180) dhp -= 360;
        else if (dhp < -180) dhp += 360;
    }
    const dHp = 2 * Math.sqrt(C1p * C2p) * Math.sin(rad(dhp / 2));

    const Lbp = (L1 + L2) / 2;
    const Cbp = (C1p + C2p) / 2;
    let hbp = h1p + h2p;
    if (C1p * C2p !== 0) {
        if (Math.abs(h1p - h2p) <= 180) hbp /= 2;
        else hbp = hbp < 360 ? (hbp + 360) / 2 : (hbp - 360) / 2;
    }
    const T = 1
        - 0.17 * Math.cos(rad(hbp - 30))
        + 0.24 * Math.cos(rad(2 * hbp))
        + 0.32 * Math.cos(rad(3 * hbp + 6))
        - 0.20 * Math.cos(rad(4 * hbp - 63));
    const dTheta = 30 * Math.exp(-(((hbp - 275) / 25) ** 2));
    const Cbp7 = Cbp ** 7;
    const Rc = 2 * Math.sqrt(Cbp7 / (Cbp7 + 25 ** 7));
    const Sl = 1 + (0.015 * (Lbp - 50) ** 2) / Math.sqrt(20 + (Lbp - 50) ** 2);
    const Sc = 1 + 0.045 * Cbp;
    const Sh = 1 + 0.015 * Cbp * T;
    const Rt = -Math.sin(rad(2 * dTheta)) * Rc;
    return Math.sqrt(
        (dLp / Sl) ** 2 + (dCp / Sc) ** 2 + (dHp / Sh) ** 2 + Rt * (dCp / Sc) * (dHp / Sh),
    );
}

/** Every pair of fills closer than the floor, named so a failure says which. */
function tooClose(fills: ReadonlyArray<[string, string]>): string[] {
    const out: string[] = [];
    for (let i = 0; i < fills.length; i++) {
        for (let j = i + 1; j < fills.length; j++) {
            const d = deltaE2000(labOf(fills[i]![1]), labOf(fills[j]![1]));
            if (d < MIN_DELTA_E) out.push(`${fills[i]![0]}/${fills[j]![0]} ${d.toFixed(1)}`);
        }
    }
    return out;
}

describe('report chart shades', () => {
    it('reads a shade for every hue family, and Other\'s slate, from every theme', () => {
        // The guard's own guard: a block that moved or a token that was renamed
        // would otherwise leave nothing to measure, and nothing would fail.
        for (const theme of THEMES) {
            const fills = fillsOf(theme);
            expect(fills).toHaveLength(CHART_HUES.length + 1);
            for (const [name, hex] of fills) expect(hex, `${theme} ${name}`).toMatch(/^#[0-9a-f]{6}$/);
        }
    });

    it.each(THEMES)('keeps every pair of fills in %s at ΔE2000 13 or more', (theme) => {
        expect(tooClose(fillsOf(theme))).toEqual([]);
    });

    it.each(THEMES)('keeps every fill in %s at 3:1 or more on the card', (theme) => {
        const card = tokensOf(theme).get('surface')!;
        expect(card).toMatch(/^#[0-9a-f]{6}$/);
        const faint = fillsOf(theme)
            .map(([name, hex]) => [name, contrastRatio(hex, card)] as const)
            .filter(([, ratio]) => ratio < MIN_CONTRAST)
            .map(([name, ratio]) => `${name} ${ratio.toFixed(2)}`);
        expect(faint).toEqual([]);
    });

    // Past nine series a hue repeats as a deeper shade (chartSeries FILL): the
    // family's token mixed 65% then 35% with the theme's text colour, in sRGB as
    // CSS color-mix does. Mixing toward the text can only raise contrast on the
    // card, and each step has to be a visible step from the one before.
    const MIX = [1, 0.65, 0.35] as const;
    const mix = (hue: string, text: string, keep: number) =>
        '#' + [1, 3, 5].map((i) => {
            const a = parseInt(hue.slice(i, i + 2), 16);
            const b = parseInt(text.slice(i, i + 2), 16);
            return Math.round(a * keep + b * (1 - keep)).toString(16).padStart(2, '0');
        }).join('');
    // Far under the 13 the base fills keep apart: a shade only separates repeats
    // of ONE hue, and a repeat is named by its tooltip and its hover as well. The
    // floor is perceptual, not measured off a ledger: ΔE2000 about 2.3 is the
    // smallest difference an eye sees at all, and 4 is a step seen side by side.
    // The dark themes' fills are pastels with little room to get lighter, which
    // is why this is not 13.
    const MIN_SHADE_STEP = 4;

    it.each(THEMES)('keeps every deeper shade in %s at 3:1 or more on the card', (theme) => {
        const t = tokensOf(theme);
        const card = t.get('surface')!;
        const text = t.get('text')!;
        const faint = fillsOf(theme)
            .filter(([name]) => name !== 'other')
            .flatMap(([name, hex]) => MIX.map((keep, step) => [`${name}#${step}`, mix(hex, text, keep)] as const))
            .map(([name, shade]) => [name, contrastRatio(shade, card)] as const)
            .filter(([, ratio]) => ratio < MIN_CONTRAST)
            .map(([name, ratio]) => `${name} ${ratio.toFixed(2)}`);
        expect(faint).toEqual([]);
    });

    it.each(THEMES)('makes each deeper shade in %s a visible step from the last', (theme) => {
        const text = tokensOf(theme).get('text')!;
        const flat = fillsOf(theme)
            .filter(([name]) => name !== 'other')
            .flatMap(([name, hex]) => [1, 2].map((step) => {
                const from = mix(hex, text, MIX[step - 1]!);
                const to = mix(hex, text, MIX[step]!);
                return [`${name}#${step}`, deltaE2000(labOf(from), labOf(to))] as const;
            }))
            .filter(([, d]) => d < MIN_SHADE_STEP)
            .map(([name, d]) => `${name} ${d.toFixed(1)}`);
        expect(flat).toEqual([]);
    });

    it('would refuse the chip text shades as chart fills', () => {
        // The test can fail on real input: the shades it replaced are each
        // legible and too close to their neighbours.
        expect(tooClose(fillsOf('light', 'text')).length).toBeGreaterThan(0);
    });
});

describe('deltaE2000', () => {
    // Sharma, Wu and Dalal (2005), "The CIEDE2000 color-difference formula:
    // implementation notes, supplementary test data, and mathematical
    // observations". Every pair of their table, which exercises the hue
    // wrap-around and the mean-hue cases a plain reading of the formula gets
    // wrong.
    const REFERENCE: ReadonlyArray<[Lab, Lab, number]> = [
        [[50, 2.6772, -79.7751], [50, 0, -82.7485], 2.0425],
        [[50, 3.1571, -77.2803], [50, 0, -82.7485], 2.8615],
        [[50, 2.8361, -74.0200], [50, 0, -82.7485], 3.4412],
        [[50, -1.3802, -84.2814], [50, 0, -82.7485], 1.0000],
        [[50, -1.1848, -84.8006], [50, 0, -82.7485], 1.0000],
        [[50, -0.9009, -85.5211], [50, 0, -82.7485], 1.0000],
        [[50, 0, 0], [50, -1, 2], 2.3669],
        [[50, -1, 2], [50, 0, 0], 2.3669],
        [[50, 2.4900, -0.0010], [50, -2.4900, 0.0009], 7.1792],
        [[50, 2.4900, -0.0010], [50, -2.4900, 0.0010], 7.1792],
        [[50, 2.4900, -0.0010], [50, -2.4900, 0.0011], 7.2195],
        [[50, 2.4900, -0.0010], [50, -2.4900, 0.0012], 7.2195],
        [[50, -0.0010, 2.4900], [50, 0.0009, -2.4900], 4.8045],
        [[50, -0.0010, 2.4900], [50, 0.0010, -2.4900], 4.8045],
        [[50, -0.0010, 2.4900], [50, 0.0011, -2.4900], 4.7461],
        [[50, 2.5, 0], [50, 0, -2.5], 4.3065],
        [[50, 2.5, 0], [73, 25, -18], 27.1492],
        [[50, 2.5, 0], [61, -5, 29], 22.8977],
        [[50, 2.5, 0], [56, -27, -3], 31.9030],
        [[50, 2.5, 0], [58, 24, 15], 19.4535],
        [[50, 2.5, 0], [50, 3.1736, 0.5854], 1.0000],
        [[50, 2.5, 0], [50, 3.2972, 0], 1.0000],
        [[50, 2.5, 0], [50, 1.8634, 0.5757], 1.0000],
        [[50, 2.5, 0], [50, 3.2592, 0.3350], 1.0000],
        [[60.2574, -34.0099, 36.2677], [60.4626, -34.1751, 39.4387], 1.2644],
        [[63.0109, -31.0961, -5.8663], [62.8187, -29.7946, -4.0864], 1.2630],
        [[61.2901, 3.7196, -5.3901], [61.4292, 2.2480, -4.9620], 1.8731],
        [[35.0831, -44.1164, 3.7933], [35.0232, -40.0716, 1.5901], 1.8645],
        [[22.7233, 20.0904, -46.6940], [23.0331, 14.9730, -42.5619], 2.0373],
        [[36.4612, 47.8580, 18.3852], [36.2715, 50.5065, 21.2231], 1.4146],
        [[90.8027, -2.0831, 1.4410], [91.1528, -1.6435, 0.0447], 1.4441],
        [[90.9257, -0.5406, -0.9208], [88.6381, -0.8985, -0.7239], 1.5381],
        [[6.7747, -0.2908, -2.4247], [5.8714, -0.0985, -2.2286], 0.6377],
        [[2.0776, 0.0795, -1.1350], [0.9033, -0.0636, -0.5514], 0.9082],
    ];

    it.each(REFERENCE.map((r, i) => [i + 1, ...r] as const))(
        'matches reference pair %i',
        (_i, a, b, expected) => {
            expect(deltaE2000(a, b)).toBeCloseTo(expected, 4);
            expect(deltaE2000(b, a)).toBeCloseTo(expected, 4);
        },
    );

    it('converts sRGB white and black to the Lab ends', () => {
        const [Lw, aw, bw] = labOf('#ffffff');
        expect(Lw).toBeCloseTo(100, 2);
        expect(Math.abs(aw)).toBeLessThan(0.01);
        expect(Math.abs(bw)).toBeLessThan(0.01);
        expect(labOf('#000000')[0]).toBeCloseTo(0, 4);
    });
});
