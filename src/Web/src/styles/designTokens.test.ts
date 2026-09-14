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
const FAMILIES = ['state', 'surface', 'accent', 'cat', 'border', 'text', 'on', 'chip'].join('|');

const REFERENCE = new RegExp(
    `(?:^|[\\s"'\`:])(?:${UTILITIES})-((?:${FAMILIES})(?:-[a-z0-9]+)*)`,
    'g',
);

/**
 * Read one top-level block's body out of the stylesheet. Blocks here are flat
 * — declarations and comments, no nesting — so the first line that is a lone
 * `}` closes them.
 */
function blockBody(css: string, opener: string): string {
    const start = css.indexOf(opener);
    if (start === -1) return '';
    const from = start + opener.length;
    const end = css.indexOf('\n}', from);
    return end === -1 ? css.slice(from) : css.slice(from, end);
}

function tokensIn(body: string): Set<string> {
    const names = new Set<string>();
    for (const m of body.matchAll(/--color-([a-z0-9-]+)\s*:/g)) names.add(m[1]!);
    return names;
}

/**
 * The tokens the app DEFINES — scoped to the `@theme` block on purpose.
 *
 * It used to scan the whole file. That was fine until the theme overrides
 * landed: a typo inside a `[data-theme]` block (`--color-serface`) would have
 * been read as a definition, so the override would silently do nothing AND
 * the typo would start vouching for itself everywhere else. The override
 * blocks are checked against this set instead, below.
 */
function definedTokens(): Set<string> {
    return tokensIn(blockBody(readFileSync(CSS, 'utf8'), '@theme {'));
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

        // KNOWN DEBT — empty, and it stays that way.
        //
        // All four entries (text-default, on-accent, accent-foreground,
        // state-danger-bg) were cleared alongside the themes. What had frozen
        // them was the note that "a wrong guess is a silent appearance change";
        // the four-theme contrast audit is what unblocked them, because every
        // substitution could be checked against its real background in each
        // theme before being made rather than guessed.
        //
        // on-accent and accent-foreground both resolved to text-text-inverse,
        // as the old note predicted — but only after that token was made
        // theme-aware, which is the thing the guess could not have known: a
        // white label is correct on the light themes' teal-700 and unreadable
        // (1.78:1) on the dark themes' teal-400.
        //
        // Do not add to this set. A name here is a class that paints nothing;
        // fix the reference instead.
        const known = new Set<string>();
        expect([...known]).toEqual([]);

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

    it('themes override only tokens that exist', () => {
        // A `[data-theme]` block can only RE-point a token the @theme block
        // already defines. Misspell one and nothing complains: the custom
        // property is set, no utility reads it, and that theme quietly keeps the
        // light value for whatever the typo was meant to change.
        const css = readFileSync(CSS, 'utf8');
        const defined = definedTokens();
        expect(defined.size).toBeGreaterThan(20);

        const selectors = [...css.matchAll(/\[data-theme="([a-z-]+)"\]\s*\{/g)]
            .map((m) => m[1]!);
        // Guard the guard: if the blocks move or the selector shape changes,
        // an empty list would make this pass while checking nothing.
        expect(selectors).toEqual(['light', 'light-hc', 'dark', 'dark-hc']);

        const offenders: string[] = [];
        for (const sel of selectors) {
            const body = blockBody(css, `[data-theme="${sel}"] {`);
            for (const token of tokensIn(body)) {
                if (!defined.has(token)) offenders.push(`${sel}: ${token}`);
            }
        }
        expect(offenders).toEqual([]);
    });

    it('the light block restates every @theme token', () => {
        // Two things ride on completeness here. First, `data-theme` only
        // COMPOSES if the light block is whole: a light preview nested inside a
        // dark page inherits the dark value for anything light omits.
        //
        // Second, and the reason this test exists: the light block is the
        // checklist. A token absent from it is a token no theme was ever asked
        // about, so its light value leaks into all four. That is how
        // `surface-sidebar` came within a commit of painting a white rail down
        // the side of both dark themes — it was never wrong anywhere, just
        // never considered.
        const css = readFileSync(CSS, 'utf8');
        const light = tokensIn(blockBody(css, '[data-theme="light"] {'));
        const missing = [...definedTokens()].filter((t) => !light.has(t)).sort();
        expect(missing).toEqual([]);
    });

    it('the style guide renders every @theme token', () => {
        // StyleGuidePage says at the top that every token in index.css renders
        // there, and ADR-0021 D.1 is what makes that page the first place a
        // token change is verified. Nothing enforced it, and it had already
        // drifted: surface-header was missing before the themes landed.
        //
        // Tokens appear there two ways — as a `--color-x` literal in the label,
        // or only as the utility class that paints the swatch — so both count.
        const page = readFileSync(
            join(SRC, 'routes', '__styleguide', 'StyleGuidePage.tsx'),
            'utf8',
        );
        const shown = new Set<string>();
        for (const m of page.matchAll(/--color-([a-z0-9-]+)/g)) shown.add(m[1]!);
        for (const m of page.matchAll(REFERENCE)) shown.add(m[1]!);

        const missing = [...definedTokens()].filter((t) => !shown.has(t)).sort();
        expect(missing).toEqual([]);
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
