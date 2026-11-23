import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { BankRegisterPage } from './BankRegisterPage';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { categoryChipVariant } from '@/lib/categoryChip';
import type { AccountSummary, BankRow, RegisterEntry } from '@/lib/types';

/**
 * The register draws the system Uncategorized category's chip neutral, on a
 * plain row and on a split's line, and a person's own category of the same
 * name keeps its hashed colour. Mocked at `fetch`, so the system flag comes
 * from the accounts read as it does in the app.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const CHECKING = '00000000-0000-0000-0000-000000000100';
const UNCATEGORIZED = '00000000-0000-0000-0000-000000000301';
const PARENT = '00000000-0000-0000-0000-000000000302';

const base = {
    ledgerId: LEDGER_ID, parentId: null, categoryKind: 'expense', currencyCode: 'USD', isActive: true,
    isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

// An id for the person's own category that hashes to a colour, so its chip
// is distinguishable from the neutral one.
const OWN = Array.from({ length: 50 }, (_, i) => `00000000-0000-0000-0000-0000000004${String(i).padStart(2, '0')}`)
    .find((id) => categoryChipVariant('Uncategorized', 'category', id, false) !== 'default')!;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: CHECKING, name: 'Account A', accountType: 'bank', categoryKind: null },
    { ...base, id: UNCATEGORIZED, name: 'Uncategorized', accountType: 'category', isSystem: true },
    { ...base, id: PARENT, name: 'Category A', accountType: 'category' },
    { ...base, id: OWN, name: 'Uncategorized', accountType: 'category', parentId: PARENT },
];

const row = (over: Partial<BankRow>): BankRow => ({
    ...NO_NOTES,
    kind: 'bank', id: 'leg-1', accountId: CHECKING, payee: 'Payee One', amount: -10,
    postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared', isHidden: false,
    balanceAfter: 90, origin: 'manual', isPending: false, externalId: null, checkNumber: null,
    currencyCode: 'USD',
    balanceCurrencyCode: 'USD',
    counterpartyId: 'cp-1', txnGroupId: null, legIndex: 0, counterpartyAccountId: UNCATEGORIZED,
    counterpartyAccountName: 'Uncategorized', counterpartyAccountType: 'category',
    headerId: '00000000-0000-0000-0000-000000000e01', clearedAt: null, clearedByUserId: null,
    createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null,
    needsReview: false, providerRawPayload: null, headerAccountNetAmount: null, providerKey: null,
    isMergeWinner: false, importSource: null, derivedAction: null, accountPostingsOnHeader: 1,
    headerTotalPostings: 1,
    ...over,
});

let stub: FetchStub;

function serve(entries: RegisterEntry[]) {
    stub
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER_ID, name: 'Ledger A', role: 'owner' }])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, { entries, cursorForOlder: null, cursorForNewer: null })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: 1, cleared: 0, uncleared: 1, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 0 })
        .on('POST', /\/transactions\/balances$/, []);
}

function renderRegister() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: BankRegisterPage,
    });
    const ledgerRoute = createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId', component: () => <main /> });
    const router = createRouter({
        routeTree: root.addChildren([registerRoute, ledgerRoute]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${CHECKING}`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/** The chip (Chip's span) that draws a label in this row. */
const chipIn = (scope: HTMLElement, label: string) =>
    within(scope).getByText(label).closest('span[class*="bg-"]') as HTMLElement;

beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('the Uncategorized chip in the bank register', () => {
    it('is neutral on a plain row, and a person\'s own category of that name is not', async () => {
        serve([
            { kind: 'txn', txn: row({}), groupId: null, legs: null },
            {
                kind: 'txn', groupId: null, legs: null,
                txn: row({
                    id: 'leg-2', headerId: '00000000-0000-0000-0000-000000000e02', payee: 'Payee Two',
                    counterpartyAccountId: OWN,
                }),
            },
        ]);
        renderRegister();

        const system = (await screen.findByText('Payee One')).closest('[role="row"]') as HTMLElement;
        const own = (await screen.findByText('Payee Two')).closest('[role="row"]') as HTMLElement;
        expect(chipIn(system, 'Uncategorized')).toHaveClass('bg-chip-neutral');
        expect(chipIn(own, 'Category A/Uncategorized')).not.toHaveClass('bg-chip-neutral');
    });

    it('is neutral on a split\'s line', async () => {
        const legs = [
            row({ id: 'leg-a', txnGroupId: 'g-1', counterpartyAccountId: PARENT, counterpartyAccountName: 'Category A', amount: -6 }),
            row({ id: 'leg-b', txnGroupId: 'g-1', legIndex: 1, amount: -4 }),
        ];
        serve([{ kind: 'group', txn: null, groupId: 'g-1', legs }]);
        renderRegister();

        // Expand the split so its lines draw their own chips.
        fireEvent.click(await screen.findByTitle('Show this split’s categories'));
        const lines = await screen.findAllByText('Uncategorized');
        expect(lines.length).toBeGreaterThan(0);
        for (const label of lines) {
            expect(label.closest('span[class*="bg-"]')).toHaveClass('bg-chip-neutral');
        }
    });
});
