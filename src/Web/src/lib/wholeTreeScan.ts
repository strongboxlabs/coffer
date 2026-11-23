/**
 * How long a test that reads the whole SPA's source may run.
 *
 * A guard test that reads or parses every source file under src takes seconds,
 * where Vitest's default timeout of five seconds is set for a component test
 * that takes milliseconds, and it takes longer still while other test files run
 * beside it. Every test that walks the whole source tree passes this as its
 * timeout, so they share one budget and it is set here only.
 */
export const WHOLE_TREE_SCAN_TIMEOUT_MS = 30_000;
