import { describe, it } from 'vitest';
import { RuleTester } from 'eslint';
import tseslint from 'typescript-eslint';

import rule from './header-notes-reader.js';

// Pinned with valid and invalid cases, so the rule can be shown to fail on the
// reads it exists to stop, and to pass on the declarations and fixtures it must
// leave alone.

RuleTester.describe = describe;
RuleTester.it = it;

const tester = new RuleTester({
    languageOptions: { parser: tseslint.parser, ecmaVersion: 2022, sourceType: 'module' },
});

const CELL = 'D:/repo/src/Web/src/routes/ledgers/register/strategies/bankRowStrategy.tsx';

tester.run('header-notes-reader', rule, {
    valid: [
        // The row's resolved fields are what every cell reads.
        { code: 'const m = row.memo; const t = row.tags;', filename: CELL },
        // The type declares the fields; a fixture writes them.
        { code: 'interface R { headerMemo: string | null; headerTags: string[] | null }', filename: CELL },
        { code: 'const r = { headerMemo: null, headerTags: null };', filename: CELL },
        // Only the split-parent builder may read them.
        { code: 'const m = legs[0].headerMemo;', filename: 'D:/repo/src/Web/src/lib/annotations/splitHeader.ts' },
        // Tests assert on the wire shape.
        { code: 'expect(row.headerMemo).toBe(null);', filename: 'D:/repo/src/Web/src/x.test.tsx' },
    ],
    invalid: [
        { code: 'const m = row.headerMemo;', filename: CELL, errors: [{ messageId: 'read' }] },
        { code: 'const t = row?.headerTags ?? [];', filename: CELL, errors: [{ messageId: 'read' }] },
        { code: "const m = row['headerMemo'];", filename: CELL, errors: [{ messageId: 'read' }] },
        { code: 'const { headerMemo, memo } = row;', filename: CELL, errors: [{ messageId: 'read' }] },
        { code: 'function f({ headerTags }) { return headerTags; }', filename: CELL, errors: [{ messageId: 'read' }] },
        // The editors are not exempt: a seed read there is the likeliest regression.
        { code: 'const m = row.headerMemo;', filename: 'D:/repo/src/Web/src/routes/ledgers/TxnRowEdit.tsx', errors: [{ messageId: 'read' }] },
        { code: 'const { headerTags } = row;', filename: 'D:/repo/src/Web/src/routes/ledgers/bank-edit/hooks/useTxnRowDraft.ts', errors: [{ messageId: 'read' }] },
        // A Windows path is matched like a POSIX one.
        { code: 'const m = row.headerMemo;', filename: 'D:\\repo\\src\\Web\\src\\routes\\ledgers\\register\\bank\\BankRegisterPage.tsx', errors: [{ messageId: 'read' }] },
    ],
});
