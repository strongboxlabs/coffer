import type { RegisterRowNotes } from '@/lib/types';

/**
 * The memo-and-tags fields of a register row, as the server sends them for a
 * one-split row that owns nothing and inherits nothing. Tests and the style
 * guide's samples spread it into their row literals and override what the case
 * is about; the style guide is why it lives outside the test files.
 *
 * Kept beside the type it fills so a contract change breaks the fixture where
 * it is defined, not in every test that builds a row.
 */
export const NO_NOTES: RegisterRowNotes = {
    memo: null,
    tags: [],
    memoInherited: false,
    tagsInherited: false,
    inheritedMemo: null,
    inheritedTags: [],
    inheritFromAccountId: null,
    headerMemo: null,
    headerTags: null,
    notesKind: 'onePosting',
    isReadOnly: false,
};
