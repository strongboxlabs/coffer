import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * Green is a status colour, never an amount's.
 *
 * ADR-0023 §H: a negative that is news is red, and everything else, gains
 * included, is default text. A green figure reads as "look at all this income"
 * on a screen that is mostly spending, and it made one figure two colours on
 * two screens. Nothing in the markup or in a role-based test shows a number
 * turning green, so this pins the set of files that may use a `state-success`
 * class at all, each with its reason. A new use fails until it is listed here
 * with a reason that is a status; a listed file that stops using it fails too,
 * so the list cannot go stale.
 */

const SRC = join(import.meta.dirname, '..');

/** Utilities that take a colour, as in designTokens.test.ts. */
const UTILITIES = [
    'bg', 'text', 'border', 'ring', 'divide', 'outline', 'fill', 'stroke',
    'shadow', 'from', 'via', 'to', 'decoration', 'accent', 'caret',
].join('|');

const GREEN = new RegExp(`(?:^|[\\s"'\`:])(?:${UTILITIES})-state-success(?:-[a-z0-9]+)*`);

const ALLOWED: Readonly<Record<string, string>> = {
    'lib/ledgerOperationDisplay.ts': "an operation's succeeded status pill",
    'routes/__styleguide/StyleGuidePage.tsx': "the success state's swatch, labelled with what it marks",
    'routes/imports/ImportLedgerPage.tsx': 'the import-finished notice',
    'routes/ledgers/register/shell/CsvMappingStep.tsx': 'the mapping-saved notice',
    'routes/ledgers/register/shell/ImportFileDialog.tsx': "the import result's check mark",
    'routes/ledgers/settings/CategoriesPanel.tsx': "the Income section's icon",
    'routes/ledgers/settings/FeedConnectionsPanel.tsx': 'connection status pills and the sync-result box',
    'routes/ledgers/settings/GeneralPanel.tsx': "a consistency check's healthy box",
    'routes/ledgers/settings/NotificationsPanel.tsx': "a notice's resolved status",
    'routes/ledgers/settings/components/RestoreSnapshotDialog.tsx': 'the restore-complete notice',
    'routes/system/BackupsPanel.tsx': "a backup action's done notice",
    'routes/system/MasterKeyPanel.tsx': 'the key-rotation done notice',
    'routes/system/NotificationsPanel.tsx': "a destination's delivering status",
    'routes/system/UpgradePanel.tsx': "the upgrade-finished notice, a completed step's check mark and success status pills",
    'routes/system/components/GoogleDriveSyncCard.tsx': 'the connect-result notice',
    'routes/system/components/RestoreBackupCard.tsx': 'the restore-complete notice',
};

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

describe('green is status, never an amount', () => {
    it('uses a state-success class only in the listed files', () => {
        const using = sourceFiles(SRC)
            .filter((file) => GREEN.test(readFileSync(file, 'utf8')))
            .map((file) => relative(SRC, file).split(sep).join('/'))
            .sort();

        // Guard the guard: if the walk ever finds nothing, an empty list would
        // pass by matching nothing against nothing.
        expect(using.length).toBeGreaterThan(5);

        expect(using).toEqual(Object.keys(ALLOWED).sort());
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});
