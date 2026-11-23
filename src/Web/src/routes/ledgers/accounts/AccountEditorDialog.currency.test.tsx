import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { currencyAffix, formatCurrency } from '@/lib/money';
import type { AccountDetail, AccountSummary } from '@/lib/types';

import { AccountEditorDialog } from './AccountEditorDialog';

/**
 * The account editor's money inputs, opening balance, principal, escrow and the fixed
 * payment, show the symbol of the dialog's own Currency field, live, and take its
 * places; the rate, the term and the payments a year are not money and show none
 * (ADR-0114 D14). The loan payment preview asks in that currency and prints at its
 * places (ADR-0114 D15). Mocked at `fetch`; every figure is invented.
 */

const LEDGER = '00000000-0000-0000-0000-000000000010';
const PREVIEW = /\/accounts\/loan-payment-preview$/;

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /\/accounts$/, [])
        .on('POST', PREVIEW, { periodicPayment: 1200, escrowAmount: 0, totalPayment: 1200 });
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderNewLoan() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={qc}>
            <AccountEditorDialog ledgerId={LEDGER} account={null} onClose={vi.fn()} onSaved={vi.fn()} />
        </QueryClientProvider>,
    );
}

const field = (name: string) => screen.getByLabelText(name) as HTMLInputElement;
const affixOf = (input: HTMLElement) =>
    input.parentElement!.querySelector('[data-money-affix]')?.textContent ?? null;

async function openLoan(user: ReturnType<typeof userEvent.setup>) {
    renderNewLoan();
    await user.selectOptions(screen.getByLabelText('Type'), 'loan');
    await user.click(screen.getByRole('radio', { name: 'Fixed amount' }));
}

describe('the account editor\'s money inputs', () => {
    it('show the Currency field\'s symbol and follow it when it changes', async () => {
        const user = userEvent.setup();
        await openLoan(user);
        const money = ['Opening balance', 'Original principal', 'Escrow amount', 'Fixed payment amount'];

        for (const name of money) expect([name, affixOf(field(name))]).toEqual([name, currencyAffix('USD')!.symbol]);

        await user.selectOptions(screen.getByLabelText('Currency'), 'JPY');
        for (const name of money) {
            expect([name, affixOf(field(name))]).toEqual([name, currencyAffix('JPY')!.symbol]);
            expect([name, field(name).step]).toEqual([name, '1']);
        }
    });

    it('round a typed figure at the Currency field\'s places on blur', async () => {
        const user = userEvent.setup();
        await openLoan(user);
        await user.selectOptions(screen.getByLabelText('Currency'), 'JPY');

        await user.type(field('Original principal'), '1234.5');
        await user.tab();
        expect(field('Original principal').value).toBe('1235');
    });

    it('leave the rate, the term and the payments a year without a symbol', async () => {
        const user = userEvent.setup();
        await openLoan(user);

        for (const name of ['Annual rate %', 'Term (payments)', 'Payments / year']) {
            expect([name, affixOf(field(name))]).toEqual([name, null]);
        }
    });
});

describe('the account editor\'s save body', () => {
    const CREATE = /\/accounts$/;
    type Body = {
        openingBalance?: number;
        loanTerms?: { originalPrincipal?: number; escrowAmount?: number; fixedPayment?: number | null };
    };
    const savedBody = async () => {
        await waitFor(() => expect(stub.callsTo('POST', CREATE).length).toBe(1));
        return stub.callsTo('POST', CREATE)[0]!.body as Body;
    };

    beforeEach(() => {
        stub.on('POST', CREATE, { id: '00000000-0000-0000-0000-000000000020' });
    });

    it('rounds a figure typed under a two-place currency at a whole-unit Currency picked after it', async () => {
        const user = userEvent.setup();
        renderNewLoan();
        await user.type(field('Name'), 'Synthetic account');
        await user.type(field('Opening balance'), '1234.56');
        await user.tab();
        expect(field('Opening balance').value).toBe('1234.56');

        await user.selectOptions(screen.getByLabelText('Currency'), 'JPY');
        await user.click(screen.getByRole('button', { name: 'Create' }));

        expect((await savedBody()).openingBalance).toBe(1235);
    });

    it('rounds each typed loan figure at the Currency picked after it', async () => {
        const user = userEvent.setup();
        await openLoan(user);
        await user.type(field('Name'), 'Synthetic loan');
        await user.type(field('Original principal'), '1000.56');
        await user.type(field('Annual rate %'), '4');
        await user.type(field('Term (payments)'), '12');
        await user.clear(field('Escrow amount'));
        await user.type(field('Escrow amount'), '10.56');
        await user.type(field('Fixed payment amount'), '100.56');
        await user.tab();

        await user.selectOptions(screen.getByLabelText('Currency'), 'JPY');
        await user.click(screen.getByRole('button', { name: 'Create' }));

        const terms = (await savedBody()).loanTerms!;
        expect([terms.originalPrincipal, terms.escrowAmount, terms.fixedPayment]).toEqual([1001, 11, 101]);
    });

    it('on an edit, sends a stored figure left alone as stored and rounds a retyped one', async () => {
        // A whole-unit loan whose stored opening balance carries a fraction beyond the
        // currency's places: the editor seeds every digit, and leaving it alone sends it back
        // unchanged, so the server compares it with the stored figure (ADR-0114 D15).
        const ACCOUNT = '00000000-0000-0000-0000-000000000030';
        const summary: AccountSummary = {
            id: ACCOUNT, ledgerId: LEDGER, parentId: null, name: 'Synthetic loan', accountType: 'loan',
            categoryKind: null, currencyCode: 'JPY', isActive: true, isSystem: false,
            feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
            isTradeCommission: false, institutionName: null,
        };
        const detail: AccountDetail = {
            id: ACCOUNT, ledgerId: LEDGER, parentId: null, name: 'Synthetic loan', accountType: 'loan',
            categoryKind: null, currencyCode: 'JPY', isActive: true, isSystem: false,
            institutionName: null, accountNumber: null, routingNumber: null,
            accountUrl: null, notes: null, openingBalance: 1234.5, openedOn: null, taxStatus: null,
            loanTerms: {
                originalPrincipal: 1000, annualInterestRate: 4, points: 0, paymentCount: 12,
                paymentsPerYear: 12, firstPaymentDate: null, escrowAmount: 0,
                interestAccountId: null, escrowAccountId: null, paymentIsComputed: true, fixedPayment: null,
            },
            managedReminder: null,
        };
        const ONE = new RegExp(`/accounts/${ACCOUNT}$`);
        stub.on('GET', ONE, detail).on('PATCH', ONE, { becameTwoAmount: 0 });

        const user = userEvent.setup();
        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={qc}>
                <AccountEditorDialog ledgerId={LEDGER} account={summary} onClose={vi.fn()} onSaved={vi.fn()} />
            </QueryClientProvider>,
        );
        await waitFor(() => expect(field('Opening balance').value).toBe('1234.5'));
        // The retyped figure is set straight on the field, so no blur rounds it first.
        fireEvent.change(field('Original principal'), { target: { value: '1000.56' } });
        await user.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(stub.callsTo('PATCH', ONE).length).toBe(1));
        const body = stub.callsTo('PATCH', ONE)[0]!.body as Body;
        expect([body.openingBalance, body.loanTerms?.originalPrincipal]).toEqual([1234.5, 1001]);
    });
});

describe('the loan payment preview', () => {
    async function fillTerms(user: ReturnType<typeof userEvent.setup>, currency: string) {
        renderNewLoan();
        await user.selectOptions(screen.getByLabelText('Type'), 'loan');
        await user.selectOptions(screen.getByLabelText('Currency'), currency);
        await user.type(field('Original principal'), '1000');
        await user.type(field('Annual rate %'), '4');
        await user.type(field('Term (payments)'), '12');
    }

    it('asks in the Currency field\'s currency', async () => {
        const user = userEvent.setup();
        await fillTerms(user, 'JPY');

        await waitFor(() => expect(stub.callsTo('POST', PREVIEW).length).toBeGreaterThan(0));
        const last = stub.callsTo('POST', PREVIEW).at(-1)!;
        expect((last.body as { currencyCode?: string }).currencyCode).toBe('JPY');
    });

    it('prints a whole-unit loan\'s payment with no fraction', async () => {
        const user = userEvent.setup();
        await fillTerms(user, 'JPY');

        const line = (await screen.findByText(/Estimated payment/)).closest('p')!;
        expect(line.textContent).toContain(formatCurrency(1200, 'JPY'));
        expect(line.textContent).not.toMatch(/\d\.\d/);
    });

    it('prints a three-place loan\'s payment at three places, which the browser\'s own digits do not', async () => {
        // The browser's currency data gives this code no minor unit; ISO 4217 gives it three.
        stub.on('POST', PREVIEW, { periodicPayment: 1200.25, escrowAmount: 0, totalPayment: 1200.25 });
        const user = userEvent.setup();
        await fillTerms(user, 'IQD');

        const line = (await screen.findByText(/Estimated payment/)).closest('p')!;
        // Compared raw: the code and the figure are joined by a no-break space.
        expect(line.textContent).toContain(formatCurrency(1200.25, 'IQD'));
        expect(formatCurrency(1200.25, 'IQD')).toMatch(/\.250\b/);
    });
});
