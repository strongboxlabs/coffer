import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

// Local rule: a test whose every assertion is satisfied by an empty render
// cannot fail. See eslint-rules/no-unanchored-negative-assertion.js for the
// three real instances that motivated it, one of which shipped.
import noUnanchoredNegativeAssertion from './eslint-rules/no-unanchored-negative-assertion.js';

export default tseslint.config(
  { ignores: ['dist', 'src/routeTree.gen.ts'] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
      local: { rules: { 'no-unanchored-negative-assertion': noUnanchoredNegativeAssertion } },
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // react-hooks 7's `recommended` preset bundles two React-Compiler *preview*
      // diagnostics that flag intentional, working patterns here rather than bugs:
      //   - set-state-in-effect: our async-query form seeding, reset-on-dep-change,
      //     and generation-counter effects (several documented / ADR-referenced).
      //   - immutability: a guarded full-page redirect (window.location) and a
      //     test harness that captures emitted values — neither is React state.
      // We keep the load-bearing rules (rules-of-hooks + exhaustive-deps) on. Adopt
      // these two deliberately later as a dedicated effect-hygiene pass if wanted.
      'react-hooks/set-state-in-effect': 'off',
      'react-hooks/immutability': 'off',
      'react-refresh/only-export-components': [
        'warn',
        { allowConstantExport: true },
      ],
      // ERROR, not warn: the predicate is exact — a block whose assertions are
      // all satisfied by an empty DOM genuinely cannot fail — so there is no
      // false positive to live with and nothing to triage.
      'local/no-unanchored-negative-assertion': 'error',
      // Honour the `_`-prefix convention for intentionally unused
      // identifiers — most commonly mock parameters whose types match a
      // production signature but whose values the test never reads.
      '@typescript-eslint/no-unused-vars': [
        'error',
        {
          argsIgnorePattern: '^_',
          varsIgnorePattern: '^_',
          caughtErrorsIgnorePattern: '^_',
        },
      ],
    },
  },
);
