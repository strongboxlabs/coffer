import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ReminderOccurrenceModal } from './ReminderOccurrenceModal';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { AccountSummary, ReminderDetail, UpcomingOccurrence } from '@/lib/types';
import type { FireBankReminderRequest } from '@/lib/types/reminder';

/**
 * Posting a bank reminder's occurrence sends the occurrence's memo and tags as
 * `notes`, through the real editor and the real API client, mocked at `fetch`.
 */

const LEDGER = 'ledger-1';
const CHECKING = 'acct-bank';
const RENT = 'cat-rent';

const base = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'USD',
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
    holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const UTILITIES = 'cat-utilities';

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: CHECKING, name: 'Checking', accountType: 'bank' },
    { ...base, id: RENT, name: 'Rent', accountType: 'category', categoryKind: 'expense' },
    { ...base, id: UTILITIES, name: 'Utilities', accountType: 'category', categoryKind: 'expense' },
];

// Template legs carry their ids; an occurrence is a new transaction, so none
// of them is sent.
const leg = (
    accountId: string, accountName: string, postingIndex: number, amount: number, legMemo: string | null,
    legTags: string[] = [],
) => ({
    legId: `tleg-${postingIndex}-${accountName}`,
    accountId, accountName, postingIndex, amount, legMemo, legTags,
    securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null,
});

// A two-line split template: the transaction memo on the header, each line's
// own on its Checking leg, where the write primitive places them.
const DETAIL: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Landlord', memo: 'Flat 2', tags: [], checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-11-01', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: CHECKING,
    legs: [
        leg(CHECKING, 'Checking', 0, -600, 'Base rent'),
        leg(RENT, 'Rent', 0, 600, null),
        leg(CHECKING, 'Checking', 1, -300, 'Water'),
        leg(UTILITIES, 'Utilities', 1, 300, null),
    ],
};

const OCC: UpcomingOccurrence = {
    date: '2026-11-01', kind: 'reminder', reminderId: 'r1', headerId: null,
    payee: 'Landlord', memo: 'Flat 2', amount: -900, seriesNextDue: '2026-11-01',
};

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/reminders\/r1$/, DETAIL)
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, [])
        .on('POST', /\/reminders\/r1\/fire\/bank$/, { headerId: 'h-new', skippedEarlierCount: 0, skippedEarlierFrom: null });
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('posting a bank occurrence', () => {
    it('sends the occurrence\'s memo in notes, from the posting account\'s side', async () => {
        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={qc}>
                <ReminderOccurrenceModal ledgerId={LEDGER} occ={OCC} onClose={() => {}} onActed={() => {}} />
            </QueryClientProvider>,
        );

        expect(await screen.findByLabelText('Memo (whole transaction)')).toHaveValue('Flat 2');
        await userEvent.click(screen.getByRole('button', { name: /^post$/i }));

        await waitFor(() => expect(stub.callsTo('POST', /\/fire\/bank$/)).toHaveLength(1));
        const body = stub.callsTo('POST', /\/fire\/bank$/)[0]!.body as FireBankReminderRequest;
        expect(body.notes).toEqual({
            side: CHECKING,
            memo: 'Flat 2',
            lines: [{ index: 0, memo: 'Base rent' }, { index: 1, memo: 'Water' }],
        });
        expect(body.postings).toEqual([
            { legId: null, counterpartyAccountId: RENT, amount: -600 },
            { legId: null, counterpartyAccountId: UTILITIES, amount: -300 },
        ]);
        expect('memo' in body).toBe(false);
    });

    it('posts the template\'s tags, the transaction\'s and each line\'s own from the source side', async () => {
        // The category leg's set is that side's own and is not this editor's.
        stub.on('GET', /\/reminders\/r1$/, {
            ...DETAIL,
            tags: ['Housing'],
            legs: [
                leg(CHECKING, 'Checking', 0, -600, 'Base rent', ['Fixed']),
                leg(RENT, 'Rent', 0, 600, null, ['Elsewhere']),
                leg(CHECKING, 'Checking', 1, -300, 'Water'),
                leg(UTILITIES, 'Utilities', 1, 300, null),
            ],
        } satisfies ReminderDetail);
        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={qc}>
                <ReminderOccurrenceModal ledgerId={LEDGER} occ={OCC} onClose={() => {}} onActed={() => {}} />
            </QueryClientProvider>,
        );

        expect(await screen.findByTitle('Housing')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: /^post$/i }));

        await waitFor(() => expect(stub.callsTo('POST', /\/fire\/bank$/)).toHaveLength(1));
        const body = stub.callsTo('POST', /\/fire\/bank$/)[0]!.body as FireBankReminderRequest;
        expect(body.notes).toEqual({
            side: CHECKING,
            memo: 'Flat 2',
            tags: ['Housing'],
            lines: [{ index: 0, memo: 'Base rent', tags: ['Fixed'] }, { index: 1, memo: 'Water' }],
        });
    });

    it('posts a one-line template\'s memo and tags, which are its Checking line\'s own', async () => {
        // A one-line template has no header notes; the read gives the header's
        // own as empty and the value on the source leg.
        stub.on('GET', /\/reminders\/r1$/, {
            ...DETAIL,
            memo: null,
            tags: [],
            legs: [
                leg(CHECKING, 'Checking', 0, -900, 'Flat 2', ['Housing']),
                leg(RENT, 'Rent', 0, 900, null, ['Elsewhere']),
            ],
        } satisfies ReminderDetail);
        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={qc}>
                <ReminderOccurrenceModal ledgerId={LEDGER} occ={OCC} onClose={() => {}} onActed={() => {}} />
            </QueryClientProvider>,
        );

        expect(await screen.findByLabelText('Memo')).toHaveValue('Flat 2');
        expect(screen.getByTitle('Housing')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: /^post$/i }));

        await waitFor(() => expect(stub.callsTo('POST', /\/fire\/bank$/)).toHaveLength(1));
        const body = stub.callsTo('POST', /\/fire\/bank$/)[0]!.body as FireBankReminderRequest;
        expect(body.notes).toEqual({ side: CHECKING, memo: 'Flat 2', tags: ['Housing'] });
    });
});
