import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';
import * as apiModule from '@/lib/api';
import type {
    AccountSummary,
    CreateTransactionRequest,
    PatchTransactionRequest,
    TagDto,
} from '@/lib/types';

/**
 * The bank editor's memo and tags, side by side with the design's states:
 * an own value, an inheriting side ("from X" and the other side's value as
 * placeholder), an own value over an inherited one (the label alone; emptying
 * the field is the way back), the split editor's captions, and the collapse
 * that joins the memos and combines the tags in the fields before Save.
 *
 * The body assertions are on `notes` exactly: PRESENCE is the contract, so an
 * extra key is as wrong as a missing one.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const CHECKING = '00000000-0000-0000-0000-000000000100';
const DINING = '00000000-0000-0000-0000-000000000300';
const SUPPLIES = '00000000-0000-0000-0000-000000000301';
const UNCATEGORIZED = '00000000-0000-0000-0000-000000000302';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';
const LEG_1 = '00000000-0000-0000-0000-000000000bb1';
const LEG_2 = '00000000-0000-0000-0000-000000000bb2';

const account = (over: Partial<AccountSummary> & { id: string; name: string }): AccountSummary => ({
    ledgerId: LEDGER_ID,
    parentId: null,
    accountType: 'category',
    categoryKind: 'expense',
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
    account({ id: CHECKING, name: 'Checking', accountType: 'bank', categoryKind: null }),
    account({ id: DINING, name: 'Dining' }),
    account({ id: SUPPLIES, name: 'Supplies' }),
    // Where a feed row lands until it is categorised; never offered for a pick.
    account({ id: UNCATEGORIZED, name: 'Uncategorized', isSystem: true }),
];

const dict = (names: readonly string[]): TagDto[] =>
    names.map((name, i) => ({ id: `t${i}`, name, color: null, usageCount: 1, transactionCount: 1 }));

/** Dining's side of a one-split Checking entered. */
function diningSide(over: Partial<Extract<TxnRowMode, { kind: 'edit' }>> = {}): TxnRowMode {
    return {
        kind: 'edit',
        headerId: HEADER_ID,
        sourceAccountId: DINING,
        postings: [{
            legId: LEG_1,
            counterpartyAccountId: CHECKING,
            counterpartyAccountName: 'Checking',
            amount: 12,
            legMemo: null,
            legTags: [],
        }],
        payee: 'Corner Cafe',
        memo: null,
        checkNumber: null,
        postedAt: '2026-10-02T00:00:00.000Z',
        transactedAt: null,
        balanceAfter: 200,
        balanceCurrencyCode: 'USD',
        tags: [],
        inherited: { memo: 'Team coffee', tags: ['Work'], fromAccountId: CHECKING },
        needsReview: false,
        ...over,
    };
}

/** Checking's side of a two-line split it owns. */
function checkingSplit(over: Partial<Extract<TxnRowMode, { kind: 'edit' }>> = {}): TxnRowMode {
    return {
        kind: 'edit',
        headerId: HEADER_ID,
        sourceAccountId: CHECKING,
        postings: [
            { legId: LEG_1, counterpartyAccountId: DINING, counterpartyAccountName: 'Dining', amount: -60, legMemo: 'Produce', legTags: ['Organic'] },
            { legId: LEG_2, counterpartyAccountId: SUPPLIES, counterpartyAccountName: 'Supplies', amount: -30, legMemo: null, legTags: [] },
        ],
        payee: 'Grocery Mart',
        memo: 'Weekly shop',
        checkNumber: null,
        postedAt: '2026-10-05T00:00:00.000Z',
        transactedAt: null,
        balanceAfter: 1858,
        balanceCurrencyCode: 'USD',
        tags: ['Household'],
        inherited: null,
        needsReview: false,
        ...over,
    };
}

function renderEditor(mode: TxnRowMode, spies: {
    onSavePatch?: (b: PatchTransactionRequest) => void;
    onSaveCreate?: (b: CreateTransactionRequest) => void;
} = {}) {
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
                isSaving={false}
                saveError={null}
                cancelOnOutsideClick={false}
                {...spies}
            />
        </QueryClientProvider>,
    );
}

/**
 * No control to go back to the other side's value, on either caption: emptying
 * the field is the way back. Called once the editor has settled on a field's
 * value, so the absence is not read off a frame still loading.
 */
function expectNoWayBackControl() {
    expect(screen.queryByRole('button', { name: /^Use\b/i })).not.toBeInTheDocument();
}

async function save(spy: ReturnType<typeof vi.fn>) {
    await userEvent.click(screen.getByRole('button', { name: /^save$/i }));
    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    return spy.mock.calls[0]![0] as PatchTransactionRequest;
}

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchMergeCandidates').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({ accounts: [], categories: [] });
    vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(dict(['Work', 'Travel', 'Reimbursable', 'Household', 'Organic']));
});

describe('a side that inherits', () => {
    it('says where the memo and tags come from, and shows them as placeholder', async () => {
        renderEditor(diningSide());

        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('');
        expect(memo).toHaveAttribute('placeholder', 'Team coffee');
        // "from Checking" on both captions, and the marker inside each field.
        expect(screen.getAllByText('from Checking')).toHaveLength(2);
        expect(screen.getAllByRole('img', { name: 'From Checking' })).toHaveLength(2);
        expect(screen.getByTitle('Work')).toHaveAttribute('data-inherited', 'true');
        expectNoWayBackControl();
    });

    it('tells assistive technology where the shown values come from', async () => {
        renderEditor(diningSide());
        expect(await screen.findByLabelText('Memo')).toHaveAccessibleDescription('from Checking');
        expect(screen.getByRole('button', { name: 'Edit Tags' })).toHaveAccessibleDescription('from Checking');
    });

    it('stops describing the field as the other side\'s once this side has its own', async () => {
        renderEditor(diningSide());
        const memo = await screen.findByLabelText('Memo');
        fireEvent.change(memo, { target: { value: 'C' } });
        expect(memo).not.toHaveAccessibleDescription('from Checking');
    });

    it('sends no notes change when saved untouched', async () => {
        const onSavePatch = vi.fn();
        renderEditor(diningSide(), { onSavePatch });
        await screen.findByLabelText('Memo');

        expect((await save(onSavePatch)).notes).toEqual({ side: DINING });
    });

    it('gives this side its own memo when the user types, and leaves the other side alone', async () => {
        const onSavePatch = vi.fn();
        renderEditor(diningSide(), { onSavePatch });

        const memo = await screen.findByLabelText('Memo');
        fireEvent.change(memo, { target: { value: 'Client lunch' } });
        // The memo caption is the label alone now; the tags still inherit.
        expect(memo).toHaveValue('Client lunch');
        expect(screen.getAllByText('from Checking')).toHaveLength(1);
        expectNoWayBackControl();

        expect((await save(onSavePatch)).notes).toEqual({ side: DINING, memo: 'Client lunch' });
    });

    it('starts the own tag set from the inherited one when a tag is picked', async () => {
        const onSavePatch = vi.fn();
        renderEditor(diningSide(), { onSavePatch });

        await userEvent.click(await screen.findByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Travel' }));

        expect((await save(onSavePatch)).notes).toEqual({ side: DINING, tags: ['Work', 'Travel'] });
    });
});

describe('a side with its own value', () => {
    const own = () => diningSide({ memo: 'Client lunch', tags: ['Reimbursable'] });

    it('shows the label alone on both captions: no "from", and no control to go back', async () => {
        renderEditor(own());
        expect(await screen.findByLabelText('Memo')).toHaveValue('Client lunch');
        expect(screen.getByTitle('Reimbursable')).toBeInTheDocument();
        expect(screen.queryByText('from Checking')).not.toBeInTheDocument();
        expectNoWayBackControl();
    });

    it('emptying the memo and the tags goes back to the other side\'s, captioned "from Checking"', async () => {
        const onSavePatch = vi.fn();
        renderEditor(own(), { onSavePatch });

        fireEvent.change(await screen.findByLabelText('Memo'), { target: { value: '' } });
        await userEvent.click(screen.getByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Reimbursable' }));
        // The first Esc closes the tag list and keeps the edit.
        await userEvent.keyboard('{Escape}');
        await waitFor(() => expect(screen.queryByRole('checkbox', { name: 'Work' })).not.toBeInTheDocument());

        // Both fields read as inheriting again: the other side's value as
        // placeholder, its marker, and where it comes from.
        expect(screen.getByLabelText('Memo')).toHaveAttribute('placeholder', 'Team coffee');
        expect(screen.getAllByText('from Checking')).toHaveLength(2);
        expect(screen.getAllByRole('img', { name: 'From Checking' })).toHaveLength(2);
        expect(screen.getByTitle('Work')).toHaveAttribute('data-inherited', 'true');
        expectNoWayBackControl();

        expect((await save(onSavePatch)).notes).toEqual({ side: DINING, memo: null, tags: [] });
    });

    it('emptying the memo alone sends only the memo\'s clear', async () => {
        const onSavePatch = vi.fn();
        renderEditor(own(), { onSavePatch });

        fireEvent.change(await screen.findByLabelText('Memo'), { target: { value: '' } });
        expect(screen.getByText('from Checking')).toBeInTheDocument();

        expect((await save(onSavePatch)).notes).toEqual({ side: DINING, memo: null });
    });
});

describe('the side a value comes from is the one the draft has', () => {
    // A save keeps the posting's legs and their ids: a changed category moves
    // the category line, with its memo and tags, to the category drafted. So
    // the editor names the drafted side, never the stored one.

    /** Checking's side of a one-split whose category line holds the memo and tags. */
    function checkingSide(over: Partial<Extract<TxnRowMode, { kind: 'edit' }>> = {}): TxnRowMode {
        return {
            kind: 'edit',
            headerId: HEADER_ID,
            sourceAccountId: CHECKING,
            postings: [{
                legId: LEG_1,
                counterpartyAccountId: DINING,
                counterpartyAccountName: 'Dining',
                amount: -12,
                legMemo: null,
                legTags: [],
            }],
            payee: 'Corner Cafe',
            memo: null,
            checkNumber: null,
            postedAt: '2026-10-02T00:00:00.000Z',
            transactedAt: null,
            balanceAfter: 1988,
            balanceCurrencyCode: 'USD',
            tags: [],
            inherited: { memo: 'Team coffee', tags: ['Work'], fromAccountId: DINING },
            needsReview: false,
            ...over,
        };
    }

    it('changing the category names the new one, with the value its line keeps', async () => {
        const user = userEvent.setup();
        renderEditor(checkingSide());
        const memo = await screen.findByLabelText('Memo');
        expect(screen.getAllByText('from Dining')).toHaveLength(2);

        const picker = screen.getByRole('combobox', { name: 'Category' });
        await user.clear(picker);
        await user.type(picker, 'Sup');
        await user.click(await screen.findByRole('option', { name: /Supplies/ }));

        // The caption, the marker in the memo field and the tag control's all
        // name the drafted category; the value is the one its line keeps.
        expect(screen.getAllByText('from Supplies')).toHaveLength(2);
        expect(screen.getAllByRole('img', { name: 'From Supplies' })).toHaveLength(2);
        expect(screen.queryByText('from Dining')).not.toBeInTheDocument();
        expect(memo).toHaveAttribute('placeholder', 'Team coffee');
        expect(screen.getByTitle('Work')).toHaveAttribute('data-inherited', 'true');
    });

    it('a row awaiting review names the category drafted in its field, not the Uncategorized it is stored with', async () => {
        vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([{
            payee: 'Corner Cafe',
            counterpartyAccountId: DINING,
            counterpartyAccountName: 'Dining',
            useCount: 2,
            lastUsedAt: '2026-09-28T12:00:00Z',
        }]);
        renderEditor(checkingSide({
            postings: [{
                legId: LEG_1,
                counterpartyAccountId: UNCATEGORIZED,
                counterpartyAccountName: 'Uncategorized',
                amount: -12,
                legMemo: null,
                legTags: [],
            }],
            inherited: { memo: 'Card purchase', tags: ['Work'], fromAccountId: UNCATEGORIZED },
            needsReview: true,
        }));
        expect(await screen.findAllByText('from Uncategorized')).toHaveLength(2);

        // The Similar hint drafts the category into the field.
        await userEvent.click(await screen.findByTitle(/^Apply payee "Corner Cafe"/));

        expect(screen.getAllByText('from Dining')).toHaveLength(2);
        expect(screen.getAllByRole('img', { name: 'From Dining' })).toHaveLength(2);
        expect(screen.queryByText('from Uncategorized')).not.toBeInTheDocument();
        expect(screen.getByLabelText('Memo')).toHaveAttribute('placeholder', 'Card purchase');
    });

    it('a line that replaced the stored one inherits nothing: the save drops the stored legs with their values', async () => {
        renderEditor(diningSide());
        await userEvent.click(await screen.findByRole('button', { name: /split this transaction/i }));
        await userEvent.click(await screen.findByLabelText('Actions for posting 1'));
        await userEvent.click(await screen.findByRole('menuitem', { name: /remove split/i }));

        // Back on one line, the one added: no placeholder from the other side,
        // no marker and no "from".
        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('');
        expect(memo).toHaveAttribute('placeholder', 'Optional (Shift+Enter for new line)');
        expect(screen.queryByText(/^from /)).not.toBeInTheDocument();
        expect(screen.queryByRole('img', { name: /^From / })).not.toBeInTheDocument();
    });
});

describe('the split editor', () => {
    it('collapses to one line with the memos joined and the tags combined, before Save', async () => {
        const onSavePatch = vi.fn();
        renderEditor(checkingSplit(), { onSavePatch });

        await userEvent.click(await screen.findByLabelText('Actions for posting 2'));
        await userEvent.click(await screen.findByRole('menuitem', { name: /remove split/i }));

        // One posting left: the single-line layout, its fields already holding
        // the joined memo and the combined tags.
        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('Weekly shop · Produce');
        expect(screen.getByTitle('Household, Organic')).toBeInTheDocument();
        // No notice and no Undo.
        expect(screen.queryByText(/joined/i)).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /undo/i })).not.toBeInTheDocument();

        expect((await save(onSavePatch)).notes).toEqual({
            side: CHECKING,
            memo: 'Weekly shop · Produce',
            tags: ['Household', 'Organic'],
        });
    });

    it('after a collapse, a memo and tags edited back to the stored transaction values are still sent', async () => {
        // The server's collapse joins the STORED values when memo and tags are
        // omitted, so a value equal to the old transaction memo is a change
        // from what the collapse would store, and must travel.
        const onSavePatch = vi.fn();
        renderEditor(checkingSplit(), { onSavePatch });

        await userEvent.click(await screen.findByLabelText('Actions for posting 2'));
        await userEvent.click(await screen.findByRole('menuitem', { name: /remove split/i }));

        fireEvent.change(await screen.findByLabelText('Memo'), { target: { value: 'Weekly shop' } });
        await userEvent.click(screen.getByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Organic' }));

        expect((await save(onSavePatch)).notes).toEqual({
            side: CHECKING,
            memo: 'Weekly shop',
            tags: ['Household'],
        });
    });

    it('splitting from an inheriting side leaves the copy to the server', async () => {
        // The single field's value is this side's own; on a split the server
        // copies it to the transaction and to the original line. The editor
        // sends nothing it did not change, and names the side that splits.
        const onSavePatch = vi.fn();
        renderEditor(diningSide(), { onSavePatch });

        await userEvent.click(await screen.findByRole('button', { name: /split this transaction/i }));
        expect(await screen.findByText('Memo (whole transaction)')).toBeInTheDocument();
        fireEvent.change(screen.getByLabelText('Posting 2 amount'), { target: { value: '-1.00' } });
        // A counterparty on the new line, so the save validates.
        const body = await (async () => {
            const picker = screen.getByLabelText('Posting 2 account');
            await userEvent.click(picker);
            await userEvent.click(await screen.findByRole('option', { name: /Supplies/ }));
            return save(onSavePatch);
        })();
        expect(body.notes).toEqual({ side: DINING });
        expect(body.postings?.sourceAccountId).toBe(DINING);
    });
});

describe('the tag limit', () => {
    const many = (n: number) => Array.from({ length: n }, (_, i) => `tag-${String(i).padStart(2, '0')}`);

    it('saves an over-limit set left as it is', async () => {
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(dict(many(25)));
        const onSavePatch = vi.fn();
        renderEditor(diningSide({ tags: many(25), inherited: null }), { onSavePatch });
        await screen.findByLabelText('Memo');

        expect(screen.getByRole('button', { name: /^save$/i })).toBeEnabled();
        expect((await save(onSavePatch)).notes).toEqual({ side: DINING });
    });

    it('refuses to save a set grown past the limit from an inherited one', async () => {
        // Inheriting 25 tags a merge put on the other side. Unpicking one makes
        // an own set of 24 where this side had none: growth past the limit,
        // which the server refuses too.
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(dict(many(25)));
        renderEditor(diningSide({
            inherited: { memo: null, tags: many(25), fromAccountId: CHECKING },
        }));

        await userEvent.click(await screen.findByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'tag-00' }));

        const saveButton = screen.getByRole('button', { name: /^save$/i });
        await waitFor(() => expect(saveButton).toBeDisabled());
        expect(screen.getByText(/can't grow past 20 tags/i)).toBeInTheDocument();
    });
});

describe('the tag limit on a create', () => {
    const many = (n: number) => Array.from({ length: n }, (_, i) => `tag-${String(i).padStart(2, '0')}`);

    it('measures a copied over-limit set from none, as the server does', async () => {
        // A Duplicate of a row a merge left with 25 tags: nothing is stored for
        // the copy, so 25 is growth past the limit.
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(dict(many(25)));
        renderEditor({
            kind: 'new',
            sourceAccountId: CHECKING,
            prefill: { tags: many(25), postings: [{ counterpartyAccountId: DINING, amount: -12 }] },
        }, { onSaveCreate: vi.fn() });

        const saveButton = await screen.findByRole('button', { name: /^save$/i });
        await waitFor(() => expect(saveButton).toBeDisabled());
        expect(screen.getByText(/can't grow past 20 tags/i)).toBeInTheDocument();
    });
});

describe('a stored template edited through the create form', () => {
    // A template's lines carry no leg id and the server rebuilds them, so a
    // save sends every line's memo: a line left out could only be matched to
    // a stored one by position.
    function template(memos: readonly string[] = ['Produce', 'Fuel']): TxnRowMode {
        const categories = [DINING, SUPPLIES, DINING];
        return {
            kind: 'new',
            sourceAccountId: CHECKING,
            prefillIsStored: true,
            prefill: {
                payee: 'Grocery Mart',
                memo: 'Weekly shop',
                postings: memos.map((legMemo, i) => ({
                    counterpartyAccountId: categories[i]!, amount: -10 * (i + 1), legMemo,
                })),
            },
        };
    }

    async function saveCreate(spy: ReturnType<typeof vi.fn>) {
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));
        await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
        return (spy.mock.calls[0]![0] as CreateTransactionRequest).notes;
    }

    it('sends every line\'s memo on an untouched save, and no tags it does not know', async () => {
        const onSaveCreate = vi.fn();
        renderEditor(template(), { onSaveCreate });
        await screen.findByLabelText('Posting 2 memo');
        expect(await saveCreate(onSaveCreate)).toEqual({
            side: CHECKING,
            lines: [{ index: 0, memo: 'Produce' }, { index: 1, memo: 'Fuel' }],
        });
    });

    it('sends a cleared line memo as a clear, next to the line left alone', async () => {
        const onSaveCreate = vi.fn();
        renderEditor(template(), { onSaveCreate });
        fireEvent.change(await screen.findByLabelText('Posting 2 memo'), { target: { value: '' } });
        expect(await saveCreate(onSaveCreate)).toEqual({
            side: CHECKING,
            lines: [{ index: 0, memo: 'Produce' }, { index: 1, memo: null }],
        });
    });

    it('after a middle line is removed, the line that moved up carries its own memo', async () => {
        const onSaveCreate = vi.fn();
        renderEditor(template(['a', 'b', 'c']), { onSaveCreate });

        await userEvent.click(await screen.findByLabelText('Actions for posting 2'));
        await userEvent.click(await screen.findByRole('menuitem', { name: /remove split/i }));
        await waitFor(() => expect(screen.queryByLabelText('Posting 3 memo')).not.toBeInTheDocument());

        expect(await saveCreate(onSaveCreate)).toEqual({
            side: CHECKING,
            lines: [{ index: 0, memo: 'a' }, { index: 1, memo: 'c' }],
        });
    });
});

describe('a create', () => {
    it('sends every value the form holds, as the new transaction\'s own', async () => {
        const onSaveCreate = vi.fn();
        renderEditor({
            kind: 'new',
            sourceAccountId: CHECKING,
            prefill: {
                payee: 'Corner Cafe',
                memo: 'Team coffee',
                tags: ['Work'],
                postings: [{ counterpartyAccountId: DINING, amount: -12 }],
            },
        }, { onSaveCreate });

        const editor = screen.getByRole('row');
        expect(await within(editor).findByLabelText('Memo')).toHaveValue('Team coffee');
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));
        await waitFor(() => expect(onSaveCreate).toHaveBeenCalledTimes(1));
        expect((onSaveCreate.mock.calls[0]![0] as CreateTransactionRequest).notes)
            .toEqual({ side: CHECKING, memo: 'Team coffee', tags: ['Work'] });
    });
});
