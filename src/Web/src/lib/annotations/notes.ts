import type { NotesKind, RegisterRowNotes } from '@/lib/types';

/**
 * Memo and tags on the client: only what the shared vectors pin
 * (`annotation-vectors.json`, read by the .NET tests too) and what the server's
 * row fields already say.
 *
 * The server decides which value a row shows and whether it is inherited. The
 * client never re-derives that: it reads `memoInherited` / `tagsInherited` to
 * know a side's own value, and computes nothing else about inheritance. The
 * one computation the editor does itself is the collapse pre-join, so the user
 * sees the joined memo and the combined tags before Save; those functions are
 * held to the same vectors as the server's.
 */

/** The server's tag limit per set. Mirrors `MaxTagsPerHeader`. */
export const TAG_LIMIT = 20;

/** The separator a collapse puts between the transaction's memo and the line's. */
export const COLLAPSE_SEPARATOR = ' · ';

/**
 * Leading and trailing whitespace as .NET's `char.IsWhiteSpace` defines it,
 * which is what the server trims. Spelled out because `String.prototype.trim`
 * differs: it leaves U+0085 (next line) and strips U+FEFF, which .NET keeps.
 */
const DOTNET_WHITESPACE =
    '\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000';
const OUTER_WHITESPACE = new RegExp(`^[${DOTNET_WHITESPACE}]+|[${DOTNET_WHITESPACE}]+$`, 'g');

/** Trim a memo; blank after trimming means no memo. */
export function normalizeMemo(value: string | null | undefined): string | null {
    if (value === null || value === undefined) return null;
    const trimmed = value.replace(OUTER_WHITESPACE, '');
    return trimmed.length === 0 ? null : trimmed;
}

/**
 * The memo a one-line transaction keeps when a split collapses: the
 * transaction's memo, then the line's, joined when they differ.
 */
export function joinCollapsedMemo(
    header: string | null | undefined,
    line: string | null | undefined,
): string | null {
    const h = normalizeMemo(header);
    const l = normalizeMemo(line);
    if (h === null) return l;
    if (l === null || l === h) return h;
    return `${h}${COLLAPSE_SEPARATOR}${l}`;
}

/** First set then second, each name once where it first appears. Names are compared exactly. */
export function unionTags(
    first: readonly string[],
    second: readonly string[],
): string[] {
    const out: string[] = [];
    const seen = new Set<string>();
    for (const name of [...first, ...second]) {
        if (seen.has(name)) continue;
        seen.add(name);
        out.push(name);
    }
    return out;
}

/**
 * Whether a tag set may be saved. Only GROWING a set past the limit is
 * refused: a set already over it (a system merge made it) may be saved as it
 * is or smaller. `baseline` is the stored set's size, or the size a shape
 * transition produced.
 */
export function tagCountAccepted(
    baseline: number,
    saved: number,
    limit: 'enforce' | 'systemMerge' = 'enforce',
): boolean {
    if (limit === 'systemMerge') return true;
    return saved <= TAG_LIMIT || saved <= baseline;
}

/** The most tags a set may hold given its baseline. */
export function tagCountCeiling(baseline: number): number {
    return Math.max(TAG_LIMIT, baseline);
}

/** Same names, in any order. Names are compared exactly. */
export function sameTagSet(a: readonly string[], b: readonly string[]): boolean {
    if (a.length !== b.length) return false;
    const set = new Set(a);
    return b.every((name) => set.has(name));
}

/** This side's own memo: the shown one unless it is inherited. */
export function ownMemo(row: Pick<RegisterRowNotes, 'memo' | 'memoInherited'>): string | null {
    return row.memoInherited ? null : row.memo;
}

/** This side's own tags: the shown ones unless they are inherited. */
export function ownTags(
    row: Pick<RegisterRowNotes, 'tags' | 'tagsInherited'>,
): readonly string[] {
    return row.tagsInherited ? [] : row.tags;
}

/**
 * What a side would show with its own memo and tags blank, and the account
 * that value comes from: the editor's placeholder and its "from X" caption
 * while the side has nothing of its own, which is also what emptying a field
 * goes back to. The editors take it as `null` where nothing can be inherited
 * (a split, a create).
 */
export interface InheritedNotes {
    memo: string | null;
    tags: readonly string[];
    fromAccountId: string | null;
}

/** The read-only badge a row carries, or null when it is editable. */
export function readOnlyBadge(kind: NotesKind): 'investment' | 'split' | null {
    switch (kind) {
        case 'eventCounterpartOne':
        case 'eventOpposite':
            return 'investment';
        case 'splitOpposite':
            return 'split';
        case 'onePosting':
        case 'splitSide':
        case 'eventSide':
            return null;
    }
}
