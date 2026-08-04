import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
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
function miscDraft(amount: number): InvestmentTxnDraft {
    return {
        brokerageAccountId: ACCOUNT_ID,
        postedAt: '2026-05-01',
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
