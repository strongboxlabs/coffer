import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * One Escape rule, held where Escape is handled (ADR-0023 §F, §L).
 *
 * THE RULE. A list or popover that handles an Escape (closes itself on it)
 * marks it, preventDefault(), and stops it, stopPropagation(), in the handler
 * that closes it. Anything further out that acts on Escape (a dialog's close,
 * an editor's cancel, the register selection's clear, a menu) ignores an
 * Escape whose defaultPrevented is true. Each half covers what the other
 * cannot: a stop never reaches a listener that ran first or sits outside the
 * React tree's propagation, and a mark is no use to a handler that never reads
 * it. Break either and one Escape does two things: the list closes and the
 * edit is discarded behind it, or the dialog closes with it, or the register's
 * selection is cleared too.
 *
 * WHAT IT CHECKS. Every comparison against the key name Escape (or Esc) in the
 * SPA's source, found by parsing it, so a comment or a label that mentions the
 * key does not count. Every file holding one must be in ROLES with a reason; a
 * file that is not fails, so a new Escape handler is classified before it
 * ships. The code the check governs (the branch it opens, or the rest of the
 * block after an early return on any other key) must then hold what the role
 * needs, outside any nested function, because a deferred call comes too late.
 * A list's mark and stop are calls in that code on the same event whose key was
 * compared: a call on another object (the native event, whose stop does not
 * stop React's propagation) is not this Escape's. A consumer's look at the mark
 * counts only where it stops the action: on the same event whose key was
 * compared, the right way round, and before anything acts.
 * Anything else that could handle Escape fails as unrecognised rather than
 * passing unseen: the key name in any case outside such a comparison, Escape
 * named as a key of a map or a member, a regular expression that can match
 * it, and a key read by number (keyCode, which, charCode) in any form. Every
 * file is parsed, with no text filter in front.
 *
 * A role is given per file, and covers the number of Escape checks the entry
 * pins. A check added to a catalogued file changes that count and fails, so it
 * is classified before it ships rather than taking on the file's role unseen,
 * which for "other" would hold it to nothing. Not caught: one check removed
 * and another added in the same file, which leaves the count as it was.
 */

type Role = 'list' | 'consumer' | 'both' | 'other';

/**
 * Every file that handles Escape, with its role, how many Escape checks it
 * holds, and why. A list closes itself; a consumer acts on an Escape that
 * reaches it; both does both; other is a field ending its own edit, where no
 * handler is nearer, and its reason must say why nothing further out can act
 * on the key too.
 */
const ROLES: ReadonlyMap<string, { role: Role; checks: number; reason: string }> = new Map([
    ['components/ui/Typeahead.tsx', {
        role: 'list',
        checks: 1,
        reason: 'The payee and security suggestions: the first Escape closes the list and the edit stays open.',
    }],
    ['components/register/AccountCategoryPicker.tsx', {
        role: 'list',
        checks: 1,
        reason: 'The category picker: closes its list from the field or from its filter buttons.',
    }],
    ['components/tags/TagSelect.tsx', {
        role: 'list',
        checks: 1,
        reason: 'The editors\' tags list: closes and hands focus back to the button that opened it.',
    }],
    ['components/tags/TagCombobox.tsx', {
        role: 'list',
        checks: 1,
        reason: 'The register filter\'s tag autocomplete: closes its suggestions while they show.',
    }],
    ['components/ui/ContextMenu.tsx', {
        role: 'both',
        checks: 1,
        reason: 'A menu closes itself and gives focus back to its opener, and leaves an Escape something nearer used.',
    }],
    ['components/ui/popoverDismissal.ts', {
        role: 'both',
        checks: 1,
        reason: 'Every popover\'s Escape (the date jump, the register\'s Filter, Show and Sort, the report range): '
            + 'closes it, and leaves an Escape a list inside already used.',
    }],
    ['components/ui/Modal.tsx', {
        role: 'consumer',
        checks: 2,
        reason: 'A dialog\'s close, from its own panel and, for a stray Escape, from the document.',
    }],
    ['lib/useSelection.ts', {
        role: 'consumer',
        checks: 1,
        reason: 'The register selection\'s clear, which must never ride on an Escape the editor or a list used.',
    }],
    ['routes/ledgers/TxnRowEdit.tsx', {
        role: 'consumer',
        checks: 1,
        reason: 'The bank editor\'s cancel, around the payee, category and tags lists.',
    }],
    ['routes/ledgers/investment-edit/InvestmentTxnRowEdit.tsx', {
        role: 'consumer',
        checks: 1,
        reason: 'The investment editor\'s cancel, around the security and category lists.',
    }],
    ['routes/ledgers/home/HomePage.tsx', {
        role: 'consumer',
        checks: 1,
        reason: 'The home screen\'s edit-mode Escape, which ends the layout edit around the settings panels.',
    }],
    ['routes/ledgers/home/WidgetPanel.tsx', {
        role: 'consumer',
        checks: 1,
        reason: 'A widget settings panel\'s Cancel, around the pickers some settings use.',
    }],
    ['components/ui/InlineNameInput.tsx', {
        role: 'other',
        checks: 1,
        reason: 'The focused name field ends its own edit; it stops every key, so nothing further out hears one.',
    }],
    ['components/RenameConnectionForm.tsx', {
        role: 'other',
        checks: 1,
        reason: 'The focused name field cancels its own rename, or refuses and marks the key while the rename is in flight, '
            + 'in a list on a page where nothing further out acts on Escape.',
    }],
    ['routes/ledgers/budget/CategoryRows.tsx', {
        role: 'other',
        checks: 1,
        reason: 'The focused target field abandons its own draft and marks the key; the budget page acts on no Escape.',
    }],
]);

const SRC = join(import.meta.dirname, '..', '..');

/** A key name that means Escape, written in any case. */
const ESCAPE_NAME = /^esc(ape)?$/i;

/** The legacy number fields a key can be read by, none of which names it. */
const KEY_NUMBERS = new Set(['keyCode', 'which', 'charCode']);

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
        // Test code and its fixtures press keys; they handle none.
        else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && !/Fixtures?\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

function rel(file: string): string {
    return file.slice(SRC.length + 1).replaceAll('\\', '/');
}

/** One place the code tests for Escape, and what the code it governs does. */
interface EscapeCheck {
    file: string;
    line: number;
    /** Why the check could not be read, when it could not. */
    unrecognised?: string;
    marks: boolean;
    stops: boolean;
    readsMark: boolean;
}

const COMPARISONS = new Set([
    ts.SyntaxKind.EqualsEqualsEqualsToken,
    ts.SyntaxKind.EqualsEqualsToken,
    ts.SyntaxKind.ExclamationEqualsEqualsToken,
    ts.SyntaxKind.ExclamationEqualsToken,
]);

/** Whether `test` holds anywhere in `nodes`, not counting a nested function. */
function anyBelow(nodes: readonly ts.Node[], test: (node: ts.Node) => boolean): boolean {
    const walk = (node: ts.Node): boolean => {
        if (test(node)) return true;
        if (ts.isFunctionLike(node)) return false;
        return ts.forEachChild(node, walk) ?? false;
    };
    return nodes.some(walk);
}

/**
 * A call of `name` on `event` itself (`e.preventDefault()` for the `e` whose key
 * was compared). A call on another object, the native event among them, is not
 * this Escape's mark or stop; with no event read, nothing matches, so the check
 * fails closed.
 */
const callsOn = (name: string, event: string | undefined) => (node: ts.Node) =>
    event !== undefined
    && ts.isCallExpression(node)
    && ts.isPropertyAccessExpression(node.expression)
    && node.expression.name.text === name
    && node.expression.expression.getText() === event;

/** A `return;` on its own, or a block holding only that. */
function isBareReturn(statement: ts.Statement): boolean {
    if (ts.isReturnStatement(statement)) return statement.expression === undefined;
    return ts.isBlock(statement)
        && statement.statements.length === 1
        && isBareReturn(statement.statements[0]!);
}

/** `node` without the parentheses around it. */
function bare(node: ts.Expression): ts.Expression {
    let n = node;
    while (ts.isParenthesizedExpression(n)) n = n.expression;
    return n;
}

/** The operands of a chain of one operator (&& or ||), through parentheses. */
function chain(node: ts.Expression, operator: ts.SyntaxKind): ts.Expression[] {
    const n = bare(node);
    if (ts.isBinaryExpression(n) && n.operatorToken.kind === operator) {
        return [...chain(n.left, operator), ...chain(n.right, operator)];
    }
    return [n];
}

/** The event a key read belongs to: `e` for `e.key` or `e.code`. */
function eventOf(keyRead: ts.Expression): string | undefined {
    const n = bare(keyRead);
    if (ts.isPropertyAccessExpression(n) && (n.name.text === 'key' || n.name.text === 'code')) {
        return n.expression.getText();
    }
    return undefined;
}

/** Whether `node` is `event.defaultPrevented`, the mark on that same event. */
function isMarkOf(node: ts.Expression, event: string): boolean {
    const n = bare(node);
    return ts.isPropertyAccessExpression(n) && n.name.text === 'defaultPrevented' && n.expression.getText() === event;
}

/** Whether `node` is `!event.defaultPrevented`. */
function isUnmarkedOf(node: ts.Expression, event: string): boolean {
    const n = bare(node);
    return ts.isPrefixUnaryExpression(n) && n.operator === ts.SyntaxKind.ExclamationToken && isMarkOf(n.operand, event);
}

/**
 * Whether a region opens on `if (event.defaultPrevented [|| …]) return;`,
 * before anything acts. Only `event.stopPropagation()` may come first, since
 * a stop never acts on the Escape.
 */
function opensOnTheMark(region: readonly ts.Node[], event: string): boolean {
    const only = region.length === 1 ? region[0]! : undefined;
    const statements = only !== undefined && ts.isBlock(only) ? only.statements : region;
    for (const statement of statements) {
        const call = ts.isExpressionStatement(statement) ? statement.expression : undefined;
        if (call !== undefined && ts.isCallExpression(call) && ts.isPropertyAccessExpression(call.expression)
            && call.expression.name.text === 'stopPropagation' && call.expression.expression.getText() === event) {
            continue;
        }
        return ts.isIfStatement(statement)
            && statement.elseStatement === undefined
            && isBareReturn(statement.thenStatement)
            && chain(statement.expression, ts.SyntaxKind.BarBarToken).some((d) => isMarkOf(d, event));
    }
    return false;
}

/**
 * The code an Escape literal governs, and whether that code is gated on the
 * mark. The code: for `case 'Escape':` the clause; for an `if` that tests the
 * key equal to Escape, its branch; for one that tests it unequal and returns,
 * the rest of the block. Gated: the mark of the same event whose key was
 * compared, read so that a marked Escape does nothing. That is
 * `!e.defaultPrevented` among the conditions an equality needs, `e.defaultPrevented`
 * among the ways an inequality returns, or a governed region that opens on
 * `if (e.defaultPrevented) return;`. A read the wrong way round, on another
 * object, after the action or only logged gates nothing.
 */
function governed(literal: ts.Node): { region: ts.Node[]; gated: boolean; event: string | undefined } | string {
    const parent = literal.parent;
    if (ts.isCaseClause(parent) && parent.expression === literal) {
        const region = [...parent.statements];
        const event = eventOf(parent.parent.parent.expression);
        return { region, gated: event !== undefined && opensOnTheMark(region, event), event };
    }
    if (!ts.isBinaryExpression(parent) || !COMPARISONS.has(parent.operatorToken.kind)) {
        return 'not compared to the key; write `e.key === \'Escape\'` (or !==) as an if condition or a case';
    }
    const kind = parent.operatorToken.kind;
    let positive = kind === ts.SyntaxKind.EqualsEqualsEqualsToken || kind === ts.SyntaxKind.EqualsEqualsToken;
    const event = eventOf(parent.left === literal ? parent.right : parent.left);

    // Up through the condition: parentheses, && and ||, and a ! that flips it.
    let node: ts.Node = parent;
    for (;;) {
        const up: ts.Node = node.parent;
        if (ts.isParenthesizedExpression(up)) { node = up; continue; }
        if (ts.isBinaryExpression(up)
            && (up.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken
                || up.operatorToken.kind === ts.SyntaxKind.BarBarToken)) { node = up; continue; }
        if (ts.isPrefixUnaryExpression(up) && up.operator === ts.SyntaxKind.ExclamationToken) {
            positive = !positive;
            node = up;
            continue;
        }
        if (ts.isIfStatement(up) && up.expression === node) {
            // A mark that is one of the conditions the whole branch needs, or
            // one of the ways the whole early return is taken, gates it
            // however the key comparison sits beside it.
            if (positive) {
                const region = [up.thenStatement];
                const gated = event !== undefined && (
                    chain(up.expression, ts.SyntaxKind.AmpersandAmpersandToken).some((c) => isUnmarkedOf(c, event))
                    || opensOnTheMark(region, event));
                return { region, gated, event };
            }
            if (!isBareReturn(up.thenStatement)) {
                return 'an inequality on Escape must return on every other key, so the code after it is the Escape\'s';
            }
            const holder = up.parent;
            if (!ts.isBlock(holder) && !ts.isSourceFile(holder) && !ts.isCaseClause(holder)) {
                return 'the early return on any other key is not inside a block';
            }
            const region = holder.statements.slice(holder.statements.indexOf(up) + 1);
            const gated = event !== undefined && (
                chain(up.expression, ts.SyntaxKind.BarBarToken).some((d) => isMarkOf(d, event))
                || opensOnTheMark(region, event));
            return { region, gated, event };
        }
        return 'the Escape comparison is not the condition of an if or a case';
    }
}

/**
 * The name a node gives a property, a method, an enum member or an object
 * binding: the places a key map or a handler table names its keys.
 */
function declaredName(node: ts.Node): ts.Node | undefined {
    if (ts.isPropertyAssignment(node) || ts.isShorthandPropertyAssignment(node)
        || ts.isMethodDeclaration(node) || ts.isPropertySignature(node)
        || ts.isPropertyDeclaration(node) || ts.isEnumMember(node)) {
        return node.name;
    }
    if (ts.isBindingElement(node) && ts.isObjectBindingPattern(node.parent)) {
        return node.propertyName ?? node.name;
    }
    return undefined;
}

/** The text of an identifier or a string literal, or undefined for anything else. */
function nameText(node: ts.Node | undefined): string | undefined {
    if (node === undefined) return undefined;
    if (ts.isIdentifier(node) || ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
        return node.text;
    }
    return undefined;
}

/** The legacy number field `node` reads (`e.which`, `e['keyCode']`, `{ keyCode }`), if any. */
function keyNumberRead(node: ts.Node): string | undefined {
    let field: string | undefined;
    if (ts.isPropertyAccessExpression(node)) field = node.name.text;
    else if (ts.isElementAccessExpression(node)) field = nameText(node.argumentExpression);
    else if (ts.isBindingElement(node) && ts.isObjectBindingPattern(node.parent)) {
        field = nameText(node.propertyName ?? node.name);
    }
    return field !== undefined && KEY_NUMBERS.has(field) ? field : undefined;
}

/** Every Escape check in one source text, named `name` in what it reports. */
function checksOf(name: string, text: string): EscapeCheck[] {
    const source = ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const out: EscapeCheck[] = [];
    const at = (node: ts.Node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    const unreadable = (node: ts.Node, why: string): EscapeCheck =>
        ({ file: name, line: at(node), unrecognised: why, marks: false, stops: false, readsMark: false });
    const visit = (node: ts.Node) => {
        const field = keyNumberRead(node);
        if ((ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) && ESCAPE_NAME.test(node.text)) {
            const g = governed(node);
            out.push(typeof g === 'string' ? unreadable(node, g) : {
                file: name,
                line: at(node),
                marks: anyBelow(g.region, callsOn('preventDefault', g.event)),
                stops: anyBelow(g.region, callsOn('stopPropagation', g.event)),
                readsMark: g.gated,
            });
        } else if (ts.isIdentifier(node) && ESCAPE_NAME.test(node.text)
            && node.parent !== undefined && declaredName(node.parent) === node) {
            out.push(unreadable(node, 'Escape named as a key of a map or a member; compare `e.key` to \'Escape\''));
        } else if (field !== undefined) {
            out.push(unreadable(node, `${field} is not read here; compare \`e.key\` to 'Escape'`));
        } else if (ts.isRegularExpressionLiteral(node) && /\besc/i.test(node.text.replace(/\\[bB]/g, ' '))) {
            out.push(unreadable(node, 'a pattern that can match Escape; compare `e.key` to \'Escape\''));
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    return out;
}

let everyCheck: EscapeCheck[] | undefined;

/**
 * Every Escape check in the SPA's source. Every file is parsed: a cheaper
 * text filter in front of the parser is one more description of what an
 * Escape check looks like, and it could drift from the detector's.
 */
function allChecks(): EscapeCheck[] {
    everyCheck ??= sourceFiles(SRC).flatMap((file) => checksOf(rel(file), readFileSync(file, 'utf8')));
    return everyCheck;
}

/** What a role is missing at one check, or null when it holds. */
function shortfall(role: Role, check: EscapeCheck): string | null {
    const missing: string[] = [];
    if (role === 'list' || role === 'both') {
        if (!check.marks) missing.push('preventDefault() (the mark)');
        if (!check.stops) missing.push('stopPropagation() (the stop)');
    }
    if ((role === 'consumer' || role === 'both') && !check.readsMark) {
        missing.push('a defaultPrevented check (ignore an Escape something nearer used)');
    }
    return missing.length === 0 ? null : missing.join(', ');
}

/** The checks in an inline snippet, for testing the detector itself. */
const checksIn = (snippet: string) => checksOf('snippet.ts', snippet);

describe('the one Escape rule', () => {
    it('reads an Escape handler the way the rule means it', () => {
        // Guard the guard: each of these is a shape the detector must tell apart.
        const [list] = checksIn(`function f(e) { if (e.key === 'Escape' && open) { e.preventDefault(); e.stopPropagation(); close(); } }`);
        expect(shortfall('list', list!)).toBeNull();

        const [early] = checksIn(`function f(e) { if (e.key !== 'Escape' || !open) return; e.preventDefault(); e.stopPropagation(); }`);
        expect(shortfall('list', early!)).toBeNull();

        const [noStop] = checksIn(`function f(e) { if (e.key === 'Escape') { e.preventDefault(); close(); } }`);
        expect(shortfall('list', noStop!)).toMatch(/stopPropagation/);

        // A stop on another key's branch is not this Escape's.
        const [elsewhere] = checksIn(`function f(e) {
            if (e.key === 'Enter') { e.stopPropagation(); }
            if (e.key === 'Escape') { e.preventDefault(); close(); }
        }`);
        expect(shortfall('list', elsewhere!)).toMatch(/stopPropagation/);

        // A mark or a stop on another object is not this Escape's: the native
        // event's stop does not stop React's propagation to a parent's handler.
        const [nativeStop] = checksIn(`function f(e) { if (e.key === 'Escape') { e.preventDefault(); e.nativeEvent.stopPropagation(); close(); } }`);
        expect(shortfall('list', nativeStop!)).toMatch(/stopPropagation/);
        const [otherMark] = checksIn(`function f(e) { if (e.key === 'Escape') { other.preventDefault(); e.stopPropagation(); close(); } }`);
        expect(shortfall('list', otherMark!)).toMatch(/preventDefault/);
        // The event's name is whatever the handler calls it.
        const [renamed] = checksIn(`function f(event) { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); } }`);
        expect(shortfall('list', renamed!)).toBeNull();
        const [renamedCase] = checksIn(`function f(ev) { switch (ev.key) { case 'Escape': ev.preventDefault(); ev.stopPropagation(); close(); break; } }`);
        expect(shortfall('list', renamedCase!)).toBeNull();

        // A comment is not a call, and a deferred call comes too late.
        const [commented] = checksIn(`function f(e) { if (e.key === 'Escape') {
            // e.preventDefault(); e.stopPropagation();
            setTimeout(() => { e.preventDefault(); e.stopPropagation(); });
        } }`);
        expect(shortfall('list', commented!)).toMatch(/preventDefault.*stopPropagation/);

        const [consumer] = checksIn(`function f(e) { if (e.key === 'Escape' && !e.defaultPrevented) { e.preventDefault(); cancel(); } }`);
        expect(shortfall('consumer', consumer!)).toBeNull();

        const [laterGuard] = checksIn(`function f(e) { if (e.key !== 'Escape') return; if (e.defaultPrevented) return; clear(); }`);
        expect(shortfall('consumer', laterGuard!)).toBeNull();

        const [blind] = checksIn(`function f(e) { if (e.key === 'Escape') { e.preventDefault(); cancel(); } }`);
        expect(shortfall('consumer', blind!)).toMatch(/defaultPrevented/);

        const [cased] = checksIn(`function f(e) { switch (e.key) { case 'Escape': e.preventDefault(); e.stopPropagation(); break; } }`);
        expect(shortfall('list', cased!)).toBeNull();

        // Anything the detector cannot read fails rather than passing unseen.
        expect(checksIn(`const isEscape = (e) => e.key === 'Escape';`)[0]!.unrecognised).toBeDefined();
        expect(checksIn(`if (e.key !== 'Escape') { close(); }`)[0]!.unrecognised).toBeDefined();
        expect(checksIn(`const keys = ['Escape', 'Enter'];`)[0]!.unrecognised).toBeDefined();
        expect(checksIn(`if (e.keyCode === 27) close();`)[0]!.unrecognised).toBeDefined();
        // And a mention that is not code is no check at all.
        expect(checksIn(`// Escape closes it. const label = 'Cancel (Esc)';`)).toEqual([]);
    });

    // Each a way to handle Escape without the comparison the rule reads.
    it.each([
        ['a key map', `const keyMap = { Escape: () => close() }; function f(e) { keyMap[e.key]?.(); }`],
        ['a key map with a shorthand entry', `const handlers = { escape }; function f(e) { handlers[e.key.toLowerCase()]?.(); }`],
        ['a handler table of methods', `const keys = { Esc() { close(); } };`],
        ['an enum of keys', `enum Key { Escape = 'Escape' }`],
        ['a destructured key map entry', `const { Escape: onEscape } = handlers;`],
        ['a key-name helper', `useHotkeys('esc', close);`],
        ['e.which', `function f(e) { if (e.which === 27) close(); }`],
        ['e.charCode', `function f(e) { if (e.charCode === 27) close(); }`],
        ['a destructured keyCode', `function f({ keyCode }) { if (keyCode === 27) close(); }`],
        ['a renamed destructured which', `function f({ which: k }) { if (k === 27) close(); }`],
        ['keyCode read by element access', `function f(e) { if (e['keyCode'] === 27) close(); }`],
        ['a pattern', `function f(e) { if (/^Esc/.test(e.key)) close(); }`],
        ['a pattern after a word boundary', `function f(e) { if (/\\bescape/i.test(e.key)) close(); }`],
    ])('fails %s as unrecognised', (_shape, snippet) => {
        const checks = checksIn(snippet);
        expect(checks.length).toBeGreaterThan(0);
        expect(checks.every((c) => c.unrecognised !== undefined)).toBe(true);
    });

    // A consumer's read of the mark counts only where it stops the action.
    it.each([
        ['the wrong way round', `function f(e) { if (e.key === 'Escape' && e.defaultPrevented) { cancel(); } }`],
        ['the wrong way round, after an early return',
            `function f(e) { if (e.key !== 'Escape') return; if (!e.defaultPrevented) return; clear(); }`],
        ['on another object', `function f(e) { if (e.key === 'Escape' && !other.defaultPrevented) { cancel(); } }`],
        ['only logged', `function f(e) { if (e.key === 'Escape') { console.log(e.defaultPrevented); cancel(); } }`],
        ['after the action', `function f(e) { if (e.key === 'Escape') { cancel(); if (e.defaultPrevented) return; } }`],
        ['joined by ||', `function f(e) { if (e.key === 'Escape' || !e.defaultPrevented) { cancel(); } }`],
        ['under a case for another event', `function f(e) { switch (e.key) { case 'Escape': if (o.defaultPrevented) return; close(); } }`],
    ])('fails a consumer whose mark is read %s', (_shape, snippet) => {
        const [check] = checksIn(snippet);
        expect(check!.unrecognised).toBeUndefined();
        expect(shortfall('consumer', check!)).toMatch(/defaultPrevented/);
    });

    it.each([
        ['among several conditions', `function f(e) { if (e.key === 'Escape' && a && !b && !e.defaultPrevented) { end(); } }`],
        ['beside a nested comparison', `function f(e) { if ((e.key === 'Escape' || x) && !e.defaultPrevented) { end(); } }`],
        ['among the ways it returns', `function f(e) { if (e.key !== 'Escape' || e.defaultPrevented) return; close(); }`],
        ['after a stop', `function f(e) { if (e.key !== 'Escape') return; e.stopPropagation(); if (e.defaultPrevented || !d) return; close(); }`],
        ['first in the branch', `function f(e) { if (e.key === 'Escape') { if (e.defaultPrevented) return; close(); } }`],
        ['first in a case', `function f(e) { switch (e.key) { case 'Escape': if (e.defaultPrevented) return; close(); } }`],
        ['on a named event', `function f(event) { if (event.key === 'Escape' && !event.defaultPrevented) { cancel(); } }`],
    ])('reads a consumer whose mark is read %s', (_shape, snippet) => {
        const [check] = checksIn(snippet);
        expect(shortfall('consumer', check!)).toBeNull();
    });

    it('holds a compare written in another case to the role, as any other', () => {
        const [lowered] = checksIn(`function f(e) { if (e.key.toLowerCase() === 'escape') close(); }`);
        expect(lowered).toBeDefined();
        expect(lowered!.unrecognised).toBeUndefined();
        expect(shortfall('list', lowered!)).toMatch(/preventDefault.*stopPropagation/);
    });

    // Each looks like one of those shapes and handles no key.
    it.each([
        ['a sort direction pattern', `const desc = /(asc|desc)/i.test(order);`],
        ['CSS.escape', `const sel = '#' + CSS.escape(id);`],
        ['a parameter named which', `function copy(which) { return which; }`],
        ['a word that contains esc', `const descending = { description: 'x' };`],
    ])('reads %s as no Escape check', (_shape, snippet) => {
        expect(checksIn(snippet)).toEqual([]);
    });

    it('reads every Escape check in the source', () => {
        const unreadable = allChecks()
            .filter((c) => c.unrecognised !== undefined)
            .map((c) => `${c.file}:${c.line} — ${c.unrecognised}`);
        expect(unreadable).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('classifies every file that handles Escape', () => {
        const unlisted = [...new Set(allChecks().map((c) => c.file))]
            .filter((file) => !ROLES.has(file))
            .map((file) => `${file} — handles Escape but is not in ROLES. Classify it: a list marks and `
                + 'stops the Escape it closes on; a consumer ignores one already marked.');
        expect(unlisted).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('pins how many Escape checks each catalogued file holds', () => {
        const found = new Map<string, number>();
        for (const check of allChecks()) {
            if (check.unrecognised !== undefined) continue;
            found.set(check.file, (found.get(check.file) ?? 0) + 1);
        }
        const drifted: string[] = [];
        for (const [file, { role, checks }] of ROLES) {
            const count = found.get(file) ?? 0;
            if (count !== checks) {
                drifted.push(`${file} holds ${count} Escape checks, the catalog says ${checks}. A new check takes `
                    + `this file's role (${role}): confirm the role and its reason fit it, then update checks.`);
            }
        }
        expect(drifted).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('holds each file to its role', () => {
        const offenders: string[] = [];
        for (const check of allChecks()) {
            const entry = ROLES.get(check.file);
            if (entry === undefined || check.unrecognised !== undefined) continue;
            const missing = shortfall(entry.role, check);
            if (missing !== null) offenders.push(`${check.file}:${check.line} (${entry.role}) — missing ${missing}`);
        }
        expect(offenders).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('keeps every entry honest', () => {
        // An entry that no longer describes anything reads as a considered
        // decision while covering nothing; and it is how this test notices the
        // detector going blind, since every entry would go stale at once.
        const handled = new Set(allChecks().map((c) => c.file));
        const stale: string[] = [];
        for (const [file, { reason }] of ROLES) {
            if (!handled.has(file)) stale.push(`${file} — no longer handles Escape; drop it from ROLES.`);
            expect(reason.length, file).toBeGreaterThan(40);
        }
        expect(stale).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);
});
