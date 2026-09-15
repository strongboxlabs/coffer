import { describe, expect, it } from 'vitest';

import { AA, contrastRatio, luminance, readableOn } from './contrast';
import { TAG_PALETTE, tagChipStyle } from './tagPalette';

// The guarantee this file exists to hold: a coloured tag chip is legible for
// EVERY colour a user can pick, on every theme, without anything per-theme.
//
// The previous treatment — the hex as text over a 13% tint of itself — failed
// 33 of 40 swatch/theme pairs, all ten of them on light. It was invisible to
// the token audit because tag colours are user data, not tokens.

describe('contrast primitives', () => {
    it('matches known WCAG values', () => {
        expect(contrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 1);
        expect(contrastRatio('#ffffff', '#ffffff')).toBeCloseTo(1, 5);
        // order must not matter
        expect(contrastRatio('#0f766e', '#ffffff'))
            .toBeCloseTo(contrastRatio('#ffffff', '#0f766e'), 10);
    });

    it('puts luminance in the right order', () => {
        expect(luminance('#000000')).toBe(0);
        expect(luminance('#ffffff')).toBeCloseTo(1, 5);
        expect(luminance('#1e293b')).toBeLessThan(luminance('#94a3b8'));
    });

    it('picks whichever of black/white actually wins', () => {
        expect(readableOn('#ffffff')).toBe('#000000');
        expect(readableOn('#000000')).toBe('#ffffff');
        expect(readableOn('#f59e0b')).toBe('#000000');   // bright amber
        expect(readableOn('#1e293b')).toBe('#ffffff');   // slate-800
    });
});

describe('tag chips are legible for any colour a user can pick', () => {
    it('clears AA across a dense sweep of the colour space', () => {
        // Not just the 10 swatches: the recolor endpoint validates the #rrggbb
        // SHAPE, not palette membership, so arbitrary hexes reach this code.
        const sample: string[] = [];
        for (let h = 0; h < 360; h += 5) {
            for (let l = 0.05; l < 1; l += 0.05) {
                for (const s of [0.35, 0.65, 0.95]) {
                    sample.push(hsl(h, s, l));
                }
            }
        }
        sample.push('#000000', '#ffffff', '#808080', '#ffff00', '#000080');

        const failures = sample.filter((c) => contrastRatio(readableOn(c), c) < AA);
        expect(failures).toEqual([]);
        expect(sample.length).toBeGreaterThan(4000);
    });

    it('never drops below the luminance-crossover floor', () => {
        // Optimal black/white bottoms out around 4.58:1 — that is WHY this
        // approach is safe, and softening the pair to near-black/near-white
        // would break it (worst case 4.34, 197 failures in the same sweep).
        const worst = Math.min(
            ...TAG_PALETTE.map((c) => contrastRatio(readableOn(c), c)),
        );
        expect(worst).toBeGreaterThan(AA);
    });

    it('every shipped swatch clears AA as a chip', () => {
        for (const swatch of TAG_PALETTE) {
            const style = tagChipStyle(swatch)!;
            expect(style.backgroundColor).toBe(swatch);
            expect(contrastRatio(style.color as string, swatch)).toBeGreaterThanOrEqual(AA);
        }
    });

    it('is independent of the theme', () => {
        // The point of the solid fill: the pair is the hex and its own label,
        // so no surface participates and the theme/direction matrix cannot
        // affect it. A tinted fill composited over the surface could not make
        // this claim — which is exactly how the old treatment failed.
        const style = tagChipStyle('#3b82f6')!;
        expect(style.backgroundColor).toBe('#3b82f6');
        expect(style.color).toBe(readableOn('#3b82f6'));
    });

    it('leaves an uncoloured tag to the default chip', () => {
        expect(tagChipStyle(null)).toBeUndefined();
        expect(tagChipStyle(undefined)).toBeUndefined();
        expect(tagChipStyle('')).toBeUndefined();
    });

    it('would catch a treatment that stops clearing AA', () => {
        // The guard's own guard. This is the OLD formula — hex over a 13% tint
        // of itself on white — and it must read as a failure, or the sweep
        // above proves nothing.
        const tinted = mix('#f59e0b', '#ffffff', 0.13);
        expect(contrastRatio('#f59e0b', tinted)).toBeLessThan(AA);
    });
});

function hsl(h: number, s: number, l: number): string {
    const k = (n: number) => (n + h / 30) % 12;
    const a = s * Math.min(l, 1 - l);
    const f = (n: number) => l - a * Math.max(-1, Math.min(k(n) - 3, 9 - k(n), 1));
    return `#${[f(0), f(8), f(4)]
        .map((v) => Math.round(v * 255).toString(16).padStart(2, '0'))
        .join('')}`;
}

function mix(fg: string, bg: string, alpha: number): string {
    const part = (c: string, i: number) => parseInt(c.replace('#', '').slice(i, i + 2), 16);
    return `#${[0, 2, 4]
        .map((i) => Math.round(part(fg, i) * alpha + part(bg, i) * (1 - alpha))
            .toString(16).padStart(2, '0'))
        .join('')}`;
}
