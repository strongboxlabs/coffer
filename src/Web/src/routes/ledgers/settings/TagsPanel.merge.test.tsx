import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { TagsPanel } from './TagsPanel';
import { mergeSummaryText } from './mergeSummary';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { renderInRouter, testQueryClient } from '../structure/structureTestFixtures';

/**
 * A tag merge also repoints the saved reports that filtered on the merged-away
 * tag. The confirmation reads the count from the response, which counts only
 * the reports the merging user can see. Mocked at `fetch`: the response field
 * is not built on the server yet.
 */

const LEDGER = 'ledger-1';

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    // The reader's role: the row menu is only drawn for someone who can edit.
    stub.on('GET', /\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger', role: 'owner' }]);
    stub.on('GET', /\/tags$/, [
        { id: 'tag-a', name: 'Holiday', color: null, usageCount: 4, transactionCount: 4 },
        { id: 'tag-b', name: 'Travel', color: null, usageCount: 9, transactionCount: 9 },
    ]);
});
afterEach(() => {
    vi.unstubAllGlobals();
});

async function mergeHolidayIntoTravel() {
    const qc = testQueryClient();
    renderInRouter(<TagsPanel ledgerId={LEDGER} />, qc);
    const row = (await screen.findByText('Holiday')).closest('li')!;
    // The row only answers a right-click once the role has been read.
    await waitFor(() => expect(qc.getQueryState(['ledgers'])?.status).toBe('success'));
    fireEvent.contextMenu(row);
    await userEvent.click(await screen.findByRole('menuitem', { name: /merge/i }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.selectOptions(within(dialog).getByLabelText('Merge into'), 'tag-b');
    await userEvent.click(within(dialog).getByRole('button', { name: /^merge$/i }));
}

describe('the merge dialog', () => {
    it('says how many assignments it moves, the figure the confirmation afterwards repeats', async () => {
        // "Holiday" is on 4 pairings; a register might show it on fewer transactions. The merge
        // repoints every pairing, so the dialog must name those, not the narrower count.
        stub.on('GET', /\/tags$/, [
            { id: 'tag-a', name: 'Holiday', color: null, usageCount: 4, transactionCount: 2 },
            { id: 'tag-b', name: 'Travel', color: null, usageCount: 9, transactionCount: 9 },
        ]);
        const qc = testQueryClient();
        renderInRouter(<TagsPanel ledgerId={LEDGER} />, qc);
        const row = (await screen.findByText('Holiday')).closest('li')!;
        await waitFor(() => expect(qc.getQueryState(['ledgers'])?.status).toBe('success'));
        fireEvent.contextMenu(row);
        await userEvent.click(await screen.findByRole('menuitem', { name: /merge/i }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.selectOptions(within(dialog).getByLabelText('Merge into'), 'tag-b');

        expect(within(dialog).getByText('4 tag assignments')).toBeInTheDocument();
        expect(within(dialog).queryByText(/2 transactions/)).not.toBeInTheDocument();
    });
});

describe('the merge confirmation', () => {
    it('says how many saved reports now filter on the surviving tag', async () => {
        stub.on('POST', /\/tags\/tag-a\/merge$/, { transactionsRepointed: 4, savedReportsRepointed: 2 });
        await mergeHolidayIntoTravel();

        expect(await screen.findByRole('status')).toHaveTextContent(
            'Merged “Holiday” into “Travel”: 4 tag assignments moved; 2 saved reports now filter on “Travel”.',
        );
        expect(stub.callsTo('POST', /\/merge$/)[0]!.body).toEqual({ intoTagId: 'tag-b' });
    });

    it('leaves the reports out when none of the user\'s followed the merge', async () => {
        stub.on('POST', /\/tags\/tag-a\/merge$/, { transactionsRepointed: 1, savedReportsRepointed: 0 });
        await mergeHolidayIntoTravel();

        const status = await screen.findByRole('status');
        expect(status).toHaveTextContent('Merged “Holiday” into “Travel”: 1 tag assignment moved.');
        expect(status).not.toHaveTextContent('saved report');
    });

    it('waits for the response before confirming', async () => {
        // A fetch that holds the merge's response until released.
        let release: (v: unknown) => void = () => {};
        vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
            const path = new URL(String(input), 'http://localhost').pathname;
            if (path.endsWith('/api/ledgers')) {
                return new Response(JSON.stringify([{ id: LEDGER, name: 'Ledger', role: 'owner' }]), {
                    status: 200, headers: { 'Content-Type': 'application/json' },
                });
            }
            if (path.endsWith('/tags')) {
                return new Response(JSON.stringify([
                    { id: 'tag-a', name: 'Holiday', color: null, usageCount: 4, transactionCount: 4 },
                    { id: 'tag-b', name: 'Travel', color: null, usageCount: 9, transactionCount: 9 },
                ]), { status: 200, headers: { 'Content-Type': 'application/json' } });
            }
            await new Promise((r) => { release = r; });
            return new Response(JSON.stringify({ transactionsRepointed: 4, savedReportsRepointed: 1 }), {
                status: 200, headers: { 'Content-Type': 'application/json' },
            });
        }));
        await mergeHolidayIntoTravel();

        // The list is on screen and the merge is in flight: no confirmation yet.
        expect(screen.getByText('Holiday')).toBeInTheDocument();
        expect(screen.queryByRole('status')).not.toBeInTheDocument();
        release(undefined);
        expect(await screen.findByRole('status')).toHaveTextContent('1 saved report now filters on “Travel”');
    });
});

describe('mergeSummaryText', () => {
    it('agrees in number', () => {
        expect(mergeSummaryText({ from: 'a', into: 'b', assignmentsMoved: 1, savedReportsRepointed: 1 }))
            .toBe('Merged “a” into “b”: 1 tag assignment moved; 1 saved report now filters on “b”.');
    });
});
