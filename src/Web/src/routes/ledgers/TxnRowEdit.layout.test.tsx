import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';
import * as apiModule from '@/lib/api';
import type { AccountSummary, MergeCandidateDto, SimilarPayeeDto } from '@/lib/types';

/**
 * Where the editor header puts its fields.
 *
 * The header is one CSS grid, and every cell names its own row with a
 * `row-start-N` class: 1 the fields, 2 the hints under Payee, 3 the notes'
 * captions, 4 their controls. jsdom does no layout, so these tests read the
 * row a cell is PLACED in, which is what decides where it renders.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BANK_ID = '00000000-0000-0000-0000-000000000100';
const CATEGORY_ID = '00000000-0000-0000-0000-000000000300';
const OTHER_CATEGORY_ID = '00000000-0000-0000-0000-000000000301';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';

const account = (over: Partial<AccountSummary>): AccountSummary => ({
    id: BANK_ID, ledgerId: LEDGER_ID, parentId: null, name: 'Checking', accountType: 'bank',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false, feedConnectionId: null,
    needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
    ...over,
});

const ACCOUNTS = [
    account({}),
    account({ id: CATEGORY_ID, name: 'Category A', accountType: 'category', categoryKind: 'expense' }),
    account({ id: OTHER_CATEGORY_ID, name: 'Category B', accountType: 'category', categoryKind: 'expense' }),
];

const ONE_SPLIT: Extract<TxnRowMode, { kind: 'edit' }> = {
    kind: 'edit', headerId: HEADER_ID, sourceAccountId: BANK_ID,
    postings: [
        { legId: 'leg-1', counterpartyAccountId: CATEGORY_ID, counterpartyAccountName: 'Category A', legTags: [], amount: -12, legMemo: null },
    ],
    payee: 'Payee One', memo: null, checkNumber: null,
    postedAt: '2026-01-02T00:00:00.000Z', transactedAt: null, balanceAfter: 100, balanceCurrencyCode: 'USD',
    tags: [], inherited: null, needsReview: false,
};

const SPLIT: Extract<TxnRowMode, { kind: 'edit' }> = {
    ...ONE_SPLIT,
    postings: [
        { legId: 'leg-1', counterpartyAccountId: CATEGORY_ID, counterpartyAccountName: 'Category A', legTags: ['Tag A'], amount: -8, legMemo: 'Line memo' },
        { legId: 'leg-2', counterpartyAccountId: OTHER_CATEGORY_ID, counterpartyAccountName: 'Category B', legTags: [], amount: -4, legMemo: null },
    ],
};

const SIMILAR: SimilarPayeeDto[] = [
    { payee: 'Payee Two', counterpartyAccountId: CATEGORY_ID, counterpartyAccountName: 'Category A', useCount: 2, lastUsedAt: '2026-01-01T00:00:00.000Z' },
];

const MERGE: MergeCandidateDto[] = [
    {
        headerId: '00000000-0000-0000-0000-000000000ccc', payee: 'Payee Three',
        postedAt: '2026-01-01T00:00:00.000Z',
        postings: [{ counterpartyAccountId: CATEGORY_ID, counterpartyAccountName: 'Category A' }],
        origin: 'manual',
    },
];

function renderEditor(mode: TxnRowMode) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <TxnRowEdit
                ledgerId={LEDGER_ID}
                mode={mode}
                payees={[]}
                accounts={ACCOUNTS}
                accountPaths={new Map(ACCOUNTS.map((a) => [a.id, a.name]))}
                currency="USD"
                cols="repeat(8, minmax(0, 1fr))"
                onCancel={() => {}}
                onSavePatch={() => {}}
                isSaving={false}
                saveError={null}
                cancelOnOutsideClick={false}
            />
        </QueryClientProvider>,
    );
}

/** The header grid's direct child that holds `el`, and the row it is placed in. */
function placement(el: HTMLElement): { grid: HTMLElement; cell: HTMLElement; row: number; col: number } {
    let cell: HTMLElement | null = el;
    while (cell !== null && !(cell.parentElement?.classList.contains('grid') ?? false)) {
        cell = cell.parentElement;
    }
    if (cell === null) throw new Error('not inside a grid');
    const read = (prefix: string) => {
        const cls = [...cell!.classList].find((c) => new RegExp(`^${prefix}-\\d+$`).test(c));
        if (cls === undefined) throw new Error(`cell has no ${prefix} class: ${cell!.className}`);
        return Number(cls.slice(prefix.length + 1));
    };
    return { grid: cell.parentElement!, cell, row: read('row-start'), col: read('col-start') };
}

const precedes = (a: Node, b: Node) =>
    (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchMergeCandidates').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({ accounts: [], categories: [] });
    vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([]);
});

describe('the split editor follows the one-split layout', () => {
    it('puts the whole-transaction Memo, then Tags, in one row under the fields', () => {
        renderEditor(SPLIT);

        const memoCaption = screen.getByText('Memo (whole transaction)');
        const tagsCaption = screen.getByText('Tags (whole transaction)');
        const memo = screen.getByLabelText('Memo (whole transaction)');
        const tags = screen.getByRole('button', { name: 'Edit Tags (whole transaction)' });
        const payee = placement(screen.getByLabelText('Payee'));

        // Memo before Tags, captions and controls alike.
        expect(precedes(memoCaption, tagsCaption)).toBe(true);
        expect(precedes(memo, tags)).toBe(true);

        // One row for the captions, one for the controls, both below the fields.
        const [mc, tc, m, t] = [memoCaption, tagsCaption, memo, tags].map(placement);
        for (const p of [mc, tc, m, t]) expect(p!.grid).toBe(payee.grid);
        expect(mc!.row).toBe(tc!.row);
        expect(m!.row).toBe(t!.row);
        expect(mc!.row).toBeGreaterThan(payee.row);
        expect(m!.row).toBeGreaterThan(mc!.row);

        // In the columns the one-split editor's Memo and Tags use: Payee's and
        // the category's, which is the one after it.
        expect(m!.col).toBe(payee.col);
        expect(t!.col).toBe(payee.col + 1);
    });

    it('leaves the first row\'s category slot empty', () => {
        renderEditor(SPLIT);

        const payee = placement(screen.getByLabelText('Payee'));
        const inSlot = [...payee.grid.children].filter((c) =>
            c.classList.contains(`row-start-${payee.row}`)
            && c.classList.contains(`col-start-${payee.col + 1}`));
        expect(inSlot).toEqual([]);
    });

    it('lays the one-split editor out the same way', () => {
        renderEditor(ONE_SPLIT);

        const memo = placement(screen.getByLabelText('Memo'));
        const tags = placement(screen.getByRole('button', { name: 'Edit Tags' }));
        const payee = placement(screen.getByLabelText('Payee'));
        expect(memo.row).toBe(tags.row);
        expect(memo.col).toBe(payee.col);
        expect(tags.col).toBe(payee.col + 1);
    });
});

describe('hints under Payee keep Memo and Tags in one row', () => {
    it('one-split: the Similar and Merge candidates hints get a row of their own', async () => {
        vi.mocked(apiModule.fetchSimilarPayees).mockResolvedValue(SIMILAR);
        vi.mocked(apiModule.fetchMergeCandidates).mockResolvedValue(MERGE);
        renderEditor(ONE_SPLIT);

        const similar = await screen.findByText('Similar:');
        const merge = await screen.findByText('Merge candidates:');
        const payee = placement(screen.getByLabelText('Payee'));
        const memoCaption = placement(screen.getByText('Memo', { selector: 'label' }));
        const tagsCaption = placement(screen.getByText('Tags', { selector: 'label' }));
        const memo = placement(screen.getByLabelText('Memo'));
        const tags = placement(screen.getByRole('button', { name: 'Edit Tags' }));

        expect(memoCaption.row).toBe(tagsCaption.row);
        expect(memo.row).toBe(tags.row);
        for (const hint of [placement(similar), placement(merge)]) {
            // Under Payee, in its column...
            expect(hint.col).toBe(payee.col);
            // ...in a row between the fields and the notes, so the notes start
            // below it in every column, not just in Payee's.
            expect(hint.row).toBeGreaterThan(payee.row);
            expect(hint.row).toBeLessThan(memoCaption.row);
            expect(hint.cell).not.toBe(memoCaption.cell);
            expect(hint.cell).not.toBe(payee.cell);
        }
    });

    it('split: the Merge candidates hint gets a row of its own', async () => {
        vi.mocked(apiModule.fetchMergeCandidates).mockResolvedValue(MERGE);
        renderEditor(SPLIT);

        const hint = placement(await screen.findByText('Merge candidates:'));
        const payee = placement(screen.getByLabelText('Payee'));
        const memoCaption = placement(screen.getByText('Memo (whole transaction)'));
        const tagsCaption = placement(screen.getByText('Tags (whole transaction)'));
        const memo = placement(screen.getByLabelText('Memo (whole transaction)'));
        const tags = placement(screen.getByRole('button', { name: 'Edit Tags (whole transaction)' }));

        expect(memoCaption.row).toBe(tagsCaption.row);
        expect(memo.row).toBe(tags.row);
        expect(hint.col).toBe(payee.col);
        expect(hint.row).toBeGreaterThan(payee.row);
        expect(hint.row).toBeLessThan(memoCaption.row);
    });
});

describe('the editors\' tag selects read "Tags (optional)" while empty', () => {
    it('one-split', () => {
        renderEditor(ONE_SPLIT);
        expect(screen.getByLabelText('Tags', { selector: 'span' })).toHaveTextContent(/^Tags \(optional\)$/);
    });

    it('split: the whole-transaction select and an empty line\'s', () => {
        renderEditor({ ...SPLIT, tags: [] });
        expect(screen.getByLabelText('Tags (whole transaction)', { selector: 'span' }))
            .toHaveTextContent(/^Tags \(optional\)$/);
        // Line 2 has no tags; line 1's own name shows instead of the placeholder.
        expect(screen.getByLabelText('Posting 2 tags', { selector: 'span' })).toHaveTextContent(/^Tags \(optional\)$/);
        expect(within(screen.getByLabelText('Posting 1 tags', { selector: 'span' })).getByText('Tag A')).toBeInTheDocument();
    });
});
