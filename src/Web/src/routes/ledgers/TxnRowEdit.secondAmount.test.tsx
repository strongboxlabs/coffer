import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type StubbedCall } from '@/lib/annotations/fetchStub';
import type {
    AccountSummary, CreateTransactionRequest, PatchTransactionRequest,
} from '@/lib/types';

import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';

// The bank editor's second amount (ADR-0114 D13, D14; boards ED-1 to ED-7): the other
// account's own figure on a posting between real accounts in different currencies,
// right of Amount, with the rate line under it. Fetch is mocked at the request layer, so
// the real client builds the derived-rate request the prefill makes.

const LEDGER = '00000000-0000-0000-0000-000000000010';
const USD_BANK = '00000000-0000-0000-0000-000000000100';
const EUR_BANK = '00000000-0000-0000-0000-000000000200';
const JPY_BANK = '00000000-0000-0000-0000-000000000250';
const USD_SAVINGS = '00000000-0000-0000-0000-000000000101';
const GROCERIES = '00000000-0000-0000-0000-000000000300';
const HEADER = '00000000-0000-0000-0000-000000000aaa';
const LEG = '00000000-0000-0000-0000-000000000bbb';

const account = (over: Partial<AccountSummary>): AccountSummary => ({
    id: USD_BANK,
    ledgerId: LEDGER,
    parentId: null,
    name: 'Test USD Bank',
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

const ACCOUNTS: AccountSummary[] = [
    account({}),
    account({ id: EUR_BANK, name: 'Test EUR Bank', currencyCode: 'EUR' }),
    account({ id: JPY_BANK, name: 'Test JPY Bank', currencyCode: 'JPY' }),
    account({ id: USD_SAVINGS, name: 'Test USD Savings' }),
    account({ id: GROCERIES, name: 'Groceries', accountType: 'category', categoryKind: 'expense' }),
];

const DERIVED = /\/fx-rates\/[A-Z]{3}\/[A-Z]{3}\/derived$/;

/** A derived rate of `rate` for 2026-11-12, converting the asked amount at it. */
function rateOf(rate: number) {
    return (call: StubbedCall) => {
        const [, from, to] = /\/fx-rates\/([A-Z]{3})\/([A-Z]{3})\/derived$/.exec(call.path)!;
        const amount = call.search.get('amount');
        const places = to === 'JPY' ? 0 : 2;
        return {
            fromCurrency: from, toCurrency: to, date: call.search.get('date'), reportingCurrency: 'USD',
            rate, rateDate: '2026-11-12', legs: [],
            converted: amount === null ? null : Number((Number(amount) * rate).toFixed(places)),
            rateUnavailable: [],
        };
    };
}

function newMode(over: Partial<{ counterpartyAccountId: string; amount: number }> = {}): TxnRowMode {
    return {
        kind: 'new',
        sourceAccountId: USD_BANK,
        postedAt: '2026-11-12',
        prefill: {
            postings: [{
                counterpartyAccountId: over.counterpartyAccountId ?? EUR_BANK,
                amount: over.amount ?? -100,
            }],
        },
    };
}

/** The same transfer opened from the EUR register, as stored (board ED-3). */
function editFromEur(over: { counterpartyAmount?: number | null } = {}): TxnRowMode {
    return {
        kind: 'edit',
        headerId: HEADER,
        sourceAccountId: EUR_BANK,
        postings: [{
            legId: LEG,
            counterpartyAccountId: USD_BANK,
            counterpartyAccountName: 'Test USD Bank',
            amount: 90,
            legMemo: null,
            legTags: [],
            counterpartyAmount: over.counterpartyAmount === undefined ? -100 : over.counterpartyAmount,
        }],
        payee: 'Fund EUR account',
        memo: null,
        checkNumber: null,
        postedAt: '2026-11-08T00:00:00.000Z',
        transactedAt: null,
        balanceAfter: 566.5,
        balanceCurrencyCode: 'EUR',
        tags: [],
        inherited: null,
        needsReview: false,
    };
}

function renderEditor(
    mode: TxnRowMode,
    {
        currency = 'USD',
        layout = 'register',
        onSaveCreate,
        onSavePatch,
        amountNote,
    }: {
        currency?: string;
        layout?: 'register' | 'dialog';
        onSaveCreate?: (b: CreateTransactionRequest) => void;
        onSavePatch?: (b: PatchTransactionRequest) => void;
        amountNote?: string;
    } = {},
) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const common = {
        ledgerId: LEDGER,
        mode,
        payees: [],
        accounts: ACCOUNTS,
        accountPaths: new Map(ACCOUNTS.map((a) => [a.id, a.name])),
        currency,
        onCancel: () => {},
        isSaving: false,
        saveError: null,
        cancelOnOutsideClick: false,
        onSaveCreate,
        onSavePatch,
        amountNote,
    };
    return render(
        <QueryClientProvider client={client}>
            {layout === 'dialog'
                ? <TxnRowEdit {...common} layout="dialog" />
                : <TxnRowEdit {...common} cols="repeat(8, minmax(0, 1fr))" />}
        </QueryClientProvider>,
    );
}

const RECEIVED_EUR = 'Amount received in Test EUR Bank, in EUR';

async function pickCounterparty(name: RegExp) {
    // Opened and chosen by its own events: the list opens on focus and a row commits on
    // mouse-down.
    const picker = screen.getByRole('combobox', { name: 'Category' });
    fireEvent.focus(picker);
    fireEvent.change(picker, { target: { value: '' } });
    fireEvent.mouseDown(await screen.findByRole('option', { name }));
}

afterEach(() => {
    vi.unstubAllGlobals();
});

describe('TxnRowEdit — the second amount on a new transfer between currencies (ED-1)', () => {
    it('prefills Received from the derived rate and names the rate and its day', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode());

        const field = await screen.findByLabelText(RECEIVED_EUR);
        await waitFor(() => expect(field).toHaveValue(90));
        expect(screen.getByText('Received')).toBeInTheDocument();
        expect(screen.getByText('Rate for Nov 12, 2026: 1 USD = 0.9000 EUR')).toBeInTheDocument();
        // The title names the account and the code, as the accessible name does.
        expect(field.closest('label')).toHaveAttribute('title', RECEIVED_EUR);
    });

    it('asks again and refills when Amount or the date changes, while it is not typed in', async () => {
        const stub = stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode());
        const field = await screen.findByLabelText(RECEIVED_EUR);
        await waitFor(() => expect(field).toHaveValue(90));

        fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '-200' } });
        await waitFor(() => expect(field).toHaveValue(180));
        // The magnitude is asked for: the sign follows from Amount.
        expect(stub.callsTo('GET', DERIVED).at(-1)!.search.get('amount')).toBe('200');

        fireEvent.change(screen.getByLabelText('Date'), { target: { value: '2026-11-10' } });
        await waitFor(() => expect(stub.callsTo('GET', DERIVED).at(-1)!.search.get('date')).toBe('2026-11-10'));
    });

    it('sends the other account\'s figure signed opposite to Amount', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        const onSaveCreate = vi.fn();
        renderEditor(newMode(), { onSaveCreate });
        await waitFor(() => expect(screen.getByLabelText(RECEIVED_EUR)).toHaveValue(90));

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(onSaveCreate).toHaveBeenCalledTimes(1));
        const body = onSaveCreate.mock.calls[0]![0] as CreateTransactionRequest;
        expect(body.postings[0]).toEqual({
            legId: null,
            counterpartyAccountId: EUR_BANK,
            amount: -100,
            counterpartyAmount: 90,
        });
    });

    it('prefills whole units for a currency with none', async () => {
        stubFetch().on('GET', DERIVED, rateOf(150.4));
        renderEditor(newMode({ counterpartyAccountId: JPY_BANK }));
        const field = await screen.findByLabelText('Amount received in Test JPY Bank, in JPY');
        await waitFor(() => expect(field).toHaveValue(15040));
        expect((field as HTMLInputElement).value).toBe('15040');
    });
});

describe('TxnRowEdit — a typed second amount (ED-2)', () => {
    it('keeps a typed figure when Amount changes, and the line shows the two amounts\' ratio', async () => {
        const stub = stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode());
        const field = await screen.findByLabelText(RECEIVED_EUR);
        await waitFor(() => expect(field).toHaveValue(90));

        fireEvent.change(field, { target: { value: '90.50' } });
        expect(screen.getByText('1 USD = 0.9050 EUR, from the two amounts')).toBeInTheDocument();
        const asked = stub.callsTo('GET', DERIVED).length;

        fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '-200' } });
        expect(field).toHaveValue(90.5);
        expect(screen.getByText('1 USD = 0.4525 EUR, from the two amounts')).toBeInTheDocument();
        // Once typed, nothing more is asked.
        await new Promise((r) => { setTimeout(r, 400); });
        expect(stub.callsTo('GET', DERIVED)).toHaveLength(asked);
    });

    it('ignores a typed minus, and flipping Amount\'s sign flips the figure it saves', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        const onSaveCreate = vi.fn();
        renderEditor(newMode(), { onSaveCreate });
        const field = await screen.findByLabelText(RECEIVED_EUR);
        await waitFor(() => expect(field).toHaveValue(90));

        fireEvent.change(field, { target: { value: '-95' } });
        expect(field).toHaveValue(95);

        fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '100' } });
        const sent = screen.getByLabelText('Amount sent from Test EUR Bank, in EUR');
        expect(sent).toHaveValue(-95);
        expect(screen.getByText('Sent')).toBeInTheDocument();
        expect(sent).toHaveClass('text-state-danger');

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(onSaveCreate).toHaveBeenCalledTimes(1));
        const body = onSaveCreate.mock.calls[0]![0] as CreateTransactionRequest;
        expect(body.postings[0]).toMatchObject({ amount: 100, counterpartyAmount: -95 });
    });
});

describe('TxnRowEdit — a saved transfer between currencies (ED-3)', () => {
    it('seeds both stored amounts, asks for no rate, and an untouched save resends them', async () => {
        const stub = stubFetch().on('GET', DERIVED, rateOf(1.1));
        const onSavePatch = vi.fn();
        renderEditor(editFromEur(), { currency: 'EUR', onSavePatch });

        const field = await screen.findByLabelText('Amount sent from Test USD Bank, in USD');
        expect((field as HTMLInputElement).value).toBe('-100.00');
        expect(field).toHaveClass('text-state-danger');
        expect(screen.getByText('Sent')).toBeInTheDocument();
        expect(screen.getByText('1 EUR = 1.111111 USD, from the two amounts')).toBeInTheDocument();

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        const body = onSavePatch.mock.calls[0]![0] as PatchTransactionRequest;
        expect(body.postings?.items?.[0]).toEqual({
            legId: LEG,
            counterpartyAccountId: USD_BANK,
            amount: 90,
            counterpartyAmount: -100,
        });
        expect(stub.callsTo('GET', DERIVED)).toHaveLength(0);
    });

    it('takes the Balance track, so the pre-edit balance shows again only once the field goes', async () => {
        stubFetch().on('GET', DERIVED, rateOf(1.1));
        renderEditor(editFromEur(), { currency: 'EUR' });
        await screen.findByLabelText('Amount sent from Test USD Bank, in USD');
        const field = screen.getByLabelText('Amount sent from Test USD Bank, in USD');
        expect(field.closest('label')).toHaveClass('col-start-8');
        expect(screen.queryByTitle('Pre-edit running balance')).toBeNull();

        await pickCounterparty(/Groceries/);
        // Anchor: the editor is still there, with its Amount.
        expect(screen.getByLabelText('Amount')).toBeInTheDocument();
        expect(screen.queryByLabelText('Amount sent from Test USD Bank, in USD')).toBeNull();
        expect(screen.getByTitle('Pre-edit running balance')).toBeInTheDocument();
    });
});

describe('TxnRowEdit — no rate (ED-4)', () => {
    it('leaves the field empty and required, names the missing stored leg, and disables Save', async () => {
        stubFetch().on('GET', DERIVED, (call: StubbedCall) => ({
            fromCurrency: 'USD', toCurrency: 'EUR', date: call.search.get('date'), reportingCurrency: 'USD',
            rate: null, rateDate: null, legs: [], converted: null,
            rateUnavailable: [{ fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-11-12', reason: 'rate_unavailable' }],
        }));
        renderEditor(newMode());

        expect(await screen.findByText('No EUR to USD rate for Nov 12, 2026')).toBeInTheDocument();
        expect(screen.getByLabelText(RECEIVED_EUR)).toHaveValue(null);
        const sentence = 'Enter the amount in EUR that arrived in Test EUR Bank.';
        const save = screen.getByRole('button', { name: 'Save' });
        expect(save).toBeDisabled();
        expect(save).toHaveAttribute('title', sentence);
        expect(screen.getByText(sentence)).toBeInTheDocument();
    });

    it('refuses a zero with the same sentence, and turns it round on the other side', async () => {
        stubFetch().on('GET', DERIVED, rateOf(1.1));
        renderEditor(editFromEur(), { currency: 'EUR' });
        const field = await screen.findByLabelText('Amount sent from Test USD Bank, in USD');
        fireEvent.change(field, { target: { value: '0' } });
        const save = screen.getByRole('button', { name: 'Save' });
        expect(save).toBeDisabled();
        expect(save).toHaveAttribute('title', 'Enter the amount in USD that left Test USD Bank.');
    });

    it('refuses a zero Amount, as one side of the pair, once the second amount is typed', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode({ amount: 0 }));
        const field = await screen.findByLabelText(RECEIVED_EUR);
        fireEvent.change(field, { target: { value: '5' } });
        expect(field).toHaveValue(5);
        const save = screen.getByRole('button', { name: 'Save' });
        expect(save).toBeDisabled();
        expect(save).toHaveAttribute('title', 'Posting 1: amount is required.');
    });
});

describe('TxnRowEdit — a counterparty that is not a real account in another currency (ED-6)', () => {
    it('removes the field and its line on picking a category, and drops the figure from the body', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        const onSaveCreate = vi.fn();
        renderEditor(newMode(), { onSaveCreate });
        await waitFor(() => expect(screen.getByLabelText(RECEIVED_EUR)).toHaveValue(90));

        await pickCounterparty(/Groceries/);
        // Anchor: the row is the one-line editor still, with its Amount.
        expect(screen.getByLabelText('Amount')).toHaveValue(-100);
        expect(screen.queryByLabelText(RECEIVED_EUR)).toBeNull();
        expect(screen.queryByText(/from the two amounts|Rate for/)).toBeNull();

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(onSaveCreate).toHaveBeenCalledTimes(1));
        const body = onSaveCreate.mock.calls[0]![0] as CreateTransactionRequest;
        expect(body.postings[0]).toEqual({ legId: null, counterpartyAccountId: GROCERIES, amount: -100 });
    });

    it('shows no field for an account in the same currency', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode({ counterpartyAccountId: USD_SAVINGS }));
        // Anchor: the one-line editor has rendered its Amount.
        expect(await screen.findByLabelText('Amount')).toHaveValue(-100);
        expect(screen.queryByText('Received')).toBeNull();
        expect(screen.queryByText('Sent')).toBeNull();
    });

    it('prefills afresh when a real account in another currency is picked again', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(editFromEur({ counterpartyAmount: -100 }), { currency: 'EUR' });
        await screen.findByLabelText('Amount sent from Test USD Bank, in USD');

        await pickCounterparty(/Groceries/);
        expect(screen.getByLabelText('Amount')).toBeInTheDocument();
        await pickCounterparty(/Test JPY Bank/);
        // A new figure from the rate, not the stored USD one.
        await waitFor(() => expect(screen.getByLabelText('Amount sent from Test JPY Bank, in JPY')).toHaveValue(-81));
    });
});

describe('TxnRowEdit — the dialog layout (OT-3)', () => {
    it('gains a sixth track only while the field shows', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode(), { layout: 'dialog' });
        const field = await screen.findByLabelText(RECEIVED_EUR);
        const grid = field.closest('label')!.parentElement!;
        expect(grid.className).toContain('grid-cols-[7.5rem_4.75rem_minmax(0,1fr)_minmax(0,1fr)_7.5rem_7.5rem]');
        expect(field.closest('label')).toHaveClass('col-start-6', '@max-3xl/dialog:row-start-6');
        // Narrow, the notes move down past the field and its line.
        expect(screen.getByText('Tax date')).toHaveClass('@max-3xl/dialog:row-start-8');

        await pickCounterparty(/Groceries/);
        expect(screen.getByLabelText('Amount')).toBeInTheDocument();
        expect(grid.className).toContain('grid-cols-[7.5rem_4.75rem_minmax(0,1fr)_minmax(0,1fr)_7.5rem]');
        expect(grid.className).not.toContain('7.5rem_7.5rem');
        expect(screen.getByText('Tax date')).toHaveClass('@max-3xl/dialog:row-start-6');
    });
});

describe('TxnRowEdit — the Review editor\'s rounded-at-import note (ADR-0114 D15, RV-2)', () => {
    const NOTE = 'Rounded from 1,234.5 as sent by the bank';

    it('takes its own line directly under Amount while the posting has a second amount, not the rate line\'s cell (ADR-0114 D15)', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode(), { layout: 'dialog', amountNote: NOTE });
        const rate = await screen.findByText('Rate for Nov 12, 2026: 1 USD = 0.9000 EUR');

        const amount = screen.getByLabelText('Amount');
        const note = screen.getByText(NOTE);
        expect(amount).toHaveAccessibleDescription(NOTE);
        expect(amount.getAttribute('aria-describedby')).toBe(note.id);
        // A cell of the header grid of its own, next after Amount's, on the row under
        // Category and Amount; narrow, the row after Amount's. Wide, it spans the same
        // tracks as the rate line, so both end at Received's right edge.
        const amountCell = amount.closest('label')!;
        const grid = amountCell.parentElement!;
        expect(note.parentElement).toBe(grid);
        expect(note.previousElementSibling).toBe(amountCell);
        expect(note).toHaveClass('row-start-2', 'col-start-4', 'col-span-3', 'text-right', '@max-3xl/dialog:row-start-6');
        expect(note).not.toHaveClass('col-span-2');
        expect(amountCell).toHaveClass('row-start-1', 'col-start-5', '@max-3xl/dialog:row-start-5');
        // Not in the rate line's cell: the line is a grid cell of its own, a row lower.
        expect(rate.contains(note)).toBe(false);
        expect(rate.parentElement).toBe(grid);
        expect(rate).toHaveClass('row-start-3', 'col-start-4', 'col-span-3', '@max-3xl/dialog:row-start-8');
        // Narrow, the second amount comes after the note, and the notes after its line;
        // wide, they move down one row, and the hints span the note's row and the line's.
        expect(screen.getByLabelText(RECEIVED_EUR).closest('label')).toHaveClass('row-start-1', 'col-start-6', '@max-3xl/dialog:row-start-7');
        expect(screen.getByText('Tax date')).toHaveClass('row-start-4', '@max-3xl/dialog:row-start-9');
        expect(grid.className).toContain('grid-rows-[auto_auto_1fr_auto_auto]');
        expect(grid.querySelector('.row-start-2.col-start-3')).toHaveClass('row-span-2', '@max-3xl/dialog:row-span-1');

        // Back to one currency, it takes its own cell under Category and Amount, and the
        // narrow dialog's row after Amount, the notes moved down past it.
        await pickCounterparty(/Groceries/);
        const alone = screen.getByText(NOTE);
        expect(alone).toHaveClass('row-start-2', 'col-start-4', 'col-span-2', '@max-3xl/dialog:row-start-6');
        expect(alone).not.toHaveClass('col-span-3');
        expect(screen.getByLabelText('Amount')).toHaveAccessibleDescription(NOTE);
        expect(screen.getByText('Tax date')).toHaveClass('row-start-3', '@max-3xl/dialog:row-start-8');
        expect(grid.className).not.toContain('grid-rows-');
    });

    it('leaves the second amount\'s layout as it was when there is no note', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(newMode(), { layout: 'dialog' });
        const rate = await screen.findByText('Rate for Nov 12, 2026: 1 USD = 0.9000 EUR');

        expect(rate).toHaveClass('row-start-2', 'col-start-4', 'col-span-3', '@max-3xl/dialog:row-start-7');
        expect(rate.parentElement!.className).not.toContain('grid-rows-');
        expect(screen.getByLabelText(RECEIVED_EUR).closest('label')).toHaveClass('@max-3xl/dialog:row-start-6');
        expect(screen.getByText('Tax date')).toHaveClass('row-start-3', '@max-3xl/dialog:row-start-8');
        expect(screen.getByLabelText('Amount')).not.toHaveAccessibleDescription();
    });

    it('is drawn by the dialog layout only', async () => {
        stubFetch();
        renderEditor(newMode({ counterpartyAccountId: GROCERIES }), { amountNote: NOTE });

        const amount = await screen.findByLabelText('Amount');
        expect(amount).not.toHaveAccessibleDescription();
        expect(screen.queryByText(NOTE)).toBeNull();
    });
});

describe('TxnRowEdit — a split with one line to another currency (ED-5)', () => {
    const CATEGORY_LEG = '00000000-0000-0000-0000-000000000ccc';
    function splitMode(): TxnRowMode {
        return {
            kind: 'edit',
            headerId: HEADER,
            sourceAccountId: USD_BANK,
            postings: [
                { legId: CATEGORY_LEG, counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Groceries', amount: -40, legMemo: null, legTags: [] },
                {
                    legId: LEG, counterpartyAccountId: EUR_BANK, counterpartyAccountName: 'Test EUR Bank', amount: -100,
                    legMemo: null, legTags: [], counterpartyAmount: 90,
                },
            ],
            payee: 'Split payee', memo: null, checkNumber: null,
            postedAt: '2026-11-12T00:00:00.000Z', transactedAt: null,
            balanceAfter: 0, balanceCurrencyCode: 'USD', tags: [], inherited: null, needsReview: false,
        };
    }

    it('sums the Amount column only, never the Received figure', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        renderEditor(splitMode());
        await screen.findByLabelText('Posting 2: Amount received in Test EUR Bank, in EUR');
        for (const total of screen.getAllByTitle('Sum of postings (saved as the transaction total)')) {
            expect(total).toHaveTextContent('-$140.00');
        }
    });

    it('sends each line\'s own pair, and no key on the category line', async () => {
        stubFetch().on('GET', DERIVED, rateOf(0.9));
        const onSavePatch = vi.fn();
        renderEditor(splitMode(), { onSavePatch });
        await screen.findByLabelText('Posting 2: Amount received in Test EUR Bank, in EUR');

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        const items = (onSavePatch.mock.calls[0]![0] as PatchTransactionRequest).postings?.items;
        expect(items).toEqual([
            { legId: CATEGORY_LEG, counterpartyAccountId: GROCERIES, amount: -40 },
            { legId: LEG, counterpartyAccountId: EUR_BANK, amount: -100, counterpartyAmount: 90 },
        ]);
    });

    it('prefills a line that gains the field, and drops it when the line goes to a category', async () => {
        const stub = stubFetch().on('GET', DERIVED, rateOf(0.9));
        const onSavePatch = vi.fn();
        renderEditor(splitMode(), { onSavePatch });
        await screen.findByLabelText('Posting 2: Amount received in Test EUR Bank, in EUR');

        // Line 1 moves from the category to the JPY account: prefilled from the rate.
        fireEvent.focus(screen.getByLabelText('Posting 1 category'));
        fireEvent.change(screen.getByLabelText('Posting 1 category'), { target: { value: '' } });
        fireEvent.mouseDown(await screen.findByRole('option', { name: /Test JPY Bank/ }));
        const jpy = await screen.findByLabelText('Posting 1: Amount received in Test JPY Bank, in JPY');
        await waitFor(() => expect(jpy).toHaveValue(36));
        expect(stub.callsTo('GET', DERIVED).at(-1)!.path).toMatch(/\/USD\/JPY\/derived$/);
        expect(screen.getAllByText('Rate for Nov 12, 2026: 1 USD = 0.9000 JPY')).toHaveLength(1);

        // Line 2 moves to the category: its figure goes.
        fireEvent.focus(screen.getByLabelText('Posting 2 category'));
        fireEvent.change(screen.getByLabelText('Posting 2 category'), { target: { value: '' } });
        fireEvent.mouseDown(await screen.findByRole('option', { name: /Groceries/ }));
        expect(screen.getByLabelText('Posting 2 amount')).toHaveValue(-100);
        expect(screen.queryByLabelText('Posting 2: Amount received in Test EUR Bank, in EUR')).toBeNull();

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        const items = (onSavePatch.mock.calls[0]![0] as PatchTransactionRequest).postings?.items;
        expect(items?.[0]).toEqual({ legId: CATEGORY_LEG, counterpartyAccountId: JPY_BANK, amount: -40, counterpartyAmount: 36 });
        expect(items?.[1]).toEqual({ legId: LEG, counterpartyAccountId: GROCERIES, amount: -100 });
    });
});
