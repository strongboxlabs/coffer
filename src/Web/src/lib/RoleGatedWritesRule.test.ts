import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, posix } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from './wholeTreeScan';

/**
 * A screen that writes ledger data asks the reader's role first (ADR-0023 §L).
 *
 * THE RULE. A viewer is shown no control they cannot use, and the server refuses a
 * viewer every write to ledger data (RequireLedgerAccess, and the row-level
 * security of migration 174). So a component that writes reads the role through
 * ledgerRole.ts — `useLedgerRole`, or a `canEdit` / `isOwner` it is given, which
 * came from it — and gates its write controls on it. A page that wrote without
 * asking offered a viewer every write the server would then refuse.
 *
 * WHAT IT CHECKS. Every file under src/routes and src/components but tests and
 * their fixtures is parsed. It WRITES when it runs a mutation (`useMutation`), or
 * imports from the API client a function that writes. A function of the client
 * writes when its body sends a method other than GET (a method that is not written
 * out counts), uploads (`requestMultipart`), or calls another function that writes.
 * A writer READS THE ROLE when its code names `useLedgerRole`, `canEdit`,
 * `isOwner`, or one of ledgerRole.ts's predicates. It is file-level, so a proxy:
 * a file that gates one control and not another passes. It catches what matters
 * most, a whole surface that never asks.
 *
 * A writer that does neither is listed in REVIEWED, by kind:
 *  - `opener`: a dialog, part, hook or helper that only gated surfaces use. Every
 *    file that renders one of its components or calls one of its functions must be
 *    gated, and at least one must exist. A file is gated when it reads the role, is
 *    reviewed as any other kind, or (writing nothing itself) is used only by gated
 *    files, followed up to the page that asks.
 *  - `membership`: the reader's own saved reports, pins or preferences, which the
 *    API lets any member write (RequireLedgerMembership). Each write function it
 *    imports must be one of MEMBERSHIP_WRITES.
 *  - `system`: an administrator's page, under routes/system, gated by admin, not by
 *    a ledger role.
 *  - `no-ledger`: a page that acts before the reader holds a role on the ledger it
 *    writes (signing in, setting up, accepting an invite, creating a ledger).
 *  - `own-account`: the reader's own account (passkeys, MCP tokens, grants), under
 *    routes/account: no ledger's data, so no ledger role.
 * A new writer that is neither fails, and so does an entry that no longer
 * describes its file. A detector gone blind fails too: it must find the client's
 * known writes and the tree's known writers, and it is run over samples.
 * Not caught: a write made through a prop, a function passed in by a parent that
 * itself reads the role (the parent is what is checked).
 */

const SRC = join(import.meta.dirname, '..');
const SCANNED = ['routes', 'components'];
const API_DIR = 'lib/api';

/** The client's primitives: an upload always writes, and `request` writes as its caller says. */
const PRIMITIVE_WRITES = ['requestMultipart'];

/** The writes any member of a ledger may make: their own saved reports, pins and preferences. */
const MEMBERSHIP_WRITES: ReadonlySet<string> = new Set([
    'createSavedReport', 'patchSavedReport', 'deleteSavedReport',
    'pinSavedReport', 'unpinSavedReport', 'pinCatalogReport', 'unpinCatalogReport',
    'saveDashboardPrefs', 'deleteDashboardPrefs', 'saveQuotesPrefs',
]);

type Kind = 'opener' | 'membership' | 'system' | 'no-ledger' | 'own-account';

interface Review {
    kind: Kind;
    reason: string;
}

/** Where a `no-ledger` page may live. */
const NO_LEDGER_PLACES = ['routes/login/', 'routes/setup/', 'routes/invite/', 'routes/landing/', 'routes/imports/'];

const REVIEWED: ReadonlyMap<string, Review> = new Map<string, Review>([
    // Dialogs and parts only a gated surface renders.
    ['components/ScheduleControl.tsx', { kind: 'opener', reason: 'A schedule\'s switch and time, rendered only where the page has asked the role (or on an admin page).' }],
    ['routes/ledgers/accounts/AccountEditorDialog.tsx', { kind: 'opener', reason: 'The account editor, opened from the gated Accounts page.' }],
    ['routes/ledgers/bank-edit/save.ts', { kind: 'opener', reason: 'The bank editor\'s save, called only by editors that gated surfaces open.' }],
    ['routes/ledgers/register/shell/brokerages.ts', { kind: 'opener', reason: 'A brokerage file import, run only from the gated import dialog.' }],
    ['routes/ledgers/register/shell/useRegisterBulkRecovery.ts', { kind: 'opener', reason: 'The registers\' bulk actions, which they offer owners and editors only.' }],
    ['routes/ledgers/register/shell/useRegisterController.ts', { kind: 'opener', reason: 'The registers\' controller; its writes run from controls the registers gate.' }],
    ['routes/ledgers/structure/useSetActive.ts', { kind: 'opener', reason: 'Closing or reopening an account, from the gated Structure and Settings surfaces.' }],
    ['routes/ledgers/components/AddSecurityDialog.tsx', { kind: 'opener', reason: 'Opened from the securities catalog\'s Add security and the investment editor, both gated.' }],
    ['routes/ledgers/home/ReviewDialog.tsx', { kind: 'opener', reason: 'Opened by To review\'s Review…, which a viewer is not offered.' }],
    ['routes/ledgers/investment-edit/InvestmentTxnRowEdit.tsx', { kind: 'opener', reason: 'The investment editor, opened only by surfaces that offer editing to owners and editors.' }],
    ['routes/ledgers/register/shell/CsvMappingStep.tsx', { kind: 'opener', reason: 'A step of the import dialog, which the registers offer owners and editors only.' }],
    ['routes/ledgers/register/shell/ImportFileDialog.tsx', { kind: 'opener', reason: 'The registers\' import, offered to owners and editors only.' }],
    ['routes/ledgers/reminders/ReminderEditorDialog.tsx', { kind: 'opener', reason: 'The series editor, opened from controls a viewer is not offered.' }],
    ['routes/ledgers/reminders/ReminderOccurrenceModal.tsx', { kind: 'opener', reason: 'Post or skip, opened from a due chip or a Bills row, neither offered to a viewer.' }],
    ['routes/ledgers/settings/CategoryDialogs.tsx', { kind: 'opener', reason: 'Category dialogs, opened from the gated Categories panel and tab.' }],
    ['routes/ledgers/settings/TagDialogs.tsx', { kind: 'opener', reason: 'Tag dialogs, opened from the gated Tags panel and tab.' }],
    ['routes/ledgers/settings/components/CreateSnapshotDialog.tsx', { kind: 'opener', reason: 'Opened by Create snapshot, offered to owners and editors only.' }],
    ['routes/ledgers/settings/components/RestoreSnapshotDialog.tsx', { kind: 'opener', reason: 'Opened by Restore, offered to the owner only.' }],
    // The reader's own saved reports, pins and preferences.
    ['routes/ledgers/home/useLayout.ts', { kind: 'membership', reason: 'The reader\'s own Overview layout, which a viewer arranges too.' }],
    ['routes/ledgers/reports/PinToggle.tsx', { kind: 'membership', reason: 'The reader\'s own rail pins.' }],
    ['routes/ledgers/reports/ReportsPage.tsx', { kind: 'membership', reason: 'Deleting the reader\'s own saved report.' }],
    ['routes/ledgers/reports/SaveReportControl.tsx', { kind: 'membership', reason: 'Saving the reader\'s own report.' }],
    // Administrators' pages.
    ['routes/system/BackupsPanel.tsx', { kind: 'system', reason: 'System backups, an administrator\'s.' }],
    ['routes/system/MasterKeyPanel.tsx', { kind: 'system', reason: 'The master key, an administrator\'s.' }],
    ['routes/system/McpConnectedAppsPanel.tsx', { kind: 'system', reason: 'MCP connected apps and their sign-ins, an administrator\'s.' }],
    ['routes/system/UsersPanel.tsx', { kind: 'system', reason: 'Users and their invites, an administrator\'s.' }],
    ['routes/system/McpAuditPanel.tsx', { kind: 'system', reason: 'The MCP audit, an administrator\'s.' }],
    ['routes/system/McpSettingsPanel.tsx', { kind: 'system', reason: 'MCP settings, an administrator\'s.' }],
    ['routes/system/NotificationsPanel.tsx', { kind: 'system', reason: 'System notification subscribers, an administrator\'s.' }],
    ['routes/system/UpgradePanel.tsx', { kind: 'system', reason: 'The in-app upgrade, an administrator\'s.' }],
    ['routes/system/components/ConnectDriveDialog.tsx', { kind: 'system', reason: 'Connecting backup storage, an administrator\'s.' }],
    ['routes/system/components/GoogleDriveSyncCard.tsx', { kind: 'system', reason: 'Backup storage sync, an administrator\'s.' }],
    ['routes/system/components/RestoreBackupCard.tsx', { kind: 'system', reason: 'Restoring a system backup, an administrator\'s.' }],
    ['routes/system/components/SetBackupPassphraseDialog.tsx', { kind: 'system', reason: 'The backup passphrase, an administrator\'s.' }],
    // Before a role exists.
    ['routes/imports/ImportLedgerPage.tsx', { kind: 'no-ledger', reason: 'Imports into a new ledger, which the reader owns once it exists.' }],
    ['routes/invite/InvitePage.tsx', { kind: 'no-ledger', reason: 'Accepting an invite, which is what grants the role.' }],
    ['routes/landing/LandingPage.tsx', { kind: 'no-ledger', reason: 'Creating a ledger, which the reader then owns.' }],
    ['routes/landing/useDemoBuild.ts', { kind: 'no-ledger', reason: 'Adding a Demo, a new ledger the reader then owns.' }],
    ['routes/login/LoginPage.tsx', { kind: 'no-ledger', reason: 'Signing in, before any role is known.' }],
    ['routes/login/RecoveryLoginPage.tsx', { kind: 'no-ledger', reason: 'Signing in with a recovery code.' }],
    ['routes/setup/SetupPage.tsx', { kind: 'no-ledger', reason: 'First-run setup, before any ledger.' }],
    // The reader's own account.
    ['routes/account/AccountSecurityPage.tsx', { kind: 'own-account', reason: 'The reader\'s passkeys, MCP tokens and OAuth grants.' }],
]);

/** The names whose use reads the reader's role through ledgerRole.ts. */
const ROLE_NAMES: ReadonlySet<string> = new Set(['useLedgerRole', 'canEdit', 'isOwner', 'canWriteLedger', 'isLedgerOwner']);

const isSource = (entry: string) => /\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && !/Fixtures?\.tsx?$/.test(entry);

function walk(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...walk(full));
        else if (isSource(entry)) out.push(full);
    }
    return out;
}

const rel = (file: string) => file.slice(SRC.length + 1).replaceAll('\\', '/');

function parse(name: string, text: string): ts.SourceFile {
    return ts.createSourceFile(name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
}

function forEachNode(node: ts.Node, visit: (n: ts.Node) => void): void {
    visit(node);
    ts.forEachChild(node, (child) => forEachNode(child, visit));
}

const hasExport = (node: ts.Node) =>
    ts.canHaveModifiers(node) && (ts.getModifiers(node) ?? []).some((m) => m.kind === ts.SyntaxKind.ExportKeyword);

/** Whether a function's body sends anything but a GET, or uploads. */
function sendsWrite(body: ts.Node): boolean {
    let found = false;
    forEachNode(body, (n) => {
        if (found) return;
        if (ts.isShorthandPropertyAssignment(n) && n.name.text === 'method') found = true;
        if (ts.isPropertyAssignment(n) && (ts.isIdentifier(n.name) || ts.isStringLiteral(n.name)) && n.name.text === 'method') {
            const v = n.initializer;
            const literal = ts.isStringLiteral(v) || ts.isNoSubstitutionTemplateLiteral(v);
            if (!literal || v.text.toUpperCase() !== 'GET') found = true;
        }
    });
    return found;
}

function calledNames(body: ts.Node): Set<string> {
    const out = new Set<string>();
    forEachNode(body, (n) => {
        if (ts.isCallExpression(n) && ts.isIdentifier(n.expression)) out.add(n.expression.text);
    });
    return out;
}

/** The API client's functions that write, by name, from its own source. */
export function apiWritesIn(modules: readonly { name: string; text: string }[]): Set<string> {
    const bodies = new Map<string, ts.Node>();
    for (const m of modules) {
        const source = parse(m.name, m.text);
        for (const s of source.statements) {
            if (ts.isFunctionDeclaration(s) && s.name && s.body && hasExport(s)) bodies.set(s.name.text, s.body);
            if (ts.isVariableStatement(s) && hasExport(s)) {
                for (const d of s.declarationList.declarations) {
                    if (ts.isIdentifier(d.name) && d.initializer
                        && (ts.isArrowFunction(d.initializer) || ts.isFunctionExpression(d.initializer))) {
                        bodies.set(d.name.text, d.initializer.body);
                    }
                }
            }
        }
    }
    const writes = new Set<string>(PRIMITIVE_WRITES);
    for (const [name, body] of bodies) {
        if (name === 'request') continue; // the primitive: its callers say the method
        if (sendsWrite(body)) writes.add(name);
    }
    // A function that calls one that writes, writes.
    for (let changed = true; changed;) {
        changed = false;
        for (const [name, body] of bodies) {
            if (writes.has(name) || name === 'request') continue;
            if ([...calledNames(body)].some((c) => writes.has(c))) { writes.add(name); changed = true; }
        }
    }
    return writes;
}

const API_MODULE = /^@\/lib\/api(\/.*)?$/;

/** Why a file writes: each mutation it runs, and each writing function it imports. */
export function writesOf(name: string, text: string, writes: ReadonlySet<string>): string[] {
    const source = parse(name, text);
    const out: string[] = [];
    forEachNode(source, (n) => {
        if (ts.isCallExpression(n) && ts.isIdentifier(n.expression) && n.expression.text === 'useMutation') {
            out.push('runs a mutation');
        }
        if (ts.isImportDeclaration(n) && ts.isStringLiteral(n.moduleSpecifier) && API_MODULE.test(n.moduleSpecifier.text)
            && n.importClause && !n.importClause.isTypeOnly) {
            const bindings = n.importClause.namedBindings;
            if (bindings && ts.isNamespaceImport(bindings)) out.push('imports the whole API client');
            if (bindings && ts.isNamedImports(bindings)) {
                for (const el of bindings.elements) {
                    const imported = (el.propertyName ?? el.name).text;
                    if (!el.isTypeOnly && writes.has(imported)) out.push(`imports ${imported}`);
                }
            }
        }
    });
    return out;
}

/** The writing functions a file imports from the client. */
const importedWrites = (reasons: readonly string[]) =>
    reasons.filter((r) => r.startsWith('imports ')).map((r) => r.slice('imports '.length));

/** Whether a file's code reads the reader's role. */
export function readsRole(name: string, text: string): boolean {
    let found = false;
    forEachNode(parse(name, text), (n) => {
        if (!found && ts.isIdentifier(n) && ROLE_NAMES.has(n.text)) found = true;
    });
    return found;
}

/** The values a file imports from each module, by the module's path under src. */
function valueImports(name: string, text: string): Map<string, string[]> {
    const out = new Map<string, string[]>();
    for (const st of parse(name, text).statements) {
        if (!ts.isImportDeclaration(st) || !ts.isStringLiteral(st.moduleSpecifier) || !st.importClause || st.importClause.isTypeOnly) continue;
        const spec = st.moduleSpecifier.text;
        const base = spec.startsWith('@/') ? spec.slice(2)
            : spec.startsWith('.') ? posix.normalize(posix.join(posix.dirname(name), spec)) : null;
        if (base === null) continue;
        const names: string[] = [];
        if (st.importClause.name) names.push(st.importClause.name.text);
        const b = st.importClause.namedBindings;
        if (b && ts.isNamedImports(b)) for (const el of b.elements) if (!el.isTypeOnly) names.push(el.name.text);
        if (b && ts.isNamespaceImport(b)) names.push(b.name.text);
        for (const candidate of [base, `${base}.ts`, `${base}.tsx`, `${base}/index.ts`, `${base}/index.tsx`]) {
            out.set(candidate, [...(out.get(candidate) ?? []), ...names]);
        }
    }
    return out;
}

/** Whether a file renders `<Name` or calls `Name(` (or `Name.x(`) for any of `names`. */
function uses(name: string, text: string, names: readonly string[]): boolean {
    if (names.length === 0) return false;
    const wanted = new Set(names);
    let found = false;
    forEachNode(parse(name, text), (n) => {
        if (found) return;
        if ((ts.isJsxOpeningElement(n) || ts.isJsxSelfClosingElement(n)) && ts.isIdentifier(n.tagName) && wanted.has(n.tagName.text)) found = true;
        if (ts.isCallExpression(n)) {
            const callee = n.expression;
            if (ts.isIdentifier(callee) && wanted.has(callee.text)) found = true;
            if (ts.isPropertyAccessExpression(callee) && ts.isIdentifier(callee.expression) && wanted.has(callee.expression.text)) found = true;
        }
    });
    return found;
}

interface Source { name: string; text: string }

function load(): { apiModules: Source[]; files: Source[] } {
    const apiModules = walk(join(SRC, API_DIR)).map((f) => ({ name: rel(f), text: readFileSync(f, 'utf8') }));
    const files = SCANNED.flatMap((d) => walk(join(SRC, d))).map((f) => ({ name: rel(f), text: readFileSync(f, 'utf8') }));
    return { apiModules, files };
}

/** What the rule fails the tree for. */
export function failuresIn(files: readonly Source[], writes: ReadonlySet<string>, reviewed: ReadonlyMap<string, Review>): string[] {
    const byName = new Map(files.map((f) => [f.name, f]));
    const writers = new Map(files.map((f) => [f.name, writesOf(f.name, f.text, writes)] as const).filter(([, r]) => r.length > 0));
    const gated = new Set(files.filter((f) => readsRole(f.name, f.text)).map((f) => f.name));
    const out: string[] = [];

    for (const [name, reasons] of writers) {
        if (gated.has(name) || reviewed.has(name)) continue;
        out.push(`${name} writes (${[...new Set(reasons)].join(', ')}) and never reads the role. Gate its write `
            + 'controls on useLedgerRole (or a canEdit / isOwner it is given), or list it in REVIEWED with its kind and why.');
    }

    // The files that render a module's components or call its functions.
    const imports = new Map(files.map((f) => [f.name, valueImports(f.name, f.text)] as const));
    const usersOf = (module: string) => files.filter((f) => f.name !== module && uses(f.name, f.text, imports.get(f.name)!.get(module) ?? []));

    // Gated: reads the role, or is reviewed as anything but a dialog, or is used, and
    // only used, by gated files; a writer reviewed as a dialog is judged the same way.
    const memo = new Map<string, boolean>();
    const isGated = (name: string, seen: ReadonlySet<string>): boolean => {
        const known = memo.get(name);
        if (known !== undefined) return known;
        if (gated.has(name)) return true;
        const review = reviewed.get(name);
        if (review !== undefined && review.kind !== 'opener') return true;
        if (writers.has(name) && review === undefined) return false;
        if (seen.has(name)) return false;
        const users = usersOf(name);
        const next = new Set(seen).add(name);
        const result = users.length > 0 && users.every((u) => isGated(u.name, next));
        memo.set(name, result);
        return result;
    };

    for (const [name, review] of reviewed) {
        const file = byName.get(name);
        if (!file) { out.push(`${name} is reviewed and does not exist; drop the entry.`); continue; }
        if (!writers.has(name)) out.push(`${name} is reviewed and writes nothing; drop the entry.`);
        if (gated.has(name)) out.push(`${name} is reviewed and reads the role itself; drop the entry.`);
        if (review.reason.length < 12) out.push(`${name}: say why in the reason.`);
        if (review.kind === 'opener' && !isGated(name, new Set())) {
            const ungated = usersOf(name).filter((u) => !isGated(u.name, new Set([name]))).map((u) => u.name);
            out.push(`${name} is reviewed as gated by what uses it, but ${ungated.length > 0
                ? `${ungated.join(', ')} ${ungated.length === 1 ? 'does' : 'do'} not ask the role` : 'nothing uses it'}.`);
        }
        if (review.kind === 'membership') {
            const other = importedWrites(writers.get(name) ?? []).filter((w) => !MEMBERSHIP_WRITES.has(w));
            if (other.length > 0) out.push(`${name} is reviewed as the reader's own data but imports ${other.join(', ')}.`);
        }
        if (review.kind === 'system' && !name.startsWith('routes/system/')) {
            out.push(`${name} is reviewed as an administrator's page but is not under routes/system.`);
        }
        if (review.kind === 'own-account' && !name.startsWith('routes/account/')) {
            out.push(`${name} is reviewed as the reader's own account but is not under routes/account.`);
        }
        if (review.kind === 'no-ledger' && !NO_LEDGER_PLACES.some((p) => name.startsWith(p))) {
            out.push(`${name} is reviewed as acting before a role exists but is not a sign-in, setup, invite, landing or import page.`);
        }
    }
    return out;
}

describe('a screen that writes ledger data asks the role (ADR-0023 §L)', () => {
    const { apiModules, files } = load();
    const writes = apiWritesIn(apiModules);

    // A POST that only computes (a preview, a lookup by many ids) counts as a write
    // too: the rule errs toward asking the role, never away from it.
    it('finds the API client\'s writes', () => {
        for (const w of ['createReminderBank', 'setReminderActive', 'skipReminder', 'unskipReminder', 'setBudgetTarget',
            'refreshQuotes', 'createSecurity', 'deleteSnapshot', 'restoreSnapshot', 'saveSchedule', 'deleteCsvMapping',
            'importCsv', ...MEMBERSHIP_WRITES]) {
            expect(writes, w).toContain(w);
        }
        for (const r of ['fetchReminders', 'fetchSnapshots', 'fetchSchedule', 'fetchBudgetProgress', 'fetchSecurities']) {
            expect(writes, r).not.toContain(r);
        }
        expect(writes.size).toBeGreaterThan(80);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('finds the tree\'s writers', () => {
        const writers = files.filter((f) => writesOf(f.name, f.text, writes).length > 0).map((f) => f.name);
        for (const w of ['routes/ledgers/reminders/RemindersListPanel.tsx', 'routes/ledgers/BudgetPage.tsx',
            'routes/ledgers/settings/SnapshotsPanel.tsx', 'components/ScheduleControl.tsx']) {
            expect(writers).toContain(w);
        }
        expect(writers.length).toBeGreaterThan(40);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('gates every writer on the role, or knows why not', () => {
        expect(failuresIn(files, writes, REVIEWED)).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});

describe('the detector', () => {
    const writes = apiWritesIn([{
        name: 'lib/api/sample.ts',
        text: `export function fetchThing(id) { return request('/x', { method: 'GET' }); }
            export function readThing(id) { return request('/x'); }
            export function saveThing(id) { return request('/x', { method: 'PUT', body: {} }); }
            export const dropThing = (id) => request('/x', { method: 'DELETE' });
            export function sendAs(id, method) { return request('/x', { method }); }
            export function upload(f) { return requestMultipart('/x', f); }
            export function saveTwice(id) { return saveThing(id).then(() => saveThing(id)); }`,
    }]);

    it('reads a client function\'s method, an upload, and a write it calls', () => {
        expect([...writes].sort()).toEqual(['dropThing', 'requestMultipart', 'saveThing', 'saveTwice', 'sendAs', 'upload']);
    });

    it.each([
        ['a mutation', `const m = useMutation({ mutationFn: go });`, ['runs a mutation']],
        ['an imported write', `import { saveThing, fetchThing } from '@/lib/api';`, ['imports saveThing']],
        ['a write from a client module', `import { dropThing } from '@/lib/api/sample';`, ['imports dropThing']],
        ['a renamed write', `import { saveThing as keep } from '@/lib/api';`, ['imports saveThing']],
        ['the whole client', `import * as api from '@/lib/api';`, ['imports the whole API client']],
        ['a read alone', `import { fetchThing } from '@/lib/api';`, []],
        ['a type', `import type { saveThing } from '@/lib/api';`, []],
    ])('finds %s', (_shape, snippet, expected) => {
        expect(writesOf('sample.tsx', snippet, writes)).toEqual(expected);
    });

    it.each([
        ['the hook', `const { canEdit } = useLedgerRole(id);`, true],
        ['a prop', `function Row({ canEdit }: { canEdit: boolean }) { return null; }`, true],
        ['a context field', `const show = home.canEdit;`, true],
        ['the owner flag', `function P({ isOwner }) { return null; }`, true],
        ['a comment only', `// canEdit is read elsewhere\nconst x = 1;`, false],
        ['a string only', `const label = 'canEdit';`, false],
    ])('reads the role through %s', (_shape, snippet, expected) => {
        expect(readsRole('sample.tsx', snippet)).toBe(expected);
    });

    it('fails a writer that never asks, and an entry that no longer holds', () => {
        const files = [
            { name: 'routes/a/Page.tsx', text: `import { saveThing } from '@/lib/api'; export function Page() { return null; }` },
            { name: 'routes/a/Gated.tsx', text: `import { saveThing } from '@/lib/api'; export function Gated() { const { canEdit } = useLedgerRole(l); return null; }` },
            { name: 'routes/a/Dialog.tsx', text: `export function Dialog() { useMutation({}); return null; }` },
            { name: 'routes/a/Ungated.tsx', text: `import { Dialog } from './Dialog'; export function Ungated() { return <Dialog />; }` },
            { name: 'routes/a/Quiet.tsx', text: `export function Quiet() { return null; }` },
        ];
        const failures = failuresIn(files, writes, new Map<string, Review>([
            ['routes/a/Dialog.tsx', { kind: 'opener', reason: 'Opened by a page that does not ask.' }],
            ['routes/a/Quiet.tsx', { kind: 'system', reason: 'Writes nothing at all, and is not a system page.' }],
        ]));
        expect(failures.some((f) => f.startsWith('routes/a/Page.tsx writes'))).toBe(true);
        expect(failures.some((f) => f.startsWith('routes/a/Gated.tsx'))).toBe(false);
        expect(failures).toContain('routes/a/Dialog.tsx is reviewed as gated by what uses it, but routes/a/Ungated.tsx does not ask the role.');
        expect(failures.some((f) => f.startsWith('routes/a/Quiet.tsx is reviewed and writes nothing'))).toBe(true);
        expect(failures.some((f) => f.startsWith('routes/a/Quiet.tsx is reviewed as an administrator'))).toBe(true);
    });

    it('passes a dialog every opener of which asks, through a part that writes nothing', () => {
        const files = [
            { name: 'routes/a/Dialog.tsx', text: `export function Dialog() { useMutation({}); return null; }` },
            { name: 'routes/a/Part.tsx', text: `import { Dialog } from './Dialog'; export function Part() { return <Dialog />; }` },
            { name: 'routes/a/Page.tsx', text: `import { Part } from './Part'; export function Page() { const { canEdit } = useLedgerRole(l); return canEdit ? <Part /> : null; }` },
        ];
        expect(failuresIn(files, writes, new Map<string, Review>([
            ['routes/a/Dialog.tsx', { kind: 'opener', reason: 'Opened by a page that asks.' }],
        ]))).toEqual([]);
    });

    it('fails a membership entry that writes ledger data', () => {
        const files = [{ name: 'routes/a/Pins.tsx', text: `import { saveThing } from '@/lib/api'; export function Pins() { return null; }` }];
        expect(failuresIn(files, writes, new Map<string, Review>([
            ['routes/a/Pins.tsx', { kind: 'membership', reason: 'Claims to be the reader\'s own pins.' }],
        ]))).toEqual(['routes/a/Pins.tsx is reviewed as the reader\'s own data but imports saveThing.']);
    });
});
