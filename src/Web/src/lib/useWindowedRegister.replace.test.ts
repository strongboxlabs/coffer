import { describe, expect, it } from 'vitest';

import { replaceHeaderEntriesInWindow } from './useWindowedRegister';
import type { RegisterEntry, RegisterRow } from './types/register';

/**
 * A save answers with every entry the saved header has in the register's
 * account, and the window swaps exactly those in for that header's rows.
 * Several rows can share a header (each line of a split seen from its other
 * side is its own row), so the swap is many-to-many, keyed by row identity,
 * and every other row stays where it is.
 */

function line(headerId: string, id: string, memo: string | null = null): RegisterRow {
    return { kind: 'bank', id, headerId, memo } as unknown as RegisterRow;
}
const txn = (r: RegisterRow): RegisterEntry => ({ kind: 'txn', txn: r, groupId: null, legs: null });
const group = (headerId: string, legs: RegisterRow[]): RegisterEntry =>
    ({ kind: 'group', txn: null, groupId: headerId, legs });

const memos = (entries: readonly RegisterEntry[]) =>
    entries.map((e) => (e.kind === 'txn' ? `${e.txn.id}:${e.txn.memo ?? ''}` : `g:${e.groupId}`));

const page = (length: number) => ({ length, top: null, bottom: null });

describe('replaceHeaderEntriesInWindow', () => {
    it('updates every row of the header in place, leaving the rows around them', () => {
        const before = {
            entries: [
                txn(line('h0', 'a')),
                txn(line('h1', 'l1', 'old')),
                txn(line('h1', 'l2', 'old')),
                txn(line('h2', 'b')),
            ],
            pages: [page(4)],
        };
        const after = replaceHeaderEntriesInWindow(before, 'h1', [
            txn(line('h1', 'l1', 'new')),
            txn(line('h1', 'l2', 'new')),
        ]);
        expect(memos(after.entries)).toEqual(['a:', 'l1:new', 'l2:new', 'b:']);
        expect(after.pages).toEqual([page(4)]);
    });

    it('keeps each row in its own slot when the header\'s rows are not next to each other', () => {
        // A column sort can put other rows between two lines of one header.
        const before = {
            entries: [txn(line('h1', 'l1', 'old')), txn(line('h0', 'a')), txn(line('h1', 'l2', 'old'))],
            pages: [page(3)],
        };
        const after = replaceHeaderEntriesInWindow(before, 'h1', [
            txn(line('h1', 'l1', 'new')),
            txn(line('h1', 'l2', 'new')),
        ]);
        expect(memos(after.entries)).toEqual(['l1:new', 'a:', 'l2:new']);
    });

    it('drops a row the header no longer has and adds one it gained, after the row before it', () => {
        const before = {
            entries: [txn(line('h0', 'a')), txn(line('h1', 'l1')), txn(line('h1', 'l2')), txn(line('h2', 'b'))],
            pages: [page(2), page(2)],
        };
        const after = replaceHeaderEntriesInWindow(before, 'h1', [
            txn(line('h1', 'l1', 'kept')),
            txn(line('h1', 'l3', 'gained')),
        ]);
        expect(memos(after.entries)).toEqual(['a:', 'l1:kept', 'l3:gained', 'b:']);
        // l2 sat on the second page and left it; l3 joined the first.
        expect(after.pages.map((p) => p.length)).toEqual([3, 1]);
    });

    it('turns a header\'s single row into its split group in the same slot', () => {
        const before = {
            entries: [txn(line('h0', 'a')), txn(line('h1', 'l1')), txn(line('h2', 'b'))],
            pages: [page(3)],
        };
        const after = replaceHeaderEntriesInWindow(before, 'h1', [
            group('h1', [line('h1', 'l1'), line('h1', 'l2')]),
        ]);
        expect(memos(after.entries)).toEqual(['a:', 'g:h1', 'b:']);
        expect(after.pages).toEqual([page(3)]);
    });

    it('removes every row of the header when it has none left here', () => {
        const before = {
            entries: [txn(line('h1', 'l1')), txn(line('h0', 'a')), txn(line('h1', 'l2'))],
            pages: [page(3)],
        };
        const after = replaceHeaderEntriesInWindow(before, 'h1', []);
        expect(memos(after.entries)).toEqual(['a:']);
        expect(after.pages).toEqual([page(1)]);
    });

    it('leaves the window alone when the header is not in it', () => {
        const before = { entries: [txn(line('h0', 'a'))], pages: [page(1)] };
        expect(replaceHeaderEntriesInWindow(before, 'h9', [txn(line('h9', 'x'))])).toBe(before);
    });
});
