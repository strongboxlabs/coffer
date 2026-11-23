import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, createEvent, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { Modal } from '@/components/ui/Modal';

import { InvestmentTxnRowEdit } from './InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from './validation';
import * as apiModule from '@/lib/api';
import type { AccountSummary } from '@/lib/types';

// An investment register edit must not show the old amount on reopen. The
// editor seeds its draft from the ['header-legs', headerId] cache
// (the full cross-account leg set legsToDraft needs), and useInvestmentTxnDraft
// captures `initial` ONCE — so a stale seed survives a reopen (a late refetch
// can't correct it). A save wholesale-replaces the header's legs (ADR-0025), so
// invalidateAfterSave must DROP that cache; otherwise reopening a just-edited
// misc txn re-seeds from the pre-save legs and shows the OLD amount.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000200';
const HOLDINGS_SIBLING_ID = '00000000-0000-0000-0000-000000000201';
const CATEGORY_ID = '00000000-0000-0000-0000-000000000300';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';

const BROKERAGE: AccountSummary = {
    id: ACCOUNT_ID,
    ledgerId: LEDGER_ID,
    parentId: null,
    name: 'Brokerage',
    accountType: 'investment',
    categoryKind: null,
    currencyCode: 'USD',
    isActive: true,
    isSystem: false,
    feedConnectionId: null,
    needsReviewCount: 0,
    holdingsAccountId: HOLDINGS_SIBLING_ID,
    isTradeCommission: false,
};

const RECALLED_CATEGORY_ID = '00000000-0000-0000-0000-000000000301';

const CATEGORY: AccountSummary = {
    id: CATEGORY_ID,
    ledgerId: LEDGER_ID,
    parentId: null,
    name: 'Bank Fees',
    accountType: 'category',
    categoryKind: 'expense',
    currencyCode: 'USD',
    isActive: true,
    isSystem: false,
    feedConnectionId: null,
    needsReviewCount: 0,
    holdingsAccountId: null,
    isTradeCommission: false,
};

// A valid `misc` draft: non-zero amount + a category (misc layout is
// [security?, amount, category, fee?]; security is optional for misc).
/** The category a recall chip suggests — deliberately NOT the one the draft
 *  starts on, so applying it is observable. */
const RECALLED_CATEGORY: AccountSummary = {
    ...CATEGORY,
    id: RECALLED_CATEGORY_ID,
    name: 'Dividend Income',
    categoryKind: 'income',
};

function miscDraft(amount: number): InvestmentTxnDraft {
    return {
        brokerageAccountId: ACCOUNT_ID,
        postedAt: '2026-05-01',
        transactedAt: '',
        action: 'misc',
        payee: '',
        memo: '',
        checkNumber: '',
        securityId: null,
        shares: null,
        price: null,
        amount,
        categoryAccountId: CATEGORY_ID,
        transferAccountId: null,
        feeAccountId: null,
        feeAmount: null,
        tags: [],
    };
}

describe('InvestmentTxnRowEdit — post-save seed cache', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue({
            accountId: ACCOUNT_ID,
            accountName: 'Brokerage',
            currencyCode: 'USD',
            summary: {
                portfolioValue: 0,
                costBasis: 0,
                unrealizedGain: 0,
                percentChange: 0,
                cashBalance: 0,
                total: 0,
            },
            positions: [],
        });
        vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({
            accounts: [],
            categories: [],
        });
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
    });

    it('drops the header-legs seed cache after a save so the next open re-fetches the saved legs', async () => {
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue([]);
        const onSaved = vi.fn();

        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false } },
        });
        // The stale seed the editor would re-read on reopen (the register page's
        // editingContext builds `initialDraft` from this cache).
        queryClient.setQueryData(
            ['header-legs', LEDGER_ID, HEADER_ID],
            [{ accountId: ACCOUNT_ID, amount: -50 }],
        );

        render(
            <QueryClientProvider client={queryClient}>
                <InvestmentTxnRowEdit
                    ledgerId={LEDGER_ID}
                    brokerageAccountId={ACCOUNT_ID}
                    accounts={[BROKERAGE, CATEGORY]}
                    isTradeCommission={false}
                    cols="1fr"
                    onCancel={() => {}}
                    mode={{
                        kind: 'edit',
                        headerId: HEADER_ID,
                        initialDraft: miscDraft(-75),
                        onSaved,
                    }}
                />
            </QueryClientProvider>,
        );

        // Seed present before the save.
        expect(
            queryClient.getQueryData(['header-legs', LEDGER_ID, HEADER_ID]),
        ).toBeDefined();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /^Save$/ }));

        await waitFor(() => expect(patchSpy).toHaveBeenCalled());
        await waitFor(() => expect(onSaved).toHaveBeenCalled());

        // The header's seed cache is dropped, so a reopen re-fetches
        // the saved legs rather than re-seeding from the pre-save amount.
        expect(
            queryClient.getQueryData(['header-legs', LEDGER_ID, HEADER_ID]),
        ).toBeUndefined();
    });
});

describe('InvestmentTxnRowEdit — parity with the bank editor', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({
            accounts: [],
            categories: [],
        });
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
    });

    function renderEditor(extra: Record<string, unknown> = {}) {
        const onCancel = vi.fn();
        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false } },
        });
        render(
            <QueryClientProvider client={queryClient}>
                <InvestmentTxnRowEdit
                    ledgerId={LEDGER_ID}
                    brokerageAccountId={ACCOUNT_ID}
                    accounts={[BROKERAGE, CATEGORY, RECALLED_CATEGORY]}
                    isTradeCommission={false}
                    cols="1fr"
                    onCancel={onCancel}
                    mode={{
                        kind: 'edit',
                        headerId: HEADER_ID,
                        initialDraft: miscDraft(-75),
                        onSaved: vi.fn(),
                    }}
                    {...extra}
                />
            </QueryClientProvider>,
        );
        return { onCancel };
    }

    it('cancels on Escape, as a bank row does', () => {
        const { onCancel } = renderEditor();

        fireEvent.keyDown(screen.getByLabelText('Date'), { key: 'Escape' });

        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('does not cancel on Escape while its save is pending, and does once the save has failed', async () => {
        // Cancel is pending while the save is in flight and refuses a press.
        // The pending Save keeps focus in the editor, so Escape (Cancel's
        // shortcut) reaches the editor then too, and must refuse as well.
        let refuse: (e: Error) => void = () => {};
        vi.spyOn(apiModule, 'patchInvestmentTransaction').mockImplementation(
            () => new Promise((_resolve, reject) => { refuse = reject; }));
        const { onCancel } = renderEditor();
        const user = userEvent.setup();

        const save = await screen.findByRole('button', { name: /^Save/ });
        await user.click(save);
        await waitFor(() => expect(save).toHaveAttribute('aria-busy', 'true'));
        expect(save).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(onCancel).not.toHaveBeenCalled();

        await act(async () => { refuse(new Error('Refused.')); });
        await waitFor(() => expect(save).not.toHaveAttribute('aria-busy'));
        await user.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    it('keeps the dialog it sits in open on Escape while the host saves', async () => {
        const onCancel = vi.fn();
        const onClose = vi.fn();
        render(
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
                <Modal open onClose={onClose} ariaLabel="Post occurrence">
                    <InvestmentTxnRowEdit
                        ledgerId={LEDGER_ID}
                        brokerageAccountId={ACCOUNT_ID}
                        accounts={[BROKERAGE, CATEGORY]}
                        isTradeCommission={false}
                        layout="dialog"
                        submitLabel="Post"
                        submittingLabel="Posting…"
                        isSaving
                        onCancel={onCancel}
                        mode={{ kind: 'fire', initialDraft: miscDraft(-75), onSubmit: vi.fn() }}
                    />
                </Modal>
            </QueryClientProvider>,
        );
        const post = await screen.findByRole('button', { name: /posting/i });
        post.focus();

        await userEvent.setup().keyboard('{Escape}');

        expect(onCancel).not.toHaveBeenCalled();
        expect(onClose).not.toHaveBeenCalled();
    });

    it('leaves the transaction alone when a child already handled Escape', () => {
        // The guard the bank editor documents: a picker that closed its own panel
        // marks the event, and without the check the same keystroke would close
        // the panel and then discard everything behind it.
        const { onCancel } = renderEditor();

        // defaultPrevented is derived from a real preventDefault() call, not
        // settable through the event init — so build the event and mark it the
        // way a child picker would.
        const date = screen.getByLabelText('Date');
        const escaped = createEvent.keyDown(date, { key: 'Escape' });
        escaped.preventDefault();
        fireEvent(date, escaped);

        expect(onCancel).not.toHaveBeenCalled();
    });

    // The security search follows the category picker's rule: the first Escape
    // closes its list and the edit stays open with what was typed; the second,
    // with no list open, cancels.
    describe('Escape in the security search', () => {
        const SECURITY = {
            id: '00000000-0000-0000-0000-0000000004ec', ticker: 'TSTA', cusip: null,
            name: 'Test Fund A', assetClass: null, exchange: null, isActive: true,
            totalQuantity: 0, latestPrice: null, latestPriceAsOf: null, priceCurrencyCode: 'USD',
        };

        it('closes the open list and keeps the edit, with the typed text', async () => {
            vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([SECURITY]);
            const { onCancel } = renderEditor();

            const security = await screen.findByRole('combobox', { name: 'Security' });
            await userEvent.type(security, 'TST');
            expect(await screen.findByRole('option', { name: 'TSTA · Test Fund A' })).toBeInTheDocument();

            await userEvent.keyboard('{Escape}');

            expect(screen.queryByRole('listbox')).toBeNull();
            expect(onCancel).not.toHaveBeenCalled();
            expect(security).toHaveFocus();
            expect(security).toHaveValue('TST');
        });

        it('cancels on the second Escape, once the list is closed', async () => {
            vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([SECURITY]);
            const { onCancel } = renderEditor();

            const security = await screen.findByRole('combobox', { name: 'Security' });
            await userEvent.type(security, 'TST');
            expect(await screen.findByRole('option', { name: 'TSTA · Test Fund A' })).toBeInTheDocument();
            await userEvent.keyboard('{Escape}');
            expect(onCancel).not.toHaveBeenCalled();

            await userEvent.keyboard('{Escape}');
            expect(onCancel).toHaveBeenCalledTimes(1);
        });
    });

    it('closes an open payee list on Escape, then cancels on the next one', async () => {
        vi.spyOn(apiModule, 'fetchPayees').mockResolvedValue([
            { name: 'Payee A', count: 1, lastUsedAt: '2026-01-01T00:00:00Z' },
        ]);
        const { onCancel } = renderEditor();

        const payee = await screen.findByRole('combobox', { name: 'Payee' });
        await userEvent.type(payee, 'Pay');
        expect(await screen.findByRole('option', { name: 'Payee A' })).toBeInTheDocument();

        await userEvent.keyboard('{Escape}');
        expect(screen.queryByRole('listbox')).toBeNull();
        expect(onCancel).not.toHaveBeenCalled();
        expect(payee).toHaveValue('Pay');

        await userEvent.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
    });

    // The category pickers open on focus. Still on screen with focus in
    // another field, a list would take the Escape meant for it to the editor.
    // Leaving the control closes it, so no list is showing when that Escape
    // cancels.
    describe('a category list when focus leaves it', () => {
        it('closes when Shift+Tab leaves Category for Amount', async () => {
            const { onCancel } = renderEditor();

            const picker = screen.getByRole('combobox', { name: 'Category' });
            await userEvent.click(picker);
            expect(await screen.findByRole('listbox')).toBeInTheDocument();
            expect(picker).toHaveAttribute('aria-expanded', 'true');

            await userEvent.tab({ shift: true });
            expect(screen.getByLabelText('Amount')).toHaveFocus();
            expect(picker).toHaveAttribute('aria-expanded', 'false');
            expect(screen.queryByRole('listbox')).toBeNull();

            await userEvent.keyboard('{Escape}');
            expect(onCancel).toHaveBeenCalledTimes(1);
        });

        it('closes when Tab leaves the fee category for the fee amount', async () => {
            const { onCancel } = renderEditor();

            const picker = screen.getByRole('combobox', { name: 'Fee category' });
            await userEvent.click(picker);
            expect(await screen.findByRole('listbox')).toBeInTheDocument();
            expect(picker).toHaveAttribute('aria-expanded', 'true');

            await userEvent.tab();
            expect(screen.getByLabelText('Fee amount')).toHaveFocus();
            expect(picker).toHaveAttribute('aria-expanded', 'false');
            expect(screen.queryByRole('listbox')).toBeNull();

            await userEvent.keyboard('{Escape}');
            expect(onCancel).toHaveBeenCalledTimes(1);
        });
    });

    it('focuses the date field on open', () => {
        renderEditor();

        expect(document.activeElement).toBe(screen.getByLabelText('Date'));
    });

    it('saves on Enter from the memo field', async () => {
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue([]);
        renderEditor();

        fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

        await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
    });

    // A key from a dialog the editor opened is that dialog's (Add security:
    // Enter there submits its form). The editor may sit in a dialog itself, as
    // it does in Review and the reminder dialogs; that dialog holds the editor,
    // and its keys are still the editor's.
    describe('a key in a dialog', () => {
        function renderInDialog() {
            const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
            render(
                <QueryClientProvider client={queryClient}>
                    <div role="dialog" aria-label="Host dialog">
                        <InvestmentTxnRowEdit
                            ledgerId={LEDGER_ID}
                            brokerageAccountId={ACCOUNT_ID}
                            accounts={[BROKERAGE, CATEGORY]}
                            isTradeCommission={false}
                            layout="dialog"
                            onCancel={vi.fn()}
                            mode={{ kind: 'edit', headerId: HEADER_ID, initialDraft: miscDraft(-75), onSaved: vi.fn() }}
                        />
                    </div>
                </QueryClientProvider>,
            );
        }

        // Amount has no Enter of its own, so the editor's own handler decides.
        it('saves on Enter from a field of an editor that sits in a dialog', async () => {
            const patchSpy = vi.spyOn(apiModule, 'patchInvestmentTransaction').mockResolvedValue([]);
            renderInDialog();

            fireEvent.keyDown(screen.getByLabelText('Amount'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
        });

        it('leaves Enter in Add security to that dialog, with the editor in a dialog of its own', async () => {
            const createSpy = vi.spyOn(apiModule, 'createSecurity')
                .mockResolvedValue({ securityId: '00000000-0000-0000-0000-0000000005e1' });
            const patchSpy = vi.spyOn(apiModule, 'patchInvestmentTransaction').mockResolvedValue([]);
            const user = userEvent.setup();
            renderInDialog();

            await user.type(await screen.findByRole('combobox', { name: 'Security' }), 'ZZQQ');
            await user.click(await screen.findByText('+ Create new security "ZZQQ"'));
            const added = await screen.findByRole('dialog', { name: 'Add security' });
            await user.type(within(added).getByPlaceholderText('e.g. Broad Market Index Fund'), 'Synthetic Fund{Enter}');

            await waitFor(() => expect(createSpy).toHaveBeenCalledTimes(1));
            expect(patchSpy).not.toHaveBeenCalled();
        });
    });

    it('does not save on Shift+Enter', () => {
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue([]);
        renderEditor();

        fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter', shiftKey: true });

        expect(patchSpy).not.toHaveBeenCalled();
    });

    // Tags (ADR-0009). Without a field on this editor, the only route to a
    // tagged investment header would be the MCP bulk tool.
    describe('tags', () => {
        it('seeds the tag editor from the row, and an untouched save leaves the tags alone', async () => {
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue([]);
            renderEditor({
                mode: {
                    kind: 'edit',
                    headerId: HEADER_ID,
                    initialDraft: { ...miscDraft(-75), tags: ['roth'] },
                    onSaved: vi.fn(),
                },
            });

            // Seeded: the chip for the row's existing tag is on screen.
            expect(await screen.findByText('roth')).toBeInTheDocument();

            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            // Tags (and memo) follow presence on the PATCH: unchanged means
            // omitted, which the server reads as "leave them".
            const body = patchSpy.mock.calls[0]![2];
            expect(body.action).toBe('misc');
            expect('tags' in body).toBe(false);
            expect('memo' in body).toBe(false);
        });

        it('sends [] rather than omitting the field when every tag is removed', async () => {
            // The server reads an omitted `tags` as "leave them alone", so
            // omitting an emptied list would make a tag impossible to remove
            // from this surface — the save would look like it worked.

            // The dictionary the FK guarantees: txn_header_tags.tag_id is a
            // composite FK into tags(id, ledger_id), so an applied tag is
            // always IN the ledger's tag list. Leaving fetchTags unmocked made
            // this fixture a state the database cannot produce.
            vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([
                { id: 'tag-roth', name: 'roth', color: null, usageCount: 1, transactionCount: 1 },
            ]);
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue([]);
            renderEditor({
                mode: {
                    kind: 'edit',
                    headerId: HEADER_ID,
                    initialDraft: { ...miscDraft(-75), tags: ['roth'] },
                    onSaved: vi.fn(),
                },
            });

            // Removal is unchecking now, not a chip's ×: ADR-0103 put one
            // control on every tag surface, and a chip strip cannot live in a
            // 30px leg row. The contract under test is unchanged.

            fireEvent.click(await screen.findByRole('button', { name: /edit tags/i }));
            fireEvent.click(await screen.findByRole('checkbox', { name: 'roth' }));
            await waitFor(() =>
                expect(screen.getByRole('checkbox', { name: 'roth' })).not.toBeChecked());

            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            const body = patchSpy.mock.calls[0]![2] as { tags?: readonly string[] };
            expect(body.tags).toEqual([]);
        });
    });

    // Similar-payees recall. The endpoint always worked for a brokerage row —
    // a feed row lands bank-shape wherever it is bound — but this editor never
    // called it, so the one surface where the suggestion is most repetitive
    // (a quarterly dividend) was the one that never offered it.
    describe('similar-payees recall', () => {
        const SUGGESTION = {
            payee: 'Company A Dividend',
            counterpartyAccountId: RECALLED_CATEGORY.id,
            counterpartyAccountName: RECALLED_CATEGORY.name,
            useCount: 3,
            lastUsedAt: '2026-04-01T12:00:00Z',
        };

        it('applies the recalled payee AND category to the draft', async () => {
            vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([SUGGESTION]);
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue([]);
            renderEditor();

            const chip = await screen.findByRole(
                'button', { name: /Company A Dividend/i });
            fireEvent.click(chip);

            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            // Both halves of the pair, not just the payee — a chip that moved
            // only the name would look like it had half-failed.
            expect(patchSpy.mock.calls[0]![2]).toMatchObject({
                payee: 'Company A Dividend',
                categoryAccountId: RECALLED_CATEGORY.id,
            });
        });

        it('recalls the payee alone on an action with no category slot', async () => {
            // A buy's layout is [security, shares, price, amount, fee] — there
            // is nowhere to put the counterparty half. The chip is still
            // offered, because the PAYEE is the repeated thing and applies
            // whatever the shape; it just drops its second half rather than
            // claiming to set something it will skip.
            vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([SUGGESTION]);
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue([]);
            renderEditor({
                mode: {
                    kind: 'edit',
                    headerId: HEADER_ID,
                    // A VALID buy — the matrix requires security/shares/price,
                    // and Enter refuses to save an invalid draft, so a bare
                    // action swap would time out rather than assert anything.
                    initialDraft: {
                        ...miscDraft(-75),
                        action: 'buy' as const,
                        securityId: '00000000-0000-0000-0000-0000000004ec',
                        shares: 2,
                        price: 10,
                        amount: 20,
                        categoryAccountId: null,
                    },
                    onSaved: vi.fn(),
                },
            });

            const chip = await screen.findByRole(
                'button', { name: /Company A Dividend/i });
            // The counterparty is not named, because it will not be applied.
            expect(chip).not.toHaveTextContent(RECALLED_CATEGORY.name);

            fireEvent.click(chip);
            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            const body = patchSpy.mock.calls[0]![2] as {
                payee?: string | null; categoryAccountId?: string | null;
            };
            expect(body.payee).toBe('Company A Dividend');
            // Anchored on a null START value, so "did not apply" is a real
            // observation rather than the fixture agreeing with itself.
            expect(body.categoryAccountId).toBeNull();
        });

        it('recalls a payee whose counterparty is a Holdings sub-account', async () => {
            // The case: the row carrying the curated name is
            // a settled BUY, whose counterparty is the structural Holdings
            // sibling (ADR-0019). Dropping such suggestions server-side to
            // avoid an unusable "-> Holdings" chip also dropped the payee, so
            // recall went silent on exactly the brokerage rows that had a name
            // worth reusing.
            vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([{
                ...SUGGESTION,
                counterpartyAccountId: HOLDINGS_SIBLING_ID,
                counterpartyAccountName: 'Brokerage Holdings',
            }]);
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue([]);
            renderEditor();   // action 'misc' — HAS a category slot

            const chip = await screen.findByRole(
                'button', { name: /Company A Dividend/i });
            expect(chip).not.toHaveTextContent('Brokerage Holdings');

            fireEvent.click(chip);
            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            const body = patchSpy.mock.calls[0]![2] as {
                payee?: string | null; categoryAccountId?: string | null;
            };
            expect(body.payee).toBe('Company A Dividend');
            // A category slot EXISTS here, so this pins the Holdings check
            // rather than the action check: the draft's own category stands.
            expect(body.categoryAccountId).toBe(CATEGORY.id);
        });
    });

    it('refuses a second Post while the HOST is saving', async () => {
        // mode 'fire': the host owns the request, so the editor's own mutation
        // never runs and `mutation.isPending` is permanently false. Without the
        // isSaving prop every control stayed live through the POST and a second
        // click issued a second one. Post is pending then, not disabled: it
        // keeps focus and refuses the click (pending.ts).
        const onSubmit = vi.fn();
        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false } },
        });
        render(
            <QueryClientProvider client={queryClient}>
                <InvestmentTxnRowEdit
                    ledgerId={LEDGER_ID}
                    brokerageAccountId={ACCOUNT_ID}
                    accounts={[BROKERAGE, CATEGORY]}
                    isTradeCommission={false}
                    cols="1fr"
                    submitLabel="Post"
                    submittingLabel="Posting…"
                    isSaving
                    onCancel={vi.fn()}
                    mode={{ kind: 'fire', initialDraft: miscDraft(-75), onSubmit }}
                />
            </QueryClientProvider>,
        );

        const post = await screen.findByRole('button', { name: /posting/i });
        expect(post).toHaveAttribute('aria-disabled', 'true');
        expect(post).toBeEnabled();
        fireEvent.click(post);
        fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });
        expect(onSubmit).not.toHaveBeenCalled();
    });

    it.each([
        // A host's create (a reminder from a transaction, an occurrence) stores
        // nothing yet, so a copied over-limit set is growth from none.
        { stored: false, enabled: false },
        // A stored template kept at its size saves, as an edit does.
        { stored: true, enabled: true },
    ])('measures the tag limit from what is stored (stored: $stored)', async ({ stored, enabled }) => {
        const many = Array.from({ length: 25 }, (_, i) => `t${i}`);
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={queryClient}>
                <InvestmentTxnRowEdit
                    ledgerId={LEDGER_ID}
                    brokerageAccountId={ACCOUNT_ID}
                    accounts={[BROKERAGE, CATEGORY]}
                    isTradeCommission={false}
                    cols="1fr"
                    submitLabel="Post"
                    onCancel={vi.fn()}
                    isSaving={false}
                    mode={{ kind: 'fire', initialDraft: { ...miscDraft(-75), tags: many }, initialIsStored: stored, onSubmit: vi.fn() }}
                />
            </QueryClientProvider>,
        );

        const post = await screen.findByRole('button', { name: /^post$/i });
        await waitFor(() => expect((post as HTMLButtonElement).disabled).toBe(!enabled));
    });
});

describe('InvestmentTxnRowEdit — a brokerage that inherits the other side\'s memo and tags', () => {
    // A one-posting row on the brokerage whose memo and tags were entered on
    // the bank side: the brokerage's own fields are blank, and the editor says
    // where the shown value comes from.
    const CHECKING_ID = '00000000-0000-0000-0000-000000000100';
    const CHECKING: AccountSummary = {
        ...BROKERAGE,
        id: CHECKING_ID,
        name: 'Checking',
        accountType: 'bank',
        holdingsAccountId: null,
    };
    const SAVINGS_ID = '00000000-0000-0000-0000-000000000101';
    const SAVINGS: AccountSummary = { ...CHECKING, id: SAVINGS_ID, name: 'Savings' };

    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({ accounts: [], categories: [] });
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([
            { id: 't1', name: 'Retirement', color: null, usageCount: 1, transactionCount: 1 },
        ]);
    });

    function transferDraft(over: Partial<InvestmentTxnDraft> = {}): InvestmentTxnDraft {
        return {
            ...miscDraft(500),
            action: 'transfer',
            categoryAccountId: null,
            transferAccountId: CHECKING_ID,
            ...over,
        };
    }

    function renderInheriting(initialDraft: InvestmentTxnDraft, accounts: AccountSummary[] = [BROKERAGE, CHECKING]) {
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={queryClient}>
                <InvestmentTxnRowEdit
                    ledgerId={LEDGER_ID}
                    brokerageAccountId={ACCOUNT_ID}
                    accounts={accounts}
                    isTradeCommission={false}
                    cols="1fr"
                    onCancel={() => {}}
                    mode={{
                        kind: 'edit',
                        headerId: HEADER_ID,
                        initialDraft,
                        inherited: {
                            memo: 'Monthly', tags: ['Retirement'], fromAccountId: CHECKING_ID, fromRole: 'transfer',
                        },
                        onSaved: vi.fn(),
                    }}
                />
            </QueryClientProvider>,
        );
    }

    it('shows the inherited memo and tags as placeholder, captioned "from Checking"', async () => {
        renderInheriting(transferDraft());

        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('');
        expect(memo).toHaveAttribute('placeholder', 'Monthly');
        expect(screen.getAllByText('from Checking')).toHaveLength(2);
        expect(screen.getByTitle('Retirement')).toHaveAttribute('data-inherited', 'true');
    });

    it('an untouched save sends neither memo nor tags', async () => {
        const patchSpy = vi.spyOn(apiModule, 'patchInvestmentTransaction').mockResolvedValue([]);
        renderInheriting(transferDraft());

        fireEvent.keyDown(await screen.findByLabelText('Memo'), { key: 'Enter' });

        await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
        const body = patchSpy.mock.calls[0]![2];
        expect(body.action).toBe('transfer');
        expect('memo' in body).toBe(false);
        expect('tags' in body).toBe(false);
    });

    it('an own memo shows the label alone, and emptying it goes back to Checking\'s', async () => {
        const patchSpy = vi.spyOn(apiModule, 'patchInvestmentTransaction').mockResolvedValue([]);
        renderInheriting(transferDraft({ memo: 'Top-up' }));

        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('Top-up');
        // Only the tags caption inherits; the memo's is the label alone, with
        // no control to go back.
        expect(screen.getAllByText('from Checking')).toHaveLength(1);
        expect(screen.queryByRole('button', { name: /^Use\b/i })).not.toBeInTheDocument();

        fireEvent.change(memo, { target: { value: '' } });
        expect(memo).toHaveAttribute('placeholder', 'Monthly');
        expect(screen.getAllByText('from Checking')).toHaveLength(2);
        expect(screen.queryByRole('button', { name: /^Use\b/i })).not.toBeInTheDocument();

        fireEvent.keyDown(memo, { key: 'Enter' });
        await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
        expect(patchSpy.mock.calls[0]![2].memo).toBeNull();
    });

    it('emptying own tags goes back to Checking\'s, with no control to do it', async () => {
        const patchSpy = vi.spyOn(apiModule, 'patchInvestmentTransaction').mockResolvedValue([]);
        renderInheriting(transferDraft({ tags: ['Retirement'] }));

        await screen.findByLabelText('Memo');
        expect(screen.getByTitle('Retirement')).not.toHaveAttribute('data-inherited', 'true');
        expect(screen.getAllByText('from Checking')).toHaveLength(1);
        expect(screen.queryByRole('button', { name: /^Use\b/i })).not.toBeInTheDocument();

        await userEvent.click(screen.getByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Retirement' }));
        expect(screen.getAllByText('from Checking')).toHaveLength(2);
        expect(screen.queryByRole('button', { name: /^Use\b/i })).not.toBeInTheDocument();

        fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });
        await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
        expect(patchSpy.mock.calls[0]![2].tags).toEqual([]);
    });

    it('a transfer drafted to another account inherits nothing: the save replaces the leg the value is on', async () => {
        // An investment save rebuilds the event's legs and keeps Checking's
        // only while the transfer is still to Checking; a new leg carries no
        // memo or tags.
        const user = userEvent.setup();
        renderInheriting(transferDraft(), [BROKERAGE, CHECKING, SAVINGS]);
        const memo = await screen.findByLabelText('Memo');
        expect(screen.getAllByText('from Checking')).toHaveLength(2);

        const picker = screen.getByRole('combobox', { name: 'Transfer destination' });
        await user.clear(picker);
        await user.type(picker, 'Sav');
        await user.click(await screen.findByRole('option', { name: /Savings/ }));

        expect(memo).toHaveAttribute('placeholder', '(optional)');
        expect(screen.queryByText(/^from /)).not.toBeInTheDocument();
        expect(screen.queryByRole('img', { name: /^From / })).not.toBeInTheDocument();

        // Back to Checking, its leg is kept, and the value with it.
        await user.clear(picker);
        await user.type(picker, 'Che');
        await user.click(await screen.findByRole('option', { name: /Checking/ }));
        expect(memo).toHaveAttribute('placeholder', 'Monthly');
        expect(screen.getAllByText('from Checking')).toHaveLength(2);
        expect(screen.getAllByRole('img', { name: 'From Checking' })).toHaveLength(2);
    });
});

