import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { EditorDialog } from '@/components/ui/EditorDialog';
import { InvestmentTxnRowEdit } from './InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from './validation';
import * as apiModule from '@/lib/api';
import type { AccountSummary } from '@/lib/types';

/**
 * The investment editor in a dialog: its form rows fill the dialog, and below
 * the dialog's `@3xl/dialog` width each field takes its own row at touch size.
 * jsdom evaluates no container query, so these tests read the classes that
 * switch the layout; the browser applying them is not shown here.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000200';
const CATEGORY_ID = '00000000-0000-0000-0000-000000000300';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';

const NARROW = '@max-3xl/dialog:';

const base: Omit<AccountSummary, 'id' | 'name' | 'accountType'> = {
    ledgerId: LEDGER_ID, parentId: null, categoryKind: null, currencyCode: 'USD', isActive: true,
    isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
};
const ACCOUNTS: AccountSummary[] = [
    { ...base, id: ACCOUNT_ID, name: 'Brokerage A', accountType: 'investment' },
    { ...base, id: CATEGORY_ID, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const DRAFT: InvestmentTxnDraft = {
    brokerageAccountId: ACCOUNT_ID, postedAt: '2026-05-01', transactedAt: '', action: 'misc',
    payee: 'Payee One', memo: '', checkNumber: '', securityId: null, shares: null, price: null,
    amount: -75, categoryAccountId: CATEGORY_ID, transferAccountId: null, feeAccountId: null,
    feeAmount: null, tags: [],
};

function renderEditor(layout: 'dialog' | 'register') {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const common = {
        ledgerId: LEDGER_ID,
        brokerageAccountId: ACCOUNT_ID,
        accounts: ACCOUNTS,
        isTradeCommission: false,
        onCancel: () => {},
        mode: { kind: 'edit' as const, headerId: HEADER_ID, initialDraft: DRAFT, onSaved: () => {} },
    };
    return render(
        <QueryClientProvider client={client}>
            {layout === 'dialog' ? (
                <EditorDialog ariaLabel="Editor" title="Editor" onClose={() => {}}>
                    <InvestmentTxnRowEdit {...common} layout="dialog" />
                </EditorDialog>
            ) : (
                <InvestmentTxnRowEdit {...common} cols="1fr" />
            )}
        </QueryClientProvider>,
    );
}

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue({
        accountId: ACCOUNT_ID, accountName: 'Brokerage A', currencyCode: 'USD',
        summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
        positions: [],
    });
    vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({ accounts: [], categories: [] });
    vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchPayees').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([]);
});

describe('the investment editor in a dialog', () => {
    it('is a named group, not a register row', () => {
        renderEditor('dialog');
        const editor = screen.getByRole('group', { name: 'Investment transaction' });
        expect(within(editor).getByLabelText('Payee')).toBeInTheDocument();
        expect(screen.queryByRole('row')).toBeNull();
    });

    it('puts each form row\'s fields one per row when the dialog is narrow', () => {
        renderEditor('dialog');
        // The rows holding the action and dates, the payee and memo, and the
        // action's own fields.
        const rows = [
            screen.getByLabelText('Action'),
            screen.getByLabelText('Payee'),
            screen.getByRole('combobox', { name: 'Category' }),
        ].map((el) => el.closest('.col-span-full')!);
        for (const row of rows) {
            expect(row).toHaveClass(`${NARROW}flex-col`, `${NARROW}items-stretch`);
        }
    });

    it('grows its fields to touch size when the dialog is narrow', () => {
        renderEditor('dialog');
        for (const el of [
            screen.getByLabelText('Action'),
            screen.getByLabelText('Date'),
            screen.getByLabelText('Payee'),
            screen.getByLabelText('Memo'),
            screen.getByRole('combobox', { name: 'Category' }),
            screen.getByLabelText('Amount'),
            screen.getByLabelText('Fee amount'),
        ]) {
            expect(el).toHaveClass(`${NARROW}h-control-40px`);
        }
    });

    it('makes Cancel and the primary 44px buttons sharing the width', () => {
        renderEditor('dialog');
        const cancel = screen.getByRole('button', { name: 'Cancel' });
        const save = screen.getByRole('button', { name: 'Save' });
        expect(cancel).toHaveClass(`${NARROW}h-control-44px`);
        expect(save).toHaveClass(`${NARROW}h-control-44px`);
        expect(cancel.parentElement).toHaveClass(`${NARROW}grid-cols-2`);
    });
});

describe('the investment editor\'s hint chips', () => {
    beforeEach(() => {
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([{
            headerId: '00000000-0000-0000-0000-000000000bbb', postedAt: '2026-04-30T00:00:00Z', dayDelta: -1,
            action: 'misc', derivedAction: 'misc', securityTicker: null, shares: null, unitPrice: null,
            amount: -75, payee: 'Payee Two', counterpartyAccountId: null, counterpartyAccountName: null,
            origin: 'manual',
        }]);
        vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([{
            payee: 'Payee Three', counterpartyAccountId: CATEGORY_ID, counterpartyAccountName: 'Category A',
            useCount: 2, lastUsedAt: '2026-04-01T00:00:00Z',
        }]);
    });

    it('take the touch size in a narrow dialog: 32px chips and 12px text', async () => {
        renderEditor('dialog');
        for (const name of [/Payee Two/, /Payee Three/]) {
            const chip = await screen.findByRole('button', { name });
            expect(chip).toHaveClass(`${NARROW}min-h-control-32px`, `${NARROW}px-2`);
            expect(chip.parentElement).toHaveClass(`${NARROW}text-xs`);
        }
    });

    it('stay compact in the register', async () => {
        renderEditor('register');
        for (const name of [/Payee Two/, /Payee Three/]) {
            const chip = await screen.findByRole('button', { name });
            expect(chip).toHaveClass('px-1.5', 'py-0.5');
            expect(chip).not.toHaveClass(`${NARROW}min-h-control-32px`);
            expect(chip.parentElement).not.toHaveClass(`${NARROW}text-xs`);
        }
    });
});

describe('the investment editor in the register', () => {
    it('is still a register row, at the register\'s sizes', () => {
        renderEditor('register');
        const row = screen.getByRole('row');
        const payee = within(row).getByLabelText('Payee');
        expect(payee).not.toHaveClass(`${NARROW}h-control-40px`);
        expect(payee.closest('.col-span-full')).not.toHaveClass(`${NARROW}flex-col`);
        expect(screen.getByRole('button', { name: 'Cancel' })).not.toHaveClass(`${NARROW}h-control-44px`);
    });
});
