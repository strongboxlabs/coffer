import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

/**
 * Lengths that must NOT move when the density axis moves `--spacing`.
 *
 * WHY THIS EXISTS. Tailwind v4 compiles `size-4` to
 * `calc(var(--spacing) * 4)` and `size-icon-md` to a bare
 * `var(--spacing-icon-md)`. Density overrides `--spacing`, so the numeric
 * form scales and the named form cannot. That is the entire mechanism, and it
 * fails silently in both directions:
 *
 *   * A NAMED utility whose token was never defined paints nothing. Tailwind
 *     drops the class with no build error and no console warning — the same
 *     silent failure `designTokens.test.ts` exists for, and the reason this
 *     file's first assertion is a spelling check rather than a style check.
 *     The sweep that introduced these tokens produced three such classes
 *     (`w-fixed-24px`, `-28px`, `-32px`) on the first pass.
 *
 *   * A SPLIT PAIR is worse, because it paints something plausible. Those
 *     three classes came from `h-7 w-7` becoming `h-7 w-fixed-28px`: the
 *     height still scaled, the width no longer did, and every icon button in
 *     the app would have gone visibly non-square at any density except 1x.
 *     Nothing about that is visible in review — the markup reads fine, and at
 *     the default density it renders correctly. It was caught only because
 *     those three tokens happened not to exist yet.
 *
 * So: assertion 1 catches the name that paints nothing, assertion 2 catches
 * the icon that slipped back onto the numeric scale, and assertion 3 pins the
 * one pair whose equality is a documented rule rather than a coincidence.
 */

const SRC = join(import.meta.dirname, '..');
const CSS = join(SRC, 'index.css');

/** Utilities that can take a pinned length. */
const SIZERS = ['size', 'w', 'h', 'min-w', 'min-h', 'max-w', 'max-h'];

/**
 * The pinned families. A utility naming any other suffix is a typo.
 *
 * `control` was missing here for one commit, and nothing went red: the
 * spelling check simply stopped seeing `h-control-28px` and friends, so 43
 * utilities were exempt from the assertion meant to cover them. It surfaced
 * only because the Rule 10 spacer check, which reuses this list, went from
 * finding a square to finding nothing. Keep the two coupled for that reason.
 */
const FAMILIES = ['icon', 'dot', 'swatch', 'fixed', 'control'];

const PINNED = new RegExp(
    `(?<![\\w-])((?:${SIZERS.join('|')})-(?:${FAMILIES.join('|')})-[a-z0-9]+)(?![\\w-])`,
    'g',
);

/**
 * Any sizer still on the NUMERIC scale — `h-4`, `w-24`, `size-4`, `min-h-9`.
 * Each compiles to `calc(var(--spacing) * N)` and therefore moves with
 * density, which is correct for NOTHING that sets a size: the axis is defined
 * to move space and only space (ADR-0021 Rule 11).
 *
 * `-0` is excluded because zero does not scale.
 */
const NUMERIC_SIZER = new RegExp(
    `(?<![\\w-])((?:${SIZERS.join('|')})-(\\d+(?:\\.5)?))(?![\\w.])`,
    'g',
);

/** The pinned square on a component, e.g. `size-control-28px`. */
const PINNED_SQUARE = new RegExp(
    `(?<![\\w-])(size-(?:${FAMILIES.join('|')})-[a-z0-9]+)(?![\\w-])`,
);

function themeBody(): string {
    const css = readFileSync(CSS, 'utf8');
    const start = css.indexOf('@theme {');
    const end = css.indexOf('\n}', start);
    return css.slice(start, end === -1 ? undefined : end);
}

function definedSpacingTokens(): Set<string> {
    const names = new Set<string>();
    for (const m of themeBody().matchAll(/--spacing-([a-z0-9-]+)\s*:/g)) names.add(m[1]!);
    return names;
}

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
        else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) out.push(full);
    }
    return out;
}

/** The first pinned square class on a component, e.g. `size-control-28px`. */
function squareOf(relPath: string): string | null {
    const m = PINNED_SQUARE.exec(readFileSync(join(SRC, relPath), 'utf8'));
    return m ? m[1]! : null;
}

describe('pinned lengths', () => {
    it('defines every pinned-length token the SPA references', () => {
        const defined = definedSpacingTokens();
        // Guard the guard: an empty set would make every reference "undefined"
        // and fail for the wrong reason; a stylesheet move would pass on nothing.
        expect(defined.size).toBeGreaterThan(20);
        expect(defined.has('icon-md')).toBe(true);

        const offenders: string[] = [];
        let seen = 0;
        for (const file of sourceFiles(SRC)) {
            const rel = file.slice(SRC.length + 1).replaceAll('\\', '/');
            for (const m of readFileSync(file, 'utf8').matchAll(PINNED)) {
                seen += 1;
                const util = m[1]!;
                // Strip the utility prefix. Longest-first, so `max-h` wins
                // over nothing and `min-w` is not read as `w`.
                const sizer = SIZERS.filter((s) => util.startsWith(`${s}-`)).sort(
                    (a, b) => b.length - a.length,
                )[0]!;
                const name = util.slice(sizer.length + 1);
                if (!defined.has(name)) offenders.push(`${rel}: ${util} (no --spacing-${name})`);
            }
        }
        expect(seen).toBeGreaterThan(50);
        // Listed rather than counted — the failure has to name the typo.
        expect(offenders).toEqual([]);
    });

    it('sets no size from the numeric scale', () => {
        // The strongest form of the rule, and the reason it can be this
        // strong: density is defined to move SPACE and only space, so there is
        // no such thing as a size that should scale. Anything setting a width,
        // a height or a square belongs on a named token.
        //
        // Stated as a total ban rather than a threshold on purpose. The
        // earlier draft of this guard allowed squares above 20px on the theory
        // that a control box may scale; that theory is what produced the split
        // `h-7 w-fixed-28px` pair, because it made "which of these two halves
        // is allowed to move" a judgement call at every call site.
        const offenders: string[] = [];
        for (const file of sourceFiles(SRC)) {
            const rel = file.slice(SRC.length + 1).replaceAll('\\', '/');
            readFileSync(file, 'utf8')
                .split('\n')
                .forEach((line, i) => {
                    for (const m of line.matchAll(NUMERIC_SIZER)) {
                        if (m[2] === '0') continue;
                        offenders.push(
                            `${rel}:${i + 1}  ${m[1]} — pin it (size-icon-*, size-control-*, w-fixed-*)`,
                        );
                    }
                });
        }
        expect(offenders).toEqual([]);
    });

    it('keeps the Rule 10 row-action spacer the same size as IconButton', () => {
        // ADR-0021 Rule 10: "A row with no actions gets a same-size spacer, so
        // the columns of rows that do have them stay aligned." Their equality
        // is a rule, not a coincidence, and nothing else in the suite asserts
        // it — a change that moved one and not the other would misalign every
        // category row, and at the default density it would still look right.
        //
        // Pinned to the DEFAULT size by name rather than to "the first square
        // in IconButton.tsx". That shortcut broke the moment IconButton grew a
        // compact `sm` variant for the sidebar: the 20px branch sorts first in
        // the ternary, so the check started comparing the spacer against a size
        // it was never meant to match.
        const iconButton = readFileSync(join(SRC, 'components', 'ui', 'IconButton.tsx'), 'utf8');
        expect(iconButton).toContain("size = 'md'");
        expect(iconButton).toContain("size === 'sm' ? 'size-control-20px' : 'size-control-28px'");

        const spacer = squareOf('routes/ledgers/settings/CategoriesPanel.tsx');
        expect(spacer).toBe('size-control-28px');
    });

    it('puts nothing but --spacing inside the density blocks', () => {
        // The axis is allowed to move exactly one token. A pinned length that
        // drifted into one of these blocks would scale again, silently, and
        // the only symptom would be a soft icon at one setting out of three.
        const css = readFileSync(CSS, 'utf8');
        const blocks = [...css.matchAll(/\[data-density="([a-z]+)"\]\s*\{([^}]*)\}/g)];
        expect(blocks.map((b) => b[1])).toEqual(['compressed', 'regular', 'relaxed']);

        for (const [, name, body] of blocks) {
            const declared = [...body!.matchAll(/(--[a-z0-9-]+)\s*:/g)].map((m) => m[1]);
            expect(declared, `[data-density="${name}"]`).toEqual(['--spacing']);
        }

        // And the pinned tokens live in @theme, where density cannot reach
        // them — a bare `var(--spacing-icon-md)`, never `calc(… * N)`.
        const theme = themeBody();
        expect(theme).toContain('--spacing-icon-md');
        expect(theme).toContain('--spacing-control-28px');
    });
});
