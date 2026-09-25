import { describe, it, expect, beforeEach, vi } from 'vitest';
import { createEvent, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { InvestmentTxnRowEdit } from './InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from './validation';
import * as apiModule from '@/lib/api';
import type { AccountSummary } from '@/lib/types';

// Regression for the "investment register edit shows the old amount on reopen"
// bug. The editor seeds its draft from the ['header-legs', headerId] cache
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
            .mockResolvedValue(null);
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

        // The fix: the header's seed cache is dropped, so a reopen re-fetches
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

    it('focuses the date field on open', () => {
        renderEditor();

        expect(document.activeElement).toBe(screen.getByLabelText('Date'));
    });

    it('saves on Enter from the memo field', async () => {
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue(null);
        renderEditor();

        fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

        await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
    });

    it('does not save on Shift+Enter', () => {
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue(null);
        renderEditor();

        fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter', shiftKey: true });

        expect(patchSpy).not.toHaveBeenCalled();
    });

    // Tags (ADR-0009) had no field on this editor at all, so the only route to
    // a tagged investment header was the MCP bulk tool — and the register
    // blanked them on the way back out.
    describe('tags', () => {
        it('seeds the tag editor from the row and sends the set on save', async () => {
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue(null);
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
            expect(patchSpy.mock.calls[0]![2]).toMatchObject({ tags: ['roth'] });
        });

        it('sends [] rather than omitting the field when every tag is removed', async () => {
            // The server reads an omitted `tags` as "leave them alone", so
            // omitting an emptied list would make a tag impossible to remove
            // from this surface — the save would look like it worked.
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue(null);
            renderEditor({
                mode: {
                    kind: 'edit',
                    headerId: HEADER_ID,
                    initialDraft: { ...miscDraft(-75), tags: ['roth'] },
                    onSaved: vi.fn(),
                },
            });

            const remove = await screen.findByRole('button', { name: /remove tag roth/i });
            fireEvent.click(remove);
            await waitFor(() => expect(screen.queryByText('roth')).toBeNull());

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
            payee: 'Acme Corp Dividend',
            counterpartyAccountId: RECALLED_CATEGORY.id,
            counterpartyAccountName: RECALLED_CATEGORY.name,
            useCount: 3,
            lastUsedAt: '2026-04-01T12:00:00Z',
        };

        it('applies the recalled payee AND category to the draft', async () => {
            vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([SUGGESTION]);
            const patchSpy = vi
                .spyOn(apiModule, 'patchInvestmentTransaction')
                .mockResolvedValue(null);
            renderEditor();

            const chip = await screen.findByRole(
                'button', { name: /Acme Corp Dividend/i });
            fireEvent.click(chip);

            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            // Both halves of the pair, not just the payee — a chip that moved
            // only the name would look like it had half-failed.
            expect(patchSpy.mock.calls[0]![2]).toMatchObject({
                payee: 'Acme Corp Dividend',
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
                .mockResolvedValue(null);
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
                'button', { name: /Acme Corp Dividend/i });
            // The counterparty is not named, because it will not be applied.
            expect(chip).not.toHaveTextContent(RECALLED_CATEGORY.name);

            fireEvent.click(chip);
            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            const body = patchSpy.mock.calls[0]![2] as {
                payee?: string | null; categoryAccountId?: string | null;
            };
            expect(body.payee).toBe('Acme Corp Dividend');
            // Anchored on a null START value, so "did not apply" is a real
            // observation rather than the fixture agreeing with itself.
            expect(body.categoryAccountId).toBeNull();
        });

        it('recalls a payee whose counterparty is a Holdings sub-account', async () => {
            // The case from the dev rig: the row carrying the curated name was
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
                .mockResolvedValue(null);
            renderEditor();   // action 'misc' — HAS a category slot

            const chip = await screen.findByRole(
                'button', { name: /Acme Corp Dividend/i });
            expect(chip).not.toHaveTextContent('Brokerage Holdings');

            fireEvent.click(chip);
            fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });

            await waitFor(() => expect(patchSpy).toHaveBeenCalledTimes(1));
            const body = patchSpy.mock.calls[0]![2] as {
                payee?: string | null; categoryAccountId?: string | null;
            };
            expect(body.payee).toBe('Acme Corp Dividend');
            // A category slot EXISTS here, so this pins the Holdings check
            // rather than the action check: the draft's own category stands.
            expect(body.categoryAccountId).toBe(CATEGORY.id);
        });
    });

    it('disables its controls while the HOST is saving', async () => {
        // mode 'fire': the host owns the request, so the editor's own mutation
        // never runs and `mutation.isPending` is permanently false. Without the
        // isSaving prop every control stayed live through the POST and a second
        // click issued a second one.
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
        expect((post as HTMLButtonElement).disabled).toBe(true);
        fireEvent.click(post);
        expect(onSubmit).not.toHaveBeenCalled();
    });
});

