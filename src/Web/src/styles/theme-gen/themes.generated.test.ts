import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { emit } from './emit';
import { hexToOklch, oklchToHex } from './oklch';
import { ACCENTS, AA, audit, build, ratio, THEMES, type Tokens } from './palette';

const GENERATED = join(import.meta.dirname, '..', 'themes.generated.css');

/**
 * Two jobs here, and they are different.
 *
 * FRESHNESS — the committed CSS must equal what the model emits today. A
 * generator whose output is edited by hand is worse than no generator: the
 * comments claim the values are solved and audited, and they would not be.
 *
 * AA — every combination the model can produce must clear 4.5:1 on every pair
 * the register renders. This is a floor, not a design check. Every visual defect
 * in this theme work so far passed an audit like this one; what it catches is
 * illegibility, not whether a colour belongs.
 */
describe('generated theme CSS', () => {
    it('matches the model — regenerate with `npm run themes:gen`', () => {
        expect(readFileSync(GENERATED, 'utf8')).toBe(emit());
    });

    it('covers every direction that is not the default', () => {
        const css = readFileSync(GENERATED, 'utf8');
        for (const theme of THEMES) {
            for (const accent of ACCENTS) {
                const sel = `[data-theme="${theme}"][data-accent="${accent}"]`;
                // teal IS the default: the [data-theme] blocks in index.css are
                // already teal, so a pair block for it would be dead weight.
                if (accent === 'teal') expect(css).not.toContain(sel);
                else expect(css).toContain(sel);
            }
        }
    });

    it('restates every token in every block', () => {
        // A partial block works only while the attribute is on <html>. Nested in
        // a preview inside a different theme, the tokens it omits are inherited
        // from whatever it sits in — which rendered light-hc as a light/dark
        // hybrid on the Appearance screen.
        const css = readFileSync(GENERATED, 'utf8');
        const counts = [...css.matchAll(/\[data-accent="[a-z]+"\]\s*\{([^}]*)\}/g)]
            .map((m) => (m[1]!.match(/--color-/g) ?? []).length);
        expect(counts.length).toBe(8);
        expect(new Set(counts).size).toBe(1);          // all blocks the same size
        expect(counts[0]).toBeGreaterThan(40);
    });

    it('clears AA for every theme x direction', () => {
        const failures: string[] = [];
        for (const theme of THEMES) {
            for (const accent of ACCENTS) {
                for (const { pair, value } of audit(build(theme, accent))) {
                    failures.push(`${theme}/${accent}: ${pair} = ${value.toFixed(2)}`);
                }
            }
        }
        expect(failures).toEqual([]);
    });

    it('would catch a combination that drops below AA', () => {
        // The guard's own guard. `audit` returning [] because it checks nothing
        // is the failure mode this repo keeps finding.
        const broken: Tokens = { ...build('dark', 'teal'), text: '#1f2b3d' };  // ~1.1:1 on its surface
        expect(audit(broken).map((f) => f.pair)).toContain('body text');
        expect(ratio('#1f2b3d', broken['surface']!)).toBeLessThan(AA);
    });
});

describe('oklch', () => {
    it('round-trips sRGB exactly', () => {
        // A silent conversion bug would poison every value in the file.
        for (const hex of ['#1e293b', '#0f766e', '#f8fafc', '#2dd4bf', '#64748b']) {
            const { L, C, H } = hexToOklch(hex);
            expect(oklchToHex(L, C, H)).toBe(hex);
        }
    });

    it('measures the shipping neutral ramp as near-constant chroma', () => {
        // This is the observation the whole model rests on: slate is not a hue
        // rotation of anything, it is one chroma held along a lightness ramp.
        const ramp = ['#1e293b', '#475569', '#94a3b8', '#64748b'];
        for (const hex of ramp) {
            expect(hexToOklch(hex).C).toBeGreaterThan(0.030);
            expect(hexToOklch(hex).C).toBeLessThan(0.045);
        }
    });
});
