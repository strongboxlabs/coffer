import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';
import * as apiModule from '@/lib/api';
import type {
    AccountSummary, CreateTransactionRequest, PatchTransactionRequest, PayeeSuggestion,
} from '@/lib/types';

/**
 * CHARACTERISATION tests — written BEFORE decomposing this file, to make
 * "behaviour-zero" mean something.
 *
 * WHY A SECOND FILE. `TxnRowEdit.test.tsx` is the stated gate for the
 * decomposition, and it is not sufficient to be one. It was written for the
 * tax-date feature (its own header says so), not as a refactor gate, and a
 * seam-mapping pass over the 1643-line component found what it leaves open:
 *
 *   - EVERY mode it passes has ONE posting, so the split branch, PostingRow,
 *     the ghost row, the tags placeholder and both side panels are never
 *     rendered. That is most of the file, and all of what the redesign touches.
 *   - It pins three accessible names, two date values and TWO KEYS of the patch
 *     body — out of nine inputs to `buildSaveBody`. Silently dropping `tags`,
 *     `approve` or the merge short-circuit is invisible to every test in the
 *     repo.
 *   - Five of its seven tax-date tests pass against an inverter that always
 *     returns `''`. "clears a tax date" is satisfied by an implementation that
 *     never reads the field at all.
 *   - Its same-day test feeds BYTE-IDENTICAL ISO strings, so a naive `===`
 *     passes while breaking any pair that needs normalising.
 *   - It passes `cancelOnOutsideClick={false}`, so it cannot see the
 *     outside-pointerdown handler at all — and nothing else in the repo opens
 *     this editor.
 *
 * So these tests pin the behaviour the decomposition could break silently.
 * They assert what the code does TODAY, not what it ought to do.
 *
 * STILL NOT COVERED, and deliberately named rather than implied:
 *
 *   - THE MERGE ARM. `buildSaveBody`'s postings loop runs BEFORE the merge
 *     short-circuit, so an armed merge over an unfilled posting returns null
 *     and Save silently no-ops. That is a real bug — and a naive "merge first"
 *     split would ACCIDENTALLY FIX it, which is a behaviour change, not a
 *     freebie. Arming it from a test means driving MergeCandidatesPanel
 *     through a mocked `fetchMergeCandidates`; until that exists, the ordering
 *     is held by inspection only. Do not reorder those two blocks while
 *     decomposing.
 *   - THE CREATE ARM. Every test here is edit-mode, so `onSaveCreate`'s body
 *     is unasserted beyond what the original gate checks.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BANK_ID = '00000000-0000-0000-0000-000000000100';
const CATEGORY_ID = '00000000-0000-0000-0000-000000000300';
const OTHER_CATEGORY_ID = '00000000-0000-0000-0000-000000000301';
const SAVINGS_ID = '00000000-0000-0000-0000-000000000101';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';
const LEG_ID = '00000000-0000-0000-0000-000000000bbb';
const LEG_ID_2 = '00000000-0000-0000-0000-000000000ccc';

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
    account({ id: OTHER_CATEGORY_ID, name: 'Dining', accountType: 'category', categoryKind: 'expense' }),
];

interface Spies {
    onSavePatch?: (b: PatchTransactionRequest) => void;
    onSaveCreate?: (b: CreateTransactionRequest) => void;
    onCancel?: () => void;
}

function renderEditor(
    mode: TxnRowMode,
    spies: Spies = {},
    over: {
        cancelOnOutsideClick?: boolean;
        payees?: PayeeSuggestion[];
        accounts?: AccountSummary[];
    } = {},
) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const accounts = over.accounts ?? ACCOUNTS;
    return render(
        <QueryClientProvider client={client}>
            <TxnRowEdit
                ledgerId={LEDGER_ID}
                mode={mode}
                payees={over.payees ?? []}
                accounts={accounts}
                accountPaths={new Map(accounts.map((a) => [a.id, a.name]))}
                currency="USD"
                cols="repeat(8, minmax(0, 1fr))"
                onCancel={spies.onCancel ?? (() => {})}
                isSaving={false}
                saveError={null}
                cancelOnOutsideClick={over.cancelOnOutsideClick ?? false}
                onSavePatch={spies.onSavePatch}
                onSaveCreate={spies.onSaveCreate}
            />
        </QueryClientProvider>,
    );
}

/** An edit mode carrying `count` postings, so the SPLIT branch renders. */
function splitMode(count: number, over: Partial<Extract<TxnRowMode, { kind: 'edit' }>> = {}) {
    const postings = Array.from({ length: count }, (_, i) => ({
        legId: i === 0 ? LEG_ID : i === 1 ? LEG_ID_2 : `${LEG_ID_2}-${i}`,
        counterpartyAccountId: i % 2 === 0 ? CATEGORY_ID : OTHER_CATEGORY_ID,
        counterpartyAccountName: i % 2 === 0 ? 'Groceries' : 'Dining', legTags: [],
        amount: -(10 + i),
        legMemo: `leg ${i + 1}`,
    }));
    return {
        kind: 'edit' as const,
        headerId: HEADER_ID,
        sourceAccountId: BANK_ID,
        postings,
        payee: 'Corner Shop',
        memo: 'umbrella memo',
        checkNumber: '4471',
        postedAt: '2026-01-02T00:00:00.000Z',
        transactedAt: null,
        balanceAfter: 100,
        balanceCurrencyCode: 'USD',
        tags: ['work'],
        inherited: null,
        needsReview: false,
        ...over,
    } satisfies TxnRowMode;
}

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchMergeCandidates').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({ accounts: [], categories: [] });
    vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([]);
});

describe('the split branch renders at all', () => {
    // The existing gate never reaches this branch: every mode it passes has one
    // posting. Nothing in the repo asserts that a split transaction renders.
    it('renders one row per posting, each with its own memo, tags slot and amount', async () => {
        renderEditor(splitMode(3));

        for (const n of [1, 2, 3]) {
            expect(await screen.findByLabelText(`Posting ${n} memo`)).toBeInTheDocument();
            expect(screen.getByLabelText(`Posting ${n} amount`)).toBeInTheDocument();
            expect(screen.getByLabelText(`Posting ${n} tags`)).toBeInTheDocument();
            expect(screen.getByLabelText(`Reorder posting ${n}`)).toBeInTheDocument();
            // Remove lives in the row menu, with Move up / Move down.
            // The visible path is the kebab (ADR-0021 Rule 10).
            expect(screen.getByLabelText(`Actions for posting ${n}`)).toBeInTheDocument();
        }
        // ...and no fourth row.
        expect(screen.queryByLabelText('Posting 4 amount')).toBeNull();
        // The add affordance is present in the split branch.
        expect(screen.getByLabelText('Add another posting')).toBeInTheDocument();
    });

    it('tells the transaction tags apart from every posting row', async () => {
        // The single-posting branch labels the control 'Tags'; the split branch
        // names it after its visible caption, 'Tags (whole transaction)', so
        // the accessible name contains the text a voice user reads off the
        // screen. A field extraction that hardcoded either label would
        // silently change the accessible name.
        //
        // Postings carry their own tags (FU-004), and the header control
        // carries a visible caption associated with it, so 'Tags'
        // legitimately matches more than one control. What must not blur is
        // that the three controls are told APART — which is what this pins, on
        // all three rather than on the header alone.
        renderEditor(splitMode(2));
        expect(await screen.findByLabelText('Tags (whole transaction)', { selector: 'span' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Edit Tags (whole transaction)' })).toBeInTheDocument();
        expect(screen.getByLabelText('Posting 1 tags')).toBeInTheDocument();
        expect(screen.getByLabelText('Posting 2 tags')).toBeInTheDocument();
    });

    it('captions the two levels as the whole transaction\'s', async () => {
        renderEditor(splitMode(2));
        expect(await screen.findByText('Memo (whole transaction)')).toBeInTheDocument();
        expect(screen.getByText('Tags (whole transaction)')).toBeInTheDocument();
        // The labels they replaced are gone.
        expect(screen.queryByText('Memo (umbrella)')).toBeNull();
        expect(screen.queryByLabelText('Header-level tags')).toBeNull();
    });

    it('never lets the last posting be removed', async () => {
        renderEditor(splitMode(2));
        // Two clicks, not one: Remove lives in the row menu so the actions
        // column can carry Move up / Move down as well.
        await userEvent.click(await screen.findByLabelText('Actions for posting 1'));
        await userEvent.click(await screen.findByRole('menuitem', { name: /remove split/i }));
        // Down to one posting: the editor swaps to the single-row branch, which
        // has no per-posting controls at all.
        await waitFor(() => expect(screen.queryByLabelText('Posting 1 amount')).toBeNull());
        expect(screen.getByLabelText('Amount')).toBeInTheDocument();
    });

    it('gives focus to "Split this transaction →" when removing a posting collapses the split', async () => {
        // The collapse takes the whole grid away, every leg's kebab and Add
        // split with it; the one-posting layout's add control takes the focus
        // (ADR-0023 §L), never the page.
        const user = userEvent.setup();
        renderEditor(splitMode(2));
        (await screen.findByLabelText('Actions for posting 2')).focus();
        await user.keyboard('{Enter}');
        await user.keyboard('{ArrowUp}{Enter}');

        await waitFor(() => expect(screen.queryByLabelText('Posting 1 amount')).toBeNull());
        expect(screen.getByRole('button', { name: 'Split this transaction →' })).toHaveFocus();
    });
});

describe('the save payload carries every field, not just the two the gate checks', () => {
    it('sends payee, check number and postings on a patch, and notes only as changed', async () => {
        const onSavePatch = vi.fn();
        renderEditor(splitMode(2), { onSavePatch });

        await userEvent.click(await screen.findByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        const body = onSavePatch.mock.calls[0]![0] as PatchTransactionRequest;
        // Every one of these is invisible to the existing gate.
        expect(body.payee).toBe('Corner Shop');
        expect(body.checkNumber).toBe('4471');
        // An untouched save carries the side and nothing else: memo and tags
        // are sent only when they changed.
        expect(body.notes).toEqual({ side: BANK_ID });
        // `postings` is NOT an array — it is { sourceAccountId, items }, and the
        // source account rides inside it. Unguarded, a refactor could drop it and
        // only the server would notice.
        expect(body.postings?.sourceAccountId).toBe(BANK_ID);
        expect(body.postings?.items).toHaveLength(2);
        // A posting carries shape only.
        expect(body.postings?.items?.[0]).toEqual({
            legId: LEG_ID,
            counterpartyAccountId: CATEGORY_ID,
            amount: -10,
        });
        expect(body.postings?.items?.[1]).toMatchObject({ legId: LEG_ID_2, amount: -11 });
    });

    it('sends the transaction memo and a line memo when the user changed them', async () => {
        const onSavePatch = vi.fn();
        renderEditor(splitMode(2), { onSavePatch });

        const memo = await screen.findByLabelText('Memo (whole transaction)');
        fireEvent.change(memo, { target: { value: 'weekly shop' } });
        fireEvent.change(screen.getByLabelText('Posting 2 memo'), { target: { value: 'soap' } });
        await userEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() => expect(onSavePatch).toHaveBeenCalledTimes(1));
        const body = onSavePatch.mock.calls[0]![0] as PatchTransactionRequest;
        expect(body.notes).toEqual({
            side: BANK_ID,
            memo: 'weekly shop',
            lines: [{ index: 1, memo: 'soap' }],
        });
    });

    it('sets approve only when the transaction needed review', async () => {
        const plain = vi.fn();
        renderEditor(splitMode(2), { onSavePatch: plain });
        await userEvent.click(await screen.findByRole('button', { name: /save/i }));
        await waitFor(() => expect(plain).toHaveBeenCalled());
        expect((plain.mock.calls[0]![0] as PatchTransactionRequest).approve).toBeUndefined();

        const needsReview = vi.fn();
        renderEditor(splitMode(2, { needsReview: true }), { onSavePatch: needsReview });
        // The primary action relabels when review is pending.
        await userEvent.click(await screen.findByRole('button', { name: /accept/i }));
        await waitFor(() => expect(needsReview).toHaveBeenCalled());
        expect((needsReview.mock.calls[0]![0] as PatchTransactionRequest).approve).toBe(true);
    });
});

describe('amounts', () => {
    it('accepts a zero amount — paycheque splits carry zero lines', async () => {
        // Deliberately legal on both client and server.
        const onSavePatch = vi.fn();
        renderEditor(splitMode(2), { onSavePatch });

        const amount1 = await screen.findByLabelText('Posting 1 amount');
        fireEvent.change(amount1, { target: { value: '0' } });

        const save = screen.getByRole('button', { name: /save/i });
        expect(save).toBeEnabled();
        await userEvent.click(save);
        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
        expect((onSavePatch.mock.calls[0]![0] as PatchTransactionRequest)
            .postings?.items?.[0]?.amount).toBe(0);
    });

    it('blocks a blank amount and names the posting that is blank', async () => {
        renderEditor(splitMode(3));

        const amount2 = await screen.findByLabelText('Posting 2 amount');
        fireEvent.change(amount2, { target: { value: '' } });

        const save = screen.getByRole('button', { name: /save/i });
        await waitFor(() => expect(save).toBeDisabled());
        // The split branch groups defects by KIND and renders each offending row
        // number as a button that focuses the field — printing each
        // index-prefixed sentence verbatim would turn eight blank amounts into
        // eight clauses wrapping to three lines, over rows that carry no visible
        // number to match them to.
        //
        // The sentence ("Posting 2: amount is required.") is still what the Save
        // button's tooltip shows; it is the accessible name of the button
        // below, which is why this asserts on the name and not on text.
        expect(
            await screen.findByRole('button', { name: /Posting 2: amount is required/i }),
        ).toBeInTheDocument();
        expect(screen.getByText('Enter an amount:')).toBeInTheDocument();
    });

    it('sends focus to the offending field when its row number is clicked', async () => {
        // The reason the summary is buttons and not text. With the list
        // scrolled, "Posting 11" is a row you cannot see, and counting to it
        // is the work the row numbers were supposed to remove.
        renderEditor(splitMode(3));

        const amount3 = await screen.findByLabelText('Posting 3 amount');
        fireEvent.change(amount3, { target: { value: '' } });

        const link = await screen.findByRole('button', { name: /Posting 3: amount is required/i });
        await userEvent.click(link);
        expect(screen.getByLabelText('Posting 3 amount')).toHaveFocus();
    });
});

describe('the outside-pointerdown handler', () => {
    // The gate turns this off, so nothing in the repo exercises it. If the ref
    // and the effect are ever separated, the ref is empty when the effect asks
    // whether the press happened inside, and the editor cancels on its OWN
    // Save click.
    it('does not cancel when the click is inside the editor', async () => {
        const onCancel = vi.fn();
        const onSavePatch = vi.fn();
        renderEditor(splitMode(2), { onCancel, onSavePatch }, { cancelOnOutsideClick: true });

        const save = await screen.findByRole('button', { name: /save/i });
        fireEvent.pointerDown(save);
        await userEvent.click(save);

        expect(onCancel).not.toHaveBeenCalled();
        await waitFor(() => expect(onSavePatch).toHaveBeenCalled());
    });

    it('cancels when the click is outside the editor', async () => {
        const onCancel = vi.fn();
        renderEditor(splitMode(2), { onCancel }, { cancelOnOutsideClick: true });
        await screen.findByRole('button', { name: /save/i });

        fireEvent.pointerDown(document.body);

        await waitFor(() => expect(onCancel).toHaveBeenCalledTimes(1));
    });
});

describe('tax-date normalisation', () => {
    it('treats two DIFFERENT timestamps on the same day as unset', async () => {
        // The existing same-day test feeds byte-identical strings, so a raw
        // `postedAt === transactedAt` comparison passes it. These two differ as
        // strings and must still normalise to the same calendar day.
        //
        // Both are midday UTC so the local calendar day is stable for any
        // offset in [-11, +10] — wide enough for CI without pinning TZ.
        renderEditor(splitMode(1, {
            postedAt: '2026-01-02T12:00:00.000Z',
            transactedAt: '2026-01-02T13:30:00.000Z',
        }));

        const taxDate = await screen.findByLabelText('Tax date');
        expect((taxDate as HTMLInputElement).value).toBe('');
    });

    it('seeds the field when the tax date is a genuinely different day', async () => {
        renderEditor(splitMode(1, {
            postedAt: '2026-01-02T12:00:00.000Z',
            transactedAt: '2026-03-15T12:00:00.000Z',
        }));

        const taxDate = await screen.findByLabelText('Tax date');
        expect((taxDate as HTMLInputElement).value).toBe('2026-03-15');
    });
});

describe('Escape, and what it must not throw away', () => {
    // The worst bug this editor has had. Escape inside an OPEN category picker
    // closed the picker AND cancelled the whole transaction — every leg of a
    // many-line paycheck split discarded because you dismissed a dropdown you opened by
    // mistake. There is no undo and no draft; the edit is simply gone.
    //
    // AccountCategoryPicker already called preventDefault() when it closed its
    // own panel on Escape. This handler never looked at the flag.
    it('does NOT cancel the edit when Escape closes an open category picker', async () => {
        const onCancel = vi.fn();
        renderEditor(splitMode(3), { onCancel });

        const picker = await screen.findByLabelText('Posting 2 category');
        await userEvent.click(picker);
        // The panel is open — the combobox says so.
        await waitFor(() => expect(picker).toHaveAttribute('aria-expanded', 'true'));

        await userEvent.keyboard('{Escape}');

        expect(onCancel).not.toHaveBeenCalled();
        await waitFor(() => expect(picker).toHaveAttribute('aria-expanded', 'false'));
        // And the work is still there.
        expect(screen.getByLabelText('Posting 2 amount')).toBeInTheDocument();
    });

    it('DOES cancel on the second Escape, once the picker is closed', async () => {
        // The guard must not make Escape inert. Once nothing is intercepting
        // it, Escape still means "abandon this edit".
        const onCancel = vi.fn();
        renderEditor(splitMode(3), { onCancel });

        const picker = await screen.findByLabelText('Posting 2 category');
        await userEvent.click(picker);
        await userEvent.keyboard('{Escape}');
        expect(onCancel).not.toHaveBeenCalled();

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('still cancels from a plain field that intercepts nothing', async () => {
        const onCancel = vi.fn();
        renderEditor(splitMode(2), { onCancel });

        await userEvent.click(await screen.findByLabelText('Posting 1 amount'));
        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    // The payee's suggestions follow the category picker's rule: the first
    // Escape closes the list and the edit stays open with what was typed; the
    // second, with no list open, cancels.
    const PAYEES: PayeeSuggestion[] = [
        { name: 'Payee A', count: 2, lastUsedAt: '2026-01-01T00:00:00Z' },
        { name: 'Payee B', count: 1, lastUsedAt: '2026-01-01T00:00:00Z' },
    ];

    it('closes an open payee list on Escape and keeps the edit, with the typed text', async () => {
        const onCancel = vi.fn();
        renderEditor(splitMode(1), { onCancel }, { payees: PAYEES });

        const payee = await screen.findByRole('combobox', { name: 'Payee' });
        await userEvent.clear(payee);
        await userEvent.type(payee, 'Pay');
        expect(screen.getByRole('option', { name: 'Payee B' })).toBeInTheDocument();

        await userEvent.keyboard('{Escape}');

        expect(screen.queryByRole('listbox')).toBeNull();
        expect(onCancel).not.toHaveBeenCalled();
        expect(payee).toHaveFocus();
        expect(payee).toHaveValue('Pay');
    });

    it('cancels on the second Escape, once the payee list is closed', async () => {
        const onCancel = vi.fn();
        renderEditor(splitMode(1), { onCancel }, { payees: PAYEES });

        const payee = await screen.findByRole('combobox', { name: 'Payee' });
        await userEvent.clear(payee);
        await userEvent.type(payee, 'Pay');
        expect(screen.getByRole('option', { name: 'Payee A' })).toBeInTheDocument();
        await userEvent.keyboard('{Escape}');
        expect(onCancel).not.toHaveBeenCalled();

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('cancels from the payee field when nothing it suggests is on screen', async () => {
        // No list, nothing to close: the first Escape is the edit's.
        const onCancel = vi.fn();
        renderEditor(splitMode(1), { onCancel }, { payees: PAYEES });

        const payee = await screen.findByRole('combobox', { name: 'Payee' });
        await userEvent.clear(payee);
        await userEvent.type(payee, 'zzz');
        expect(payee).toHaveValue('zzz');
        expect(screen.queryByRole('listbox')).toBeNull();

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('closes an open tags list on Escape, then cancels on the next one', async () => {
        // The same rule for the tags list. Its filter field goes away with the
        // list, so focus returns to the button that opened it: left on the
        // page, the second Escape would never reach the editor.
        const onCancel = vi.fn();
        renderEditor(splitMode(1), { onCancel });

        const button = await screen.findByLabelText('Edit Tags');
        await userEvent.click(button);
        const filter = await screen.findByLabelText('Filter tags');
        await userEvent.type(filter, 'wo');
        expect(filter).toHaveFocus();

        await userEvent.keyboard('{Escape}');
        expect(screen.queryByLabelText('Filter tags')).toBeNull();
        expect(onCancel).not.toHaveBeenCalled();
        expect(button).toHaveFocus();

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('closes the tags list when Shift+Tab leaves it for the memo', async () => {
        // A list still on screen with focus in another field would take an
        // Escape meant for it to the editor. Leaving the control closes it,
        // so no list is showing when the Escape there cancels.
        const onCancel = vi.fn();
        renderEditor(splitMode(1), { onCancel });

        const button = await screen.findByLabelText('Edit Tags');
        await userEvent.click(button);
        expect(await screen.findByLabelText('Filter tags')).toHaveFocus();

        await userEvent.tab({ shift: true });
        expect(button).toHaveFocus();
        expect(screen.getByLabelText('Filter tags')).toBeInTheDocument();

        await userEvent.tab({ shift: true });
        expect(screen.getByDisplayValue('umbrella memo')).toHaveFocus();
        expect(screen.queryByLabelText('Filter tags')).toBeNull();

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('closes the category list when Tab leaves it for the amount', async () => {
        // The list opens on focus. Still on screen with focus in the amount,
        // it would take the Escape meant for it to the editor. Leaving the
        // control closes it, so no list is showing when the Escape there
        // cancels.
        const onCancel = vi.fn();
        renderEditor(splitMode(1), { onCancel });

        const picker = await screen.findByRole('combobox', { name: 'Category' });
        await userEvent.click(picker);
        expect(await screen.findByRole('listbox')).toBeInTheDocument();
        expect(picker).toHaveAttribute('aria-expanded', 'true');

        await userEvent.tab();
        expect(screen.getByLabelText('Amount')).toHaveFocus();
        expect(picker).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('listbox')).toBeNull();

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('closes the category list on Escape from its filter buttons, then cancels on the next one', async () => {
        // With a money account to transfer to, the list carries
        // All · Accounts · Categories, and Tab from Category reaches them
        // before Amount. An Escape there is the list's, as it is in the input:
        // it closes the list, gives focus back to the input and keeps the edit.
        const onCancel = vi.fn();
        renderEditor(splitMode(1), { onCancel }, {
            accounts: [...ACCOUNTS, account({ id: SAVINGS_ID, name: 'Savings' })],
        });

        const picker = await screen.findByRole('combobox', { name: 'Category' });
        await userEvent.click(picker);
        expect(await screen.findByRole('listbox')).toBeInTheDocument();

        await userEvent.tab();
        expect(screen.getByRole('button', { name: /^All/ })).toHaveFocus();
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await userEvent.keyboard('{Escape}');
        expect(screen.queryByRole('listbox')).toBeNull();
        expect(onCancel).not.toHaveBeenCalled();
        expect(picker).toHaveFocus();
        expect(picker).toHaveAttribute('aria-expanded', 'false');

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('closes a split\'s actions menu on Escape, then cancels on the next one', async () => {
        // The menu holds focus while open. Closing it hands focus back to the
        // button that opened it; dropped on the page instead, the second
        // Escape would never reach the editor.
        const onCancel = vi.fn();
        renderEditor(splitMode(2), { onCancel });

        const opener = await screen.findByLabelText('Actions for posting 1');
        await userEvent.click(opener);
        expect(screen.getByRole('menu')).toHaveFocus();

        await userEvent.keyboard('{Escape}');
        expect(screen.queryByRole('menu')).toBeNull();
        expect(onCancel).not.toHaveBeenCalled();
        expect(opener).toHaveFocus();

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });
});
