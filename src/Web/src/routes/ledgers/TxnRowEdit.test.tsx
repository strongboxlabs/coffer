import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';
import * as apiModule from '@/lib/api';
import type { AccountSummary } from '@/lib/types';
import type { PatchTransactionRequest } from '@/lib/types';

/**
 * First direct tests for the bank transaction editor. It had none — 1600 lines
 * reached only indirectly through BankRegisterPage — which is why the tax-date
 * work started here rather than with the feature.
 *
 * The centrepiece is `preserves an existing tax date`. Sending `transactedAt` on
 * every save means an edit-mode caller that forgets to seed the field silently
 * WIPES a tax date the transaction already had, with no error and nothing on
 * screen to notice. That is a data-loss bug this test exists to prevent; the mode
 * type also makes the prop required so the compiler catches the same mistake.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BANK_ID = '00000000-0000-0000-0000-000000000100';
const CATEGORY_ID = '00000000-0000-0000-0000-000000000300';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';
const LEG_ID = '00000000-0000-0000-0000-000000000bbb';

const account = (over: Partial<AccountSummary>): AccountSummary => ({
    id: BANK_ID,
    ledgerId: LEDGER_ID,
    parentId: null,
    name: 'Checking',
    accountType: 'bank',
    categoryKind: null,
    currencyCode: 'USD',
    isActive: true,
    isSystem: false,
    feedConnectionId: null,
    needsReviewCount: 0,
    holdingsAccountId: null,
    isTradeCommission: false,
    ...over,
});

const ACCOUNTS = [
    account({}),
    account({ id: CATEGORY_ID, name: 'Groceries', accountType: 'category', categoryKind: 'expense' }),
];

const EDIT_MODE: TxnRowMode = {
    kind: 'edit',
    headerId: HEADER_ID,
    sourceAccountId: BANK_ID,
    postings: [
        {
            legId: LEG_ID,
            counterpartyAccountId: CATEGORY_ID,
            counterpartyAccountName: 'Groceries',
            amount: -40.25,
            legMemo: null,
        },
    ],
    payee: 'Corner Shop',
    memo: null,
    checkNumber: null,
    postedAt: '2026-01-02T00:00:00.000Z',
    transactedAt: null,
    balanceAfter: 100,
    tags: [],
    needsReview: false,
};

function renderEditor(mode: TxnRowMode, spies: {
    onSavePatch?: (b: PatchTransactionRequest) => void;
}) {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
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
                isSaving={false}
                saveError={null}
                cancelOnOutsideClick={false}
                {...spies}
            />
        </QueryClientProvider>,
    );
}

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchMergeCandidates').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({ accounts: [], categories: [] });
    vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([]);
});

describe('TxnRowEdit tax date', () => {
    it('offers a Tax date field that is blank by default', async () => {
        renderEditor({ kind: 'new', sourceAccountId: BANK_ID }, {});

        const tax = await screen.findByLabelText('Tax date');
        // Blank, NOT prefilled with the posted date: writing transactedAt ==
        // postedAt on every row would make the column meaningless and silence
        // the register's tax sub-label, which only shows when they differ.
        expect(tax).toHaveValue('');
    });

    it('sends the posted date when the field is blank, never null', async () => {
        const onSavePatch = vi.fn();
        renderEditor(EDIT_MODE, { onSavePatch });   // transactedAt: null

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
        // NOT null. On the patch path null means "leave this column alone"
        // (override layer, ADR-0003), so a null would make clearing a no-op.
        // Migration 189 stores "no distinct tax date" as the posted date.
        expect(onSavePatch.mock.calls[0]![0].transactedAt).toBe('2026-01-02T00:00:00.000Z');
    });

    it('sends a set tax date UTC-anchored', async () => {
        const onSavePatch = vi.fn();
        renderEditor(EDIT_MODE, { onSavePatch });

        const user = userEvent.setup();
        // fireEvent-style set: native date inputs don't take synthetic typing.
        const tax = await screen.findByLabelText('Tax date');
        fireEvent.change(tax, { target: { value: '2025-12-29' } });
        await user.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
        // The dividend booked Dec 29 but posted Jan 2 — the case this field exists for.
        expect(onSavePatch.mock.calls[0]![0].transactedAt).toBe('2025-12-29T00:00:00.000Z');
    });

    it('seeds the field from an existing tax date', async () => {
        renderEditor({ ...EDIT_MODE, transactedAt: '2025-12-29T00:00:00.000Z' }, {});

        expect(await screen.findByLabelText('Tax date')).toHaveValue('2025-12-29');
    });

    it('treats a same-day tax date as unset', async () => {
        // The MD importer writes transacted_at on every row, equal to posted_at.
        // Echoing that back into the field would make every imported transaction
        // look like it has a deliberate tax date.
        renderEditor(
            { ...EDIT_MODE, transactedAt: '2026-01-02T00:00:00.000Z' },  // == postedAt
            {},
        );

        expect(await screen.findByLabelText('Tax date')).toHaveValue('');
    });

    it('preserves an existing tax date when saving an untouched edit', async () => {
        const onSavePatch = vi.fn();
        renderEditor(
            { ...EDIT_MODE, transactedAt: '2025-12-29T00:00:00.000Z' },
            { onSavePatch },
        );

        const user = userEvent.setup();
        await waitFor(() => expect(screen.getByLabelText('Tax date')).toHaveValue('2025-12-29'));
        await user.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
        // Data loss if this regresses: every save sends transactedAt, so an
        // unseeded editor would send null and silently drop the tax date.
        expect(onSavePatch.mock.calls[0]![0].transactedAt).toBe('2025-12-29T00:00:00.000Z');
    });

    it('clears a tax date by sending the posted date, not null', async () => {
        const onSavePatch = vi.fn();
        renderEditor(
            { ...EDIT_MODE, transactedAt: '2025-12-29T00:00:00.000Z' },
            { onSavePatch },
        );

        const user = userEvent.setup();
        fireEvent.change(await screen.findByLabelText('Tax date'), { target: { value: '' } });
        await user.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
        // Sending null here would leave the existing override in place — the
        // clear would silently not happen. This assertion is the difference.
        expect(onSavePatch.mock.calls[0]![0].transactedAt).toBe('2026-01-02T00:00:00.000Z');
    });
});

describe('TxnRowEdit posted date (unchanged by the DateField extraction)', () => {
    it('still sends the posted date UTC-anchored', async () => {
        const onSavePatch = vi.fn();
        renderEditor(EDIT_MODE, { onSavePatch });

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
        expect(onSavePatch.mock.calls[0]![0].postedAt).toBe('2026-01-02T00:00:00.000Z');
    });

    it('sends an edited posted date', async () => {
        const onSavePatch = vi.fn();
        renderEditor(EDIT_MODE, { onSavePatch });

        const user = userEvent.setup();
        fireEvent.change(await screen.findByLabelText('Date'), {
            target: { value: '2026-03-04' },
        });
        await user.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
        expect(onSavePatch.mock.calls[0]![0].postedAt).toBe('2026-03-04T00:00:00.000Z');
    });
});
