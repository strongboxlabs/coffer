import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * One reading of a ledger role (ADR-0111 D3).
 *
 * THE RULE. What a role allows is decided by the pure predicates in
 * ledgerRole.ts (`canWriteLedger`, `isLedgerOwner`), and a screen asks
 * `useLedgerRole`, which answers with them. No other file compares a role with
 * one of its names, so a rule cannot be worked out by hand in one place and
 * differently in another: a panel that tested `role === 'owner'` itself could
 * offer an owner's control to someone the predicate refuses, or hide one it
 * allows.
 *
 * WHAT IT CHECKS. Every file of the SPA's source but tests and their fixtures is
 * parsed, so a comment or a label that names a role does not count. A use of a
 * role name (owner, editor, viewer) as a string fails outside ledgerRole.ts,
 * by file and line, where it decides something:
 *  - an equality comparison (`===`, `!==`, `==`, `!=`) with the name on either
 *    side, parentheses and type assertions looked through;
 *  - a `case` of a switch;
 *  - a list or set of names that something is looked up in (`[...].includes`,
 *    `.indexOf`, `.some`, `new Set([...])`), or an object keyed by a name that is
 *    indexed (`{ owner: … }[role]`).
 * A name as a value (a select's option, a state's starting value, a type's
 * member) decides nothing and passes.
 *
 * A detector gone blind fails too: the predicate file's own comparisons are
 * counted, and the detector is run over samples of every form above.
 * Not caught: a name assembled at run time, or held in a constant and compared
 * under another name.
 */

const ROLE_NAMES: ReadonlySet<string> = new Set(['owner', 'editor', 'viewer']);
const PREDICATE_FILE = 'lib/ledgerRole.ts';
/** canWriteLedger compares with owner and editor; isLedgerOwner with owner. */
const PREDICATE_COMPARISONS = 3;

const SRC = join(import.meta.dirname, '..');

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
        // Test code and its fixtures seed and assert roles; they offer no control.
        else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && !/Fixtures?\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

function rel(file: string): string {
    return file.slice(SRC.length + 1).replaceAll('\\', '/');
}

/** The expression with parentheses, type assertions and `satisfies` taken off. */
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

/** Whether the expression is a role name written as a string. */
function isRoleName(node: ts.Expression): boolean {
    const b = bare(node);
    return (ts.isStringLiteral(b) || ts.isNoSubstitutionTemplateLiteral(b)) && ROLE_NAMES.has(b.text);
}

function holdsRoleName(list: ts.Expression): boolean {
    const b = bare(list);
    return ts.isArrayLiteralExpression(b) && b.elements.some((e) => !ts.isSpreadElement(e) && isRoleName(e));
}

function keyedByRoleName(object: ts.Expression): boolean {
    const b = bare(object);
    return ts.isObjectLiteralExpression(b) && b.properties.some((p) => {
        const name = p.name;
        if (name === undefined) return false;
        if (ts.isIdentifier(name) || ts.isStringLiteral(name)) return ROLE_NAMES.has(name.text);
        return false;
    });
}

const EQUALITY: ReadonlySet<ts.SyntaxKind> = new Set([
    ts.SyntaxKind.EqualsEqualsEqualsToken,
    ts.SyntaxKind.ExclamationEqualsEqualsToken,
    ts.SyntaxKind.EqualsEqualsToken,
    ts.SyntaxKind.ExclamationEqualsToken,
]);

/** The lines of `text` where a role name decides something. */
function roleDecisions(name: string, text: string): number[] {
    const source = ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const lines: number[] = [];
    const lineOf = (node: ts.Node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    const visit = (node: ts.Node) => {
        if (ts.isBinaryExpression(node) && EQUALITY.has(node.operatorToken.kind)
            && (isRoleName(node.left) || isRoleName(node.right))) {
            lines.push(lineOf(node));
        } else if (ts.isCaseClause(node) && isRoleName(node.expression)) {
            lines.push(lineOf(node));
        } else if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)
            && holdsRoleName(node.expression.expression)) {
            lines.push(lineOf(node));
        } else if (ts.isNewExpression(node) && node.arguments?.some((a) => holdsRoleName(a))) {
            lines.push(lineOf(node));
        } else if (ts.isElementAccessExpression(node) && keyedByRoleName(node.expression)) {
            lines.push(lineOf(node));
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    return lines;
}

describe('a ledger role is read through ledgerRole.ts (ADR-0111 D3)', () => {
    const files = sourceFiles(SRC).map((file) => ({ name: rel(file), text: readFileSync(file, 'utf8') }));

    it('compares a role with a role name nowhere else', () => {
        const found = files
            .filter((f) => f.name !== PREDICATE_FILE)
            .flatMap((f) => roleDecisions(f.name, f.text).map((line) => `${f.name}:${line}`));
        expect(found, 'Ask useLedgerRole, or a predicate in ledgerRole.ts, instead').toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('still sees the predicate file\'s own comparisons', () => {
        const predicate = files.find((f) => f.name === PREDICATE_FILE);
        expect(predicate, `${PREDICATE_FILE} is where the rules live`).toBeDefined();
        expect(roleDecisions(predicate!.name, predicate!.text)).toHaveLength(PREDICATE_COMPARISONS);
    });

    it.each([
        ['an equality', "const ok = ledger?.role === 'owner';"],
        ['a reversed inequality', "if ('viewer' != role) go();"],
        ['a comparison through a type assertion', "const ok = role === ('editor' as string);"],
        ['a template literal', 'const ok = role !== `owner`;'],
        ['a switch case', "switch (role) { case 'editor': go(); }"],
        ['a list looked up in', "const ok = ['owner', 'editor'].includes(role);"],
        ['a set looked up in', "const ok = new Set(['owner']).has(role);"],
        ['an object keyed by a name', 'const ok = ({ owner: true, editor: true } as Record<string, boolean>)[role];'],
    ])('the detector sees %s', (_label, snippet) => {
        expect(roleDecisions('sample.ts', snippet)).toEqual([1]);
    });

    it.each([
        ['a select option', "const options = [{ value: 'owner', label: 'Owner' }];"],
        ['a starting value', "const [role, setRole] = useState('viewer');"],
        ['a type', "type Role = 'owner' | 'editor' | 'viewer';"],
        ['another word compared', "const ok = mode === 'edit';"],
        ['a predicate asked', 'const ok = isLedgerOwner(member.role);'],
    ])('the detector passes %s', (_label, snippet) => {
        expect(roleDecisions('sample.tsx', snippet)).toEqual([]);
    });
});
