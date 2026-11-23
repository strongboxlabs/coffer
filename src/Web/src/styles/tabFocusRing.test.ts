import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * Every tab strip shows where keyboard focus is.
 *
 * The app's tab strips are hand-rolled `role="tab"` buttons with an underline, and
 * none showed focus until each gained `focus-visible:ring`. A role-based test
 * cannot see a missing ring, so this checks the source: a file that renders a tab
 * must carry a focus ring. Deliberately coarse, like a lint rule: a file that
 * names both passes, and a new strip without a ring fails.
 *
 * A strip of router links (Structure's, ADR-0111) is not `role="tab"`, since each tab is
 * a page of its own and not a panel, but it is drawn with the same underline, so the
 * underline is what marks a file as having a strip.
 */

const SRC = join(import.meta.dirname, '..');

/** What marks a file as drawing a tab strip: the role, or the underline every strip shares. */
const isTabStrip = (source: string): boolean =>
    source.includes('role="tab"') || source.includes('-mb-px border-b-2');

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) {
            out.push(...sourceFiles(full));
        } else if (/\.tsx$/.test(entry) && !/\.test\.tsx$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

describe('tab strips', () => {
    it('give every tab a focus ring', () => {
        const withTabs = sourceFiles(SRC).filter((f) => isTabStrip(readFileSync(f, 'utf8')));

        // Guard the guard: the app has tab strips, so finding none means the walk broke. And the
        // Structure strip is one of them, so a detector that stopped seeing it would pass silently.
        expect(withTabs.length).toBeGreaterThanOrEqual(5);
        expect(withTabs.map((f) => relative(SRC, f).split(sep).join('/')))
            .toContain('routes/ledgers/structure/StructureLayout.tsx');

        const missing = withTabs
            .filter((f) => !readFileSync(f, 'utf8').includes('focus-visible:ring'))
            .map((f) => relative(SRC, f).split(sep).join('/'));
        expect(missing).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});
