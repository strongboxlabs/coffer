import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { EditorDialog } from '@/components/ui/EditorDialog';
import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';
import * as apiModule from '@/lib/api';
import * as fitModule from '@/lib/fitTextareaHeight';
import type { AccountSummary, MergeCandidateDto, PatchTransactionRequest, SimilarPayeeDto } from '@/lib/types';

/**
 * The editor's dialog layout (Post or skip, the reminder series editor,
 * Review): five columns of its own where they fit, one field per row below the
 * dialog's `@3xl/dialog` width.
 *
 * jsdom evaluates no CSS, container queries included, so these tests read the
 * classes that place each cell: the wide row and column (`row-start-N`,
 * `col-start-N`) and the narrow overrides (`@max-3xl/dialog:row-start-N`,
 * `@max-3xl/dialog:col-span-full`). That the browser applies them at the
 * stated width is not something this file can show.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BANK_ID = '00000000-0000-0000-0000-000000000100';
const CATEGORY_ID = '00000000-0000-0000-0000-000000000300';
const OTHER_CATEGORY_ID = '00000000-0000-0000-0000-000000000301';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';

const NARROW = '@max-3xl/dialog:';

const account = (over: Partial<AccountSummary>): AccountSummary => ({
    id: BANK_ID, ledgerId: LEDGER_ID, parentId: null, name: 'Account A', accountType: 'bank',
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
        { legId: 'leg-1', counterpartyAccountId: CATEGORY_ID, counterpartyAccountName: 'Category A', legTags: [], amount: -8, legMemo: null },
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

function renderEditor(
    mode: TxnRowMode,
    { layout, onSavePatch = () => {} }: { layout: 'dialog' | 'register'; onSavePatch?: (b: PatchTransactionRequest) => void },
) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const common = {
        ledgerId: LEDGER_ID,
        mode,
        payees: [],
        accounts: ACCOUNTS,
        accountPaths: new Map(ACCOUNTS.map((a) => [a.id, a.name])),
        currency: 'USD',
        onCancel: () => {},
        onSavePatch,
        isSaving: false,
        saveError: null,
        cancelOnOutsideClick: false,
        footerLeading: <button type="button">Leading action</button>,
    };
    return render(
        <QueryClientProvider client={client}>
            {layout === 'dialog' ? (
                <EditorDialog ariaLabel="Editor" title="Editor" onClose={() => {}}>
                    <TxnRowEdit {...common} layout="dialog" />
                </EditorDialog>
            ) : (
                <TxnRowEdit {...common} cols="repeat(8, minmax(0, 1fr))" />
            )}
        </QueryClientProvider>,
    );
}

/** The header grid's direct child that holds `el`. */
function cellOf(el: HTMLElement): HTMLElement {
    let cell: HTMLElement | null = el;
    while (cell !== null && !(cell.parentElement?.classList.contains('grid') ?? false)) {
        cell = cell.parentElement;
    }
    if (cell === null) throw new Error('not inside a grid');
    return cell;
}

/** The number in the cell's `<prefix>-N` class, wide (no variant) or narrow. */
function placed(el: HTMLElement, prefix: 'row-start' | 'col-start', narrow = false): number {
    const cell = cellOf(el);
    const want = new RegExp(`^${narrow ? NARROW.replace(/[/:]/g, '\\$&') : ''}${prefix}-(\\d+)$`);
    for (const c of cell.classList) {
        const m = want.exec(c);
        if (m) return Number(m[1]);
    }
    throw new Error(`cell has no ${narrow ? NARROW : ''}${prefix} class: ${cell.className}`);
}

const narrowFull = (el: HTMLElement) => cellOf(el).classList.contains(`${NARROW}col-span-full`);

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue(SIMILAR);
    vi.spyOn(apiModule, 'fetchMergeCandidates').mockResolvedValue(MERGE);
    vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({ accounts: [], categories: [] });
    vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([]);
});

describe('the dialog that hosts the editor', () => {
    it('is the query container the dialog layout reads', () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });
        expect(screen.getByRole('dialog')).toHaveClass('@container/dialog');
    });
});

describe('the editor in a dialog, wide', () => {
    it('lays out Date, Check #, Payee, Category and Amount on its own five columns', () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });

        const fields = [
            screen.getByLabelText('Date'),
            screen.getByLabelText('Check number'),
            screen.getByLabelText('Payee'),
            screen.getByRole('combobox', { name: 'Category' }),
            screen.getByLabelText('Amount'),
        ];
        expect(fields.map((f) => placed(f, 'row-start'))).toEqual([1, 1, 1, 1, 1]);
        expect(fields.map((f) => placed(f, 'col-start'))).toEqual([1, 2, 3, 4, 5]);
        expect(cellOf(fields[0]!).parentElement)
            .toHaveClass('grid-cols-[7.5rem_4.75rem_minmax(0,1fr)_minmax(0,1fr)_7.5rem]');
    });

    it('puts Tax date, Memo and Tags beneath Date, Payee and Category', () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });

        const tax = screen.getByLabelText('Tax date');
        const memo = screen.getByLabelText('Memo');
        const tags = screen.getByRole('button', { name: 'Edit Tags' });
        expect([tax, memo, tags].map((f) => placed(f, 'row-start'))).toEqual([4, 4, 4]);
        expect([tax, memo, tags].map((f) => placed(f, 'col-start'))).toEqual([1, 3, 4]);
    });

    it('has none of the register\'s own tracks', async () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });
        // Settled first: the hints are the last thing to arrive.
        expect(await screen.findByText('Merge candidates:')).toBeInTheDocument();

        const editor = screen.getByRole('group', { name: 'Transaction' });
        expect(within(editor).getByLabelText('Payee')).toBeInTheDocument();
        expect(within(editor).queryByTitle('Pre-edit running balance')).toBeNull();
        expect(screen.queryByRole('row')).toBeNull();
    });

    it('puts the leading action and the Split link left, then Cancel and the primary', () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });

        const order = [
            screen.getByRole('button', { name: 'Leading action' }),
            screen.getByRole('button', { name: 'Split this transaction →' }),
            screen.getByRole('button', { name: 'Cancel' }),
            screen.getByRole('button', { name: 'Save' }),
        ];
        for (let i = 1; i < order.length; i += 1) {
            expect(order[i - 1]!.compareDocumentPosition(order[i]!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        }
    });
});

describe('the editor in a dialog, narrow', () => {
    it('stacks one field per row: Date beside Check #, Payee, the hints, Category, Amount, Tax date, Memo, Tags', async () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });
        const similar = await screen.findByText('Similar:');
        const merge = await screen.findByText('Merge candidates:');

        const date = screen.getByLabelText('Date');
        const check = screen.getByLabelText('Check number');
        expect(cellOf(date).parentElement).toHaveClass(`${NARROW}grid-cols-[minmax(0,1fr)_6rem]`);
        // Date beside Check #, on the first row.
        expect(placed(date, 'row-start')).toBe(1);
        expect(placed(check, 'row-start')).toBe(1);
        expect(placed(check, 'col-start')).toBe(2);

        const stacked = [
            screen.getByLabelText('Payee'),
            similar,
            merge,
            screen.getByRole('combobox', { name: 'Category' }),
            screen.getByLabelText('Amount'),
            screen.getByText('Tax date', { selector: 'label' }),
            screen.getByLabelText('Tax date'),
            screen.getByText('Memo', { selector: 'label' }),
            screen.getByLabelText('Memo'),
            screen.getByText('Tags', { selector: 'label' }),
            screen.getByRole('button', { name: 'Edit Tags' }),
        ];
        expect(stacked.map((el) => placed(el, 'row-start', true)))
            .toEqual([2, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        for (const el of stacked) expect(narrowFull(el)).toBe(true);
    });

    it('grows the header fields to touch size, and only in a dialog', () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });
        const touch = `${NARROW}h-control-40px`;
        for (const el of [
            screen.getByLabelText('Date'),
            screen.getByLabelText('Check number'),
            screen.getByLabelText('Payee'),
            screen.getByRole('combobox', { name: 'Category' }),
            screen.getByLabelText('Amount'),
            screen.getByLabelText('Tax date'),
            screen.getByRole('button', { name: 'Edit Tags' }),
        ]) {
            expect(el).toHaveClass(touch);
        }
        expect(screen.getByLabelText('Memo')).toHaveClass(`${NARROW}min-h-control-40px`);
    });

    it('makes Cancel and the primary 44px buttons sharing the width', () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });
        const cancel = screen.getByRole('button', { name: 'Cancel' });
        const save = screen.getByRole('button', { name: 'Save' });
        expect(cancel).toHaveClass(`${NARROW}h-control-44px`);
        expect(save).toHaveClass(`${NARROW}h-control-44px`);
        expect(cancel.parentElement).toBe(save.parentElement);
        expect(cancel.parentElement).toHaveClass(`${NARROW}grid`, `${NARROW}grid-cols-2`);
    });
});

describe('the whole-transaction memo', () => {
    // The fit itself, border included, is lib/fitTextareaHeight's test: jsdom
    // draws no box to measure. What this pins is that the editor's memo is
    // fitted by it, on mount and again on a width change, in both layouts.
    it.each(['dialog', 'register'] as const)('is fitted by the shared measurement in the %s layout', (layout) => {
        const fit = vi.spyOn(fitModule, 'fitTextareaHeight');
        const refit = vi.spyOn(fitModule, 'refitOnWidthChange');
        renderEditor(SPLIT, { layout });

        const memo = screen.getByLabelText('Memo (whole transaction)');
        expect(fit).toHaveBeenCalledWith(memo, 96);
        expect(refit).toHaveBeenCalledWith(memo, 96);
    });
});

describe('splits in the dialog layout', () => {
    it('Split this transaction opens the splits grid under the dialog\'s header', async () => {
        renderEditor(ONE_SPLIT, { layout: 'dialog' });

        await userEvent.click(screen.getByRole('button', { name: 'Split this transaction →' }));

        expect(screen.getByLabelText('Posting 2 amount')).toBeInTheDocument();
        // The header keeps the dialog's columns; the whole-transaction notes
        // sit where the one-split's did.
        const payee = screen.getByLabelText('Payee');
        expect(placed(payee, 'col-start')).toBe(3);
        expect(placed(screen.getByLabelText('Memo (whole transaction)'), 'col-start')).toBe(3);
        expect(placed(screen.getByLabelText('Memo (whole transaction)'), 'row-start', true)).toBe(9);
        // The legs keep the splits grid's own sizes.
        expect(screen.getByLabelText('Posting 1 amount')).not.toHaveClass(`${NARROW}h-control-40px`);
        expect(screen.getByLabelText('Payee')).toHaveClass(`${NARROW}h-control-40px`);
    });

    it('saves a split from the dialog layout', async () => {
        const onSavePatch = vi.fn();
        renderEditor(SPLIT, { layout: 'dialog', onSavePatch });

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));

        expect(onSavePatch).toHaveBeenCalledTimes(1);
        const body = onSavePatch.mock.calls[0]![0] as PatchTransactionRequest;
        expect(body.postings?.items.map((p) => [p.legId, p.counterpartyAccountId, p.amount])).toEqual([
            ['leg-1', CATEGORY_ID, -8],
            ['leg-2', OTHER_CATEGORY_ID, -4],
        ]);
    });
});

describe('the register layout', () => {
    it('is still a register row with the pre-edit balance and no touch sizes', () => {
        renderEditor(ONE_SPLIT, { layout: 'register' });

        const row = screen.getByRole('row');
        expect(within(row).getByTitle('Pre-edit running balance')).toBeInTheDocument();
        const payee = screen.getByLabelText('Payee');
        expect(placed(payee, 'col-start')).toBe(5);
        expect(payee).not.toHaveClass(`${NARROW}h-control-40px`);
        expect(screen.getByRole('button', { name: 'Cancel' })).not.toHaveClass(`${NARROW}h-control-44px`);
    });
});
