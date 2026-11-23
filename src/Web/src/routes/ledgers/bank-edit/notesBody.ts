import type { TransactionLineNotesRequest, TransactionNotesRequest } from '@/lib/types';
import {
    TAG_LIMIT,
    joinCollapsedMemo,
    normalizeMemo,
    sameTagSet,
    tagCountAccepted,
    unionTags,
} from '@/lib/annotations/notes';

import type { PostingDraft } from './postingDraft';

/**
 * The bank editor's `notes` body: what changed, never what was merely shown.
 *
 * PRESENCE IS THE CONTRACT. The server reads an omitted key as "leave it",
 * and `null` / `[]` as "clear it", which on a one-split means "inherit the
 * other side's". So an edit sends a key only when the user changed that value
 * from what the editor opened with. That is what keeps an untouched save from
 * the inheriting side from writing anything, and from copying the inherited
 * value onto this side as if it were its own.
 *
 * A create has nothing stored, so it sends every value that is not blank.
 *
 * After a collapse the fields hold what the user saw before Save, while an
 * omitted key would leave the server's own join of the STORED values. The
 * two differ as soon as the user edits the joined value, even back to the old
 * transaction value, so a collapsed save always sends memo and tags.
 *
 * `memo` / `tags` are this side's on a one-posting result and the whole
 * transaction's on a multi-posting one; lines are sent only for the latter,
 * addressed by their position in the postings sent, and compared against the
 * values each line carries as `stored`.
 *
 * A reminder template edit is the exception. The server rebuilds a
 * template's lines. Each stored line goes out named by its source leg's id,
 * which is what carries its values, on both sides, to the rebuilt line; a
 * line without one is new and starts blank. On top of that, every line of
 * such an edit sends the memo and tags it holds in full, blank included, so
 * what the user saw is what is stored however the lines were moved. The
 * template read carries each line's own tags, so what a stored line holds is
 * known and the editor opens with it. A line whose prefill does not say what
 * it holds is the one exception: an empty set there means "not known", and
 * sending it would clear the stored set, so its tags are left out unless
 * picked and the stored set rides on the leg id. A transaction's lines are
 * matched by leg id too, so a line left out of its edit keeps its own values,
 * and a line just added has none.
 */

/** What the editor opened with: the values a save compares against. */
export interface NotesBaseline {
    memo: string;
    tags: readonly string[];
}

function changedMemo(current: string, baseline: string | undefined): { send: boolean; value: string | null } {
    const value = normalizeMemo(current);
    if (baseline === undefined) return { send: value !== null, value };
    return { send: value !== normalizeMemo(baseline), value };
}

function changedTags(current: readonly string[], baseline: readonly string[] | undefined): boolean {
    if (baseline === undefined) return current.length > 0;
    return !sameTagSet(current, baseline);
}

export function buildNotes(args: {
    side: string;
    isNew: boolean;
    memo: string;
    tags: readonly string[];
    postings: readonly Pick<PostingDraft, 'legId' | 'legMemo' | 'tags' | 'stored'>[];
    baseline: NotesBaseline;
    /** The editor collapsed the split: memo and tags are always sent. */
    collapsed?: boolean;
    /** The server rebuilds the lines, carrying a stored line's values to the
     *  rebuilt one by its leg id (a reminder template edit): every line also
     *  restates what it holds. */
    linesRebuilt?: boolean;
}): TransactionNotesRequest {
    const { side, isNew, memo, tags, postings, baseline, collapsed = false, linesRebuilt = false } = args;
    const notes: TransactionNotesRequest = { side };

    if (!isNew && collapsed) {
        notes.memo = normalizeMemo(memo);
        notes.tags = [...tags];
    } else {
        const m = changedMemo(memo, isNew ? undefined : baseline.memo);
        if (m.send) notes.memo = m.value;
        if (changedTags(tags, isNew ? undefined : baseline.tags)) notes.tags = [...tags];
    }

    if (postings.length > 1) {
        const lines: TransactionLineNotesRequest[] = [];
        postings.forEach((p, index) => {
            const base = isNew ? undefined : p.stored ?? undefined;
            const line: TransactionLineNotesRequest = { index };
            if (linesRebuilt && !isNew) {
                // Rebuilt: the leg id ties a stored line to its rebuilt one,
                // and the line restates what it holds on top. A line added in
                // this edit stores nothing, so its tags are known (none); a
                // stored line whose tags are not known leaves them out.
                line.memo = normalizeMemo(p.legMemo);
                if (base === undefined || base.tags !== null || p.tags.length > 0) line.tags = [...p.tags];
            } else {
                const lm = changedMemo(p.legMemo, base?.memo);
                if (lm.send) line.memo = lm.value;
                if (changedTags(p.tags, base?.tags ?? undefined)) line.tags = [...p.tags];
            }
            if (line.memo !== undefined || line.tags !== undefined) lines.push(line);
        });
        if (lines.length > 0) notes.lines = lines;
    }
    return notes;
}

/**
 * What the Memo and Tags fields hold once a split goes back to one line: the
 * transaction's memo joined in front of the remaining line's, and the two tag
 * sets combined. The user sees it before Save; the server computes the same
 * from the same shared vectors. Never capped: nothing is dropped.
 */
export function collapseNotes(
    header: { memo: string; tags: readonly string[] },
    remaining: Pick<PostingDraft, 'legMemo' | 'tags'>,
): { memo: string; tags: string[] } {
    return {
        memo: joinCollapsedMemo(header.memo, remaining.legMemo) ?? '',
        tags: unionTags(header.tags, remaining.tags),
    };
}

/**
 * Tag-limit messages, mirroring the server: only a set that GROWS past the
 * limit is refused. Line sets count only on a split; a one-posting result has
 * no line notes.
 */
export function notesIssues(args: {
    tags: readonly string[];
    tagBaseline: number;
    postings: readonly Pick<PostingDraft, 'tags' | 'tagBaseline'>[];
}): string[] {
    const issues: string[] = [];
    if (!tagCountAccepted(args.tagBaseline, args.tags.length)) {
        issues.push(`This tag set can't grow past ${TAG_LIMIT} tags.`);
    }
    if (args.postings.length > 1) {
        args.postings.forEach((p, i) => {
            if (!tagCountAccepted(p.tagBaseline, p.tags.length)) {
                issues.push(`Posting ${i + 1}: this tag set can't grow past ${TAG_LIMIT} tags.`);
            }
        });
    }
    return issues;
}
