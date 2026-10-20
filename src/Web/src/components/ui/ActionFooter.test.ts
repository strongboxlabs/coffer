import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

/**
 * ADR-0021 Rule 9 says every "commit or back out" footer looks the same:
 * right-aligned, Cancel then the affirmative, secondary then primary, size sm.
 *
 * WHY THIS EXISTS. That was true of 22 footers across 16 files and still did
 * not hold — the Appearance panel shipped left-aligned Apply/Cancel, because a
 * convention enforced by review is enforced only when the reviewer remembers
 * it. `ActionFooter` makes the shape unforgettable for anyone who uses it; this
 * test is what stops the next footer being written by hand instead.
 *
 * WHAT IT CANNOT DO. It is a text scan, not a renderer. It sees a `justify-end`
 * container holding two or more `<Button>`s and calls that a hand-rolled
 * footer. It will not notice a footer built some other way, and it would not
 * have caught the Appearance panel's original sin — that one was
 * `justify-start`, which is legal here because plenty of non-footer rows are.
 * Its job is narrower than Rule 9: hold the line at the current count.
 */

const SRC = join(import.meta.dirname, '..', '..');

/**
 * FROZEN. Files still hand-rolling a footer, recorded rather than migrated.
 *
 * They were not converted in one pass on purpose. A regex sweep matched ZERO
 * of them — the convention is semantic, not textual, so they vary in attribute
 * order, formatting and label expressions — and hand-editing them all
 * blind is a worse risk than the drift it would prevent. Each becomes a
 * one-line change the next time someone opens the file for another reason.
 *
 * SHRINK THIS LIST; NEVER GROW IT. A new entry means a footer written by hand
 * after the primitive existed, which is the exact thing Rule 9 exists to stop.
 */
const FROZEN = new Set([
    'components/invites/InviteLinkModal.tsx',
    'routes/account/AccountSecurityPage.tsx',
    'routes/imports/ImportLedgerPage.tsx',
    'routes/landing/LandingPage.tsx',
    'routes/ledgers/accounts/AccountEditorDialog.tsx',
    'routes/ledgers/components/AddSecurityDialog.tsx',
    'routes/ledgers/register/shell/CsvMappingStep.tsx',
    'routes/ledgers/register/shell/ImportFileDialog.tsx',
    'routes/ledgers/register/shell/MoveToAccountDialog.tsx',
    'routes/ledgers/settings/CategoryDialogs.tsx',
    'routes/ledgers/settings/SnapshotsPanel.tsx',
    'routes/ledgers/settings/TagDialogs.tsx',
    'routes/ledgers/settings/components/CreateSnapshotDialog.tsx',
    'routes/ledgers/settings/components/RestoreSnapshotDialog.tsx',
    'routes/oauth/ConsentPage.tsx',
    'routes/system/BackupsPanel.tsx',
    'routes/system/components/ConnectDriveDialog.tsx',
    'routes/system/components/RestoreBackupCard.tsx',
    'routes/system/components/SetBackupPassphraseDialog.tsx',
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

/**
 * A `justify-end` container holding at least one Button.
 *
 * ONE, not two. A two-button threshold reads as "a Cancel/commit pair" and
 * misses single-slot footers — AccountSecurityPage's lone "Done" is one. It
 * also over-captures: a right-aligned toolbar action is not a footer. Both
 * errors are inherent to scanning text, so the threshold is chosen on which
 * error is cheaper — a false positive costs one line on the frozen list, a
 * false negative lets drift through unseen.
 */
function handRolledFooters(text: string): number {
    let hits = 0;
    for (const m of text.matchAll(/justify-end/g)) {
        const segment = text.slice(m.index, m.index + 1400);
        if ((segment.match(/<Button/g) ?? []).length >= 1) hits += 1;
    }
    return hits;
}

describe('action footers (ADR-0021 Rule 9)', () => {
    it('are built with ActionFooter, except where frozen', () => {
        const offenders: string[] = [];
        for (const file of sourceFiles(SRC)) {
            const rel = file.slice(SRC.length + 1).replace(/\\/g, '/');
            if (rel.endsWith('components/ui/ActionFooter.tsx')) continue;
            if (handRolledFooters(readFileSync(file, 'utf8')) > 0 && !FROZEN.has(rel)) {
                offenders.push(rel);
            }
        }
        // Named, not counted — "3 offenders" sends the reader hunting.
        expect(offenders).toEqual([]);
    });

    it('has no stale entries in the frozen list', () => {
        // A file that got migrated must leave the list, or the list stops
        // meaning anything and the count never falls.
        const stale = [...FROZEN].filter((rel) => {
            try {
                return handRolledFooters(readFileSync(join(SRC, rel), 'utf8')) === 0;
            } catch {
                return true;          // deleted or renamed
            }
        });
        expect(stale).toEqual([]);
    });

    it('would catch a newly hand-rolled footer', () => {
        // The guard's own guard: a matcher that quietly stopped matching would
        // make the test above pass forever.
        const sample = `
            <div className="flex justify-end gap-2">
                <Button variant="secondary">Cancel</Button>
                <Button variant="primary">Save</Button>
            </div>`;
        expect(handRolledFooters(sample)).toBe(1);
        expect(handRolledFooters('<div className="flex justify-start"><Button/></div>')).toBe(0);
    });
});
