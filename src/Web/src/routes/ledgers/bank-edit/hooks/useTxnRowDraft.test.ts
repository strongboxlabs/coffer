import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { useTxnRowDraft } from './useTxnRowDraft';
import type { TxnRowMode } from '../../TxnRowEdit';

/**
 * Unit tests for the draft state that previously lived inline in the shell.
 *
 * Every case here is one the component-level gate cannot reach or cannot see:
 * the reorder semantics (the editor's only path to them is a drag, which jsdom
 * does not produce), the stable mutator identity (invisible in the DOM, but it
 * decides whether the picker's scroll effect fights the user), and the
 * once-only seeding (visible only as lost focus, three renders later).
 */

const BANK = '00000000-0000-0000-0000-000000000100';
const CAT = '00000000-0000-0000-0000-000000000300';

function editMode(n: number): TxnRowMode {
    return {
        kind: 'edit',
        headerId: '00000000-0000-0000-0000-000000000aaa',
        sourceAccountId: BANK,
        postings: Array.from({ length: n }, (_, i) => ({
            legId: `leg-${i + 1}`,
            counterpartyAccountId: CAT,
            counterpartyAccountName: `Cat ${i + 1}`,
            amount: -(i + 1),
            legMemo: null,
        })),
        payee: 'Corner Shop',
        memo: null,
        checkNumber: null,
        postedAt: '2026-01-02T00:00:00.000Z',
        transactedAt: null,
        balanceAfter: 100,
        tags: [],
        needsReview: false,
    };
}

/** Amounts are the row identity in these tests — '-1.00', '-2.00', … */
const order = (api: { draft: { postings: readonly { amount: string }[] } }) =>
    api.draft.postings.map((p) => p.amount);

describe('useTxnRowDraft — seeding', () => {
    it('seeds once, so a re-render does not re-mint the React keys', () => {
        // The property that forces useState over useMemo. A recomputed initial
        // carries different keys from the live draft, which remounts every leg
        // row: focus lost, textarea heights reset, in-flight edit discarded.
        const { result, rerender } = renderHook(({ m }) => useTxnRowDraft(m), {
            initialProps: { m: editMode(3) },
        });
        const before = result.current.draft.postings.map((p) => p.key);
        rerender({ m: editMode(3) });
        expect(result.current.draft.postings.map((p) => p.key)).toEqual(before);
    });

    it('gives the mutators an identity that survives a state change', () => {
        // They are passed to the row components and into the picker's
        // eligibility chain. A new function per render re-runs a
        // scroll-highlight effect on every keystroke anywhere in the form.
        const { result } = renderHook(() => useTxnRowDraft(editMode(2)));
        const before = {
            patch: result.current.patchPosting,
            add: result.current.addPosting,
            remove: result.current.removePosting,
            reorder: result.current.reorderPostings,
            move: result.current.movePosting,
        };
        act(() => result.current.setPayee('changed'));
        expect(result.current.draft.payee).toBe('changed');
        expect(result.current.patchPosting).toBe(before.patch);
        expect(result.current.addPosting).toBe(before.add);
        expect(result.current.removePosting).toBe(before.remove);
        expect(result.current.reorderPostings).toBe(before.reorder);
        expect(result.current.movePosting).toBe(before.move);
    });
});

describe('useTxnRowDraft — addPosting', () => {
    it('returns the new key and arms the focus marker in one act', () => {
        // Both in ONE act() deliberately: that is the batching the ghost row
        // depends on. If the key had to come back from a later render, the new
        // row would mount once with autoFocusAmount=false and never focus.
        const { result } = renderHook(() => useTxnRowDraft(editMode(1)));
        let key = '';
        act(() => {
            key = result.current.addPosting();
        });
        expect(key).not.toBe('');
        expect(result.current.focusKey).toBe(key);
        expect(result.current.draft.postings.at(-1)?.key).toBe(key);
    });

    it('applies a prefill but forces legId null, so an add never PATCHes a leg', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(1)));
        act(() => {
            result.current.addPosting({ amount: '-12.50', legId: 'stolen-leg' });
        });
        const added = result.current.draft.postings.at(-1)!;
        expect(added.amount).toBe('-12.50');
        expect(added.legId).toBeNull();
    });
});

describe('useTxnRowDraft — removePosting', () => {
    it('drops the named posting', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(3)));
        const second = result.current.draft.postings[1]!.key;
        act(() => result.current.removePosting(second));
        expect(order(result.current)).toEqual(['-1.00', '-3.00']);
    });

    it('refuses to empty the transaction', () => {
        // A transaction with no postings is not representable — the single-row
        // branch renders postings[0] unconditionally.
        const { result } = renderHook(() => useTxnRowDraft(editMode(1)));
        const only = result.current.draft.postings[0]!.key;
        act(() => result.current.removePosting(only));
        expect(result.current.draft.postings).toHaveLength(1);
    });
});

describe('useTxnRowDraft — reorderPostings (the drop semantic)', () => {
    // Pinned because it LOOKS like an off-by-one and is not. The row is
    // spliced out before it is re-inserted, so the same drop expresses
    // "after you" when dragging down and "before you" when dragging up —
    // which is what a drag reads as. A well-meaning symmetric "fix" would
    // change where every downward drag lands.
    it('dropping DOWN lands after the target', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(4)));
        const [a, , c] = result.current.draft.postings;
        act(() => result.current.reorderPostings(a!.key, c!.key));
        expect(order(result.current)).toEqual(['-2.00', '-3.00', '-1.00', '-4.00']);
    });

    it('dropping UP lands before the target', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(4)));
        const [a, , , d] = result.current.draft.postings;
        act(() => result.current.reorderPostings(d!.key, a!.key));
        expect(order(result.current)).toEqual(['-4.00', '-1.00', '-2.00', '-3.00']);
    });

    it('ignores a drop on itself and an unknown key', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(3)));
        const a = result.current.draft.postings[0]!.key;
        act(() => result.current.reorderPostings(a, a));
        act(() => result.current.reorderPostings(a, 'no-such-key'));
        expect(order(result.current)).toEqual(['-1.00', '-2.00', '-3.00']);
    });
});

describe('useTxnRowDraft — movePosting (the keyboard path)', () => {
    it('moves exactly one slot in each direction', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(4)));
        const third = result.current.draft.postings[2]!.key;
        act(() => result.current.movePosting(third, -1));
        expect(order(result.current)).toEqual(['-1.00', '-3.00', '-2.00', '-4.00']);
        act(() => result.current.movePosting(third, 1));
        expect(order(result.current)).toEqual(['-1.00', '-2.00', '-3.00', '-4.00']);
    });

    it('clamps at both ends instead of wrapping', () => {
        // Wrapping would send the top row to the bottom on an Alt+Up that the
        // user expected to do nothing — a 25-split reorder you cannot undo by
        // pressing the opposite key, because the opposite key wraps too.
        const { result } = renderHook(() => useTxnRowDraft(editMode(3)));
        const first = result.current.draft.postings[0]!.key;
        const last = result.current.draft.postings[2]!.key;
        act(() => result.current.movePosting(first, -1));
        act(() => result.current.movePosting(last, 1));
        expect(order(result.current)).toEqual(['-1.00', '-2.00', '-3.00']);
    });

    it('preserves every key, so no row remounts on a move', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(3)));
        const before = new Set(result.current.draft.postings.map((p) => p.key));
        act(() => result.current.movePosting(result.current.draft.postings[0]!.key, 1));
        expect(new Set(result.current.draft.postings.map((p) => p.key))).toEqual(before);
    });
});

describe('useTxnRowDraft — patchPosting', () => {
    it('merges into one posting and leaves the others alone', () => {
        const { result } = renderHook(() => useTxnRowDraft(editMode(2)));
        const second = result.current.draft.postings[1]!;
        act(() => result.current.patchPosting(second.key, { legMemo: 'plumber' }));
        expect(result.current.draft.postings[1]).toMatchObject({
            legMemo: 'plumber',
            legId: 'leg-2',
            amount: '-2.00',
        });
        expect(result.current.draft.postings[0]?.legMemo).toBe('');
    });
});
