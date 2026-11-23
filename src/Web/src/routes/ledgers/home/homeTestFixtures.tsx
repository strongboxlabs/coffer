// Test support for the home screen: a stubbed API answering every read the page
// makes, and a router with the page and every route its links point at. Every name
// and figure is invented for one synthetic ledger.

import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { expect } from 'vitest';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { stubFetch, type FetchStub, type StubbedCall, type StubProblem } from '@/lib/annotations/fetchStub';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { groupAmount } from '@/lib/splitCollapse';
import type {
    AccountSummary,
    AllocationResult,
    BalanceForecast,
    BankRow,
    BudgetProgress,
    FxPair,
    FxRatesView,
    InvestmentRow,
    LedgerAttention,
    LedgerConsistencyState,
    LedgerEntriesPage,
    LedgerEntry,
    LedgerOverview,
    OutstandingProblem,
    RegisterRow,
    ReminderSummary,
    ReturnsResult,
    Schedule,
    StoredDashboardPrefs,
    UpcomingOccurrence,
} from '@/lib/types';
import type { ReportResult, SavedReport } from '@/types/report';

import type { WidgetKey } from './dashboardCatalog';
import { defaultLayout, toDocument, type WidgetSettings } from './dashboardLayout';
import { HomePage } from './HomePage';

/**
 * A stored layout: the default one with some widgets shown or hidden and some
 * settings changed, e.g. `layoutWith({ watchlist: { visible: true, settings: { categoryIds: ['cat-a'] } } })`.
 */
export function layoutWith(
    changes: Partial<Record<WidgetKey, { visible?: boolean; settings?: WidgetSettings }>>,
    updatedAt: string | null = '2026-03-01T00:00:00Z',
): StoredDashboardPrefs {
    const doc = toDocument(defaultLayout());
    return {
        ...doc,
        widgets: doc.widgets.map((w) => {
            const c = changes[w.key as WidgetKey];
            return c ? { ...w, visible: c.visible ?? w.visible, settings: { ...w.settings, ...c.settings } } : w;
        }),
        updatedAt,
    };
}

/**
 * A stored layout with report tiles after the default widgets, e.g.
 * `layoutWithTiles([{ id: 'report:catalog:income-and-expenses' }])`, and other
 * widgets changed as `layoutWith` changes them.
 */
export function layoutWithTiles(
    tiles: { id: string; visible?: boolean; column?: 'main' | 'side'; settings?: WidgetSettings }[],
    changes: Parameters<typeof layoutWith>[0] = {},
): StoredDashboardPrefs {
    const doc = layoutWith(changes);
    return {
        ...doc,
        widgets: [
            ...(doc as { widgets: object[] }).widgets,
            ...tiles.map((t) => ({
                id: t.id, key: 'report', visible: t.visible ?? true, column: t.column ?? 'main',
                settings: { view: 'chart', height: 'compact', ...t.settings },
            })),
        ],
    } as StoredDashboardPrefs;
}

export const LEDGER = '00000000-0000-0000-0000-0000000000aa';
export const CHECKING = 'acct-checking';
export const CARD = 'acct-card';
/** The fixture's local today. */
export const TODAY = '2026-03-12';

const accountBase = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'USD',
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
    holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

export const account = (over: Partial<AccountSummary> & Pick<AccountSummary, 'id' | 'name' | 'accountType'>): AccountSummary =>
    ({ ...accountBase, ...over });

export const schedule = (over: Partial<Schedule> = {}): Schedule => ({
    enabled: false, hourLocal: 5, minuteLocal: 0, timezone: null, lastRunAt: null, nextRunAt: null,
    consecutiveFailures: 0, lastError: null, lastFailureAt: null, disabledReason: null, ...over,
});

export const occurrence = (over: Partial<UpcomingOccurrence>): UpcomingOccurrence => ({
    date: TODAY, kind: 'reminder', reminderId: 'series-a', headerId: null, payee: 'Payee A', memo: null,
    amount: -45, seriesNextDue: null, sourceAccountId: CHECKING, sourceAccountName: 'Account A',
    currencyCode: 'USD', autoPost: null, ...over,
});

export const series = (over: Partial<ReminderSummary>): ReminderSummary => ({
    id: 'series-a', payee: 'Payee A', memo: null, amount: -45, rrule: 'FREQ=MONTHLY', startDate: '2026-01-01',
    endDate: null, nextDueDate: null, autoCommitDaysBefore: null, isActive: true, isLoanReminder: false,
    origin: 'manual', currencyCode: 'USD', ...over,
});

/** A bank register row, as the register page and the ledger-wide entries send one:
 *  a manual, uncleared one-split row dated the fixture's today. */
export const bankRow = (over: Partial<BankRow> = {}): BankRow => ({
    ...NO_NOTES,
    kind: 'bank',
    id: 'leg-1', headerId: 'header-1', accountId: CHECKING, payee: 'Payee A', amount: -10,
    postedAt: `${TODAY}T00:00:00Z`, transactedAt: null, status: 'uncleared', isHidden: false, currencyCode: 'USD', balanceCurrencyCode: 'USD', balanceAfter: null,
    origin: 'manual', isPending: false, externalId: null, checkNumber: null, counterpartyId: 'leg-cp-1',
    txnGroupId: null, legIndex: 0, counterpartyAccountId: 'cat-a', counterpartyAccountName: 'Category A',
    counterpartyAccountType: 'category', clearedAt: null, clearedByUserId: null, createdAt: `${TODAY}T00:00:00Z`,
    onlineMatchFitid: null, onlineMatchFiId: null, needsReview: false, providerRawPayload: null,
    headerAccountNetAmount: null, providerKey: null, isMergeWinner: false, importSource: null, derivedAction: null,
    accountPostingsOnHeader: 1, headerTotalPostings: 2,
    ...over,
});

/** An investment register row: one collapsed event, here a buy with no category. */
export const investmentRow = (over: Partial<InvestmentRow> = {}): InvestmentRow => ({
    ...NO_NOTES,
    kind: 'investment',
    id: 'inv-leg-1', headerId: 'inv-header-1', accountId: 'acct-brokerage', payee: 'Security A', amount: -100,
    postedAt: `${TODAY}T00:00:00Z`, transactedAt: null, status: 'uncleared', isHidden: false, currencyCode: 'USD', balanceCurrencyCode: 'USD', balanceAfter: null,
    origin: 'manual', isPending: false, externalId: null, checkNumber: null, counterpartyId: 'inv-cp-1',
    txnGroupId: null, legIndex: 0, counterpartyAccountId: null, counterpartyAccountName: null,
    counterpartyAccountType: null, clearedAt: null, clearedByUserId: null, createdAt: `${TODAY}T00:00:00Z`,
    onlineMatchFitid: null, onlineMatchFiId: null, needsReview: false, providerRawPayload: null,
    headerAccountNetAmount: null, providerKey: null, isMergeWinner: false, importSource: null, derivedAction: 'buy',
    accountPostingsOnHeader: 2, headerTotalPostings: 2,
    investmentAction: 'buy', securityId: 'sec-a', securityTicker: 'SECA', securityName: 'Security A',
    quantity: 1, unitPrice: 100, postingRole: 'security', ingestActionHint: null, ingestSecurityId: null,
    ingestShares: null, ingestUnitPrice: null, ingestFee: null, ingestAmount: null, ingestSecurityTickerHint: null,
    categoryAccountId: null, categoryAccountName: null, categoryAccountType: null, transferAccountId: null,
    transferAccountName: null, transferAccountType: null, feeAmount: null, feeCategoryId: null, feeCategoryName: null,
    ...over,
});

/** One row as an entry of the ledger-wide read, on the row's own account. */
export const txnEntry = (row: RegisterRow, currencyCode = 'USD'): LedgerEntry => ({
    accountId: row.accountId, currencyCode, entry: { kind: 'txn', txn: row, groupId: null, legs: null },
});

/** A split on the account that owns it: one entry holding its lines. */
export const splitEntry = (groupId: string, legs: RegisterRow[], currencyCode = 'USD'): LedgerEntry => ({
    accountId: legs[0]!.accountId, currencyCode, entry: { kind: 'group', txn: null, groupId, legs },
});

/** The fixture's entries narrowed and cut as the server narrows them: the review
 *  flag, the cash-flow direction of the amount the row shows, the today cut, and
 *  the page size. The entries are given newest first. */
function entriesPage(all: readonly LedgerEntry[], search: URLSearchParams): LedgerEntriesPage {
    const direction = search.get('direction') ?? 'all';
    const today = search.get('today');
    const limit = Number(search.get('limit') ?? 50);
    const kept = all.filter(({ entry }) => {
        const first = entry.kind === 'txn' ? entry.txn : entry.legs[0]!;
        const shown = (entry.kind === 'txn' ? entry.txn.amount : groupAmount(entry.legs)) ?? 0;
        if (search.get('needs_review') === 'true' && !first.needsReview) return false;
        if (today !== null && first.postedAt.slice(0, 10) > today) return false;
        if (direction === 'outflow' && !(shown < 0)) return false;
        if (direction === 'inflow' && !(shown > 0)) return false;
        return true;
    });
    return { entries: kept.slice(0, limit), hasMore: kept.length > limit };
}

export const returns = (over: Partial<ReturnsResult> = {}): ReturnsResult => ({
    scope: 'ledger', startDate: '2026-01-01T00:00:00Z', endDate: '2026-03-12T23:59:59.999999Z',
    startValue: 1_000, endValue: 1_100, netContributions: 50, gain: 50,
    moneyWeightedReturn: 0.05, moneyWeightedUnavailableReason: null,
    timeWeightedReturn: 0.04, timeWeightedUnavailableReason: null,
    timeWeightedCoveredYears: 0.2, timeWeightedCoveredDays: 71,
    contributionsIn: 50, contributionsOut: 0, currencyCode: 'USD', rateUnavailable: [],
    ...over,
});

export const allocation = (over: Partial<AllocationResult> = {}): AllocationResult => ({
    buckets: [{ bucket: 'us_equity', marketValue: 1_000, percent: 100, currencyCode: 'USD', rateUnavailable: [] }],
    totalMarketValue: 1_000, asOf: '2026-03-12T23:59:59.999999Z', excludedBrokerageCash: 0, undecomposedMultiAssets: [],
    currencyCode: 'USD', rateUnavailable: [],
    ...over,
});

export interface HomeFixture {
    role?: 'owner' | 'editor' | 'viewer';
    /** The ledger's reporting currency, as the ledger list carries it; USD by default. */
    reportingCurrency?: string;
    /** The rates view (ADR-0114 D10); no pairs by default. */
    fxRates?: FxRatesView;
    prefs?: StoredDashboardPrefs | StubProblem;
    accounts?: AccountSummary[];
    upcoming?: UpcomingOccurrence[];
    reminders?: ReminderSummary[];
    schedules?: Record<string, Schedule>;
    attention?: LedgerAttention[];
    outstanding?: OutstandingProblem[];
    consistency?: LedgerConsistencyState;
    overview?: Partial<LedgerOverview>;
    budget?: Partial<BudgetProgress>;
    /** The ledger-wide entries, newest first. */
    entries?: LedgerEntry[];
    returns?: Partial<ReturnsResult>;
    allocation?: Partial<AllocationResult>;
    /** The saved reports the reader can see. */
    savedReports?: SavedReport[];
    /** The report summary, or a handler answering each request for one. */
    summary?: ReportResult | ((call: StubbedCall) => ReportResult | StubProblem);
    /** The balance forecast, or a handler answering each request for one. */
    forecast?: BalanceForecast | ((call: StubbedCall) => BalanceForecast | StubProblem);
}

export const overview = (over: Partial<LedgerOverview> = {}): LedgerOverview => ({
    netWorth: 1_000, totalAssets: 1_200, totalLiabilities: -200, investmentsValue: 0, currencyCode: 'USD',
    rateUnavailable: [],
    accountGroups: [{
        accountType: 'bank', subtotal: 1_200, rateUnavailable: [],
        accounts: [{ id: CHECKING, name: 'Account A', accountType: 'bank', currencyCode: 'USD', balance: 1_200, isActive: true, rateUnavailable: [] }],
    }],
    portfolio: {
        value: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, currencyCode: 'USD', rateUnavailable: [],
    },
    comparison: { since: '2026-02-28', netWorth: { then: 900, change: 100 }, noHistory: false, rateUnavailable: [] },
    ...over,
});

export const emptyReport = (): ReportResult => ({
    rows: [], total: 0, fromUtc: '2025-04-01T00:00:00Z', toUtc: '2026-03-13T00:00:00Z',
    periodTotals: [], groupTotals: [], currencyCode: 'USD',
});

export const emptyForecast = (): BalanceForecast => ({
    accounts: [], combined: null, includesEstimates: false, overdueOnDay1: 0, excluded: [], rateUnavailable: [],
});

/** A rate a converted figure needed and the ledger does not hold (ADR-0114 D9). */
export const missingRate = (date: string, fromCurrency = 'CAD', toCurrency = 'USD') => ({
    fromCurrency, toCurrency, date, reason: 'rate_unavailable' as const,
});

/** One pair of the rates view, fetched through `fetchedThroughDate`. */
export const fxPair = (over: Partial<FxPair> = {}): FxPair => ({
    fromCurrency: 'CAD', toCurrency: 'USD', neededFor: ['net-worth'], neededFrom: '2025-01-01',
    fetchedFromDate: '2025-01-01', fetchedThroughDate: '2026-03-06', latestManualDate: null,
    coveredThroughDate: '2026-03-06', rateCount: 300, firstRateDate: '2025-01-01', missingDays: [],
    neededBy: [], ...over,
});

export const budget = (over: Partial<BudgetProgress> = {}): BudgetProgress => ({
    month: '2026-03', windowMonths: 3, rows: [], actualTotal: 0, typicalTotal: null, currencyCode: 'USD',
    daysInMonth: 31, elapsedDays: 12, dailyActual: [], dailyNormal: [],
    latestMonthWithSpending: null, scheduledTotal: 0, historyFrom: null, ...over,
});

/** Answers every read the home screen makes, from the fixture. */
export function homeStub(f: HomeFixture = {}): FetchStub {
    const stub = stubFetch();
    const accounts = f.accounts ?? [account({ id: CHECKING, name: 'Account A', accountType: 'bank' })];
    stub
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role: f.role ?? 'owner', currencyCode: f.reportingCurrency ?? 'USD' }])
        .on('GET', /\/fx-rates$/, f.fxRates ?? { reportingCurrency: f.reportingCurrency ?? 'USD', pairs: [] })
        .on('GET', /^\/api\/auth\/me$/, { id: 'u1', username: 'user', displayName: 'User', isAdmin: false })
        .on('GET', /\/api\/ledgers\/attention$/, f.attention ?? [])
        .on('GET', /\/preferences\/dashboard$/, f.prefs ?? { widgets: [], updatedAt: null })
        .on('GET', /\/accounts$/, accounts)
        .on('GET', /\/reminders\/upcoming$/, f.upcoming ?? [])
        .on('GET', /\/reminders$/, f.reminders ?? [])
        .on('GET', /\/schedules$/, f.schedules ?? { 'reminder-auto-post': schedule() })
        .on('GET', /\/balances\/consistency\/state$/, f.consistency ?? { lastCheckedAt: null, healthy: null, failingProjections: [], lastRepairedAt: null })
        .on('GET', /\/notifications\/outstanding$/, f.outstanding ?? [])
        .on('GET', /\/feed-connections$/, [])
        .on('GET', /\/snapshots$/, [])
        .on('GET', /\/securities$/, [])
        .on('GET', /\/overview$/, overview(f.overview))
        .on('GET', /\/reports\/net-worth-history$/, { fromUtc: '', toUtc: '', interval: 'month', currencyCode: 'USD', points: [] })
        .on('GET', /\/reports\/summary$/, f.summary ?? emptyReport())
        .on('GET', /\/reports\/saved$/, f.savedReports ?? [])
        .on('GET', /\/accounts\/balance-forecast$/, f.forecast ?? emptyForecast())
        .on('GET', /\/budget\/progress$/, budget(f.budget))
        .on('GET', /\/ledger-operations$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/transactions\/entries$/, (call: StubbedCall) => entriesPage(f.entries ?? [], call.search))
        .on('GET', /\/reports\/returns$/, returns(f.returns))
        .on('GET', /\/reports\/allocation$/, allocation(f.allocation));
    return stub;
}

/**
 * Customise, once the stored layout has been read. While it is read Customise is
 * pending, which refuses a press but is not disabled, so being enabled alone does
 * not say the read has answered.
 */
export function customiseWhenRead(): Promise<HTMLElement> {
    return waitFor(() => {
        const customise = screen.getByRole('button', { name: 'Customise' });
        expect(customise).toBeEnabled();
        expect(customise).not.toHaveAttribute('aria-busy');
        return customise;
    });
}

/** The home screen at /ledgers/LEDGER, with every route its links reach. */
export function renderHome() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const stubPage = () => <main>elsewhere</main>;
    const paths = [
        '/ledgers/$ledgerId/settings', '/ledgers/$ledgerId/reminders', '/ledgers/$ledgerId/accounts',
        '/ledgers/$ledgerId/accounts/$accountId', '/ledgers/$ledgerId/budget', '/ledgers/$ledgerId/reports/view',
        '/ledgers/$ledgerId/securities', '/ledgers/$ledgerId/categories', '/imports/moneydance', '/system',
    ];
    const home = createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId', component: HomePage });
    const others = paths.map((path) => createRoute({ getParentRoute: () => root, path, component: stubPage }));
    const router = createRouter({
        routeTree: root.addChildren([home, ...others]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}`] }),
        context: { queryClient },
    });
    const rendered = render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    return { ...rendered, queryClient, router };
}
