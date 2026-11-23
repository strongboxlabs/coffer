import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';

import { ApiError, patchTransaction } from '@/lib/api';
import { problem, stubFetch, type FetchStub, type StubbedCall } from '@/lib/annotations/fetchStub';
import type { AccountSummary } from '@/lib/types';

import { InvestmentTxnRowEdit } from './investment-edit/InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from './investment-edit/validation';
import { saveRefusal } from './saveErrorWords';
import { TxnRowEdit, type TxnRowMode } from './TxnRowEdit';

// The editors say the new refusals in words (ADR-0114 D13, D15). The server is mocked at
// fetch, so each refusal reaches the editor through the real client and its error
// mapping, as it would from the server.

const LEDGER = '00000000-0000-0000-0000-000000000010';
const USD_BANK = '00000000-0000-0000-0000-000000000100';
const EUR_BANK = '00000000-0000-0000-0000-000000000200';
const GBP_BROKER = '00000000-0000-0000-0000-000000000210';
const EUR_BROKER = '00000000-0000-0000-0000-000000000220';
const GROCERIES = '00000000-0000-0000-0000-000000000300';
const SECURITY = '00000000-0000-0000-0000-000000000400';
const HEADER = '00000000-0000-0000-0000-000000000aaa';
const LEG = '00000000-0000-0000-0000-000000000bbb';
const CATEGORY_LEG = '00000000-0000-0000-0000-000000000ccc';

const acct = (id: string, name: string, accountType: string, currencyCode: string): AccountSummary => ({
    id, name, ledgerId: LEDGER, parentId: null, accountType,
    categoryKind: accountType === 'category' ? 'expense' : null, currencyCode, isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
});

const ACCOUNTS: AccountSummary[] = [
    acct(USD_BANK, 'Test USD Bank', 'bank', 'USD'),
    acct(EUR_BANK, 'Test EUR Bank', 'bank', 'EUR'),
    acct(GBP_BROKER, 'Test GBP Broker', 'investment', 'GBP'),
    acct(EUR_BROKER, 'Test EUR Broker', 'investment', 'EUR'),
    acct(GROCERIES, 'Groceries', 'category', 'USD'),
];

const PATCH = /\/transactions\/[^/]+$/;
const CREATE_INVESTMENT = /\/investment-transactions$/;
const DERIVED = /\/fx-rates\/[A-Z]{3}\/[A-Z]{3}\/derived$/;

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch()
        .on('GET', /\/securities$/, [{ id: SECURITY, ticker: 'TSTF', name: 'Test Fund', currencyCode: 'GBP' }])
        .on('GET', /\/holdings$/, {
            accountId: GBP_BROKER, accountName: 'Test GBP Broker', currencyCode: 'GBP',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', DERIVED, (call: StubbedCall) => {
            const amount = call.search.get('amount');
            return {
                fromCurrency: 'GBP', toCurrency: 'USD', date: call.search.get('date'), reportingCurrency: 'USD',
                rate: 1.25, rateDate: '2026-11-13', legs: [],
                converted: amount === null ? null : Number(amount) * 1.25, rateUnavailable: [],
            };
        });
});
afterEach(() => {
    vi.unstubAllGlobals();
});

// ---------------------------------------------------------------------------
// The bank editor
// ---------------------------------------------------------------------------

/** A saved transfer opened from the EUR register: this side received, the USD side sent. */
const FROM_EUR: TxnRowMode = {
    kind: 'edit',
    headerId: HEADER,
    sourceAccountId: EUR_BANK,
    postings: [{
        legId: LEG, counterpartyAccountId: USD_BANK, counterpartyAccountName: 'Test USD Bank',
        amount: 90, legMemo: null, legTags: [], counterpartyAmount: -100,
    }],
    payee: 'Payee A', memo: null, checkNumber: null,
    postedAt: '2026-11-08T00:00:00.000Z', transactedAt: null,
    balanceAfter: 0, balanceCurrencyCode: 'EUR', tags: [], inherited: null, needsReview: false,
};

/** A split on the USD register: a category line and two lines to the EUR account. */
const TWO_CROSSING: TxnRowMode = {
    ...FROM_EUR,
    sourceAccountId: USD_BANK,
    balanceCurrencyCode: 'USD',
    postings: [
        { legId: CATEGORY_LEG, counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Groceries', amount: -40, legMemo: null, legTags: [] },
        { legId: LEG, counterpartyAccountId: EUR_BANK, counterpartyAccountName: 'Test EUR Bank', amount: -100, legMemo: null, legTags: [], counterpartyAmount: 90 },
        { legId: '00000000-0000-0000-0000-000000000ddd', counterpartyAccountId: EUR_BANK, counterpartyAccountName: 'Test EUR Bank', amount: -50, legMemo: null, legTags: [], counterpartyAmount: 45 },
    ],
};

/** The bank editor in a host that saves through the real client, as the register does. */
function BankHost({ mode, currency }: { mode: TxnRowMode; currency: string }) {
    const [error, setError] = useState<string | ApiError | null>(null);
    return (
        <TxnRowEdit
            ledgerId={LEDGER}
            mode={mode}
            payees={[]}
            accounts={ACCOUNTS}
            accountPaths={new Map(ACCOUNTS.map((a) => [a.id, a.name]))}
            currency={currency}
            cols="repeat(8, minmax(0, 1fr))"
            onCancel={() => {}}
            isSaving={false}
            saveError={error}
            cancelOnOutsideClick={false}
            onSavePatch={(body) => {
                patchTransaction(LEDGER, HEADER, body, mode.kind === 'edit' ? mode.sourceAccountId : undefined)
                    .then(() => setError(null), (e: unknown) => setError(saveRefusal(e)));
            }}
        />
    );
}

function renderBank(mode: TxnRowMode = FROM_EUR, currency = 'EUR') {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><BankHost mode={mode} currency={currency} /></QueryClientProvider>);
}

async function saveRefusedWith(detail: string, code: string, extensions?: Record<string, unknown>): Promise<HTMLElement> {
    stub.on('PATCH', PATCH, problem(422, detail, code, extensions));
    fireEvent.click(await screen.findByRole('button', { name: 'Save' }));
    return screen.findByRole('alert');
}

describe('the bank editor says a refused second amount in the field\'s words (ADR-0114 D13)', () => {
    it.each([
        'transaction-posting-counterparty-amount-required',
        'transaction-posting-two-amounts-nonzero',
    ])('%s reads as the sentence naming the currency and the account', async (code) => {
        renderBank();
        const alert = await saveRefusedWith('Posting 1 refused as sent.', code);

        expect(alert).toHaveTextContent('Enter the amount in USD that left Test USD Bank.');
        expect(alert).not.toHaveTextContent(code);
        expect(alert).not.toHaveTextContent('Posting 1 refused as sent.');
    });

    it('keeps the server\'s detail when two lines carry a second amount, since it names the line', async () => {
        renderBank(TWO_CROSSING, 'USD');
        const alert = await saveRefusedWith('Posting 3 refused as sent.', 'transaction-posting-two-amounts-nonzero');

        expect(alert).toHaveTextContent('Posting 3 refused as sent.');
    });
});

describe('the bank editor says a figure finer than its currency (ADR-0114 D15)', () => {
    it('shows the server\'s sentence naming the currency and its places, not the code', async () => {
        renderBank();
        const alert = await saveRefusedWith(
            'An amount in EUR has at most 2 decimal places.', 'amount-places-exceed-currency',
            { currencyCode: 'EUR', places: 2, field: 'postings.items[0].amount' });

        expect(alert).toHaveTextContent('An amount in EUR has at most 2 decimal places.');
        expect(alert).not.toHaveTextContent('amount-places-exceed-currency');
    });

    it('leaves an unrelated refusal as the server words it', async () => {
        renderBank();
        const alert = await saveRefusedWith('That transaction is not in this ledger.', 'transaction-not-in-ledger');

        expect(alert).toHaveTextContent('That transaction is not in this ledger.');
    });
});

// ---------------------------------------------------------------------------
// The investment editor
// ---------------------------------------------------------------------------

const draft = (over: Partial<InvestmentTxnDraft>): InvestmentTxnDraft => ({
    brokerageAccountId: GBP_BROKER, postedAt: '2026-11-13', transactedAt: '', action: 'transfer',
    payee: '', memo: '', checkNumber: '', securityId: null, shares: null, price: null, amount: 80,
    categoryAccountId: null, transferAccountId: USD_BANK, feeAccountId: null, feeAmount: null, tags: [],
    ...over,
});

function renderInvestment(initial: InvestmentTxnDraft) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={client}>
            <InvestmentTxnRowEdit
                ledgerId={LEDGER}
                brokerageAccountId={GBP_BROKER}
                accounts={ACCOUNTS}
                currency="GBP"
                isTradeCommission={false}
                cols="1fr"
                onCancel={() => {}}
                mode={{ kind: 'new', initialDraft: initial, onCreated: () => {} }}
            />
        </QueryClientProvider>,
    );
}

async function createRefusedWith(detail: string, code: string, extensions?: Record<string, unknown>): Promise<HTMLElement> {
    stub.on('POST', CREATE_INVESTMENT, problem(422, detail, code, extensions));
    // Save waits for the prefilled second amount on a transfer between currencies.
    const save = await screen.findByRole('button', { name: 'Save' });
    await vi.waitFor(() => expect(save).toBeEnabled());
    fireEvent.click(save);
    return screen.findByRole('alert');
}

describe('the investment editor says its refusals in words (ADR-0114 D13, D15)', () => {
    it.each([
        'investment-txn-transfer-amount-required',
        'investment-txn-transfer-amounts-nonzero',
    ])('%s reads as the second amount\'s sentence', async (code) => {
        renderInvestment(draft({}));
        const alert = await createRefusedWith('Refused as sent.', code);

        expect(alert).toHaveTextContent('Enter the amount in USD that left Test USD Bank');
        expect(alert).not.toHaveTextContent('Refused as sent.');
    });

    it('names each missing stored leg and its day when a share move has a lot with no rate', async () => {
        renderInvestment(draft({
            action: 'transfer_shares', securityId: SECURITY, shares: 10, amount: null, transferAccountId: EUR_BROKER,
        }));
        const alert = await createRefusedWith('No rate for a lot.', 'rate-unavailable', {
            rateUnavailable: [
                { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-03-09', reason: 'rate_unavailable' },
                { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-02-02', reason: 'rate_unavailable' },
                { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-02-02', reason: 'rate_unavailable' },
            ],
        });

        expect(alert).toHaveTextContent(
            'No GBP or EUR to USD rate for Feb 2, 2026. No GBP to USD rate for Mar 9, 2026.');
        expect(alert).not.toHaveTextContent('No rate for a lot.');
    });

    it('shows the server\'s sentence for a figure finer than its currency, naming the currency and its places', async () => {
        renderInvestment(draft({}));
        expect(await createRefusedWith('An amount in GBP has at most 2 decimal places.', 'amount-places-exceed-currency',
            { currencyCode: 'GBP', places: 2, field: 'amount' })).toHaveTextContent('An amount in GBP has at most 2 decimal places.');
    });

    it('leaves a rate-unavailable refusal with no list of legs as the server words it', async () => {
        renderInvestment(draft({}));
        expect(await createRefusedWith('No GBP to USD rate for that day.', 'rate-unavailable'))
            .toHaveTextContent('No GBP to USD rate for that day.');
    });
});
