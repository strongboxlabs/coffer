import { describe, expect, it } from 'vitest';

import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import type { DisplayRow } from '@/lib/splitCollapse';
import type { BankRow, SelectionRequest, SelectionSummary } from '@/lib/types';

import { selectionFooterCounts } from './selectionFooterCounts';

const OWN = '00000000-0000-0000-0000-0000000000a1';
const READ_ONLY = '00000000-0000-0000-0000-0000000000a2';

function row(headerId: string, isReadOnly: boolean): DisplayRow<BankRow> {
    return { kind: 'txn', txn: { ...NO_NOTES, headerId, isReadOnly } as BankRow };
}

const BOTH: SelectionRequest = {
    kind: 'explicit',
    accountId: '00000000-0000-0000-0000-000000000100',
    headerIds: [OWN, READ_ONLY],
};

function summary(readOnlyCount: number): SelectionSummary {
    return { count: 2, totalCount: 2, readOnlyCount, sumOnAccount: -10, currencyCode: 'USD' };
}

describe('selectionFooterCounts', () => {
    it('names a selected read-only row the window no longer holds, once the summary arrives', () => {
        // The read-only row's page is not loaded: only the server can see it.
        const server = summary(1);
        const counts = selectionFooterCounts(BOTH, server, true, [row(OWN, false)]);
        expect(counts).toEqual({ selected: 2, readOnly: 1 });
        // The total is the ids held, and the server's count agrees with it.
        expect(server.totalCount).toBe(counts.selected);
    });

    it('counts from the loaded rows while the summary is in flight', () => {
        const counts = selectionFooterCounts(BOTH, null, false, [row(OWN, false), row(READ_ONLY, true)]);
        expect(counts).toEqual({ selected: 2, readOnly: 1 });
    });

    it('does not read the previous selection\'s summary while this one\'s is in flight', () => {
        // The summary on hand answered an earlier selection with no read-only row.
        const counts = selectionFooterCounts(BOTH, summary(0), false, [row(OWN, false), row(READ_ONLY, true)]);
        expect(counts).toEqual({ selected: 2, readOnly: 1 });
    });

    it('takes a select-all\'s counts from the server', () => {
        const all: SelectionRequest = {
            kind: 'all',
            accountId: BOTH.accountId,
            statusFilter: 'all',
            selectedAt: '2026-05-01T12:00:00Z',
            excludeIds: [],
        };
        const counts = selectionFooterCounts(
            all,
            { count: 12, totalCount: 15, readOnlyCount: 3, sumOnAccount: -120, currencyCode: 'USD' },
            true,
            [row(OWN, false)],
        );
        expect(counts).toEqual({ selected: 15, readOnly: 3 });
    });
});
