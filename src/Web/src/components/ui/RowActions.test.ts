import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

/**
 * A surface with row context menus also offers a visible way in.
 *
 * WHY THIS EXISTS. ADR-0021 Rule 10 closes with "a check that a surface
 * rendering `onContextMenu` also renders a visible control would catch the
 * next one. Not built yet." This is that check.
 *
 * The rule was written after the Tags and Categories settings panels were
 * found to contain ZERO buttons between them: eight actions — rename,
 * recolour, merge, delete, add sub-category, move — reachable only by
 * right-clicking a row. Nothing about that is visible in review. The markup
 * reads fine, every action works, and the panel looks finished; what is
 * missing is an affordance, and absence is the one thing a screenshot cannot
 * show you.
 *
 * WHAT IT CHECKS, AND WHY THAT AND NOT SOMETHING WEAKER. "Renders any button"
 * is the obvious check and it is worthless: the sidebar carries four
 * IconButtons — System settings, collapse, Account security, Sign out — all of
 * them global chrome, none of them a row action, and it would have passed
 * while its account rows had no visible path at all. So the check names the
 * mechanism the rule itself prescribes: "On a list row, the visible path is
 * `RowActionsButton`".
 *
 * That is still file-level, and therefore a proxy rather than a proof — a file
 * could render a kebab on one row type and not another. It is the strongest
 * check available without a DOM, and it catches the failure that has actually
 * happened twice: a whole surface with no affordance on it.
 *
 * EXCEPTIONS CARRY REASONS, not just names. An allowlist of bare paths
 * eventually vouches for itself; an entry that has to state why is one someone
 * can argue with. Each is re-validated below, so an exception that stops being
 * true fails rather than lingering.
 */

const SRC = join(import.meta.dirname, '..', '..');

/** The handler actually being attached, not a prop being declared or threaded. */
const ATTACHES_CONTEXT_MENU = /onContextMenu=\{/;

/** The visible path Rule 10 prescribes for a list row. */
const ROW_ACTIONS = /<RowActionsButton/;

/**
 * Surfaces exempt from the kebab, each with the reason ADR-0021 records.
 *
 * The register trio is Rule 10's stated partial exception — and the ADR is
 * careful to call it "a gap, not a clean bill of health". It is listed here on
 * the same terms: selecting rows reveals a bulk bar carrying recon status,
 * Delete, Unhide and Move to account, so those actions have a visible path,
 * while Accept, Edit, Duplicate, Create reminder, Show other side and Show raw
 * data remain right-click-only. Anyone adding a row action there should ask
 * whether it also belongs on the bulk bar.
 *
 * This block, the two rationales below and the same claim in RowActionsButton
 * all used to say the bar carried "Categorize / Move / Delete". Categorize and
 * Tag are not on it and never have been — they are blocked on bulk write
 * endpoints that do not exist — so three copies of one sentence advertised a
 * visible path that was not there.
 */
const EXCEPTIONS: ReadonlyMap<string, string> = new Map([
    [
        'routes/ledgers/register/shell/RegisterRow.tsx',
        'Register row. A control on every row of a grid this dense costs more than it buys; '
        + 'the bulk bar on the page is the visible path (ADR-0021 Rule 10).',
    ],
    [
        'routes/ledgers/register/bank/BankRegisterPage.tsx',
        'Bank register. Selection reveals a bulk bar carrying recon status, Delete, '
        + 'Unhide and Move to account.',
    ],
    [
        'routes/ledgers/register/investment/InvestmentRegisterPage.tsx',
        'Investment register. Selection reveals a bulk bar carrying recon status, '
        + 'Delete, Unhide and Move to account.',
    ],
]);

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
        else if (/\.tsx$/.test(entry) && !/\.test\.tsx$/.test(entry)) out.push(full);
    }
    return out;
}

function rel(file: string): string {
    return file.slice(SRC.length + 1).replaceAll('\\', '/');
}

/** Every surface that attaches a context-menu handler. */
function contextMenuSurfaces(): string[] {
    return sourceFiles(SRC)
        .filter((f) => ATTACHES_CONTEXT_MENU.test(readFileSync(f, 'utf8')))
        .map(rel)
        .sort();
}

describe('Rule 10 — a visible affordance beside every row context menu', () => {
    it('finds the surfaces at all', () => {
        // Guard the guard. If the pattern ever stops matching, every assertion
        // below passes on an empty list and the check silently stops working —
        // which, for a rule about absence, would be its own punchline.
        const surfaces = contextMenuSurfaces();
        expect(surfaces.length).toBeGreaterThanOrEqual(5);
        expect(surfaces).toContain('routes/ledgers/settings/CategoriesPanel.tsx');

        // And prove the detector distinguishes attaching from threading: a
        // prop TYPE or a pass-through is not a surface.
        expect(ATTACHES_CONTEXT_MENU.test('onContextMenu={(e) => open(e)}')).toBe(true);
        expect(ATTACHES_CONTEXT_MENU.test('onContextMenu?: (a: Anchor) => void;')).toBe(false);
        expect(ROW_ACTIONS.test('<RowActionsButton items={items} />')).toBe(true);
        expect(ROW_ACTIONS.test('<IconButton aria-label="Sign out" />')).toBe(false);
    });

    it('gives every context-menu surface a RowActionsButton', () => {
        const offenders: string[] = [];
        for (const surface of contextMenuSurfaces()) {
            if (EXCEPTIONS.has(surface)) continue;
            const text = readFileSync(join(SRC, surface), 'utf8');
            if (!ROW_ACTIONS.test(text)) {
                offenders.push(
                    `${surface} — right-click is the only way in. Add RowActionsButton, `
                    + 'or list it in EXCEPTIONS with a reason.',
                );
            }
        }
        expect(offenders).toEqual([]);
    });

    it('keeps every exception honest', () => {
        // An exception that no longer describes anything is worse than no
        // exception: it reads as a considered decision while covering nothing.
        const surfaces = new Set(contextMenuSurfaces());
        const stale: string[] = [];
        for (const [path, reason] of EXCEPTIONS) {
            if (!surfaces.has(path)) {
                stale.push(`${path} — no longer attaches onContextMenu; drop the exception.`);
            }
            expect(reason.length, path).toBeGreaterThan(40);
        }
        expect(stale).toEqual([]);
    });
});
