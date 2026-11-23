import type { RegisterRowNotes } from '@/lib/types';

/**
 * The ONE reader of a row's `headerMemo` / `headerTags` (the lint rule
 * `local/header-notes-reader` holds every other file to that).
 *
 * The transaction's own memo and tags are in play only on the account that
 * owns a split: its collapsed row shows them, and its editor edits them as
 * "the whole transaction's". Every other row shows `memo` / `tags`, which the
 * server already resolved for that register. Keeping the header read here
 * means a register cell cannot show the transaction's memo where the rule
 * says the line's belongs, which is exactly what a line on its other side
 * must never do.
 */

/** The transaction's memo and tags, read off a split's lines (any line carries them). */
export function splitHeaderNotes(
    legs: readonly Pick<RegisterRowNotes, 'headerMemo' | 'headerTags'>[],
): { memo: string | null; tags: readonly string[] } {
    const first = legs[0];
    return { memo: first?.headerMemo ?? null, tags: first?.headerTags ?? [] };
}

/**
 * The collapsed row of a split on the account that owns it: the canonical line
 * with the transaction's memo and tags in the fields a row cell reads. They
 * are the transaction's own, so nothing on this row is inherited.
 */
export function splitParentNotes<R extends RegisterRowNotes>(
    canonical: R,
    legs: readonly Pick<RegisterRowNotes, 'headerMemo' | 'headerTags'>[],
): R {
    const header = splitHeaderNotes(legs);
    return {
        ...canonical,
        memo: header.memo,
        tags: [...header.tags],
        memoInherited: false,
        tagsInherited: false,
        inheritedMemo: null,
        inheritedTags: [],
        inheritFromAccountId: null,
    };
}
