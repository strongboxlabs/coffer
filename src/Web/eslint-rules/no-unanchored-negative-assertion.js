/**
 * no-unanchored-negative-assertion — a test whose every assertion is satisfied
 * by an empty render cannot fail.
 *
 * THE DEFECT. `expect(screen.queryByRole('alert')).toBeNull()` is true of a
 * component that rendered an alert-free page, and equally true of one that
 * rendered NOTHING. On its own it cannot tell those apart, so it passes
 * whenever the code under test never actually ran — a broken import, a
 * provider that threw, a short-circuit on a loading state, an async render
 * nobody awaited.
 *
 * It is not theoretical. One shipped (#504): the only case in its block with
 * no `await`, so both `queryByText` calls ran on the pending frame where the
 * component short-circuits and the DOM is empty. Found by adversarial review
 * of a release diff, after release. Two more were found by the sweep that
 * produced this rule, and one of them — SplitsGrid "says nothing at all when
 * every posting is complete" — passed against a SplitsGrid mutated to
 * `return null`.
 *
 * WHAT COUNTS AS AN ANCHOR. Any assertion that a blank DOM would FAIL:
 * `getBy*`/`findBy*` (they throw when absent), `toBeInTheDocument`,
 * `toHaveTextContent`, a spy assertion, and so on. Position does not matter —
 * an anchor later in the block still proves the render happened, which is why
 * this looks at the whole block rather than only what precedes the negative.
 * A synchronous component needs no `await`, so awaiting is sufficient but not
 * necessary.
 *
 * WHY IT HAS NO LEGITIMATE EXCEPTION. A test whose intent really is "nothing
 * appears" still cannot distinguish that from a broken render, so it needs an
 * anchor too — on the container, a heading, anything outside the thing being
 * asserted absent. That is what makes this deterministic rather than a
 * heuristic, and worth a rule instead of a habit: the predicate is exact, so
 * there is nothing to suppress and no judgement to get wrong.
 *
 * Measured when written: 2 hits across 896 test blocks. The suite is clean, so
 * this exists to keep it that way — the three instances above were each found
 * by something other than the test suite, which is the one defect a test suite
 * is blind to by construction.
 */

const NEGATIVE_MATCHERS = new Set([
    'toBeNull',
    'toBeUndefined',
    'toBeFalsy',
    'toHaveLength',
]);

/** Matchers that a blank DOM fails — the presence of any one anchors the block. */
const ANCHOR_MATCHERS = new Set([
    'toBeInTheDocument',
    'toBeVisible',
    'toHaveTextContent',
    'toHaveValue',
    'toHaveAttribute',
    'toHaveClass',
    'toBeChecked',
    'toBeDisabled',
    'toBeEnabled',
    'toHaveFocus',
    'toHaveBeenCalled',
    'toHaveBeenCalledTimes',
    'toHaveBeenCalledWith',
    'toHaveBeenLastCalledWith',
    'toEqual',
    'toStrictEqual',
    'toBe',
    'toContain',
    'toMatch',
    'toMatchObject',
    'toBeTruthy',
    'toBeGreaterThan',
    'toThrow',
]);

const QUERY_RE = /^query(All)?By[A-Z]/;
const ANCHOR_QUERY_RE = /^(get|find)(All)?By[A-Z]/;

export default {
    meta: {
        type: 'problem',
        docs: {
            description:
                'require a test block with negative DOM assertions to contain at least ' +
                'one assertion an empty render would fail',
        },
        schema: [],
        messages: {
            unanchored:
                'Every assertion in "{{name}}" is satisfied by an empty render, so it ' +
                'passes even if the component rendered nothing. Add one assertion a ' +
                'blank DOM would fail — a getBy*/findBy* on something that must be ' +
                'there — so the negative means "absent" rather than "nothing ran".',
        },
    },

    create(context) {
        /** @type {{node: any, name: string, negatives: any[], anchored: boolean}[]} */
        const stack = [];

        function testName(node) {
            const arg = node.arguments && node.arguments[0];
            if (arg && arg.type === 'Literal' && typeof arg.value === 'string') return arg.value;
            if (arg && arg.type === 'TemplateLiteral' && arg.quasis.length)
                return arg.quasis[0].value.cooked;
            return '(unnamed)';
        }

        function isTestCall(node) {
            const c = node.callee;
            const base =
                c.type === 'Identifier' ? c.name
                : c.type === 'MemberExpression' && c.object.type === 'Identifier' ? c.object.name
                : null;
            // `it.each(...)` / `test.skip(...)` still declare a block.
            return base === 'it' || base === 'test';
        }

        /** The matcher name at the end of an `expect(...)...matcher()` chain. */
        function matcherOf(node) {
            let n = node.callee;
            if (n.type !== 'MemberExpression') return null;
            return n.property.type === 'Identifier' ? n.property.name : null;
        }

        /** Does this expect() chain contain `.not`? */
        function isNegated(node) {
            let n = node.callee;
            while (n && n.type === 'MemberExpression') {
                if (n.property.type === 'Identifier' && n.property.name === 'not') return true;
                n = n.object;
            }
            return false;
        }

        return {
            CallExpression(node) {
                if (isTestCall(node)) {
                    stack.push({ node, name: testName(node), negatives: [], anchored: false });
                    return;
                }
                const frame = stack[stack.length - 1];
                if (!frame) return;

                // A getBy*/findBy* anywhere anchors the block: it throws when absent.
                if (
                    node.callee.type === 'MemberExpression' &&
                    node.callee.property.type === 'Identifier' &&
                    ANCHOR_QUERY_RE.test(node.callee.property.name)
                ) {
                    frame.anchored = true;
                    return;
                }

                const matcher = matcherOf(node);
                if (!matcher) return;

                const negated = isNegated(node);
                // `.not.toBeInTheDocument()` is a negative; plain `toBeInTheDocument()`
                // is an anchor. Same matcher, opposite meaning — so read `.not` rather
                // than the matcher name alone.
                if (negated) {
                    if (ANCHOR_MATCHERS.has(matcher) || NEGATIVE_MATCHERS.has(matcher)) {
                        frame.negatives.push(node);
                    }
                    return;
                }
                if (NEGATIVE_MATCHERS.has(matcher)) {
                    // `toHaveLength(3)` is an anchor; `toHaveLength(0)` is not.
                    if (
                        matcher === 'toHaveLength' &&
                        !(node.arguments[0] && node.arguments[0].value === 0)
                    ) {
                        frame.anchored = true;
                        return;
                    }
                    frame.negatives.push(node);
                    return;
                }
                if (ANCHOR_MATCHERS.has(matcher)) frame.anchored = true;
            },

            'CallExpression:exit'(node) {
                const frame = stack[stack.length - 1];
                if (!frame || frame.node !== node) return;
                stack.pop();

                // Only DOM-query negatives are in scope. A block asserting
                // `expect(result).toBeNull()` on a plain value is a different thing
                // entirely and has no DOM to be empty.
                const domNegatives = frame.negatives.filter((n) => {
                    const src = context.sourceCode ?? context.getSourceCode();
                    return QUERY_RE.test(
                        (src.getText(n).match(/\b(query(?:All)?By[A-Z]\w*)/) || [])[1] || '',
                    );
                });

                if (domNegatives.length > 0 && !frame.anchored) {
                    context.report({
                        node: domNegatives[0],
                        messageId: 'unanchored',
                        data: { name: frame.name },
                    });
                }
            },
        };
    },
};
