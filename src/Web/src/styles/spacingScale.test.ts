import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

/**
 * Rhythm — padding, margin, gap, space — comes off a sanctioned ramp.
 *
 * WHY THIS EXISTS. Not to clean anything up: when this landed, 1,690 of the
 * SPA's 1,691 rhythm utilities were already on the ramp below. The vocabulary
 * was coherent before anyone wrote it down, because it grew by copying the
 * neighbouring component. That is a fine way to hold a line and a terrible way
 * to defend one — nothing distinguished `py-2.5` (the list-row standard, nine
 * call sites) from `py-3.5` (one tile, no reason).
 *
 * What makes it worth enforcing is DENSITY. Tailwind v4 derives every one of
 * these utilities from `--spacing`, so `p-4` is `calc(var(--spacing) * 4)`.
 * The moment density becomes a user setting that variable moves — and every
 * step in the codebase gets multiplied by a number nobody chose it against. A
 * step that exists for no reason at 1x is a step nobody reasoned about at
 * 0.8x, and an ARBITRARY value (`py-[0.3rem]`) is worse: it does not move at
 * all, so it silently stops matching its neighbours at every density but one.
 * That was real — the sidebar nav row was pinned at `py-[0.3rem]` while every
 * row around it would have scaled.
 *
 * THREE KINDS OF LENGTH live in this codebase and only the first is rhythm:
 *
 *   1. Rhythm — padding, margin, gap, space. Rides `--spacing`, moves with
 *      density. This is what the ramp governs.
 *   2. Hairlines — `px`. Dividers (`gap-px`), border pull-ups (`-mb-px`),
 *      optical nudges (`py-px`). One device pixel, on purpose; scaling it to
 *      0.8px would make dividers vanish on non-HiDPI screens. Sanctioned, and
 *      deliberately NOT on the ramp.
 *   3. Pinned lengths — icon sizes, control heights, layout widths, and the
 *      register's scroll-gutter offsets (`right-[26px]`, `right-7`), which
 *      align to a browser-drawn scrollbar rather than to any design scale.
 *      These ride `--spacing` today by accident rather than by intent, which
 *      is the open problem density has to solve. They are NOT checked here —
 *      a ramp is the wrong rule for them, and pretending otherwise would put
 *      a green test over an unanswered question.
 *
 * So this guard is narrow on purpose. It proves one thing completely rather
 * than gesturing at three.
 *
 * COMMENTS ARE SCANNED TOO, and that is deliberate rather than an oversight.
 * The first run flagged a comment in AuthedSidebar describing the sidebar row
 * padding as `py-[0.3rem]` — a value that was about to stop being true two
 * lines of diff later. A comment naming a utility is a claim about the code,
 * and a stale one sends the next reader to a value that no longer exists.
 * Stripping comments out of TSX reliably is also a worse problem than the
 * occasional false positive it would avoid.
 */

const SRC = join(import.meta.dirname, '..');

/**
 * Utilities that consume the spacing scale as RHYTHM. Longest-first so that
 * `gap-x` is tried before `gap`, and `space-y` before nothing at all.
 */
const RHYTHM = [
    'p', 'px', 'py', 'pt', 'pr', 'pb', 'pl', 'ps', 'pe',
    'm', 'mx', 'my', 'mt', 'mr', 'mb', 'ml', 'ms', 'me',
    'gap', 'gap-x', 'gap-y', 'space-x', 'space-y',
].sort((a, b) => b.length - a.length);

/**
 * The sanctioned ramp.
 *
 * Read off the codebase rather than invented, because a ramp nobody is already
 * using is a ramp that generates 400 failures and then gets an allowlist.
 * Every step here earns its place:
 *
 *   0.5 1 1.5 2 3   — inside controls: icon-to-label gaps, chip padding, the
 *                     dense end of the register.
 *   2.5             — the list-row vertical padding (Panel, SidebarLayout and
 *                     seven settings panels). Sits between 2 and 3 on purpose:
 *                     8px rows are cramped, 12px rows waste a settings page.
 *   4 5 6           — between blocks. 5 is the page/dialog padding standard
 *                     (25 call sites: every page shell and every dialog).
 *   8 10 12         — between sections, and empty-state breathing room.
 *
 * 7, 9 and 11 are absent from rhythm today and stay absent: they exist in this
 * codebase only as control heights (`h-7`) and gutter offsets, which are
 * pinned lengths, not rhythm.
 */
const RAMP = new Set([
    '0', '0.5', '1', '1.5', '2', '2.5', '3', '4', '5', '6', '8', '10', '12',
]);

const ALT = RHYTHM.join('|');

/**
 * The lookbehind keeps `-mt-2` from also matching as `mt-2`, and keeps `gap-2`
 * inside `flex-gap-2` from matching at all. Variant prefixes (`sm:`, `hover:`)
 * end in a colon, which is neither a word character nor a dash, so they pass
 * straight through.
 */
const NUMERIC = new RegExp(`(?<![\\w-])(-?(?:${ALT})-(\\d+(?:\\.5)?))(?![\\w.])`, 'g');
const ARBITRARY = new RegExp(`(?<![\\w-])(-?(?:${ALT})-\\[[^\\]]+\\])`, 'g');

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) {
            out.push(...sourceFiles(full));
        } else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

interface Scan {
    offRamp: string[];
    arbitrary: string[];
    onRamp: number;
}

function scan(files: string[]): Scan {
    const result: Scan = { offRamp: [], arbitrary: [], onRamp: 0 };
    for (const file of files) {
        const rel = file.slice(SRC.length + 1).replaceAll('\\', '/');
        readFileSync(file, 'utf8').split('\n').forEach((line, i) => {
            for (const m of line.matchAll(NUMERIC)) {
                if (RAMP.has(m[2]!)) result.onRamp += 1;
                else result.offRamp.push(`${rel}:${i + 1}  ${m[1]}`);
            }
            for (const m of line.matchAll(ARBITRARY)) {
                result.arbitrary.push(`${rel}:${i + 1}  ${m[1]}`);
            }
        });
    }
    return result;
}

describe('spacing scale', () => {
    it('detects off-ramp and arbitrary rhythm values', () => {
        // Guard the guard. Both patterns are fiddly enough that a silent
        // failure to match ANYTHING would leave the real assertions vacuously
        // green — the same trap the token guard's size check exists for.
        const probe = scan([join(SRC, 'components', 'ui', 'Button.tsx')]);
        expect(probe.onRamp).toBeGreaterThan(3);

        // And prove each arm fires on a known-bad string, since "found
        // nothing" is the expected result everywhere else and so proves
        // nothing by itself.
        const sample = 'className="py-3.5 gap-[7px] -mt-2 sm:px-2.5"';
        const numeric = [...sample.matchAll(NUMERIC)];
        expect(numeric.filter((m) => !RAMP.has(m[2]!)).map((m) => m[1])).toEqual(['py-3.5']);
        expect([...sample.matchAll(ARBITRARY)].map((m) => m[1])).toEqual(['gap-[7px]']);
        // `-mt-2` matches once, as itself — not a second time as `mt-2`.
        expect(numeric.map((m) => m[1])).toContain('-mt-2');
        // A variant prefix does not hide a value from the scan.
        expect(numeric.map((m) => m[1])).toContain('px-2.5');
    });

    it('uses only sanctioned steps for padding, margin, gap and space', () => {
        const result = scan(sourceFiles(SRC));

        // A floor, not a count: this is here so that a refactor moving the SPA
        // out from under SRC fails loudly instead of passing on nothing.
        expect(result.onRamp).toBeGreaterThan(1000);

        // Listed, not counted — "4 off-ramp values" sends the reader hunting.
        expect(result.offRamp).toEqual([]);
    });

    it('uses no arbitrary rhythm values', () => {
        // Separate from the ramp check because the FAILURE MODE is different.
        // An off-ramp step is merely unsanctioned; an arbitrary value is
        // frozen — it will not move with density, so it drifts away from every
        // neighbour at every setting except the one it was eyeballed at.
        //
        // If one is ever genuinely needed it does not belong in a className:
        // give it a token, so the density work can decide what happens to it.
        expect(scan(sourceFiles(SRC)).arbitrary).toEqual([]);
    });
});
