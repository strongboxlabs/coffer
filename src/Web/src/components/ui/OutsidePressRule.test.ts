import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { WHOLE_TREE_SCAN_TIMEOUT_MS } from '@/lib/wholeTreeScan';

/**
 * One way to tell a press inside from a press outside (ADR-0023 §L).
 *
 * THE RULE. Whether an event happened inside an element is read from the
 * event's path, through `happenedInside` in popoverDismissal.ts, never from
 * where the event's target is now: not `element.contains(event.target)`, not
 * `event.target.closest(...)`, not a walk up its parents. The path is fixed
 * when dispatch starts; the target's place in the page is not. A control can
 * remove itself as the press goes down (a pick in the category picker takes
 * its list away), and a listener further out that then asks where the target
 * is asks about a node no longer in the page: it reads the press as outside
 * and closes the popover the pick was made in.
 *
 * WHAT IT CHECKS. Every use, in the SPA's source, of a DOM member that relates
 * a node to the elements around it: the methods `contains`, `closest` and
 * `compareDocumentPosition`, and the links `parentNode` and `parentElement`.
 * Every file is parsed, the helper's own included, with no text filter in
 * front, and a use counts whatever it asks about.
 *  - A method is called where it is read (`a.contains(b)`, `a?.closest(s)`,
 *    `a['contains'](b)`). One named any other way fails outright, by file and
 *    line: bound, taken into a local or off a prototype, destructured, wrapped
 *    in parentheses or `!`. What it is later asked cannot be followed.
 *  - Each file's uses of each member are counted in REVIEWED, with why they do
 *    not ask whether an event happened inside, or why the event's target is
 *    still in the page when they ask. A count that differs fails, and so does
 *    a file that is not listed, so a new use fails until it is reviewed.
 *  - Of those uses, the number asked about an event's target is pinned too, so
 *    a reviewed use turned onto a target fails although the counts stay. A
 *    target is `target`, `relatedTarget` or `srcElement`, read through casts,
 *    parentheses and conditionals, or through a local or a field bound to such
 *    a read by any assignment, destructuring, parameter default or for-of.
 *  - Every file that asks through the helper is in ASKS with the number of
 *    times it does, so a check that stops asking fails, and so does a new one
 *    until it is listed.
 * `classList.contains` is not counted: it asks for a class name, not a node.
 *
 * A detector gone blind fails too: every catalogued count would drop to none.
 * Not caught: one use removed and another added in the same file, which
 * leaves its counts as they were; a member named by a computed key, or by a
 * string anywhere but an element access (`Reflect.get(node, 'contains')`);
 * a target handed to, or returned from, a function, for the pin on targets;
 * and a test of inside that names none of these members (a search of an
 * element's descendants for the target, say).
 */

/** The members that relate a node to the elements around it. */
const METHODS = ['contains', 'closest', 'compareDocumentPosition'] as const;
const LINKS = ['parentNode', 'parentElement'] as const;
type Member = (typeof METHODS)[number] | (typeof LINKS)[number];
const MEMBERS: readonly Member[] = [...METHODS, ...LINKS];

interface Review {
    /** How many times the file uses each member. */
    uses: Partial<Record<Member, number>>;
    /** How many of those uses are asked about an event's target. */
    onTarget?: number;
    reason: string;
}

/**
 * Every file that uses one of the members, how often, and why that is not a
 * test of whether an event happened inside. A check of where focus or a drag
 * went reads `relatedTarget`, which the event names directly and no removal
 * can move.
 */
const REVIEWED: ReadonlyMap<string, Review> = new Map<string, Review>([
    ['components/register/AccountCategoryPicker.tsx', {
        uses: { contains: 1 },
        onTarget: 1,
        reason: 'Focus moving to another field closes the list; relatedTarget is the element focus went to.',
    }],
    ['components/tags/TagSelect.tsx', {
        uses: { contains: 1 },
        onTarget: 1,
        reason: 'Focus moving to another field closes the tags list; relatedTarget is the element focus went to.',
    }],
    ['components/SidebarAccounts.tsx', {
        uses: { contains: 1 },
        onTarget: 1,
        reason: 'A drag that moves onto a child of the drop target has not left it; relatedTarget is where it went.',
    }],
    ['routes/ledgers/home/HomePage.tsx', {
        uses: { contains: 1 },
        onTarget: 1,
        reason: 'A drag that moves onto a child of a drop slot has not left it; relatedTarget is where it went.',
    }],
    ['components/ui/ContextMenu.tsx', {
        uses: { contains: 2 },
        reason: 'Where focus was as the row menu opens, and who holds it as the menu goes, asked of the focused '
            + 'element and the menu while it is mounted. No event is asked about.',
    }],
    ['components/ui/Modal.tsx', {
        uses: { contains: 2 },
        reason: 'Whether what has focus as a dialog opens lies inside it, where the content put it and where it '
            + 'stays, or outside it, to give focus back to on close: read in the open effect, and once more as '
            + 'the content finishes mounting, before a dialog closing in the same update moves focus. The '
            + 'focused element, not an event\'s target.',
    }],
    ['components/ui/rowRemovalFocus.ts', {
        uses: { contains: 1 },
        reason: 'Whether the focused element lies inside a part of the page, or a row\'s companion, that goes with '
            + 'controls of its own: read as its ref detaches, while it is still in the page, and once more in the '
            + 'layout effect after it has gone. The focused element, not an event\'s target.',
    }],
    ['routes/ledgers/register/shell/useRegisterFocusHome.ts', {
        uses: { contains: 1 },
        reason: 'Whether the focused element still lies inside the register\'s row list as an inline edit closes, '
            + 'so focus is followed until that control has left the page. The focused element, not an event\'s target.',
    }],
    ['routes/ledgers/home/WidgetHost.tsx', {
        uses: { closest: 4 },
        onTarget: 3,
        reason: 'While the layout is edited, a widget\'s controls are inert and its settings panel is not. One call '
            + 'asks of each control the widget holds; the others ask of a click\'s or a right-click\'s target in a '
            + 'capture handler, which runs before any handler inside the widget can take the target away.',
    }],
]);

/** Every file that asks the helper, how many times, and what it decides. */
const ASKS: ReadonlyMap<string, { calls: number; reason: string }> = new Map([
    ['components/register/AccountCategoryPicker.tsx', {
        calls: 2,
        reason: 'The category picker closes its list on a press outside, and on a scroll that did not come from inside.',
    }],
    ['components/tags/TagSelect.tsx', {
        calls: 2,
        reason: 'The editors\' tags list closes on a press outside, and on a scroll that did not come from inside.',
    }],
    ['components/ui/ContextMenu.tsx', {
        calls: 1,
        reason: 'A row\'s actions menu closes on a press outside it.',
    }],
    ['components/ui/Modal.tsx', {
        calls: 1,
        reason: 'A stray Escape that reaches the document closes the newest dialog only when it came from outside its panel.',
    }],
    ['components/ui/Typeahead.tsx', {
        calls: 1,
        reason: 'The payee and security suggestions close on a press outside the field.',
    }],
    ['routes/ledgers/TxnRowEdit.tsx', {
        calls: 1,
        reason: 'A press outside the bank editor cancels the edit, and a press on anything in it, its lists included, does not.',
    }],
    ['routes/ledgers/investment-edit/InvestmentTxnRowEdit.tsx', {
        calls: 1,
        reason: 'A key from a dialog the investment editor opened (Add security) is that dialog\'s, not the editor\'s.',
    }],
    ['routes/ledgers/reports/RangePicker.tsx', {
        calls: 1,
        reason: 'A report\'s range popover closes on a press outside it.',
    }],
    ['routes/ledgers/reports/BenchmarkPicker.tsx', {
        calls: 1,
        reason: 'The Returns report\'s Benchmark popover closes on a press outside it.',
    }],
    ['routes/ledgers/register/RegisterDateJumpPopover.tsx', {
        calls: 2,
        reason: 'The register\'s date jump closes on a press outside it, and its shortcut leaves a key pressed in a dialog, a menu or a list to that popup.',
    }],
    ['routes/ledgers/register/shell/RegisterFilterControls.tsx', {
        calls: 1,
        reason: 'The register\'s Filter popover closes on a press outside it, and a pick in the category picker inside it is not one.',
    }],
    ['routes/ledgers/register/shell/RegisterSortMenu.tsx', {
        calls: 1,
        reason: 'The register\'s Sort list closes on a press outside it.',
    }],
    ['routes/ledgers/register/shell/RegisterStatusMenu.tsx', {
        calls: 1,
        reason: 'The register\'s Show list closes on a press outside it.',
    }],
    ['routes/ledgers/register/shell/useRegisterKeyboardNav.ts', {
        calls: 1,
        reason: 'The register\'s shortcuts leave a key pressed in a dialog, a menu or a list to that popup.',
    }],
]);

const SRC = join(import.meta.dirname, '..', '..');

/** The fields an event names its targets by. */
const TARGET_FIELDS = new Set(['target', 'relatedTarget', 'srcElement']);

/** The helper's name, and the module it comes from. */
const HELPER = 'happenedInside';
const HELPER_MODULE = /(^|\/)popoverDismissal$/;

function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry === 'dist') continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
        // Test code and its fixtures dispatch presses; they decide none.
        else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && !/Fixtures?\.tsx?$/.test(entry)) {
            out.push(full);
        }
    }
    return out;
}

function rel(file: string): string {
    return file.slice(SRC.length + 1).replaceAll('\\', '/');
}

/** The text of a name, a string literal, or a computed key that is one; undefined for anything else. */
function nameText(node: ts.Node | undefined): string | undefined {
    if (node === undefined) return undefined;
    if (ts.isIdentifier(node) || ts.isPrivateIdentifier(node) || ts.isStringLiteral(node)
        || ts.isNoSubstitutionTemplateLiteral(node)) {
        return node.text;
    }
    if (ts.isComputedPropertyName(node)) return nameText(node.expression);
    return undefined;
}

/** A field read as a name: `downAt.current`. */
const fieldText = (node: ts.PropertyAccessExpression) => node.getText().replace(/\s+/g, '');

/** `e.target`, `e['target']`, and the same for the other target fields. */
function isTargetRead(node: ts.Node): boolean {
    if (ts.isPropertyAccessExpression(node)) return TARGET_FIELDS.has(node.name.text);
    if (ts.isElementAccessExpression(node)) {
        const field = nameText(node.argumentExpression);
        return field !== undefined && TARGET_FIELDS.has(field);
    }
    return false;
}

/**
 * Whether `expr` reads an event's target anywhere in it, directly or through a
 * name or a field in `aliases`. A nested function is not part of it, and
 * neither is the name a member access reads (`x.next` does not read a local
 * named next).
 */
function readsTarget(expr: ts.Node, aliases: ReadonlySet<string>): boolean {
    const walk = (node: ts.Node): boolean => {
        if (ts.isFunctionLike(node)) return false;
        if (isTargetRead(node)) return true;
        if (ts.isIdentifier(node)) return aliases.has(node.text);
        if (ts.isPropertyAccessExpression(node)) return aliases.has(fieldText(node)) || walk(node.expression);
        return ts.forEachChild(node, walk) ?? false;
    };
    return walk(expr);
}

const isAssignment = (kind: ts.SyntaxKind) =>
    kind >= ts.SyntaxKind.FirstAssignment && kind <= ts.SyntaxKind.LastAssignment;

/** Whether `node`, an object or array literal, is (part of) what a plain `=` or a for-of assigns to. */
function isAssignedTo(node: ts.Node): boolean {
    let at = node;
    for (let up = at.parent; up !== undefined; at = up, up = at.parent) {
        if (ts.isBinaryExpression(up)) return up.left === at && up.operatorToken.kind === ts.SyntaxKind.EqualsToken;
        if (ts.isForOfStatement(up) || ts.isForInStatement(up)) return up.initializer === at;
        if (!(ts.isParenthesizedExpression(up) || ts.isArrayLiteralExpression(up) || ts.isObjectLiteralExpression(up)
            || ts.isPropertyAssignment(up) || ts.isShorthandPropertyAssignment(up)
            || ts.isSpreadElement(up) || ts.isSpreadAssignment(up))) {
            return false;
        }
    }
    return false;
}

/**
 * Every name and field in the file bound to a read of an event's target: a
 * variable, parameter or for-of name given one, a name or field assigned one
 * by any assignment, and a destructured target field. A pattern given one
 * binds every name in it. By name rather than by scope, so a name bound to a
 * target anywhere in the file counts everywhere in it; that can only report
 * more, never less.
 */
function targetAliases(source: ts.SourceFile): Set<string> {
    const aliases = new Set<string>();
    for (let grew = true; grew;) {
        grew = false;
        const note = (name: string) => {
            if (!aliases.has(name)) { aliases.add(name); grew = true; }
        };
        const bind = (target: ts.Node): void => {
            if (ts.isIdentifier(target)) note(target.text);
            else if (ts.isPropertyAccessExpression(target)) note(fieldText(target));
            else if (ts.isObjectBindingPattern(target) || ts.isArrayBindingPattern(target)) {
                for (const element of target.elements) if (ts.isBindingElement(element)) bind(element.name);
            } else if (ts.isArrayLiteralExpression(target)) target.elements.forEach(bind);
            else if (ts.isObjectLiteralExpression(target)) {
                for (const property of target.properties) {
                    if (ts.isShorthandPropertyAssignment(property)) bind(property.name);
                    else if (ts.isPropertyAssignment(property)) bind(property.initializer);
                    else if (ts.isSpreadAssignment(property)) bind(property.expression);
                }
            } else if (ts.isParenthesizedExpression(target) || ts.isSpreadElement(target)) bind(target.expression);
            else if (ts.isBinaryExpression(target) && target.operatorToken.kind === ts.SyntaxKind.EqualsToken) {
                bind(target.left);
            } else if (ts.isVariableDeclarationList(target)) target.declarations.forEach((d) => bind(d.name));
        };
        const visit = (node: ts.Node) => {
            if ((ts.isVariableDeclaration(node) || ts.isParameter(node) || ts.isBindingElement(node))
                && node.initializer !== undefined && readsTarget(node.initializer, aliases)) {
                bind(node.name);
            } else if (ts.isBinaryExpression(node) && isAssignment(node.operatorToken.kind)
                && readsTarget(node.right, aliases)) {
                bind(node.left);
            } else if ((ts.isForOfStatement(node) || ts.isForInStatement(node)) && readsTarget(node.expression, aliases)) {
                bind(node.initializer);
            }
            // A target field taken by name: `const { target: t } = e`, `({ target } = e)`.
            if (ts.isBindingElement(node) && ts.isObjectBindingPattern(node.parent)) {
                const field = nameText(node.propertyName ?? node.name);
                if (field !== undefined && TARGET_FIELDS.has(field)) bind(node.name);
            } else if ((ts.isPropertyAssignment(node) || ts.isShorthandPropertyAssignment(node))
                && isAssignedTo(node.parent)) {
                const field = nameText(node.name);
                if (field !== undefined && TARGET_FIELDS.has(field)) {
                    bind(ts.isPropertyAssignment(node) ? node.initializer : node.name);
                }
            }
            ts.forEachChild(node, visit);
        };
        visit(source);
    }
    return aliases;
}

/** The names the helper goes by in a file: its own, or the one an import gives it. */
function helperNames(source: ts.SourceFile): Set<string> {
    const names = new Set([HELPER]);
    for (const statement of source.statements) {
        if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) continue;
        if (!HELPER_MODULE.test(statement.moduleSpecifier.text)) continue;
        const bindings = statement.importClause?.namedBindings;
        if (bindings === undefined || !ts.isNamedImports(bindings)) continue;
        for (const element of bindings.elements) {
            if ((element.propertyName ?? element.name).text === HELPER) names.add(element.name.text);
        }
    }
    return names;
}

interface Scan {
    /** Each use of each member, by line: a method called where it is read, a link read. */
    uses: Record<Member, number[]>;
    /** Each method named without being called where it is read. */
    loose: { member: Member; line: number }[];
    /** Each use asked about an event's target, by line. */
    onTarget: number[];
    /** How many times the file calls the helper. */
    asks: number;
}

const isMember = (name: string | undefined): name is Member => (MEMBERS as readonly string[]).includes(name ?? '');
const isMethod = (member: Member) => (METHODS as readonly string[]).includes(member);

/** What one source text holds, named `name`. */
function scanOf(name: string, text: string): Scan {
    const source = ts.createSourceFile(
        name, text, ts.ScriptTarget.Latest, true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const aliases = targetAliases(source);
    const helper = helperNames(source);
    const out: Scan = {
        uses: Object.fromEntries(MEMBERS.map((m) => [m, []])) as unknown as Record<Member, number[]>,
        loose: [],
        onTarget: [],
        asks: 0,
    };
    const lineOf = (node: ts.Node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    const visit = (node: ts.Node) => {
        // A member read: `a.contains`, `a['contains']`.
        if (ts.isPropertyAccessExpression(node) || ts.isElementAccessExpression(node)) {
            const member = ts.isPropertyAccessExpression(node) ? node.name.text : nameText(node.argumentExpression);
            const receiver = node.expression;
            const classList = ts.isPropertyAccessExpression(receiver) && receiver.name.text === 'classList';
            if (isMember(member) && !(member === 'contains' && classList)) {
                const call = ts.isCallExpression(node.parent) && node.parent.expression === node ? node.parent : undefined;
                if (isMethod(member) && call === undefined) {
                    out.loose.push({ member, line: lineOf(node) });
                } else {
                    out.uses[member].push(lineOf(node));
                    const asked = [receiver, ...(call?.arguments ?? [])];
                    if (asked.some((a) => readsTarget(a, aliases))) out.onTarget.push(lineOf(node));
                }
            }
        }
        // A member taken by name: `const { contains } = Node.prototype`, `({ contains } = x)`.
        if (ts.isBindingElement(node) && ts.isObjectBindingPattern(node.parent)) {
            const member = nameText(node.propertyName ?? node.name);
            if (isMember(member)) out.loose.push({ member, line: lineOf(node) });
        } else if ((ts.isPropertyAssignment(node) || ts.isShorthandPropertyAssignment(node)) && isAssignedTo(node.parent)) {
            const member = nameText(node.name);
            if (isMember(member)) out.loose.push({ member, line: lineOf(node) });
        }
        if (ts.isCallExpression(node)) {
            const callee = node.expression;
            if ((ts.isIdentifier(callee) && helper.has(callee.text))
                || (ts.isPropertyAccessExpression(callee) && callee.name.text === HELPER)) {
                out.asks += 1;
            }
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    return out;
}

/** What the rule fails a file for, given its review, or none when it is not listed. */
function failuresOf(file: string, scan: Scan, review: Review | undefined): string[] {
    const out: string[] = [];
    for (const { member, line } of scan.loose) {
        out.push(`${file}:${line} — ${member} is named without being called where it is read, so what it is `
            + 'asked cannot be followed. Call it where it is read, and review the call.');
    }
    for (const member of MEMBERS) {
        const lines = scan.uses[member];
        const listed = review?.uses[member] ?? 0;
        if (lines.length !== listed) {
            out.push(`${file} uses ${member} ${lines.length} times (lines ${lines.join(', ') || 'none'}), `
                + `REVIEWED says ${listed}. Whether an event happened inside is asked of happenedInside(event, `
                + 'element); any other use is listed with why it does not ask that, or why the target is still '
                + 'in the page when it does.');
        }
    }
    const listedOnTarget = review?.onTarget ?? 0;
    if (scan.onTarget.length !== listedOnTarget) {
        out.push(`${file} asks ${scan.onTarget.length} of them about an event's target (lines `
            + `${scan.onTarget.join(', ') || 'none'}), REVIEWED says ${listedOnTarget}. A control pressed can remove `
            + 'itself before this runs; ask happenedInside(event, element) instead.');
    }
    return out;
}

let everyFile: Map<string, Scan> | undefined;

/** Every source file's scan. Every file is parsed: a text filter in front could drift from the detector. */
function allScans(): Map<string, Scan> {
    everyFile ??= new Map(sourceFiles(SRC).map((file) => [rel(file), scanOf(rel(file), readFileSync(file, 'utf8'))]));
    return everyFile;
}

const scanIn = (snippet: string) => scanOf('snippet.ts', snippet);
const usesIn = (snippet: string) => MEMBERS.flatMap((m) => scanIn(snippet).uses[m]);

describe('one way to tell a press inside from a press outside', () => {
    // Guard the guard: each of these, in a file no review lists, fails.
    it.each([
        ['contains on a target', `function f(e) { if (!root.contains(e.target as Node)) close(); }`],
        ['contains on the focused element', `const inside = root.contains(document.activeElement);`],
        ['contains on the first entry of the path', `function f(e) { root.contains(e.composedPath()[0] as Node); }`],
        ['a target held in an array', `function f(e) { const [t] = [e.target]; root.contains(t as Node); }`],
        ['a target taken by a destructuring assignment', `function f(e) { let t; ({ target: t } = e); root.contains(t); }`],
        ['a target kept in a ref, then asked on a later event', `const onDown = (e: MouseEvent) => { downAt.current = e.target as Node; };
            const onClick = () => { if (!panel.contains(downAt.current)) close(); };`],
        ['a target as a parameter default', `function f(e, t = e.target) { root.contains(t); }`],
        ['closest on the target', `function f(e) { if (!(e.target as Element).closest('[data-popover]')) close(); }`],
        ['a walk up the target\'s parents', `function f(e) { let n = e.target as Node | null; while (n && n !== root) n = n.parentNode; }`],
        ['a walk up through parentElement', `function f(e) { let n = e.target as Element | null; while (n && n !== root) n = n.parentElement; }`],
        ['compareDocumentPosition', `function f(e) { return root.compareDocumentPosition(e.target as Node) & 16; }`],
        ['contains bound', `function f(e) { const c = root.contains.bind(root); c(e.target); }`],
        ['contains called through the prototype', `function f(e) { Node.prototype.contains.call(root, e.target); }`],
        ['contains taken off the prototype', `function f(e) { const has = Node.prototype.contains; has.call(root, e.target); }`],
        ['contains destructured', `function f(e) { const { contains } = Node.prototype; contains.call(root, e.target); }`],
        ['contains taken by a destructuring assignment', `let contains; ({ contains } = Node.prototype);`],
        ['contains under a non-null assertion', `function f(e) { ref.current?.contains!(e.target as Node); }`],
        ['contains in parentheses', `function f(e) { (root.contains)(e.target as Node); }`],
        ['contains read by element access', `function f(e) { root['contains'](e.target); }`],
        ['closest read by a computed key in a pattern', `const { ['closest']: near } = Element.prototype;`],
    ])('fails %s until it is reviewed', (_shape, snippet) => {
        expect(failuresOf('snippet.ts', scanIn(snippet), undefined)).not.toEqual([]);
    });

    it('fails a method named without a call even in a reviewed file', () => {
        const review: Review = { uses: { contains: 1 }, reason: 'A reviewed call, for the test.' };
        const failures = failuresOf('a.ts', scanIn(`const has = root.contains.bind(root); has(node);`), review);
        expect(failures.some((f) => f.includes('named without being called'))).toBe(true);
    });

    it('passes a file as it was reviewed, and fails it once a reviewed use is turned onto a target', () => {
        const review: Review = { uses: { contains: 1 }, reason: 'A focus check, for the test.' };
        expect(failuresOf('a.ts', scanIn(`const inside = root.contains(document.activeElement);`), review)).toEqual([]);
        expect(failuresOf('a.ts', scanIn(`function f(e) { return root.contains(e.target); }`), review)).not.toEqual([]);
    });

    // The pin on targets: each of these asks about an event's target.
    it.each([
        ['a cast target', `function f(e) { if (!root.contains(e.target as Node)) close(); }`],
        ['an optional call', `function f(e) { if (ref.current?.contains(e.target)) return; }`],
        ['a non-null target', `function f(e) { root.contains(e.target!); }`],
        ['a target in parentheses', `function f(e) { root.contains(((e.target as Node | null))); }`],
        ['the native event\'s target', `function f(e) { root.contains(e.nativeEvent.target as Node); }`],
        ['a related target', `function f(e) { root.contains(e.relatedTarget as Node | null); }`],
        ['srcElement', `function f(e) { root.contains(e.srcElement); }`],
        ['a target read by element access', `function f(e) { root.contains(e['target']); }`],
        ['contains read by element access', `function f(e) { root['contains'](e.target); }`],
        ['a target behind a conditional', `function f(e) { root.contains(e.target instanceof Node ? e.target : null); }`],
        ['a local holding the target', `function f(e) { const t = e.target; if (root.contains(t)) return; }`],
        ['a local holding a cast target', `function f(e) { const t = e.target as Node; root.contains(t); }`],
        ['a local assigned the target', `function f(e) { let t; t = e.target; root.contains(t); }`],
        ['a local assigned the target with ??=', `function f(e) { let t = null; t ??= e.target; root.contains(t); }`],
        ['a local assigned the target with ||=', `function f(e) { let t = null; t ||= e.target; root.contains(t); }`],
        ['a local of a local', `function f(e) { const t = e.target; const n = t as Node; root.contains(n); }`],
        ['a target held in an array', `function f(e) { const [t] = [e.target]; root.contains(t as Node); }`],
        ['a destructured target', `function f(e) { const { target } = e; root.contains(target as Node); }`],
        ['a renamed destructured target', `const f = ({ target: t }) => root.contains(t);`],
        ['a destructured related target', `function f({ relatedTarget }) { root.contains(relatedTarget); }`],
        ['a target taken by a destructuring assignment', `function f(e) { let t; ({ target: t } = e); root.contains(t); }`],
        ['the target field taken by a destructuring assignment', `function f(e) { let target; ({ target } = e); root.contains(target); }`],
        ['a target as a parameter default', `function f(e, t = e.target) { root.contains(t); }`],
        ['a target from a for-of', `function f(e) { for (const t of [e.target]) root.contains(t); }`],
        ['a target kept in a ref', `function f(e) { downAt.current = e.target; } function g() { root.contains(downAt.current); }`],
        ['a target kept on a field', `function f(e) { state.t = e.target; root.contains(state.t); }`],
        ['closest on the target', `function f(e) { (e.target as Element).closest('[data-popover]'); }`],
        ['closest on a local holding the target', `function f(e) { const t = e.target as HTMLElement; t.closest('a'); }`],
        ['the target\'s parent', `function f(e) { return (e.target as Node).parentNode; }`],
        ['compareDocumentPosition on the target', `function f(e) { root.compareDocumentPosition(e.target as Node); }`],
    ])('reads %s as asked about an event\'s target', (_shape, snippet) => {
        expect(scanIn(snippet).onTarget).toHaveLength(1);
    });

    // Each looks like one and asks nothing about an event's target.
    it.each([
        ['the helper', `function f(e) { if (!happenedInside(e, root)) close(); }`],
        ['the focused element', `const inside = root.contains(document.activeElement);`],
        ['a node held by no event', `function f(node) { return root.contains(node); }`],
        ['a member named like a local', `function f(e) { const next = e.relatedTarget; root.contains(x.next); }`],
        ['the element a handler is on', `function f(e) { return e.currentTarget.closest('[role="dialog"]'); }`],
        ['a comment', `// if (!root.contains(e.target as Node)) close();`],
        ['a string', `const text = 'root.contains(e.target)';`],
    ])('reads %s as asked about no event', (_shape, snippet) => {
        expect(scanIn(snippet).onTarget).toEqual([]);
    });

    it('does not count a class name asked of classList', () => {
        expect(usesIn(`const grid = el.classList.contains('grid');`)).toEqual([]);
        expect(usesIn(`const grid = el?.classList.contains('grid');`)).toEqual([]);
    });

    it('counts the helper under its own name and under the name an import gives it', () => {
        expect(scanOf('a.ts', `function f(e) { if (happenedInside(e, root)) return; }`).asks).toBe(1);
        expect(scanOf('a.ts', `function f(e) { if (happenedInside(e, '[role="dialog"]')) return; }`).asks).toBe(1);
        expect(scanOf('a.ts', `import { happenedInside as inside } from './popoverDismissal';
            function f(e) { if (inside(e, root)) return; }`).asks).toBe(1);
        expect(scanOf('a.ts', `function f(e) { if (inside(e, root)) return; }`).asks).toBe(0);
    });

    it('reviews every use in the SPA of a member that relates a node to the elements around it', () => {
        const failures: string[] = [];
        for (const [file, scan] of allScans()) failures.push(...failuresOf(file, scan, REVIEWED.get(file)));
        for (const file of REVIEWED.keys()) {
            if (!allScans().has(file)) failures.push(`${file} is in REVIEWED and is not in the source.`);
        }
        expect(failures).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('pins how many times each file asks the helper', () => {
        const drifted: string[] = [];
        for (const [file, { asks }] of allScans()) {
            const listed = ASKS.get(file)?.calls ?? 0;
            if (asks !== listed) {
                drifted.push(`${file} asks happenedInside ${asks} times, ASKS says ${listed}. A check that `
                    + 'stopped asking decides inside some other way; a new one is listed with what it decides.');
            }
        }
        for (const file of ASKS.keys()) {
            if (!allScans().has(file)) drifted.push(`${file} is in ASKS and is not in the source.`);
        }
        expect(drifted).toEqual([]);
    }, WHOLE_TREE_SCAN_TIMEOUT_MS);

    it('keeps every entry honest', () => {
        for (const [file, { uses, onTarget, reason }] of REVIEWED) {
            const total = MEMBERS.reduce((sum, m) => sum + (uses[m] ?? 0), 0);
            expect(total, file).toBeGreaterThan(0);
            expect(onTarget ?? 0, file).toBeLessThanOrEqual(total);
            expect(reason.length, file).toBeGreaterThan(40);
        }
        for (const [file, { calls, reason }] of ASKS) {
            expect(calls, file).toBeGreaterThan(0);
            expect(reason.length, file).toBeGreaterThan(40);
        }
    });
});
