import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { problem } from '@/lib/annotations/fetchStub';
import { formatLedgerDate } from '@/lib/dates';
import { currencyAffix, formatCurrency } from '@/lib/money';

import { BudgetPage } from './BudgetPage';
import { PROJECTION_LEGEND } from './budget/cumulative';
import { account, budget, CHECKING, fxPair, homeStub, LEDGER, missingRate } from './home/homeTestFixtures';

// The Budget page's current month, through the real page and API client with the
// server mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

function renderBudget() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const budget = createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/budget', component: BudgetPage });
    const router = createRouter({
        routeTree: root.addChildren([budget]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/budget`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('the current month', () => {
    it('says how much of Spent is scheduled, on the hero and on the Total row (ADR-0109 D10)', async () => {
        homeStub({
            budget: {
                actualTotal: 1_660, scheduledTotal: 1_520, elapsedDays: 12, daysInMonth: 31,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 1_660, typical: 300, target: null, targetCurrencyCode: null, mark: 300 }],
                dailyActual: [
                    { categoryId: 'cat-a', day: 2, amount: 140 },
                    { categoryId: 'cat-a', day: 20, amount: 1_520 },
                ],
            },
        });
        renderBudget();

        expect(await screen.findByText(/incl\. \$1,520\.00 scheduled/)).toBeInTheDocument();
        const total = screen.getByText('Total').parentElement as HTMLElement;
        expect(total.textContent).toContain('incl. $1,520.00 scheduled');
        // The circle the curve draws on the scheduled bill's date is explained, and
        // so is what the dashed line adds up.
        expect(document.querySelector('[data-scheduled-step="20"]')).not.toBeNull();
        expect(screen.getByText(PROJECTION_LEGEND)).toBeInTheDocument();
    });

    it('projects the month from the baseline the band draws, so a payment on the 1st counts once', async () => {
        // The window paid MONTHLY on the 1st and DAILY on every day after it; this
        // month has done the same so far.
        const MONTHLY = 1_200;
        const DAILY = 20;
        const DAYS = 31;
        const TODAY = 12;
        const days = (from: number, through: number) =>
            Array.from({ length: through - from + 1 }, (_, i) => ({ categoryId: 'cat-a', day: from + i, amount: DAILY }));
        const SPENT = MONTHLY + DAILY * (TODAY - 1);
        const USUAL_MONTH = MONTHLY + DAILY * (DAYS - 1);
        const CEILING = USUAL_MONTH * 2;
        homeStub({
            budget: {
                actualTotal: SPENT, elapsedDays: TODAY, daysInMonth: DAYS, typicalTotal: CEILING,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: SPENT, typical: CEILING, target: null, targetCurrencyCode: null, mark: CEILING }],
                dailyActual: [{ categoryId: 'cat-a', day: 1, amount: MONTHLY }, ...days(2, TODAY)],
                dailyNormal: [{ categoryId: 'cat-a', day: 1, amount: MONTHLY }, ...days(2, DAYS)],
            },
        });
        renderBudget();

        const label = await screen.findByText('on track for, by month end');
        expect(label.previousElementSibling).toHaveTextContent(formatCurrency(USUAL_MONTH, 'USD'));
    });

    it('counts what the window usually spends on days a short month lacks, on its last day', async () => {
        // A 28-day month after longer ones: the window paid FIRST on the 1st and
        // LATE on the 30th, and this month has paid FIRST.
        vi.setSystemTime(new Date(2026, 1, 12, 15, 0, 0));
        const FIRST = 100;
        const LATE = 60;
        const DAYS = 28;
        const CEILING = (FIRST + LATE) * 2;
        homeStub({
            budget: {
                month: '2026-02', actualTotal: FIRST, elapsedDays: 12, daysInMonth: DAYS, typicalTotal: CEILING,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: FIRST, typical: CEILING, target: null, targetCurrencyCode: null, mark: CEILING }],
                dailyActual: [{ categoryId: 'cat-a', day: 1, amount: FIRST }],
                dailyNormal: [{ categoryId: 'cat-a', day: 1, amount: FIRST }, { categoryId: 'cat-a', day: 30, amount: LATE }],
            },
        });
        renderBudget();

        const label = await screen.findByText('on track for, by month end');
        expect(label.previousElementSibling).toHaveTextContent(formatCurrency(FIRST + LATE, 'USD'));
    });

    describe('the captions that name the window', () => {
        const stub = (historyFrom: string | null) => homeStub({
            budget: {
                actualTotal: 140, elapsedDays: 12, daysInMonth: 31, typicalTotal: 300, historyFrom,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 140, typical: 300, target: null, targetCurrencyCode: null, mark: 300 }],
                dailyActual: [{ categoryId: 'cat-a', day: 2, amount: 140 }],
                dailyNormal: [{ categoryId: 'cat-a', day: 2, amount: 300 }],
            },
        });

        it('name the whole window for a ledger at least as old as it', async () => {
            stub(null);
            renderBudget();
            expect(await screen.findByText('shaded band = your last 3 months, same days')).toBeInTheDocument();
            expect(screen.getByText('unset = average of the 3 months before March 2026')).toBeInTheDocument();
            expect(screen.getByRole('button', { name: 'Fill from 3-month average' })).toBeInTheDocument();
        });

        it('say the true count for a ledger that began on a month\'s first day inside it', async () => {
            stub('2026-02-01');
            renderBudget();
            expect(await screen.findByText('shaded band = your last month, same days')).toBeInTheDocument();
            expect(screen.getByText('unset = average of the month before March 2026')).toBeInTheDocument();
            expect(screen.getByRole('button', { name: 'Fill from 1-month average' })).toBeInTheDocument();
            expect(screen.queryByText(/3 months/)).toBeNull();
        });

        it('name the day the history starts for a ledger that began mid-month inside it', async () => {
            const FROM = '2026-01-15';
            const day = formatLedgerDate(FROM);
            stub(FROM);
            renderBudget();
            expect(await screen.findByText(`shaded band = your months since ${day}, same days`)).toBeInTheDocument();
            expect(screen.getByText(`unset = average of your months since ${day}, before March 2026`)).toBeInTheDocument();
            expect(screen.getByRole('button', { name: `Fill from average since ${day}` })).toBeInTheDocument();
            expect(screen.queryByText(/3 months/)).toBeNull();
        });

        it('say there is no history for a ledger that began this month, with no caption for a band it does not have', async () => {
            stub('2026-03-02');
            renderBudget();
            expect(await screen.findByText('unset = no history before March 2026')).toBeInTheDocument();
            expect(screen.getByRole('button', { name: 'Fill from average' })).toBeInTheDocument();
            expect(screen.queryByText(/shaded band/)).toBeNull();
        });
    });

    it('reads a figure with a missing rate "no rate", as the Overview does, and draws no curve', async () => {
        const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-05', reason: 'rate_unavailable' as const };
        homeStub({
            budget: {
                actualTotal: null, typicalTotal: 80, scheduledTotal: 20, rateUnavailable: [missing],
                rows: [
                    { categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 70, typical: 40, target: null, targetCurrencyCode: null, mark: 40 },
                    { categoryId: 'cat-b', name: 'Category B', parentId: null, actual: null, typical: 40, target: null, targetCurrencyCode: null, mark: 40, rateUnavailable: [missing] },
                ],
                dailyActual: [
                    { categoryId: 'cat-a', day: 2, amount: 50 },
                    { categoryId: 'cat-a', day: 20, amount: 20 },
                    { categoryId: 'cat-b', day: 5, amount: null, rateUnavailable: [missing] },
                ],
            },
        });
        renderBudget();

        // The hero's line in place of the curve, then the figures it settles.
        expect(await screen.findByText(/^No CAD to USD rate for Mar 5, 2026\./)).toBeInTheDocument();
        expect(screen.queryByRole('img')).toBeNull();
        const total = screen.getByText('Total').parentElement as HTMLElement;
        expect(within(total).getByText('no rate')).toHaveAttribute('title', 'No CAD to USD rate for Mar 5, 2026.');
        const row = screen.getByRole('button', { name: /^Category B/ }).closest('tr') as HTMLElement;
        expect(within(row).getAllByText('no rate').length).toBeGreaterThan(0);
        // Every figure that has its rates still prints.
        expect(document.body.textContent).toContain('$70.00');
        expect(within(total).getByText('$80.00')).toBeInTheDocument();
    });
});

describe('converted figures (ADR-0114 D7, D9)', () => {
    const ACCOUNTS = [
        account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
        account({ id: 'acct-b', name: 'Account B', accountType: 'bank', currencyCode: 'CAD' }),
    ];
    const header = () => screen.findByRole('heading', { level: 1, name: 'Budget' })
        .then((h) => h.closest('header') as HTMLElement);

    it('says under the header that totals are converted, when an account is in another currency', async () => {
        homeStub({ accounts: ACCOUNTS, budget: { actualTotal: 10, rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 10, typical: 20, target: null, targetCurrencyCode: null, mark: 20 }] } });
        renderBudget();

        const note = await within(await header()).findByText(/^Totals are in USD, with CAD accounts converted at the rate for each figure's date \(the latest rate from today on\)\./);
        expect(within(note).getByRole('link', { name: 'Exchange rates →' })).toBeInTheDocument();
    });

    it('carries no note when every account is in the reporting currency', async () => {
        homeStub({ budget: { actualTotal: 10, rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 10, typical: 20, target: null, targetCurrencyCode: null, mark: 20 }] } });
        renderBudget();

        const head = await header();
        // Settled: the breadcrumb names the ledger from the read the note's currency comes from.
        await screen.findByText('Synthetic');
        await screen.findByRole('button', { name: /^Category A/ });
        expect(within(head).queryByText(/^Totals are in/)).toBeNull();
    });

    it('says how far rates are fetched where the curve would be', async () => {
        const missing = missingRate('2026-03-08');
        homeStub({
            accounts: ACCOUNTS,
            fxRates: { reportingCurrency: 'USD', pairs: [fxPair({ fetchedThroughDate: '2026-03-06' })] },
            budget: {
                actualTotal: null, rateUnavailable: [missing],
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: null, typical: 20, target: null, targetCurrencyCode: null, mark: 20, rateUnavailable: [missing] }],
                dailyActual: [{ categoryId: 'cat-a', day: 8, amount: null, rateUnavailable: [missing] }],
            },
        });
        renderBudget();

        const line = await screen.findByText(/^No CAD to USD rate for Mar 8, 2026\./);
        await waitFor(() => expect(line).toHaveTextContent('Rates are fetched through Mar 6.'));
        // The mark's tick still stands in the row; the spend draws no run.
        const row = screen.getByRole('button', { name: /^Category A/ }).closest('tr') as HTMLElement;
        const bar = within(row).getAllByRole('cell')[4]!;
        expect(bar.querySelector('.bg-border-strong')).not.toBeNull();
        expect(bar.querySelector('.bg-accent')).toBeNull();
    });
});

describe('Copy last month', () => {
    // The server copies last month's stored targets, each in the currency it was
    // typed in (ADR-0114 D12), so the copy names the month and sends no amounts. A
    // fill is pending from the press until it has answered: a press made meanwhile
    // sends no second fill.
    const FILL = /\/budget\/targets\/fill$/;
    const ROW = { categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 10, typical: 30, mark: 30 };

    /** The page, this month holding `target`, with the fill held until `release` and then answered with `answer`. */
    function renderHeld(opts: { target: number | null; targetCurrencyCode?: string | null; answer?: unknown }) {
        const stub = homeStub();
        const targetCurrencyCode = opts.targetCurrencyCode !== undefined ? opts.targetCurrencyCode
            : opts.target === null ? null : 'USD';
        stub.on('GET', /\/budget\/progress$/, budget({ rows: [{ ...ROW, target: opts.target, targetCurrencyCode }] }));
        stub.on('POST', FILL, opts.answer ?? { applied: 1, skipped: [] });
        const answer = globalThis.fetch;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            if (FILL.test(new URL(raw, 'http://localhost').pathname)) await held;
            return answer(input, init);
        });
        renderBudget();
        return { stub, release };
    }

    it('names last month and sends no entries, once for every press made while it runs', async () => {
        const { stub, release } = renderHeld({ target: null });
        const user = userEvent.setup();

        const copy = await screen.findByRole('button', { name: 'Copy last month' });
        await user.click(copy);
        expect(copy).toHaveAttribute('aria-busy', 'true');
        expect(copy).toHaveFocus();
        await user.click(copy);
        await user.keyboard('{Enter}');

        release();
        expect(await screen.findByText('Set 1 target.')).toBeInTheDocument();
        await waitFor(() => expect(copy).not.toHaveAttribute('aria-busy'));
        const fills = stub.callsTo('POST', FILL);
        expect(fills).toHaveLength(1);
        expect(fills[0]!.body).toEqual({ month: '2026-03', copyFromMonth: '2026-02' });
        // Last month is not read to build the copy.
        expect(stub.calls.filter((c) => c.search.get('month') === '2026-02')).toHaveLength(0);
    });

    it('sends one fill when the month already has targets and Replace is confirmed', async () => {
        const { stub, release } = renderHeld({ target: 20 });
        const user = userEvent.setup();

        const copy = await screen.findByRole('button', { name: 'Copy last month' });
        await user.click(copy);
        await user.click(await screen.findByRole('button', { name: 'Replace' }));
        expect(copy).toHaveAttribute('aria-busy', 'true');
        await user.click(copy);
        expect(screen.queryByRole('dialog')).toBeNull();

        release();
        expect(await screen.findByText('Set 1 target.')).toBeInTheDocument();
        expect(stub.callsTo('POST', FILL)).toHaveLength(1);
    });

    it('asks before replacing a stored target that did not convert (ADR-0114 D9)', async () => {
        // Target null for a missing rate, and its own currency to say it is stored: the
        // month has a target, and the copy would replace it.
        const { stub, release } = renderHeld({ target: null, targetCurrencyCode: 'EUR' });
        release();

        await userEvent.setup().click(await screen.findByRole('button', { name: 'Copy last month' }));

        expect(await screen.findByRole('button', { name: 'Replace' })).toBeInTheDocument();
        expect(stub.callsTo('POST', FILL)).toHaveLength(0);
    });

    it('says last month has no targets when the copy applies and skips none', async () => {
        const { stub, release } = renderHeld({ target: null, answer: { applied: 0, skipped: [] } });
        release();

        await userEvent.setup().click(await screen.findByRole('button', { name: 'Copy last month' }));

        expect(await screen.findByText('February 2026 has no targets to copy.')).toBeInTheDocument();
        expect(stub.callsTo('POST', FILL)).toHaveLength(1);
        expect(screen.queryByText(/^Set 0/)).toBeNull();
    });

    it('names what the copy skipped', async () => {
        const { release } = renderHeld({
            target: null,
            answer: { applied: 0, skipped: [{ categoryId: 'cat-a', status: 'conflict', conflictingCategoryName: 'Category Z' }] },
        });
        release();

        await userEvent.setup().click(await screen.findByRole('button', { name: 'Copy last month' }));

        expect(await screen.findByText(/^Set 0, skipped 1: Category Z/)).toBeInTheDocument();
        expect(screen.queryByText('February 2026 has no targets to copy.')).toBeNull();
    });

    it('says why when the copy is refused', async () => {
        const { release } = renderHeld({ target: null, answer: problem(422, 'A copy names the month to copy from and carries no entries.', 'budget-range-invalid') });
        release();

        await userEvent.setup().click(await screen.findByRole('button', { name: 'Copy last month' }));

        expect(await screen.findByText('A copy names the month to copy from and carries no entries.')).toBeInTheDocument();
    });
});

describe('by role (ADR-0023 §L)', () => {
    // One category with a target, so every write control the page has is drawn.
    const month = {
        actualTotal: 100,
        rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 100, typical: 250, target: 300, targetCurrencyCode: 'USD', mark: 300 }],
    };
    const settledRow = async () => {
        // The breadcrumb names the ledger from the read that also carries the role.
        await screen.findByText('Synthetic');
        return (await screen.findByRole('button', { name: /Category A/ })).closest('tr') as HTMLElement;
    };

    it('gives a viewer every figure and no fill, copy or target to set', async () => {
        homeStub({ role: 'viewer', budget: month });
        renderBudget();
        const row = await settledRow();

        expect(within(row).getByText('$300.00')).toBeInTheDocument();
        expect(within(row).queryByRole('button', { name: '$300.00' })).toBeNull();
        expect(within(row).queryByRole('textbox')).toBeNull();
        expect(screen.queryByRole('button', { name: 'Copy last month' })).toBeNull();
        expect(screen.queryByRole('button', { name: /^Fill from/ })).toBeNull();
    });

    it.each(['owner', 'editor'] as const)('keeps every write control for an %s', async (role) => {
        homeStub({ role, budget: month });
        renderBudget();
        const row = await settledRow();

        expect(within(row).getByRole('button', { name: '$300.00' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Copy last month' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /^Fill from/ })).toBeInTheDocument();
    });
});

// A target is typed in the reporting currency, which the save stores (ADR-0114 D12): its
// input shows that currency's symbol and takes its places, and Fill from average rounds
// each average at them, half away from zero (ADR-0114 D14, D15).
describe('targets at the reporting currency\'s places', () => {
    const TARGETS = /\/budget\/targets$/;
    const FILL = /\/budget\/targets\/fill$/;
    const row = (over: Record<string, unknown> = {}) => ({
        categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 10, typical: 30,
        target: null, targetCurrencyCode: null, mark: 30, ...over,
    });
    const onWholeUnits = (rows = [row()]) => {
        const stub = homeStub({ reportingCurrency: 'JPY', budget: { currencyCode: 'JPY', rows } });
        stub.on('PUT', TARGETS, () => undefined).on('POST', FILL, { applied: 1, skipped: [] });
        return stub;
    };

    it('shows a whole-unit currency\'s symbol and commits a typed fraction whole', async () => {
        const stub = onWholeUnits();
        renderBudget();
        const user = userEvent.setup();

        await user.click(await screen.findByTitle('Set a target'));
        const input = screen.getByRole('spinbutton', { name: 'Target for Category A' });
        expect(input.parentElement!.querySelector('[data-money-affix]')!.textContent).toBe(currencyAffix('JPY')!.symbol);
        expect(input).toHaveAttribute('step', '1');
        await user.type(input, '1234.5{Enter}');

        await waitFor(() => expect(stub.callsTo('PUT', TARGETS)).toHaveLength(1));
        expect(stub.callsTo('PUT', TARGETS)[0]!.body).toMatchObject({ categoryId: 'cat-a', amount: 1235 });
    });

    it('fills from the average at the currency\'s places, a decimal midpoint away from zero', async () => {
        // A midpoint the binary double holds just under: a ×100 rounding takes it down.
        const stub = homeStub({ budget: { rows: [row({ typical: 1.005, mark: 1.005 })] } });
        stub.on('POST', FILL, { applied: 1, skipped: [] });
        renderBudget();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /^Fill from/ }));

        await waitFor(() => expect(stub.callsTo('POST', FILL)).toHaveLength(1));
        expect(stub.callsTo('POST', FILL)[0]!.body).toMatchObject({ entries: [{ categoryId: 'cat-a', amount: 1.01 }] });
    });

    it('fills a whole-unit ledger with whole figures', async () => {
        const stub = onWholeUnits([row({ typical: 30.5, mark: 30.5 })]);
        renderBudget();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /^Fill from/ }));

        await waitFor(() => expect(stub.callsTo('POST', FILL)).toHaveLength(1));
        expect(stub.callsTo('POST', FILL)[0]!.body).toMatchObject({ entries: [{ categoryId: 'cat-a', amount: 31 }] });
    });

    it('keeps the Enter-clears placeholder for a target typed in a currency with no rate', async () => {
        const missing = { fromCurrency: 'EUR', toCurrency: 'JPY', date: '2026-03-31', reason: 'rate_unavailable' as const };
        onWholeUnits([row({ target: null, targetCurrencyCode: 'EUR', mark: null, rateUnavailable: [missing] })]);
        renderBudget();
        const user = userEvent.setup();

        await user.click(await screen.findByTitle(/^Your target\. Typed in EUR/));
        const input = screen.getByRole('spinbutton', { name: 'Target for Category A' });
        expect(input).toHaveAttribute('placeholder', 'Enter clears');
        expect(input).toHaveValue(null);
    });
});
