import { describe, it, expect, beforeEach, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';

import { useWindowedRegister } from './useWindowedRegister';
import type { RegisterEntry, RegisterRow } from './types/register';
import * as apiModule from '@/lib/api';

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000100';
const ANCHOR = '00000000-0000-0000-0000-0000000000aa';

function row(headerId: string): RegisterRow {
    return {
        kind: 'bank',
        id: `leg-${headerId}`,
        accountId: ACCOUNT_ID,
        headerId,
        payee: 'Row',
        memo: null,
        amount: -1,
        postedAt: '2026-05-01T12:00:00Z',
        transactedAt: null,
        balanceAfter: 0,
        status: 'uncleared',
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        tags: [],
        isHidden: false,
        isPending: false,
        needsReview: false,
        origin: 'manual',
        checkNumber: null,
        hasOverrides: false,
        txnGroupId: null,
        legIndex: 0,
        isMergeWinner: false,
        isMergedInto: null,
    } as unknown as RegisterRow;
}

function entry(headerId: string): RegisterEntry {
    return { kind: 'txn', txn: row(headerId), groupId: null, legs: null };
}

/**
 * The hook must report an anchor only when the server actually placed it in the
 * window.
 *
 * `RegisterRepository.GetPageAsync` pins an anchor only if the row matches the
 * active filter, and otherwise silently returns the ordinary most-recent page —
 * `RegisterPage` has no field distinguishing the two. The hook used to treat
 * "an anchor was requested" as "the anchor is at index 0", which is a guarantee
 * the server never made.
 *
 * Tested at the hook rather than through a page because a page that resolves
 * focus by header id is immune to the bad value anyway: an id that is not in the
 * window simply is not found. That makes the page a poor witness for this
 * contract, and it is the contract that would mislead the NEXT consumer.
 */
describe('useWindowedRegister — anchor resolution', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    function renderWithPage(entries: RegisterEntry[]) {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries,
            cursorForOlder: null,
            cursorForNewer: null,
        });
        return renderHook(() =>
            useWindowedRegister({
                ledgerId: LEDGER_ID,
                accountId: ACCOUNT_ID,
                focusHeaderId: ANCHOR,
            }),
        );
    }

    it('reports the anchor when the server returned it', async () => {
        const { result } = renderWithPage([entry('other'), entry(ANCHOR)]);

        await waitFor(() => expect(result.current.initialLoaded).toBe(true));
        expect(result.current.focusAnchorHeaderId).toBe(ANCHOR);
    });

    it('reports NO anchor when the server declined to pin it', async () => {
        // A non-empty page that simply does not contain the requested row —
        // exactly what the server returns when the anchor fails the filter.
        const { result } = renderWithPage([entry('other'), entry('another')]);

        await waitFor(() => expect(result.current.initialLoaded).toBe(true));
        expect(result.current.focusAnchorHeaderId).toBeNull();
    });

    it('resolves an anchor that ARRIVES AFTER mount', async () => {
        // The client-side navigation path. On a full page load the anchor is
        // present in the very first render; arriving from another register it
        // appears only once the router has settled the new search. If the hook
        // only honoured an anchor it had at mount, "Show other side" would work
        // after a refresh and not on the navigation that produced it.
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entry('other'), entry(ANCHOR)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        const { result, rerender } = renderHook(
            ({ focus }: { focus?: string }) =>
                useWindowedRegister({
                    ledgerId: LEDGER_ID,
                    accountId: ACCOUNT_ID,
                    focusHeaderId: focus,
                }),
            { initialProps: { focus: undefined as string | undefined } },
        );

        await waitFor(() => expect(result.current.initialLoaded).toBe(true));
        expect(result.current.focusAnchorHeaderId).toBeNull();

        rerender({ focus: ANCHOR });

        await waitFor(() =>
            expect(result.current.focusAnchorHeaderId).toBe(ANCHOR));
    });

    it('reports NO anchor for an empty page', async () => {
        const { result } = renderWithPage([]);

        await waitFor(() => expect(result.current.initialLoaded).toBe(true));
        expect(result.current.focusAnchorHeaderId).toBeNull();
    });
});
