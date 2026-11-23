import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { AccountSummary, ReminderDetail, UpcomingOccurrence } from '@/lib/types';

import { ReminderOccurrenceModal } from './ReminderOccurrenceModal';

/**
 * While any of the occurrence dialog's requests is in flight, every action in
 * it refuses: a post sent while the skip of the same occurrence is unanswered
 * is a second, conflicting answer to it. Through the real dialog, the real
 * editors and the real API client, mocked at `fetch`, with the skip held.
 */

const LEDGER = 'ledger-1';
const CHECKING = 'acct-bank';
const BROKERAGE = 'acct-brokerage';
const CATEGORY = 'cat-x';

const base = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'USD',
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
    holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: CHECKING, name: 'Account A', accountType: 'bank' },
    { ...base, id: BROKERAGE, name: 'Account B', accountType: 'investment', holdingsAccountId: 'acct-holdings' },
    { ...base, id: CATEGORY, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const leg = (accountId: string, accountName: string, amount: number, postingRole: string | null = null) => ({
    legId: `tleg-${accountName}`, accountId, accountName, postingIndex: 0, amount, legMemo: null, legTags: [],
    securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole,
});

const BANK: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Payee A', memo: null, tags: [], checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-11-01', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: CHECKING,
    legs: [leg(CHECKING, 'Account A', -10), leg(CATEGORY, 'Category A', 10)],
};

const INVESTMENT: ReminderDetail = {
    ...BANK, kind: 'investment', action: 'misc', sourceAccountId: BROKERAGE,
    legs: [leg(BROKERAGE, 'Account B', -10, 'income'), leg(CATEGORY, 'Category A', 10, 'income')],
};

const OCC: UpcomingOccurrence = {
    date: '2026-11-01', kind: 'reminder', reminderId: 'r1', headerId: null,
    payee: 'Payee A', memo: null, amount: -10, seriesNextDue: '2026-11-01',
};

const SKIP = /\/reminders\/r1\/skip$/;
const FIRE = /\/reminders\/r1\/fire\/(bank|investment)$/;

let stub: FetchStub;

beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/securities$/, [])
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, [])
        .on('POST', SKIP, { occurrenceDate: '2026-11-01', nextDueDate: null, skippedEarlierCount: 0, skippedEarlierFrom: null })
        .on('POST', FIRE, { headerId: 'h-new', skippedEarlierCount: 0, skippedEarlierFrom: null });
    // The skip waits until the test lets it answer; every other request answers.
    const answer = globalThis.fetch;
    const held = new Promise<void>(() => {});
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        if (SKIP.test(new URL(raw, 'http://localhost').pathname)) await held;
        return answer(input, init);
    });
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe.each([
    { editor: 'bank', detail: BANK },
    { editor: 'investment', detail: INVESTMENT },
])('the $editor editor while the skip is in flight', ({ detail }) => {
    it('holds Post pending, and posts nothing on a press or on Enter', async () => {
        stub.on('GET', /\/reminders\/r1$/, detail);
        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={qc}>
                <ReminderOccurrenceModal ledgerId={LEDGER} occ={OCC} onClose={() => {}} onActed={() => {}} />
            </QueryClientProvider>,
        );
        const user = userEvent.setup();
        const post = await screen.findByRole('button', { name: 'Post' });
        await waitFor(() => expect(post).toBeEnabled());

        const skip = screen.getByRole('button', { name: 'Skip this occurrence' });
        await user.click(skip);
        await waitFor(() => expect(skip).toHaveAttribute('aria-busy', 'true'));

        // Pending, and still "Post": the request in flight is the skip, not a post.
        expect(post).toHaveAttribute('aria-busy', 'true');
        expect(post).toHaveAccessibleName('Post');
        await user.click(post);
        // The memo field's Enter saves; it refuses as Post does.
        fireEvent.keyDown(screen.getByLabelText('Memo'), { key: 'Enter' });
        await new Promise((resolve) => { setTimeout(resolve, 20); });

        expect(stub.callsTo('POST', FIRE)).toHaveLength(0);
    });

    it('sends no post when Post is pressed straight after Skip, before Skip renders pending', async () => {
        // The dialog's actions are one group (pending.ts): Skip's press holds
        // Post from the press, not from the render.
        stub.on('GET', /\/reminders\/r1$/, detail);
        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={qc}>
                <ReminderOccurrenceModal ledgerId={LEDGER} occ={OCC} onClose={() => {}} onActed={() => {}} />
            </QueryClientProvider>,
        );
        const post = await screen.findByRole('button', { name: 'Post' });
        await waitFor(() => expect(post).toBeEnabled());

        const skip = screen.getByRole('button', { name: 'Skip this occurrence' });
        fireEvent.click(skip);
        fireEvent.click(post);
        await new Promise((resolve) => { setTimeout(resolve, 20); });

        // The skip went out (it is held in flight here), and no post did.
        expect(skip).toHaveAttribute('aria-busy', 'true');
        expect(stub.callsTo('POST', FIRE)).toHaveLength(0);
    });
});
