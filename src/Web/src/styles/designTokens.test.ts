import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

/**
 * Every design-token colour class in the SPA must name a token that exists.
 *
 * WHY THIS EXISTS. Tailwind resolves `bg-state-warning-soft` from a
 * `--color-state-warning-soft` custom property. Name a token that was never
 * defined and nothing complains: no build error, no console warning, no visual
 * error state. The class is simply dropped, and the element renders without the
 * colour it asked for. That is invisible in review — the markup reads correctly —
 * and invisible in tests, which assert text and roles rather than paint.
 *
 * It has bitten three times:
 *   * `border-border-subtle` in BOTH notifications panels (no such token; the
 *     divider between target rows never got its colour).
 *   * `state-warn` / `state-warn-soft` in the import dialog, twice — the preview
 *     and result warning boxes. The token is `state-warning`. Those two panels
 *     exist ONLY to be noticed, and they were rendering with no border colour and
 *     no background. Reported by a human looking at the screen, which is the only
 *     thing that was ever going to catch it.
 *
 * So this reads the token definitions out of the stylesheet and checks every
 * reference against them. A typo becomes a failing test instead of an invisible
 * panel.
 */

const SRC = join(import.meta.dirname, '..');
const CSS = join(SRC, 'index.css');

/** Utility prefixes that take a colour. */
const UTILITIES = [
    'bg', 'text', 'border', 'ring', 'divide', 'outline', 'fill', 'stroke',
    'shadow', 'from', 'via', 'to', 'decoration', 'accent', 'caret',
].join('|');

/**
 * Token FAMILIES this project defines. Scoped deliberately: `text-xs` and
 * `border-2` are Tailwind built-ins and must not be mistaken for token
 * references, so only names beginning with one of these are checked.
 */
const FAMILIES = ['state', 'surface', 'accent', 'cat', 'border', 'text', 'on'].join('|');

const REFERENCE = new RegExp(
    `(?:^|[\\s"'\`:])(?:${UTILITIES})-((?:${FAMILIES})(?:-[a-z0-9]+)*)`,
    'g',
);

function definedTokens(): Set<string> {
    const css = readFileSync(CSS, 'utf8');
    const names = new Set<string>();
    for (const m of css.matchAll(/--color-([a-z0-9-]+)\s*:/g)) {
        names.add(m[1]!);
    }
    return names;
}

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) {
            out.push(...sourceFiles(full));
        } else if (/\.(tsx?|css)$/.test(entry) && !/\.test\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

describe('design tokens', () => {
    it('defines every colour token the SPA references', () => {
        const defined = definedTokens();
        // Guard the guard: if the stylesheet ever moves, an empty set would make
        // every reference "undefined" and this test would fail for the wrong reason
        // — or, with the assertion inverted, pass while checking nothing.
        expect(defined.size).toBeGreaterThan(20);
        expect(defined.has('state-warning')).toBe(true);

        // KNOWN DEBT, frozen deliberately. These predate the guard and each is a
        // real defect — the class resolves to nothing, so the element renders without
        // the colour it asked for. They are NOT fixed here because each needs a
        // visual decision in a file this change does not otherwise touch, and a
        // wrong guess is a silent appearance change. The list exists to stop NEW
        // ones, not to bless these.
        //
        //   text-default      -> almost certainly `text-text` (--color-text)
        //   on-accent         -> almost certainly `text-text-inverse`
        //   accent-foreground -> almost certainly `text-text-inverse`
        //   state-danger-bg   -> almost certainly `state-danger-soft`
        //
        // Shrink this list; never grow it.
        const known = new Set([
            'text-default', 'on-accent', 'accent-foreground', 'state-danger-bg',
        ]);

        const offenders: string[] = [];
        for (const file of sourceFiles(SRC)) {
            const text = readFileSync(file, 'utf8');
            for (const m of text.matchAll(REFERENCE)) {
                const token = m[1]!;
                if (!defined.has(token) && !known.has(token)) {
                    offenders.push(`${file.slice(SRC.length + 1)}: ${token}`);
                }
            }
        }

        // Listed rather than counted, because the failure has to name the typo to be
        // actionable — "3 bad tokens" sends the reader hunting.
        expect(offenders).toEqual([]);
    });

    it('would catch a token that does not exist', () => {
        // The guard's own guard. A regex that quietly stopped matching would make the
        // test above pass forever, which is the failure mode this repo keeps finding:
        // a check that cannot fail. This proves the matcher still fires on the exact
        // shape of the bug that prompted it.
        const defined = definedTokens();
        const sample = 'className="rounded border border-state-warn/40 bg-state-warn-soft"';

        const found = [...sample.matchAll(REFERENCE)]
            .map((m) => m[1]!)
            .filter((t) => !defined.has(t));

        expect(found).toEqual(['state-warn', 'state-warn-soft']);
    });
});
