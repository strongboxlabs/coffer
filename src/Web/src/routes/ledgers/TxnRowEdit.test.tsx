import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';

import { EditorDialog } from '@/components/ui/EditorDialog';
import { Modal } from '@/components/ui/Modal';

import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';
import * as apiModule from '@/lib/api';
import { currencyAffix } from '@/lib/money';
import type { AccountSummary } from '@/lib/types';
import type { PatchTransactionRequest } from '@/lib/types';

/**
 * Direct tests for the bank transaction editor, rather than reaching it only
 * indirectly through BankRegisterPage.
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

const EDIT_MODE: Extract<TxnRowMode, { kind: 'edit' }> = {
    kind: 'edit',
    headerId: HEADER_ID,
    sourceAccountId: BANK_ID,
    postings: [
        {
            legId: LEG_ID,
            counterpartyAccountId: CATEGORY_ID,
            counterpartyAccountName: 'Groceries', legTags: [],
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
    balanceCurrencyCode: 'USD',
    tags: [],
    inherited: null,
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

// A category's running balance combines accounts, so it is in the reporting currency,
// not the category's own; the editor's pre-edit copy reads it as the row does.
describe('TxnRowEdit pre-edit balance (ADR-0114 D7, D9)', () => {
    it('prints the balance in its own currency, not the register\'s', async () => {
        renderEditor({ ...EDIT_MODE, balanceAfter: 250, balanceCurrencyCode: 'EUR' }, {});

        const balance = await screen.findByTitle('Pre-edit running balance');
        expect(balance).toHaveTextContent('€250.00');
    });

    it('reads a balance that needs a missing rate "no rate"', async () => {
        const missing = { fromCurrency: 'USD', toCurrency: 'EUR', date: '2026-01-02', reason: 'rate_unavailable' as const };
        renderEditor({ ...EDIT_MODE, balanceAfter: null, balanceCurrencyCode: 'EUR', balanceRateUnavailable: [missing] }, {});

        const balance = await screen.findByTitle('Pre-edit running balance');
        expect(within(balance).getByText('no rate')).toHaveAttribute('title', 'No USD to EUR rate for Jan 2, 2026.');
    });
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

describe('the counterparty field is named for the side it faces', () => {
    // The field holds the OTHER side of the posting, so its name depends on
    // the register. An account's register faces categories; a CATEGORY's
    // register faces money accounts, and the field there listed account names
    // under a label reading "Category". Third instance of the same defect —
    // the register's column header and the picker's group heading were the
    // other two — so the pair is asserted rather than the single case.

    it('says Category on an account register', async () => {
        renderEditor({ kind: 'new', sourceAccountId: BANK_ID }, {});

        expect(await screen.findByLabelText('Category')).toBeInTheDocument();
        expect(screen.queryByLabelText('Account')).not.toBeInTheDocument();
    });

    it('says Account on a CATEGORY register', async () => {
        renderEditor({ kind: 'new', sourceAccountId: CATEGORY_ID }, {});

        expect(await screen.findByLabelText('Account')).toBeInTheDocument();
        expect(screen.queryByLabelText('Category')).not.toBeInTheDocument();
    });
});

describe('TxnRowEdit in a dialog that opens with it', () => {
    // The editor focuses its Date as it mounts. A dialog it mounts with opens
    // there, not on the dialog header's Close (ADR-0023 §L).
    it('opens on the editor\'s Date', async () => {
        render(
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
                <EditorDialog ariaLabel="Post occurrence" title="Post occurrence" onClose={() => {}}>
                    <TxnRowEdit
                        ledgerId={LEDGER_ID}
                        mode={EDIT_MODE}
                        layout="dialog"
                        payees={[]}
                        accounts={ACCOUNTS}
                        accountPaths={new Map(ACCOUNTS.map((a) => [a.id, a.name]))}
                        currency="USD"
                        onCancel={() => {}}
                        isSaving={false}
                        saveError={null}
                        cancelOnOutsideClick={false}
                    />
                </EditorDialog>
            </QueryClientProvider>,
        );

        const dialog = await screen.findByRole('dialog', { name: 'Post occurrence' });
        expect(within(dialog).getByLabelText('Date')).toHaveFocus();
        expect(within(dialog).getByRole('button', { name: 'Close' })).not.toHaveFocus();
    });
});

describe('the counterparty picker on a value it does not offer', () => {
    // The system Uncategorized category and an inactive category are shown as
    // the row's counterparty but never offered for a fresh pick (ADR-0043).
    // Opening keeps the value's text, selected for the copy round trip, and
    // lists everything it does offer until the user types.
    const UNCATEGORIZED_ID = '00000000-0000-0000-0000-000000000301';
    const RETIRED_ID = '00000000-0000-0000-0000-000000000302';
    const RENT_ID = '00000000-0000-0000-0000-000000000303';
    const PICKER_ACCOUNTS = [
        ...ACCOUNTS,
        account({ id: RENT_ID, name: 'Rent', accountType: 'category', categoryKind: 'expense' }),
        account({
            id: UNCATEGORIZED_ID, name: 'Uncategorized', accountType: 'category',
            categoryKind: 'expense', isSystem: true,
        }),
        account({
            id: RETIRED_ID, name: 'Retired', accountType: 'category',
            categoryKind: 'expense', isActive: false,
        }),
    ];

    function renderOn(counterpartyAccountId: string, counterpartyAccountName: string) {
        const onSavePatch = vi.fn();
        render(
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
                <TxnRowEdit
                    ledgerId={LEDGER_ID}
                    mode={{
                        ...EDIT_MODE,
                        postings: [{ ...EDIT_MODE.postings[0]!, counterpartyAccountId, counterpartyAccountName }],
                    }}
                    payees={[]}
                    accounts={PICKER_ACCOUNTS}
                    accountPaths={new Map(PICKER_ACCOUNTS.map((a) => [a.id, a.name]))}
                    currency="USD"
                    cols="repeat(8, minmax(0, 1fr))"
                    onCancel={() => {}}
                    onSavePatch={onSavePatch}
                    isSaving={false}
                    saveError={null}
                    cancelOnOutsideClick={false}
                />
            </QueryClientProvider>,
        );
        return { onSavePatch };
    }

    const options = () => screen.getAllByRole('option').map((o) => o.textContent);

    async function openPicker(user: ReturnType<typeof userEvent.setup>) {
        const picker = await screen.findByRole('combobox', { name: 'Category' });
        await user.click(picker);
        expect(picker).toHaveAttribute('aria-expanded', 'true');
        return picker as HTMLInputElement;
    }

    it.each([
        ['the system Uncategorized category', UNCATEGORIZED_ID, 'Uncategorized'],
        ['an inactive category', RETIRED_ID, 'Retired'],
    ])('opens on %s with its text selected and the full list', async (_name, id, name) => {
        const user = userEvent.setup();
        renderOn(id, name);

        const picker = await openPicker(user);

        expect(picker).toHaveValue(name);
        expect(picker.selectionStart).toBe(0);
        expect(picker.selectionEnd).toBe(name.length);
        expect(options()).toEqual(['Groceries', 'Rent']);
        expect(screen.queryByRole('option', { selected: true })).toBeNull();
    });

    it('narrows on the first keystroke, which replaces the text', async () => {
        const user = userEvent.setup();
        renderOn(UNCATEGORIZED_ID, 'Uncategorized');
        const picker = await openPicker(user);

        await user.keyboard('r');

        expect(picker).toHaveValue('r');
        expect(options()).toEqual(['Groceries', 'Rent']);
        await user.keyboard('e');
        expect(options()).toEqual(['Rent']);
    });

    it('keeps the value on an Enter before any row is chosen, and saves it unchanged', async () => {
        const user = userEvent.setup();
        const { onSavePatch } = renderOn(UNCATEGORIZED_ID, 'Uncategorized');
        const picker = await openPicker(user);

        await user.keyboard('{Enter}');
        expect(picker).toHaveValue('Uncategorized');
        await user.keyboard('{Escape}');
        expect(picker).toHaveAttribute('aria-expanded', 'false');
        expect(picker).toHaveValue('Uncategorized');

        await user.click(screen.getByRole('button', { name: /^Save/ }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        const body = JSON.stringify(onSavePatch.mock.calls[0]![0]);
        expect(body).toContain(UNCATEGORIZED_ID);
        expect(body).not.toContain(CATEGORY_ID);
        expect(body).not.toContain(RENT_ID);
    });

    it('opens on a value it offers narrowed to that value, as before', async () => {
        const user = userEvent.setup();
        renderOn(RENT_ID, 'Rent');

        const picker = await openPicker(user);

        expect(picker).toHaveValue('Rent');
        expect(options()).toEqual(['Rent']);
    });
});

describe('TxnRowEdit while its save is in flight', () => {
    // Cancel is pending while the host saves, and refuses a press. The pending
    // Save keeps focus in the editor, so Escape (Cancel's shortcut) and a press
    // outside reach the editor then too; neither may cancel what Cancel refuses.

    /** The editor in the register, saving from the first press of Save until `finish`. */
    function renderSaving(opts: { outside?: boolean } = {}) {
        const onCancel = vi.fn();
        const onSavePatch = vi.fn();
        let finish: () => void = () => {};
        function Host() {
            const [saving, setSaving] = useState(false);
            finish = () => setSaving(false);
            return (
                <TxnRowEdit
                    ledgerId={LEDGER_ID}
                    mode={EDIT_MODE}
                    payees={[]}
                    accounts={ACCOUNTS}
                    accountPaths={new Map(ACCOUNTS.map((a) => [a.id, a.name]))}
                    currency="USD"
                    cols="repeat(8, minmax(0, 1fr))"
                    onCancel={onCancel}
                    isSaving={saving}
                    saveError={null}
                    cancelOnOutsideClick={opts.outside === true}
                    onSavePatch={(b) => { onSavePatch(b); setSaving(true); }}
                />
            );
        }
        render(
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
                <Host />
                <button type="button">Elsewhere</button>
            </QueryClientProvider>,
        );
        return { onCancel, onSavePatch, finish: () => act(() => finish()) };
    }

    it('does not cancel on Escape pressed on the pending Save, and does once the save is done', async () => {
        const { onCancel, onSavePatch, finish } = renderSaving();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Save' }));
        const save = await screen.findByRole('button', { name: 'Saving…' });
        expect(save).toHaveAttribute('aria-busy', 'true');
        expect(save).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(onCancel).not.toHaveBeenCalled();
        expect(onSavePatch).toHaveBeenCalledTimes(1);

        await finish();
        await user.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('does not cancel on a press outside, and does once the save is done', async () => {
        const { onCancel, finish } = renderSaving({ outside: true });
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Save' }));
        expect(await screen.findByRole('button', { name: 'Saving…' })).toHaveAttribute('aria-busy', 'true');

        await user.click(screen.getByRole('button', { name: 'Elsewhere' }));
        expect(onCancel).not.toHaveBeenCalled();

        await finish();
        await user.click(screen.getByRole('button', { name: 'Elsewhere' }));
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('keeps the dialog it sits in open on Escape while the host saves', async () => {
        const onCancel = vi.fn();
        const onClose = vi.fn();
        render(
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
                <Modal open onClose={onClose} ariaLabel="Post occurrence">
                    <TxnRowEdit
                        ledgerId={LEDGER_ID}
                        mode={EDIT_MODE}
                        layout="dialog"
                        payees={[]}
                        accounts={ACCOUNTS}
                        accountPaths={new Map(ACCOUNTS.map((a) => [a.id, a.name]))}
                        currency="USD"
                        onCancel={onCancel}
                        isSaving
                        saveError={null}
                        cancelOnOutsideClick={false}
                        submitLabel="Post"
                        submittingLabel="Posting…"
                    />
                </Modal>
            </QueryClientProvider>,
        );
        const post = await screen.findByRole('button', { name: 'Posting…' });
        post.focus();

        await userEvent.setup().keyboard('{Escape}');

        expect(onCancel).not.toHaveBeenCalled();
        expect(onClose).not.toHaveBeenCalled();
    });
});

// ADR-0114 D14, D15: every money input shows its currency's symbol and takes its
// places; a figure the user changed is rounded to them, half away from zero, on blur and
// in the save body; a stored figure left alone is sent as it was stored.
describe('TxnRowEdit amounts at the posting currency\'s places (ADR-0114 D14, D15)', () => {
    const BANK_JPY = '00000000-0000-0000-0000-000000000101';
    const LEG_ID_2 = '00000000-0000-0000-0000-000000000bbc';
    const MIXED = [
        ...ACCOUNTS,
        account({ id: BANK_JPY, name: 'Account Y', currencyCode: 'JPY' }),
    ];

    function renderIn(currency: string, mode: TxnRowMode, onSavePatch?: (b: PatchTransactionRequest) => void) {
        const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        return render(
            <QueryClientProvider client={client}>
                <TxnRowEdit
                    ledgerId={LEDGER_ID}
                    mode={mode}
                    payees={[]}
                    accounts={MIXED}
                    accountPaths={new Map(MIXED.map((a) => [a.id, a.name]))}
                    currency={currency}
                    cols="repeat(8, minmax(0, 1fr))"
                    onCancel={() => {}}
                    onSavePatch={onSavePatch}
                    isSaving={false}
                    saveError={null}
                    cancelOnOutsideClick={false}
                />
            </QueryClientProvider>,
        );
    }

    const onJpy = (amount: number): TxnRowMode => ({
        ...EDIT_MODE,
        sourceAccountId: BANK_JPY,
        postings: [{ ...EDIT_MODE.postings[0]!, amount }],
    });
    const affixOf = (input: HTMLElement) =>
        input.parentElement!.querySelector('[data-money-affix]')?.textContent ?? null;
    const savedAmounts = (spy: ReturnType<typeof vi.fn>) =>
        (spy.mock.calls[0]![0] as PatchTransactionRequest).postings?.items?.map((i) => i.amount);
    const typeAndLeave = (input: HTMLElement, text: string) => {
        fireEvent.focus(input);
        fireEvent.change(input, { target: { value: text } });
        fireEvent.blur(input);
    };

    it('shows a whole-unit currency\'s symbol and steps in whole units', async () => {
        renderIn('JPY', onJpy(-40));

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        expect(affixOf(amount)).toBe(currencyAffix('JPY')!.symbol);
        expect(amount).toHaveAttribute('step', '1');
        expect(amount).toHaveValue(-40);
    });

    it('rounds a typed fraction to whole units on blur, half away from zero', async () => {
        renderIn('JPY', onJpy(-40));

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' }) as HTMLInputElement;
        typeAndLeave(amount, '-1234.5');
        expect(amount.value).toBe('-1235');
    });

    it('rounds a figure typed and saved without a blur in the body', async () => {
        const onSavePatch = vi.fn();
        renderIn('JPY', onJpy(-40), onSavePatch);

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        // A change with no blur after it: the save is the only thing that sees it.
        fireEvent.change(amount, { target: { value: '-99.5' } });
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        expect(savedAmounts(onSavePatch)).toEqual([-100]);
    });

    it('seeds a stored figure with every place it has, and sends it unchanged', async () => {
        const onSavePatch = vi.fn();
        renderIn('USD', { ...EDIT_MODE, postings: [{ ...EDIT_MODE.postings[0]!, amount: -40.125 }] }, onSavePatch);

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' }) as HTMLInputElement;
        expect(amount.value).toBe('-40.125');
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        expect(savedAmounts(onSavePatch)).toEqual([-40.125]);
    });

    it('keeps a stored whole-unit figure that carries a fraction through a visit and a save', async () => {
        // Saved before Coffer kept each currency's places: kept as it is (ADR-0114 D15).
        const onSavePatch = vi.fn();
        renderIn('JPY', onJpy(-4.5), onSavePatch);

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' }) as HTMLInputElement;
        fireEvent.focus(amount);
        fireEvent.blur(amount);
        expect(amount.value).toBe('-4.5');
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        expect(savedAmounts(onSavePatch)).toEqual([-4.5]);
    });

    it('takes the paying account\'s currency on a category register, not the category\'s', async () => {
        // The category is in the ledger's code; the row was paid from a whole-unit account.
        renderIn('USD', {
            ...EDIT_MODE,
            sourceAccountId: CATEGORY_ID,
            postings: [{ ...EDIT_MODE.postings[0]!, counterpartyAccountId: BANK_JPY, counterpartyAccountName: 'Account Y', amount: 40 }],
        });

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        expect(affixOf(amount)).toBe(currencyAffix('JPY')!.symbol);
        expect(amount).toHaveAttribute('step', '1');
    });

    it('takes the same places on each line of a split', async () => {
        const onSavePatch = vi.fn();
        renderIn('JPY', {
            ...EDIT_MODE,
            sourceAccountId: BANK_JPY,
            postings: [
                { ...EDIT_MODE.postings[0]!, amount: -4.5 },
                { ...EDIT_MODE.postings[0]!, legId: LEG_ID_2, amount: -30 },
            ],
        }, onSavePatch);

        const first = await screen.findByLabelText('Posting 1 amount') as HTMLInputElement;
        const second = screen.getByLabelText('Posting 2 amount') as HTMLInputElement;
        expect(affixOf(second)).toBe(currencyAffix('JPY')!.symbol);
        expect(second).toHaveAttribute('step', '1');
        // The stored line keeps its fraction; a typed one rounds on blur.
        expect(first.value).toBe('-4.5');
        typeAndLeave(second, '-12.5');
        expect(second.value).toBe('-13');
        // A change saved with no blur is rounded in the body; the stored line is not.
        fireEvent.change(second, { target: { value: '-20.4' } });
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        expect(savedAmounts(onSavePatch)).toEqual([-4.5, -20]);
    });
});
