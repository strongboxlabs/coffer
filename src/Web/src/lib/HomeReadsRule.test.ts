import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, posix } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * A write that refreshes the accounts refreshes the home screen too (ADR-0079,
 * as amended 2026-11-03).
 *
 * THE RULE. A handler that refreshes the accounts read (`['accounts', ledgerId]`,
 * or any key that starts with 'accounts') has written ledger data the home
 * screen reads as well: its review and recent rows, the overview's balances,
 * this month's spending. So the same handler calls `invalidateHomeReads`, or
 * `invalidateLedgerRegister`, which calls it. A precise register writer that
 * patches its window in place and refreshes the accounts, but not the home
 * screen's reads, leaves the home screen showing the figures from before the
 * write until something else refreshes them.
 *
 * WHAT IT CHECKS. Every file of the SPA's source but tests and their fixtures is
 * parsed, so a comment that names a key does not count. A refresh is a call of
 * `invalidateQueries`, `refetchQueries`, `resetQueries` or `removeQueries`, on a
 * QueryClient or under any other name. Its key is read from the filter's
 * `queryKey`: a list written out, or a const that holds one, in the file or in
 * a module it imports by name, followed through other consts. A refresh of the
 * accounts is one whose key's first word is 'accounts' (either branch of a
 * conditional counts). Its handler is the function the call is written in, the
 * nearest one; the home screen's refresh must be called in that function's own
 * body, not in a function nested in it, which may run later or never.
 *
 * A refresh it cannot read fails as unrecognised rather than passing unseen: no
 * filter or a filter with no queryKey (each refreshes every query it matches,
 * the accounts among them), a filter not written out or spreading another, and
 * a key that is not a list, starts with a spread or with a word not written
 * out, or is named by a parameter or a binding that is not a const.
 *
 * A file whose accounts refreshes may go without the home screen's is listed in
 * REVIEWED with how many and why. A count that no longer matches fails, so a new
 * refresh in a listed file is reviewed before it ships, and an entry that covers
 * nothing is dropped. A detector gone blind fails too: the helper's own refresh
 * of the accounts is counted, and the detector is run over samples of every form
 * above. Not caught: a key assembled at run time from something other than a
 * const, or a QueryClient method called through a name of the caller's own.
 */

/** The word that starts every key of the accounts read. */
const ACCOUNTS = 'accounts';

/** The QueryClient methods that make the next read of a key go to the server. */
const REFRESHES: ReadonlySet<string> = new Set(['invalidateQueries', 'refetchQueries', 'resetQueries', 'removeQueries']);

/** The calls that refresh the home screen's reads: the helper itself, and the wholesale one that calls it. */
const HOME_REFRESHES: ReadonlySet<string> = new Set(['invalidateHomeReads', 'invalidateLedgerRegister']);

/** Where both helpers live; invalidateLedgerRegister refreshes the accounts beside invalidateHomeReads. */
const HELPER_FILE = 'lib/registerInvalidation.ts';

interface Review {
    /** How many of the file's accounts refreshes go without the home screen's. */
    count: number;
    reason: string;
}

/**
 * Every file whose accounts refreshes may go without the home screen's, with
 * how many and why. None today.
 */
const REVIEWED: ReadonlyMap<string, Review> = new Map<string, Review>([]);

const SRC = join(import.meta.dirname, '..');

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
        // Test code and its fixtures seed and refresh a cache of their own; they write no ledger data.
        else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && !/Fixtures?\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

function rel(file: string): string {
    return file.slice(SRC.length + 1).replaceAll('\\', '/');
}

function parse(name: string, text: string): ts.SourceFile {
    return ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
}

/** The expression with parentheses, type assertions, `satisfies` and `!` taken off. */
function bare(node: ts.Expression): ts.Expression {
    let cur = node;
    for (;;) {
        if (ts.isParenthesizedExpression(cur) || ts.isAsExpression(cur)
            || ts.isTypeAssertionExpression(cur) || ts.isSatisfiesExpression(cur)
            || ts.isNonNullExpression(cur)) {
            cur = cur.expression;
        } else {
            return cur;
        }
    }
}

/** The text of an identifier or a string literal naming a property, or undefined. */
function nameText(node: ts.Node | undefined): string | undefined {
    if (node === undefined) return undefined;
    if (ts.isIdentifier(node) || ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) return node.text;
    return undefined;
}

/** The name a call calls: `f()` → f, `qc.invalidateQueries()` → invalidateQueries, `qc['x']()` → x. */
function calleeName(call: ts.CallExpression): string | undefined {
    const callee = bare(call.expression);
    if (ts.isIdentifier(callee)) return callee.text;
    if (ts.isPropertyAccessExpression(callee)) return callee.name.text;
    if (ts.isElementAccessExpression(callee)) return nameText(callee.argumentExpression);
    return undefined;
}

/** A source file read for the consts it holds, by its path under src, read once. */
const modules = new Map<string, ts.SourceFile | null>();

/** The module an import names, as a path under src, or undefined when it is not a file of the SPA. */
function moduleOf(file: string, spec: string): ts.SourceFile | undefined {
    let base: string;
    if (spec.startsWith('@/')) base = spec.slice(2);
    else if (spec.startsWith('.')) base = posix.join(posix.dirname(file), spec);
    else return undefined;
    for (const candidate of [base, `${base}.ts`, `${base}.tsx`, `${base}/index.ts`, `${base}/index.tsx`]) {
        if (!/\.tsx?$/.test(candidate)) continue;
        if (!modules.has(candidate)) {
            const full = join(SRC, candidate);
            modules.set(candidate, existsSync(full) ? parse(candidate, readFileSync(full, 'utf8')) : null);
        }
        const found = modules.get(candidate);
        if (found) return found;
    }
    return undefined;
}

/** Where a const's value is written: the expression, and the file it is read in. */
interface Value {
    init: ts.Expression;
    source: ts.SourceFile;
}

/** Whether a binding (a parameter's name, a destructuring) binds `name`. */
function binds(name: ts.BindingName, wanted: string): boolean {
    if (ts.isIdentifier(name)) return name.text === wanted;
    const elements: readonly ts.ArrayBindingElement[] = name.elements;
    return elements.some((e) => !ts.isOmittedExpression(e) && binds(e.name, wanted));
}

/**
 * The value an import of `name` from a module reads: the module's top-level
 * const of that name, or what the module re-exports under it (a barrel's
 * `export { … } from`, `export * from`, or `export { … }` of a name it holds or
 * imports), followed to the const.
 */
function exportedConst(source: ts.SourceFile, name: string, hops = 0): Value | string {
    if (hops >= MAX_HOPS) return `${name} is re-exported through too many modules to follow`;
    for (const statement of source.statements) {
        if (!ts.isVariableStatement(statement)) continue;
        for (const d of statement.declarationList.declarations) {
            if (!ts.isIdentifier(d.name) || d.name.text !== name) continue;
            if ((statement.declarationList.flags & ts.NodeFlags.Const) === 0 || d.initializer === undefined) {
                return `${name} in ${source.fileName} is not a const with a value`;
            }
            return { init: d.initializer, source };
        }
    }
    for (const statement of source.statements) {
        if (!ts.isExportDeclaration(statement) || statement.isTypeOnly) continue;
        const clause = statement.exportClause;
        const spec = statement.moduleSpecifier !== undefined && ts.isStringLiteral(statement.moduleSpecifier)
            ? statement.moduleSpecifier.text
            : undefined;
        if (clause !== undefined && ts.isNamedExports(clause)) {
            const element = clause.elements.find((e) => nameText(e.name) === name);
            if (element === undefined) continue;
            const inner = element.propertyName ?? element.name;
            if (spec === undefined) {
                return ts.isIdentifier(inner) ? constValue(inner, source) : `${name} is re-exported under a string name`;
            }
            const target = moduleOf(source.fileName, spec);
            if (target === undefined) return `${name} is re-exported from outside the SPA's source`;
            return exportedConst(target, inner.text, hops + 1);
        }
        if (clause === undefined && spec !== undefined) {
            const target = moduleOf(source.fileName, spec);
            const found = target === undefined ? undefined : exportedConst(target, name, hops + 1);
            if (found !== undefined && typeof found !== 'string') return found;
        }
    }
    return `${name} is not a const of ${source.fileName}`;
}

/**
 * The value of the const an identifier names: the nearest declaration of it
 * that encloses the use, or a const of a module the file imports it from by
 * name. A parameter, a binding that is not a const, a destructuring or a default
 * or namespace import is not read.
 */
function constValue(id: ts.Identifier, source: ts.SourceFile): Value | string {
    const name = id.text;
    for (let scope: ts.Node | undefined = id.parent; scope !== undefined; scope = scope.parent) {
        if (ts.isFunctionLike(scope) && scope.parameters.some((p) => binds(p.name, name))) {
            return `${name} is a parameter; name the key where it is refreshed`;
        }
        let statements: ts.NodeArray<ts.Statement> | undefined;
        if (ts.isBlock(scope) || ts.isSourceFile(scope) || ts.isModuleBlock(scope)
            || ts.isCaseClause(scope) || ts.isDefaultClause(scope)) {
            statements = scope.statements;
        }
        if (statements === undefined) continue;
        for (const statement of statements) {
            if (ts.isVariableStatement(statement)) {
                for (const d of statement.declarationList.declarations) {
                    if (!binds(d.name, name)) continue;
                    if (!ts.isIdentifier(d.name) || (statement.declarationList.flags & ts.NodeFlags.Const) === 0
                        || d.initializer === undefined) {
                        return `${name} is not a const with a value of its own`;
                    }
                    return { init: d.initializer, source };
                }
            }
            if (ts.isImportDeclaration(statement) && ts.isStringLiteral(statement.moduleSpecifier)) {
                const clause = statement.importClause;
                if (clause?.name?.text === name) return `${name} is a default import`;
                const bindings = clause?.namedBindings;
                if (bindings !== undefined && ts.isNamespaceImport(bindings) && bindings.name.text === name) {
                    return `${name} is a namespace import`;
                }
                if (bindings === undefined || !ts.isNamedImports(bindings)) continue;
                const element = bindings.elements.find((e) => e.name.text === name);
                if (element === undefined) continue;
                const target = moduleOf(source.fileName, statement.moduleSpecifier.text);
                if (target === undefined) return `${name} is imported from outside the SPA's source`;
                return exportedConst(target, (element.propertyName ?? element.name).text);
            }
        }
    }
    return `${name} is not a const of this file or of a module it imports`;
}

/** How deep a chain of consts is followed before the key counts as unreadable. */
const MAX_HOPS = 5;

/** The words a key's first element can be, or why they cannot be read. */
function wordsOf(expr: ts.Expression, source: ts.SourceFile, hops: number): string[] | string {
    const e = bare(expr);
    if (ts.isStringLiteral(e) || ts.isNoSubstitutionTemplateLiteral(e)) return [e.text];
    if (ts.isConditionalExpression(e)) {
        const yes = wordsOf(e.whenTrue, source, hops);
        const no = wordsOf(e.whenFalse, source, hops);
        if (typeof yes === 'string') return yes;
        if (typeof no === 'string') return no;
        return [...yes, ...no];
    }
    if (ts.isIdentifier(e) && hops < MAX_HOPS) {
        const value = constValue(e, source);
        return typeof value === 'string' ? value : wordsOf(value.init, value.source, hops + 1);
    }
    return 'the key\'s first word is not written out';
}

/** The words a key can start with, or why they cannot be read. */
function keyWords(key: ts.Expression, source: ts.SourceFile, hops = 0): string[] | string {
    const k = bare(key);
    if (ts.isArrayLiteralExpression(k)) {
        const first = k.elements[0];
        if (first === undefined) return 'an empty key matches every query, the accounts among them';
        if (ts.isSpreadElement(first)) return 'the key starts with a spread';
        return wordsOf(first, source, hops);
    }
    if (ts.isIdentifier(k) && hops < MAX_HOPS) {
        const value = constValue(k, source);
        return typeof value === 'string' ? value : keyWords(value.init, value.source, hops + 1);
    }
    return 'the key is not a list written out, nor a const that holds one';
}

/** The key a refresh's filter names, or why there is none to read. */
function filterKey(call: ts.CallExpression, method: string): ts.Expression | string {
    const filter = call.arguments[0];
    if (filter === undefined) return `${method}() with no filter refreshes every query, the accounts among them`;
    const f = bare(filter);
    if (!ts.isObjectLiteralExpression(f)) return 'the filter is not written out; pass { queryKey: [...] }';
    if (f.properties.some((p) => ts.isSpreadAssignment(p))) return 'the filter spreads another object';
    const key = f.properties.find((p) => nameText(p.name) === 'queryKey');
    if (key === undefined) return 'a filter with no queryKey refreshes every query it matches, the accounts among them';
    if (ts.isShorthandPropertyAssignment(key)) return key.name;
    if (ts.isPropertyAssignment(key)) return key.initializer;
    return 'the queryKey is not a plain property';
}

/** Whether a function's own body calls a refresh of the home screen's reads (not a function nested in it). */
function refreshesHome(handler: ts.Node): boolean {
    const walk = (node: ts.Node): boolean => {
        if (ts.isCallExpression(node)) {
            const name = calleeName(node);
            if (name !== undefined && HOME_REFRESHES.has(name)) return true;
        }
        if (node !== handler && ts.isFunctionLike(node)) return false;
        return ts.forEachChild(node, walk) ?? false;
    };
    return walk(handler);
}

/** The function a call is written in, the nearest one; the file for a call outside any. */
function handlerOf(node: ts.Node): ts.Node {
    for (let n = node.parent; n !== undefined; n = n.parent) if (ts.isFunctionLike(n)) return n;
    return node.getSourceFile();
}

/** One refresh call and what the scan read of it. */
interface Refresh {
    line: number;
    /** Why the key could not be read, when it could not. */
    unrecognised?: string;
    /** Whether its key starts with 'accounts'. */
    accounts: boolean;
    /** Whether its handler refreshes the home screen's reads too. */
    home: boolean;
}

/** Every refresh in one source text, named `name` (its path under src) in what it reports. */
function refreshesOf(name: string, text: string): Refresh[] {
    const source = parse(name, text);
    const out: Refresh[] = [];
    const at = (node: ts.Node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    const visit = (node: ts.Node) => {
        const method = ts.isCallExpression(node) ? calleeName(node) : undefined;
        if (ts.isCallExpression(node) && method !== undefined && REFRESHES.has(method)) {
            const key = filterKey(node, method);
            const words = typeof key === 'string' ? key : keyWords(key, source);
            const home = refreshesHome(handlerOf(node));
            out.push(typeof words === 'string'
                ? { line: at(node), unrecognised: words, accounts: false, home }
                : { line: at(node), accounts: words.includes(ACCOUNTS), home });
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    return out;
}

/** What fails in one file, given its review. */
function failuresOf(file: string, refreshes: readonly Refresh[], review: Review | undefined): string[] {
    const out = refreshes
        .filter((r) => r.unrecognised !== undefined)
        .map((r) => `${file}:${r.line} — the key cannot be read: ${r.unrecognised}`);
    const alone = refreshes.filter((r) => r.unrecognised === undefined && r.accounts && !r.home);
    const allowed = review?.count ?? 0;
    if (alone.length === allowed) return out;
    if (allowed === 0) {
        out.push(...alone.map((r) => `${file}:${r.line} — refreshes the accounts and not the home screen's reads. `
            + 'Call invalidateHomeReads (a precise writer) or invalidateLedgerRegister (a wholesale one) in the '
            + 'same handler, or list the file in REVIEWED with why it may not.'));
    } else {
        out.push(`${file} holds ${alone.length} accounts refreshes without the home screen's reads `
            + `(lines ${alone.map((r) => r.line).join(', ') || 'none'}), REVIEWED says ${allowed}. `
            + 'Review a new one before it ships, or lower the count.');
    }
    return out;
}

let everyFile: Map<string, Refresh[]> | undefined;

/** Every source file's refreshes. Every file is parsed: a text filter in front could drift from the detector. */
function allRefreshes(): Map<string, Refresh[]> {
    everyFile ??= new Map(sourceFiles(SRC).map((file) => [rel(file), refreshesOf(rel(file), readFileSync(file, 'utf8'))]));
    return everyFile;
}

/** The refreshes in an inline snippet, for testing the detector itself. */
const refreshesIn = (snippet: string, name = 'snippet.ts') => refreshesOf(name, snippet);

describe('a write that refreshes the accounts refreshes the home screen (ADR-0079)', () => {
    // Guard the guard: each of these refreshes the accounts read.
    it.each([
        ['a key written out', `qc.invalidateQueries({ queryKey: ['accounts', ledgerId] });`],
        ['the prefix of every ledger\'s', `qc.invalidateQueries({ queryKey: ['accounts'] });`],
        ['a key under the read', `qc.invalidateQueries({ queryKey: ['accounts', ledgerId, 'balances'] });`],
        ['a refetch', `void qc.refetchQueries({ queryKey: ['accounts', ledgerId] });`],
        ['a reset', `await qc.resetQueries({ queryKey: ['accounts', ledgerId] });`],
        ['a removal', `qc.removeQueries({ queryKey: ['accounts', ledgerId] });`],
        ['a key through a type assertion', `qc.invalidateQueries({ queryKey: ['accounts', ledgerId] as const });`],
        ['a template literal', 'qc.invalidateQueries({ queryKey: [`accounts`, ledgerId] });'],
        ['a conditional first word', `qc.invalidateQueries({ queryKey: [all ? 'accounts' : 'holdings', ledgerId] });`],
        ['a key held in a const', `const key = ['accounts', ledgerId] as const; qc.invalidateQueries({ queryKey: key });`],
        ['a key passed by its own name', `const queryKey = ['accounts', ledgerId]; qc.invalidateQueries({ queryKey });`],
        ['a const named through another', `const a = ['accounts', id]; const b = a; qc.invalidateQueries({ queryKey: b });`],
        ['a first word held in a const', `const WORD = 'accounts'; qc.invalidateQueries({ queryKey: [WORD, id] });`],
        ['the method under its own name', `invalidateQueries({ queryKey: ['accounts', ledgerId] });`],
        ['the method read by its name', `qc['invalidateQueries']({ queryKey: ['accounts', ledgerId] });`],
        ['an optional call', `qc?.invalidateQueries({ queryKey: ['accounts', ledgerId] });`],
    ])('sees %s as a refresh of the accounts', (_label, snippet) => {
        const [refresh, ...rest] = refreshesIn(`function f() { ${snippet} }`);
        expect(rest).toEqual([]);
        expect(refresh!.unrecognised).toBeUndefined();
        expect(refresh!.accounts).toBe(true);
    });

    it.each([
        ['another key', `qc.invalidateQueries({ queryKey: ['holdings', ledgerId] });`],
        ['the singular account', `qc.invalidateQueries({ queryKey: ['account', ledgerId, id] });`],
        ['a key held in a const', `const KEY = ['admin-users'] as const; qc.invalidateQueries({ queryKey: KEY });`],
        ['a second word that is accounts', `qc.invalidateQueries({ queryKey: ['feed-connection', 'accounts'] });`],
    ])('sees %s as a refresh of something else', (_label, snippet) => {
        const [refresh] = refreshesIn(`function f() { ${snippet} }`);
        expect(refresh!.unrecognised).toBeUndefined();
        expect(refresh!.accounts).toBe(false);
    });

    it.each([
        ['a write of the cached value', `qc.setQueryData(['accounts', ledgerId], rows);`],
        ['a read', `useQuery({ queryKey: ['accounts', ledgerId], queryFn });`],
        ['a comment', `// qc.invalidateQueries({ queryKey: ['accounts', ledgerId] });`],
        ['a string', `const doc = "qc.invalidateQueries({ queryKey: ['accounts'] })";`],
    ])('sees no refresh in %s', (_label, snippet) => {
        expect(refreshesIn(`function f() { ${snippet} }`)).toEqual([]);
    });

    // Each a refresh whose key the detector cannot read; it fails rather than passing unseen.
    it.each([
        ['no filter', `qc.invalidateQueries();`],
        ['a predicate', `qc.invalidateQueries({ predicate: (q) => q.queryKey[0] === 'accounts' });`],
        ['a filter of another kind', `qc.refetchQueries({ type: 'active' });`],
        ['a filter held elsewhere', `qc.invalidateQueries(filters);`],
        ['a spread filter', `qc.invalidateQueries({ ...filters, queryKey: ['holdings'] });`],
        ['an empty key', `qc.invalidateQueries({ queryKey: [] });`],
        ['a key that starts with a spread', `qc.invalidateQueries({ queryKey: [...base, id] });`],
        ['a key built by a call', `qc.invalidateQueries({ queryKey: keyFor(id) });`],
        ['a first word from a call', `qc.invalidateQueries({ queryKey: [word(), id] });`],
        ['a key that is a parameter', `function g(key) { qc.invalidateQueries({ queryKey: key }); }`],
        ['a first word that is a parameter', `function g(word) { qc.invalidateQueries({ queryKey: [word, id] }); }`],
        ['a key in a let', `let key = ['holdings']; qc.invalidateQueries({ queryKey: key });`],
        ['a key from a destructuring', `const { key } = keys; qc.invalidateQueries({ queryKey: key });`],
        ['a key that is not declared', `qc.invalidateQueries({ queryKey: KEY });`],
        ['a key from outside the SPA', `import { KEY } from 'some-package'; qc.invalidateQueries({ queryKey: KEY });`],
        ['a key a module does not hold', `import { NOT_THERE } from '@/lib/api/upgrade'; qc.invalidateQueries({ queryKey: NOT_THERE });`],
        ['a namespace\'s key', `import * as keys from '@/lib/api/upgrade'; qc.invalidateQueries({ queryKey: keys });`],
    ])('fails %s as unrecognised', (_label, snippet) => {
        const refreshes = refreshesIn(snippet.startsWith('import') ? snippet : `function f() { ${snippet} }`);
        expect(refreshes).toHaveLength(1);
        expect(refreshes[0]!.unrecognised).toBeDefined();
        expect(failuresOf('a.ts', refreshes, undefined)).toHaveLength(1);
    });

    it('reads a key a module of the SPA exports, by an import from @/, a relative path or a barrel', () => {
        const fromAlias = refreshesIn(`import { UPGRADE_QUERY_KEY } from '@/lib/api/upgrade';
            function f() { qc.invalidateQueries({ queryKey: UPGRADE_QUERY_KEY }); }`);
        expect(fromAlias[0]!.unrecognised).toBeUndefined();
        expect(fromAlias[0]!.accounts).toBe(false);
        const fromPath = refreshesIn(`import { UPGRADE_QUERY_KEY as K } from './api/upgrade';
            function f() { qc.invalidateQueries({ queryKey: K }); }`, 'lib/sample.ts');
        expect(fromPath[0]!.unrecognised).toBeUndefined();
        // lib/api.ts re-exports the API modules' names.
        const fromBarrel = refreshesIn(`import { UPGRADE_QUERY_KEY } from '@/lib/api';
            function f() { qc.invalidateQueries({ queryKey: UPGRADE_QUERY_KEY }); }`);
        expect(fromBarrel[0]!.unrecognised).toBeUndefined();
    });

    it.each([
        ['beside it', `function f() { qc.invalidateQueries({ queryKey: ['accounts', l] }); invalidateHomeReads(qc, l); }`],
        ['before it', `function f() { invalidateHomeReads(qc, l); qc.invalidateQueries({ queryKey: ['accounts', l] }); }`],
        ['in a branch of the handler', `const f = () => { qc.invalidateQueries({ queryKey: ['accounts', l] }); if (ok) invalidateHomeReads(qc, l); };`],
        ['through the wholesale helper', `function f() { qc.invalidateQueries({ queryKey: ['accounts', l] }); invalidateLedgerRegister(qc, l); }`],
        ['through a module name', `function f() { qc.invalidateQueries({ queryKey: ['accounts', l] }); inv.invalidateHomeReads(qc, l); }`],
        ['in a mutation\'s onSuccess', `useMutation({ onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            invalidateHomeReads(queryClient, ledgerId);
        } });`],
    ])('passes a refresh of the accounts with the home screen\'s %s', (_label, snippet) => {
        const refreshes = refreshesIn(snippet);
        expect(refreshes.filter((r) => r.accounts)).toHaveLength(1);
        expect(failuresOf('a.ts', refreshes, undefined)).toEqual([]);
    });

    it.each([
        ['with no refresh of the home screen', `function f() { qc.invalidateQueries({ queryKey: ['accounts', l] }); }`],
        ['whose home refresh is in another handler', `function f() { qc.invalidateQueries({ queryKey: ['accounts', l] }); }
            function g() { invalidateHomeReads(qc, l); }`],
        ['whose home refresh is deferred to a nested function', `function f() {
            qc.invalidateQueries({ queryKey: ['accounts', l] }); setTimeout(() => invalidateHomeReads(qc, l)); }`],
        ['nested in a callback of the handler that refreshes the home screen', `function f() {
            invalidateHomeReads(qc, l); p.then(() => qc.invalidateQueries({ queryKey: ['accounts', l] })); }`],
        ['whose home refresh is only named', `function f() { qc.invalidateQueries({ queryKey: ['accounts', l] }); const h = invalidateHomeReads; }`],
        ['outside any function', `qc.invalidateQueries({ queryKey: ['accounts', l] });`],
    ])('fails a refresh of the accounts %s', (_label, snippet) => {
        const refreshes = refreshesIn(snippet);
        expect(failuresOf('a.ts', refreshes, undefined)).toHaveLength(1);
    });

    it('counts a reviewed file as it was reviewed, and fails one more or one fewer', () => {
        const review: Review = { count: 1, reason: 'Reviewed to go without the home screen\'s reads, for the test.' };
        const one = `function f() { qc.invalidateQueries({ queryKey: ['accounts', l] }); }`;
        expect(failuresOf('a.ts', refreshesIn(one), review)).toEqual([]);
        expect(failuresOf('a.ts', refreshesIn(`${one}\n${one.replace('f(', 'g(')}`), review)).not.toEqual([]);
        expect(failuresOf('a.ts', refreshesIn(`function f() { invalidateHomeReads(qc, l); }`), review)).not.toEqual([]);
    });

    it('still sees the helper\'s own refresh of the accounts, and the registers\'', () => {
        const helper = allRefreshes().get(HELPER_FILE);
        expect(helper, HELPER_FILE).toBeDefined();
        // invalidateLedgerRegister refreshes the accounts, beside its call of invalidateHomeReads.
        expect(helper!.filter((r) => r.accounts && r.home)).toHaveLength(1);
        for (const page of [
            'routes/ledgers/register/bank/BankRegisterPage.tsx',
            'routes/ledgers/register/investment/InvestmentRegisterPage.tsx',
        ]) {
            expect(allRefreshes().get(page)?.some((r) => r.accounts), page).toBe(true);
        }
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('keeps every refresh of the accounts in the SPA beside a refresh of the home screen', () => {
        const failures: string[] = [];
        for (const [file, refreshes] of allRefreshes()) failures.push(...failuresOf(file, refreshes, REVIEWED.get(file)));
        expect(failures).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('keeps every entry honest', () => {
        // An entry that no longer describes anything reads as a considered
        // decision while covering nothing.
        for (const [file, { count, reason }] of REVIEWED) {
            expect(allRefreshes().has(file), `${file} is in REVIEWED and is not in the source`).toBe(true);
            expect(count, file).toBeGreaterThan(0);
            expect(reason.length, file).toBeGreaterThan(40);
        }
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});
