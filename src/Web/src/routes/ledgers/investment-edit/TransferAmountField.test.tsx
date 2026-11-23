import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type FetchStub, type StubbedCall } from '@/lib/annotations/fetchStub';
import type { AccountSummary, CreateInvestmentTransactionRequest, LedgerInvestmentAction } from '@/lib/types';

import { InvestmentTxnRowEdit } from './InvestmentTxnRowEdit';
import { legsToDraft } from './legsToDraft';
import type { InvestmentTxnDraft } from './validation';

// The investment editor's second amount (ADR-0114 D13, D14; boards OT-1, OT-2): the
// transfer account's own figure on an Xfr, BuyXfr, SellXfr or DividendXfr to a real
// account in another currency, right of Amount, with the rate line in its hint slot.
// Mocked at fetch, through the real editor in its host-submitted ('fire') mode.

const LEDGER = '00000000-0000-0000-0000-000000000010';
const EUR_BROKER = '00000000-0000-0000-0000-000000000200';
const USD_BANK = '00000000-0000-0000-0000-000000000101';
const EUR_BANK = '00000000-0000-0000-0000-000000000102';
const USD_BROKER = '00000000-0000-0000-0000-000000000201';
const FEES = '00000000-0000-0000-0000-000000000300';
const SECURITY = '00000000-0000-0000-0000-000000000400';

const acct = (id: string, name: string, accountType: string, currencyCode: string): AccountSummary => ({
    id, name, ledgerId: LEDGER, parentId: null, accountType,
    categoryKind: accountType === 'category' ? 'expense' : null, currencyCode, isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
});

const ACCOUNTS: AccountSummary[] = [
    acct(EUR_BROKER, 'Test EUR Broker', 'investment', 'EUR'),
    acct(USD_BANK, 'Test USD Bank', 'bank', 'USD'),
    acct(EUR_BANK, 'Test EUR Bank', 'bank', 'EUR'),
    acct(USD_BROKER, 'Test USD Broker', 'investment', 'USD'),
    acct(FEES, 'Fees', 'category', 'EUR'),
];

const draft = (over: Partial<InvestmentTxnDraft>): InvestmentTxnDraft => ({
    brokerageAccountId: EUR_BROKER, postedAt: '2026-11-13', transactedAt: '', action: 'transfer',
    payee: '', memo: '', checkNumber: '', securityId: null, shares: null, price: null, amount: 90,
    categoryAccountId: null, transferAccountId: USD_BANK, feeAccountId: null, feeAmount: null, tags: [],
    ...over,
});

const DERIVED = /\/fx-rates\/[A-Z]{3}\/[A-Z]{3}\/derived$/;

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch()
        .on('GET', /\/securities$/, [{ id: SECURITY, ticker: 'TSTF', name: 'Test Fund', currencyCode: 'EUR' }])
        .on('GET', /\/holdings$/, {
            accountId: EUR_BROKER, accountName: 'Test EUR Broker', currencyCode: 'EUR',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', DERIVED, (call: StubbedCall) => {
            const amount = call.search.get('amount');
            return {
                fromCurrency: 'EUR', toCurrency: 'USD', date: call.search.get('date'), reportingCurrency: 'USD',
                rate: 1.1111, rateDate: '2026-11-13', legs: [],
                converted: amount === null ? null : Math.round(Number(amount) * 1.1111 * 100) / 100,
                rateUnavailable: [],
            };
        });
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderEditor(initial: InvestmentTxnDraft, { stored = false, duplicate = false } = {}) {
    const onSubmit = vi.fn<(req: CreateInvestmentTransactionRequest) => void>();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={client}>
            <InvestmentTxnRowEdit
                ledgerId={LEDGER}
                brokerageAccountId={EUR_BROKER}
                accounts={ACCOUNTS}
                currency="EUR"
                isTradeCommission={false}
                cols="1fr"
                onCancel={() => {}}
                mode={duplicate
                    ? { kind: 'new', initialDraft: initial, onCreated: () => {} }
                    : { kind: 'fire', initialDraft: initial, initialIsStored: stored, onSubmit }}
            />
        </QueryClientProvider>,
    );
    return onSubmit;
}

const SENT_USD = 'Amount sent from Test USD Bank, in USD';
const RECEIVED_USD = 'Amount received in Test USD Bank, in USD';

async function submitted(onSubmit: ReturnType<typeof renderEditor>): Promise<CreateInvestmentTransactionRequest> {
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    return onSubmit.mock.calls[0]![0];
}

describe('TransferAmountField — an Xfr between currencies (OT-1)', () => {
    it('shows Sent, prefilled from the derived rate, with the rate line in its hint slot', async () => {
        const onSubmit = renderEditor(draft({}));
        const field = await screen.findByLabelText(SENT_USD);
        await waitFor(() => expect(field).toHaveValue(-100));
        expect(screen.getByText('Sent')).toBeInTheDocument();
        expect(screen.getByText('Rate for Nov 13, 2026: 1 EUR = 1.1111 USD')).toBeInTheDocument();

        const req = await submitted(onSubmit);
        expect(req.transferAmount).toBe(-100);
    });

    it('turns round to Received for a negative Amount', async () => {
        const onSubmit = renderEditor(draft({ amount: -90 }));
        const field = await screen.findByLabelText(RECEIVED_USD);
        await waitFor(() => expect(field).toHaveValue(100));
        expect((await submitted(onSubmit)).transferAmount).toBe(100);
    });

    it('asks for the amount when there is no rate, and holds Save with that as its title', async () => {
        // A fresh stub with the no-rate answer: a stub answers the first route that matches.
        vi.unstubAllGlobals();
        stub = stubFetch()
            .on('GET', /\/fx-rates\/EUR\/USD\/derived$/, (call: StubbedCall) => ({
                fromCurrency: 'EUR', toCurrency: 'USD', date: call.search.get('date'), reportingCurrency: 'USD',
                rate: null, rateDate: null, legs: [], converted: null,
                rateUnavailable: [{ fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-11-13', reason: 'rate_unavailable' }],
            }))
            .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] });
        renderEditor(draft({}));

        expect(await screen.findByText('No EUR to USD rate for Nov 13, 2026')).toBeInTheDocument();
        expect(screen.getByLabelText(SENT_USD)).toHaveValue(null);
        const sentence = 'Enter the amount in USD that left Test USD Bank';
        expect(screen.getByText(sentence)).toHaveClass('text-state-danger');
        const save = screen.getByRole('button', { name: 'Save' });
        expect(save).toBeDisabled();
        expect(save).toHaveAttribute('title', sentence);
    });

    it('holds Save on a zero Amount, one side of the pair, with a typed second amount', async () => {
        renderEditor(draft({ amount: 0 }));
        const field = await screen.findByLabelText(RECEIVED_USD);
        fireEvent.change(field, { target: { value: '5' } });
        expect(field).toHaveValue(5);
        expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    });
});

describe('TransferAmountField — a buy paid from another currency (OT-2)', () => {
    const buy = draft({ action: 'buyx', securityId: SECURITY, shares: 10, price: 9, amount: 90 });

    it('pairs with the trade amount: the fee leaves it alone, shares × price refills it', async () => {
        renderEditor(buy);
        const field = await screen.findByLabelText(SENT_USD);
        await waitFor(() => expect(field).toHaveValue(-100));

        fireEvent.change(screen.getByLabelText('Fee amount'), { target: { value: '5' } });
        expect(field).toHaveValue(-100);

        fireEvent.change(screen.getByLabelText('Shares'), { target: { value: '20' } });
        await waitFor(() => expect(field).toHaveValue(-200));
    });

    it('captions a BuyXfr Sent before it has an amount', async () => {
        renderEditor(draft({ action: 'buyx', securityId: SECURITY, shares: null, price: null, amount: null }));
        expect(await screen.findByLabelText(SENT_USD)).toHaveValue(null);
    });

    const captions: [LedgerInvestmentAction, string][] = [['sellx', 'Received'], ['divx', 'Received']];
    it.each(captions)('captions %s as %s', async (action, caption) => {
        renderEditor(draft({ action, securityId: SECURITY, shares: action === 'sellx' ? -10 : null, price: 9, amount: 90 }));
        expect(await screen.findByLabelText(RECEIVED_USD)).toBeInTheDocument();
        expect(screen.getByText(caption)).toBeInTheDocument();
    });
});

describe('TransferAmountField — a stored transfer', () => {
    it('seeds the stored figure, asks for no rate, and an untouched save resends it', async () => {
        const seeded = legsToDraft('transfer', EUR_BROKER, {
            postedAt: '2026-11-13T00:00:00Z', payee: null, memo: null, checkNumber: null,
        }, [
            { accountId: EUR_BROKER, amount: 90, securityId: null, quantity: null, unitPrice: null, postingRole: 'transfer' },
            { accountId: USD_BANK, amount: -100.25, securityId: null, quantity: null, unitPrice: null, postingRole: 'transfer' },
        ]);
        const onSubmit = renderEditor(seeded, { stored: true });
        expect(await screen.findByLabelText(SENT_USD)).toHaveValue(-100.25);
        expect(screen.getByText('1 EUR = 1.113889 USD, from the two amounts')).toBeInTheDocument();

        expect((await submitted(onSubmit)).transferAmount).toBe(-100.25);
        expect(stub.callsTo('GET', DERIVED)).toHaveLength(0);
    });

    // A zero second amount resent as it opened passes only on a stored template edited
    // in place; a Duplicate or Post on a reminder is a new write the server judges.
    const zeroPair = draft({ transferAmount: 0, transferAmountFixed: true });
    const sentence = 'Enter the amount in USD that left Test USD Bank';
    it.each([
        ['a Duplicate', { duplicate: true }],
        ['Post on a reminder', { stored: false }],
    ])('holds Save on a copied zero second amount: %s', async (_name, options) => {
        renderEditor(zeroPair, options);
        expect(await screen.findByLabelText(SENT_USD)).toHaveValue(0);
        const save = screen.getByRole('button', { name: 'Save' });
        expect(save).toBeDisabled();
        expect(save).toHaveAttribute('title', sentence);
    });

    it('lets a stored template keep its zero second amount when it is left alone', async () => {
        renderEditor(zeroPair, { stored: true });
        expect(await screen.findByLabelText(SENT_USD)).toHaveValue(0);
        expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
    });
});

describe('TransferAmountField — no second amount', () => {
    it('shows none for a category or a same-currency account, and the body carries none', async () => {
        for (const transferAccountId of [FEES, EUR_BANK]) {
            const onSubmit = renderEditor(draft({ transferAccountId }));
            // Anchor: the editor is up, with the Xfr's Amount and its hint.
            expect(await screen.findByText('positive = in, negative = out')).toBeInTheDocument();
            expect(screen.queryByText('Sent')).toBeNull();
            const req = await submitted(onSubmit);
            expect(req).not.toHaveProperty('transferAmount');
            document.body.innerHTML = '';
        }
    });

    it('never shows one on an in-kind share move', async () => {
        renderEditor(draft({ action: 'transfer_shares', transferAccountId: USD_BROKER, securityId: SECURITY, shares: 5, amount: null }));
        // Anchor: the share move's destination field is there.
        expect(await screen.findByText('Transfer shares to')).toBeInTheDocument();
        expect(screen.queryByText('Sent')).toBeNull();
        expect(screen.queryByText('Received')).toBeNull();
    });
});
