/**
 * header-notes-reader — a register row's `headerMemo` / `headerTags` may be
 * read only by the split-parent builder and the bank editor's draft.
 *
 * THE DEFECT IT PREVENTS. A row's `memo` / `tags` are what the server resolved
 * for that register; `headerMemo` / `headerTags` are the transaction's own,
 * present only where the transaction's values are in play (the account that
 * owns a split, a multi-posting event). A cell that reads the header directly
 * shows the transaction's memo where the rule says the line's belongs — on a
 * split's line seen from its other side, exactly what that row must never
 * show — and no test of the rule would notice, because the server sent the
 * right `memo`.
 *
 * WHAT IS ALLOWED. Reading the fields (member access, destructuring) in the
 * files named in `allow` — by default only the split-parent builder
 * (`lib/annotations/splitHeader.ts`). The editors are deliberately not exempt:
 * a seed that reads the header is the regression most likely to be written
 * there, so their own draft field has a different name. Declaring the fields (the type) and
 * writing them in object literals (fixtures) are not reads and are not flagged.
 * Test files are exempt: they assert on the wire shape.
 */

const FIELDS = new Set(['headerMemo', 'headerTags']);

const DEFAULT_ALLOW = [
    'src/lib/annotations/splitHeader.ts',
];

function normalise(path) {
    return path.replace(/\\/g, '/');
}

export default {
    meta: {
        type: 'problem',
        docs: {
            description:
                'allow reading a row\'s headerMemo / headerTags only in the split-parent builder',
        },
        schema: [
            {
                type: 'object',
                properties: { allow: { type: 'array', items: { type: 'string' } } },
                additionalProperties: false,
            },
        ],
        messages: {
            read:
                '"{{name}}" is the transaction\'s own value, in play only on a split\'s owning account. ' +
                'Read the row\'s resolved `memo` / `tags`, or go through lib/annotations/splitHeader.ts.',
        },
    },

    create(context) {
        const file = normalise(context.filename ?? context.getFilename());
        if (/\.test\.[jt]sx?$/.test(file)) return {};
        const allow = (context.options[0]?.allow ?? DEFAULT_ALLOW).map(normalise);
        if (allow.some((suffix) => file.endsWith(suffix))) return {};

        function report(node, name) {
            context.report({ node, messageId: 'read', data: { name } });
        }

        return {
            MemberExpression(node) {
                if (node.computed) {
                    if (node.property.type === 'Literal' && FIELDS.has(node.property.value)) {
                        report(node, node.property.value);
                    }
                    return;
                }
                if (node.property.type === 'Identifier' && FIELDS.has(node.property.name)) {
                    report(node, node.property.name);
                }
            },
            // `const { headerMemo } = row` — a read by destructuring.
            ObjectPattern(node) {
                for (const p of node.properties) {
                    if (p.type !== 'Property') continue;
                    const key = p.key.type === 'Identifier' ? p.key.name
                        : p.key.type === 'Literal' ? p.key.value : null;
                    if (key !== null && FIELDS.has(key)) report(p, key);
                }
            },
        };
    },
};
