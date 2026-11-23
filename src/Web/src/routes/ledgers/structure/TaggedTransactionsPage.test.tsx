import { describe, it, expect, beforeEach, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import * as apiModule from '@/lib/api';
import { formatLedgerDate } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
import type { AccountSummary, TagDto, TagTransactionLine, TagTransactionPage } from '@/lib/types';

import { TaggedTransactionsPage } from './TaggedTransactionsPage';
import { LEDGER_ID, renderInRouter, seedRole, testQueryClient } from './structureTestFixtures';

// What one tag labels (ADR-0111 D6): the register lines it is in play for, across every account,
// narrowed by search, account and dates, a page at a time.

const TAG_ID = '00000000-0000-0000-0000-0000000000a1';
const WHERE = { path: '/ledgers/$ledgerId/tags/$tagId', location: `/ledgers/${LEDGER_ID}/tags/${TAG_ID}` };

function tag(over: Partial<TagDto> = {}): TagDto {
    return { id: TAG_ID, name: 'Trip', color: '#3366cc', usageCount: 3, transactionCount: 3, ...over };
}

function line(over: Partial<TagTransactionLine> = {}): TagTransactionLine {
    const headerId = over.headerId ?? 'h1';
    const accountId = over.accountId ?? 'a1';
    return {
        headerId, postedAt: '2026-03-05T00:00:00Z', payee: 'Corner Cafe', accountId,
        accountName: 'Checking', counterpartyAccountId: 'c1', counterpartyAccountName: 'Dining/Coffee',
        amount: -12.5, memo: null, status: 'cleared', transactionMemo: null, tags: ['Trip'],
        legId: `${headerId}-${accountId}-leg`, currencyCode: 'USD', ...over,
    };
}

function page(lines: TagTransactionLine[], over: Partial<TagTransactionPage> = {}): TagTransactionPage {
    return { lines, limit: 50, offset: 0, hasMore: false, ...over };
}

function account(id: string, name: string, accountType = 'bank'): AccountSummary {
    return {
        id, ledgerId: LEDGER_ID, parentId: null, name, accountType, categoryKind: null, currencyCode: 'USD',
        isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
        isTradeCommission: false, institutionName: null,
    } as AccountSummary;
}

const ACCOUNTS = [account('a1', 'Checking'), account('a2', 'Travel Card', 'credit_card'), account('c1', 'Dining', 'category')];

/** `tags: null` seeds nothing, so the page has to read the tags itself. */
function renderPage({ role = 'owner', tags = [tag()] as TagDto[] | null } = {}) {
    const queryClient = testQueryClient();
    seedRole(queryClient, role);
    if (tags) queryClient.setQueryData(['tags', LEDGER_ID], tags);
    queryClient.setQueryData(['accounts', LEDGER_ID, { includeInactive: false }], ACCOUNTS);
    renderInRouter(<TaggedTransactionsPage />, queryClient, WHERE);
    return { queryClient };
}

function spyOnLines() {
    return vi.spyOn(apiModule, 'fetchTagTransactions').mockResolvedValue(page([line()]));
}

let fetchLines: ReturnType<typeof spyOnLines>;

beforeEach(() => {
    vi.restoreAllMocks();
    fetchLines = spyOnLines();
});

describe('TaggedTransactionsPage', () => {
    it('names the tag, says how many transactions carry it, and lists a line for each', async () => {
        fetchLines.mockResolvedValue(page([
            line(),
            line({ headerId: 'h2', payee: 'Airline', accountId: 'a2', accountName: 'Travel Card',
                counterpartyAccountName: 'Travel/Air', amount: -300, tags: ['Trip', 'Work'] }),
        ]));
        renderPage();

        expect(await screen.findByRole('heading', { level: 1, name: 'Trip' })).toBeInTheDocument();
        expect(screen.getByText('3 transactions carry this tag.')).toBeInTheDocument();

        const cafe = (await screen.findByRole('link', { name: 'Corner Cafe' })).closest('tr')!;
        expect(within(cafe).getByText(formatLedgerDate('2026-03-05T00:00:00Z'))).toBeInTheDocument();
        expect(within(cafe).getByText('Checking')).toBeInTheDocument();
        expect(within(cafe).getByText('Dining/Coffee')).toBeInTheDocument();
        expect(within(cafe).getByText(formatCurrency(-12.5, 'USD'))).toBeInTheDocument();

        // "Other tags" leaves this tag out: the page is already about it.
        const air = screen.getByRole('link', { name: 'Airline' }).closest('tr')!;
        expect(within(air).getByText('Work')).toBeInTheDocument();
        expect(within(air).queryByText(/Trip/)).not.toBeInTheDocument();
    });

    it('prints each line in its own account\'s currency', async () => {
        fetchLines.mockResolvedValue(page([
            line({ payee: 'Corner Cafe', amount: -12.5, currencyCode: 'EUR' }),
            line({ headerId: 'h2', payee: 'Airline', accountId: 'a2', accountName: 'Travel Card',
                amount: -300, currencyCode: 'GBP' }),
        ]));
        renderPage();

        const cafe = (await screen.findByRole('link', { name: 'Corner Cafe' })).closest('tr')!;
        expect(within(cafe).getByText(formatCurrency(-12.5, 'EUR'))).toBeInTheDocument();
        const air = screen.getByRole('link', { name: 'Airline' }).closest('tr')!;
        expect(within(air).getByText(formatCurrency(-300, 'GBP'))).toBeInTheDocument();
        // Neither wears the default dollar sign.
        expect(screen.getByRole('table').textContent).not.toContain('$');
    });

    it('says "1 transaction carries" for one', async () => {
        renderPage({ tags: [tag({ transactionCount: 1 })] });
        expect(await screen.findByText('1 transaction carries this tag.')).toBeInTheDocument();
    });

    it('says that nothing carries a tag nothing is on, and how to put it on something', async () => {
        fetchLines.mockResolvedValue(page([]));
        renderPage({ tags: [tag({ transactionCount: 0, usageCount: 0 })] });
        expect(await screen.findByText('No transaction carries this tag yet.')).toBeInTheDocument();
        expect(await screen.findByText('Nothing is tagged with this yet')).toBeInTheDocument();
        expect(screen.getByText(/Add the tag to a transaction in its register/)).toBeInTheDocument();
    });

    it('opens a payee in its account’s register, scrolled to that transaction', async () => {
        renderPage();
        const link = await screen.findByRole('link', { name: 'Corner Cafe' });
        expect(link).toHaveAttribute('href', `/ledgers/${LEDGER_ID}/accounts/a1?focus=h1`);
        await userEvent.click(link);
        expect(await screen.findByText('register page')).toBeInTheDocument();
    });

    it('shows a dash for a line with no payee', async () => {
        fetchLines.mockResolvedValue(page([line({ payee: null })]));
        renderPage();
        expect(await screen.findByRole('link', { name: '—' })).toBeInTheDocument();
    });

    it('asks for the first page, fifty lines, with no filter', async () => {
        renderPage();
        await screen.findByRole('link', { name: 'Corner Cafe' });
        expect(fetchLines).toHaveBeenCalledWith(LEDGER_ID, TAG_ID, {}, { limit: 50, offset: 0 });
    });

    it('offers the real accounts to narrow to, not the categories, and asks for the one chosen', async () => {
        renderPage();
        await screen.findByRole('link', { name: 'Corner Cafe' });
        const select = screen.getByRole('combobox', { name: 'Account' });
        expect(within(select).getAllByRole('option').map((o) => o.textContent))
            .toEqual(['All accounts', 'Checking', 'Travel Card']);

        await userEvent.selectOptions(select, 'a2');
        await waitFor(() => expect(fetchLines).toHaveBeenLastCalledWith(
            LEDGER_ID, TAG_ID, { accountId: 'a2' }, { limit: 50, offset: 0 }));
    });

    it('waits for a pause in typing before it searches', async () => {
        renderPage();
        await screen.findByRole('link', { name: 'Corner Cafe' });
        fetchLines.mockClear();

        await userEvent.type(screen.getByRole('searchbox', { name: 'Search payee or memo' }), 'caf');
        // Three keystrokes, not three requests.
        expect(fetchLines).not.toHaveBeenCalled();
        await waitFor(() => expect(fetchLines).toHaveBeenCalledTimes(1));
        expect(fetchLines).toHaveBeenCalledWith(LEDGER_ID, TAG_ID, { search: 'caf' }, { limit: 50, offset: 0 });
    });

    it('passes the dates through as calendar days', async () => {
        renderPage();
        await screen.findByRole('link', { name: 'Corner Cafe' });

        fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-01-01' } });
        fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-03-31' } });

        await waitFor(() => expect(fetchLines).toHaveBeenLastCalledWith(
            LEDGER_ID, TAG_ID, { dateFrom: '2026-01-01', dateTo: '2026-03-31' }, { limit: 50, offset: 0 }));
        // Each date bounds the other, so the range cannot be drawn backwards.
        expect(screen.getByLabelText('From')).toHaveAttribute('max', '2026-03-31');
        expect(screen.getByLabelText('To')).toHaveAttribute('min', '2026-01-01');
    });

    it('says nothing matches when the filter leaves nothing, and clears the filter from there', async () => {
        fetchLines.mockImplementation((_l, _t, filter) =>
            Promise.resolve(filter.accountId ? page([]) : page([line()])));
        renderPage();
        await screen.findByRole('link', { name: 'Corner Cafe' });

        await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Account' }), 'a2');
        expect(await screen.findByText('No transaction matches')).toBeInTheDocument();

        await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
        expect(await screen.findByRole('link', { name: 'Corner Cafe' })).toBeInTheDocument();
        expect(screen.getByRole('combobox', { name: 'Account' })).toHaveValue('');
    });

    it('loads the next page on request and appends it to what is shown', async () => {
        fetchLines.mockImplementation((_l, _t, _f, p) => Promise.resolve(p.offset === 0
            ? page([line({ headerId: 'h1', payee: 'First' })], { hasMore: true })
            : page([line({ headerId: 'h2', payee: 'Second' })], { offset: 50 })));
        renderPage();

        expect(await screen.findByRole('link', { name: 'First' })).toBeInTheDocument();
        expect(screen.getByText('Showing 1 so far')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: 'Load more' }));

        expect(await screen.findByRole('link', { name: 'Second' })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'First' })).toBeInTheDocument();
        expect(fetchLines).toHaveBeenLastCalledWith(LEDGER_ID, TAG_ID, {}, { limit: 50, offset: 50 });
        // The last page has no "more" to offer.
        expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    });

    it('says when the tag is gone, and offers the way back to the list', async () => {
        renderPage({ tags: [tag({ id: 'someone-else', name: 'Other' })] });
        expect(await screen.findByText('That tag no longer exists')).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Back to Tags' })).toHaveAttribute('href', `/ledgers/${LEDGER_ID}/tags`);
        // Outside the Structure layout this page has no heading but its own, and a reader who
        // moves by headings finds the page named even when there is no tag to name.
        expect(screen.getByRole('heading', { level: 1, name: 'Tag not found' })).toBeInTheDocument();
        expect(screen.queryByRole('heading', { level: 1, name: 'Trip' })).not.toBeInTheDocument();
    });

    it('says the tag could not be read, instead of waiting for it for good, and tries again', async () => {
        const fetchTags = vi.spyOn(apiModule, 'fetchTags')
            .mockRejectedValueOnce(new Error('offline'))
            .mockResolvedValue([tag()]);
        renderPage({ tags: null });

        expect(await screen.findByRole('alert')).toHaveTextContent('offline');
        // Not "Loading…": nothing is on its way.
        expect(screen.queryByText('Loading…')).not.toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: 'Try again' }));

        expect(await screen.findByText('3 transactions carry this tag.')).toBeInTheDocument();
        expect(fetchTags).toHaveBeenCalledTimes(2);
    });

    it('stays to say what a merge did, because the tag it merged away is not there to go back to', async () => {
        const target = tag({ id: 'other-tag', name: 'Travel', transactionCount: 9, usageCount: 9 });
        const fetchTags = vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([target]);
        vi.spyOn(apiModule, 'patchTag').mockRejectedValue(
            new apiModule.ApiError(422, 'A tag with that name exists.', 'tag-name-exists'),
        );
        const mergeTag = vi.spyOn(apiModule, 'mergeTag')
            .mockResolvedValue({ transactionsRepointed: 3, savedReportsRepointed: 1 });
        renderPage({ tags: [tag(), target] });

        // Renaming into a name another tag has is how a merge is offered from this page.
        await userEvent.click(await screen.findByRole('button', { name: 'Edit tag…' }));
        const linesReadBeforeTheMerge = fetchLines.mock.calls.length;
        const edit = await screen.findByRole('dialog');
        const name = within(edit).getByLabelText(/name/i);
        await userEvent.clear(name);
        await userEvent.type(name, 'Travel');
        await userEvent.click(within(edit).getByRole('button', { name: /save/i }));
        await userEvent.click(await within(edit).findByRole('button', { name: /Merge “Trip” into “Travel” instead/ }));

        const merge = await screen.findByRole('dialog');
        await userEvent.click(within(merge).getByRole('button', { name: /^merge$/i }));

        await waitFor(() => expect(mergeTag).toHaveBeenCalledWith(LEDGER_ID, TAG_ID, { intoTagId: 'other-tag' }));
        // The page does not go to the list, which could not know a merge was made here.
        expect(await screen.findByRole('status')).toHaveTextContent('Merged “Trip” into “Travel”: 3 tag assignments moved');
        expect(await screen.findByText('That tag no longer exists')).toBeInTheDocument();
        expect(screen.queryByText('tags page')).not.toBeInTheDocument();
        expect(fetchTags).toHaveBeenCalled();
        // The merged-away tag's lines are not asked for again, which would only be answered with
        // an error that flashes before the page learns the tag is gone.
        expect(fetchLines).toHaveBeenCalledTimes(linesReadBeforeTheMerge);
        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('refreshes the home screen after the tag is renamed here, since its rows draw tags', async () => {
        const patch = vi.spyOn(apiModule, 'patchTag').mockResolvedValue(undefined);
        const { queryClient } = renderPage();
        const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
        const keys = () => invalidated.mock.calls.map((c) => c[0]?.queryKey);

        await userEvent.click(await screen.findByRole('button', { name: 'Edit tag…' }));
        const edit = await screen.findByRole('dialog');
        await userEvent.clear(within(edit).getByLabelText('Name'));
        await userEvent.type(within(edit).getByLabelText('Name'), 'Journey');
        // Anchor: nothing is refreshed before the save, so the check after it cannot pass early.
        expect(keys()).toEqual([]);
        await userEvent.click(within(edit).getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(patch).toHaveBeenCalledWith(LEDGER_ID, TAG_ID, { name: 'Journey' }));
        await waitFor(() => expect(keys()).toEqual(expect.arrayContaining([
            ['home', LEDGER_ID], ['overview', LEDGER_ID], ['budget-progress', LEDGER_ID],
        ])));
    });

    it('says the lines could not be read, and tries again', async () => {
        fetchLines.mockRejectedValueOnce(new Error('offline')).mockResolvedValue(page([line()]));
        renderPage();

        expect(await screen.findByRole('alert')).toHaveTextContent('offline');
        await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
        expect(await screen.findByRole('link', { name: 'Corner Cafe' })).toBeInTheDocument();
        expect(fetchLines).toHaveBeenCalledTimes(2);
    });

    it('draws a busy placeholder while the lines are on their way', async () => {
        fetchLines.mockReturnValue(new Promise(() => {}));
        renderPage();
        expect(await screen.findByLabelText('Loading transactions')).toHaveAttribute('aria-busy', 'true');
    });

    it('offers an owner the tag’s edit dialog', async () => {
        renderPage();
        await userEvent.click(await screen.findByRole('button', { name: 'Edit tag…' }));
        expect(await screen.findByRole('dialog')).toBeInTheDocument();
    });

    it('gives a viewer no way to change the tag', async () => {
        const { queryClient } = renderPage({ role: 'viewer' });
        expect(await screen.findByRole('link', { name: 'Corner Cafe' })).toBeInTheDocument();
        // The role arrives with the ledger list; settle that read before looking for an absence.
        await waitFor(() => expect(queryClient.getQueryState(['ledgers'])?.status).toBe('success'));
        expect(screen.queryByRole('button', { name: 'Edit tag…' })).not.toBeInTheDocument();
    });
});
