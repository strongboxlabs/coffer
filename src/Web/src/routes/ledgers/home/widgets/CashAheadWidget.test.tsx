import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { StubbedCall } from '@/lib/annotations/fetchStub';
import { formatCurrency } from '@/lib/money';
import type { BalanceForecast, BalanceForecastAccount, BalanceForecastPoint } from '@/lib/types';

import {
    account,
    CARD,
    CHECKING,
    customiseWhenRead,
    emptyForecast,
    homeStub,
    layoutWith,
    LEDGER,
    renderHome,
    series,
    type HomeFixture,
} from '../homeTestFixtures';

// Cash ahead (ADR-0109, the balance forecast) through the real page, API client and router, with the
// server mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const ACCOUNTS = [
    account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
    account({ id: CARD, name: 'Account B', accountType: 'credit_card' }),
];

/** Daily points from the fixture's today, one balance per day. */
const points = (balances: number[]): BalanceForecastPoint[] =>
    balances.map((balance, i) => ({ date: `2026-03-${String(12 + i).padStart(2, '0')}`, balance }));

const checking: BalanceForecastAccount = {
    accountId: CHECKING, name: 'Account A', currencyCode: 'USD', today: 500,
    lowest: { amount: 120, date: '2026-03-15', estimated: false }, points: points([500, 500, 480, 120, 900]),
};
const card: BalanceForecastAccount = {
    accountId: CARD, name: 'Account B', currencyCode: 'USD', today: -40,
    lowest: { amount: -90, date: '2026-03-14', estimated: false }, points: points([-40, -40, -90, -90, -90]),
};

/** The whole scope, listed in the server's order: the card first. */
const forecast = (over: Partial<BalanceForecast> = {}): BalanceForecast => ({
    ...emptyForecast(),
    accounts: [card, checking],
    combined: {
        currencyCode: 'USD', today: 460, lowest: { amount: 30, date: '2026-03-15', estimated: false },
        points: points([460, 460, 390, 30, 810]),
    },
    ...over,
});

function stub(f: HomeFixture & { settings?: Record<string, unknown> } = {}) {
    const { settings, ...rest } = f;
    const s = homeStub({
        accounts: ACCOUNTS,
        forecast: forecast(),
        prefs: layoutWith({ 'cash-forecast': { visible: true, settings: settings as never } }),
        ...rest,
    });
    s.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
    return s;
}

const FORECAST = /\/accounts\/balance-forecast$/;

const widget = () => screen.findByRole('heading', { level: 2, name: /^Cash ahead/ })
    .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

/** The widget once its projection is drawn: its rows are on the screen. */
async function settled(): Promise<HTMLElement> {
    const panel = await widget();
    await within(panel).findByRole('table');
    return panel;
}

/** One account's row, by the account's name. */
const row = (panel: HTMLElement, name: string) => within(panel).getByRole('link', { name }).closest('tr') as HTMLElement;
const rowNames = (panel: HTMLElement) =>
    within(panel).getAllByRole('row').slice(1).map((r) => within(r).getByRole('link').textContent);

describe('Cash ahead', () => {
    it('lists each account with today\'s balance and its lowest, with the day', async () => {
        stub();
        renderHome();
        const panel = await settled();

        const a = row(panel, 'Account A');
        expect(within(a).getAllByRole('cell').map((c) => c.textContent)).toEqual(['$500.00', '$120.00 Mar 15']);
        const b = row(panel, 'Account B');
        expect(within(b).getAllByRole('cell').map((c) => c.textContent)).toEqual([formatCurrency(-40, 'USD'), `${formatCurrency(-90, 'USD')} Mar 14`]);
        // A balance is a total whose sign is fixed by what it is: never red.
        expect(panel.querySelector('.text-state-danger')).toBeNull();
        expect(within(panel).getByText('Projection from scheduled items; not a forecast of anything unscheduled.')).toBeInTheDocument();
    });

    it('marks "≈" on each lowest figure an estimate went into, and only those', async () => {
        stub({
            forecast: forecast({
                includesEstimates: true,
                accounts: [card, { ...checking, lowest: { ...checking.lowest, estimated: true } }],
            }),
        });
        renderHome();
        const panel = await settled();

        expect(within(row(panel, 'Account A')).getAllByRole('cell')[1]).toHaveTextContent(/^≈ \$120\.00 Mar 15$/);
        expect(within(row(panel, 'Account B')).getAllByRole('cell')[1]!.textContent).not.toContain('≈');
        // Today's balance is the register's own, never an estimate.
        expect(within(row(panel, 'Account A')).getAllByRole('cell')[0]).toHaveTextContent(/^\$500\.00$/);
        // The caption follows the projection's own flag.
        expect(within(panel).getByText(/^Projection from scheduled items · includes estimates;/)).toBeInTheDocument();
        // The combined lowest had none, so the chart's figure is not marked.
        expect(within(panel).getByRole('img').getAttribute('aria-label')).toContain('lowest $30.00');
    });

    it('marks the chart\'s lowest "≈" when an estimate went into it', async () => {
        stub({
            forecast: forecast({
                includesEstimates: true,
                combined: { ...forecast().combined!, lowest: { amount: 30, date: '2026-03-15', estimated: true } },
            }),
        });
        renderHome();
        const panel = await settled();
        expect(within(panel).getByRole('img').getAttribute('aria-label')).toContain('lowest ≈ $30.00');
        expect(within(row(panel, 'Account A')).getAllByRole('cell')[1]!.textContent).not.toContain('≈');
    });

    it('says how many overdue reminders it placed on day 1', async () => {
        stub({ forecast: forecast({ overdueOnDay1: 2 }) });
        renderHome();
        const panel = await settled();
        expect(within(panel).getByText('Projection from scheduled items · includes 2 overdue; not a forecast of anything unscheduled.')).toBeInTheDocument();
    });

    it('names each reminder it could not project, each opening that reminder\'s editor', async () => {
        stub({
            forecast: forecast({ excluded: [{ reminderId: 'series-a', reason: 'no-lines' }, { reminderId: 'series-b', reason: 'cross-currency' }] }),
            reminders: [series({ id: 'series-a', payee: 'Payee A' }), series({ id: 'series-b', payee: 'Payee B' })],
        });
        renderHome();
        const panel = await settled();

        const note = await waitFor(() => {
            const p = panel.querySelector<HTMLElement>('[data-excluded]');
            expect(p).toHaveTextContent('2 reminders not projected: Payee A · Payee B');
            return p!;
        });
        for (const [name, id] of [['Payee A', 'series-a'], ['Payee B', 'series-b']] as const) {
            const href = new URL(within(note).getByRole('link', { name }).getAttribute('href')!, 'http://x');
            expect(href.pathname).toBe(`/ledgers/${LEDGER}/reminders`);
            expect(href.searchParams.get('reminder')).toBe(id);
        }
        // No reason text: the editor it opens says what the reminder is.
        expect(note.textContent).not.toMatch(/no-lines|currency/);
    });

    it('names each missing stored leg and its day on a series left out for want of a rate (ADR-0114 D13)', async () => {
        stub({
            forecast: forecast({ excluded: [
                { reminderId: 'series-a', reason: 'no-lines' },
                { reminderId: 'series-b', reason: 'rate-unavailable', rateUnavailable: [
                    { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-03-11', reason: 'rate_unavailable' },
                    { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-03-11', reason: 'rate_unavailable' },
                ] },
            ] }),
            reminders: [series({ id: 'series-a', payee: 'Payee A' }), series({ id: 'series-b', payee: 'Payee B' })],
        });
        renderHome();
        const panel = await settled();

        await waitFor(() => {
            const p = panel.querySelector<HTMLElement>('[data-excluded]');
            expect(p).toHaveTextContent(
                '2 reminders not projected: Payee A · Payee B (No EUR or GBP to USD rate for Mar 11, 2026)');
        });
        // Only that row names a rate: the other keeps to its name.
        expect(panel.querySelectorAll('[data-excluded-rate]')).toHaveLength(1);
    });

    it('names each reminder it could not project to a viewer as text: the editor would refuse them', async () => {
        stub({
            role: 'viewer',
            forecast: forecast({ excluded: [{ reminderId: 'series-a', reason: 'no-lines' }, { reminderId: 'series-b', reason: 'cross-currency' }] }),
            reminders: [series({ id: 'series-a', payee: 'Payee A' }), series({ id: 'series-b', payee: 'Payee B' })],
        });
        renderHome();
        const panel = await settled();

        const note = await waitFor(() => {
            const p = panel.querySelector<HTMLElement>('[data-excluded]');
            expect(p).toHaveTextContent('2 reminders not projected: Payee A · Payee B');
            return p!;
        });
        expect(within(note).queryAllByRole('link')).toEqual([]);
    });

    it('says nothing of exclusions when every reminder was projected', async () => {
        stub();
        renderHome();
        const panel = await settled();
        // Anchored on the caption the footnote would follow.
        expect(within(panel).getByText(/^Projection from scheduled items/)).toBeInTheDocument();
        expect(panel.querySelector('[data-excluded]')).toBeNull();
        expect(within(panel).queryByText(/not projected/)).toBeNull();
    });

    // Asserted on structure: jsdom lays nothing out and applies no container query,
    // so what each width does is read off the classes that do it. The real browser is
    // the check that they do (the name's own line below 22.5rem, a line per figure
    // below 18rem, nothing past the widget at any width).
    describe('its rows at every width', () => {
        it('lays the rows on one grid of three columns, each part keeping its table role', async () => {
            stub();
            renderHome();
            const panel = await settled();
            const table = within(panel).getByRole('table');
            expect(table).toHaveAttribute('role', 'table');
            expect(table.className).toMatch(/(^|\s)grid(\s|$)/);
            expect(table.className).toContain('grid-cols-[minmax(0,1fr)_minmax(min-content,max-content)_minmax(min-content,max-content)]');
            for (const group of table.querySelectorAll('thead, tbody')) {
                expect(group).toHaveAttribute('role', 'rowgroup');
                expect(group.className).toContain('grid-cols-subgrid');
            }
            for (const tr of table.querySelectorAll('tr')) {
                expect(tr).toHaveAttribute('role', 'row');
                expect(tr.className).toContain('grid-cols-subgrid');
            }
            for (const th of table.querySelectorAll('thead th')) expect(th).toHaveAttribute('role', 'columnheader');
            const a = row(panel, 'Account A');
            expect(a.querySelector('th')).toHaveAttribute('role', 'rowheader');
            for (const td of a.querySelectorAll('td')) expect(td).toHaveAttribute('role', 'cell');
        });

        it('gives the name what the figures leave, truncating with its title, and its own line when narrow', async () => {
            stub();
            renderHome();
            const panel = await settled();
            for (const name of ['Account A', 'Account B']) {
                const cell = row(panel, name).querySelector<HTMLElement>('[data-account-name]')!;
                expect(cell).toHaveAttribute('role', 'rowheader');
                expect(cell.className).toContain('col-start-1');
                expect(cell.className).toContain('min-w-0');
                expect(cell.className).toContain('@max-[22.5rem]/panel:col-span-full');
                const link = within(cell).getByRole('link', { name });
                expect(link.className).toMatch(/(^|\s)block(\s|$)/);
                expect(link.className).toContain('truncate');
                expect(link).toHaveAttribute('title', name);
            }
            // The figures keep their columns under the name until the narrowest step.
            const [today, lowest] = within(row(panel, 'Account A')).getAllByRole('cell');
            expect(today!.className).toContain('col-start-2');
            expect(lowest!.className).toContain('col-start-3');
            expect(today!.className).not.toContain('@max-[22.5rem]');
            expect(lowest!.className).not.toContain('@max-[22.5rem]');
        });

        it('puts each figure on a line of its own at the narrowest, named, with the headers kept for assistive technology', async () => {
            stub();
            renderHome();
            const panel = await settled();
            const [today, lowest] = within(row(panel, 'Account A')).getAllByRole('cell');
            for (const cell of [today!, lowest!]) {
                expect(cell.className).toContain('@max-2xs/panel:col-span-full');
                expect(cell.className).toContain('@max-2xs/panel:flex-wrap');
                expect(cell.className).toContain('@max-2xs/panel:justify-end');
                expect(cell.className).toContain('@max-2xs/panel:before:mr-auto');
            }
            // The word beside each figure is generated content with no alternative
            // text, so the cell reads as its figure alone.
            expect(today!.className).toContain("@max-2xs/panel:before:[content:'Today'_/_'']");
            expect(lowest!.className).toContain("@max-2xs/panel:before:[content:'Lowest'_/_'']");
            expect(today).toHaveTextContent(/^\$500\.00$/);
            const head = within(panel).getByRole('columnheader', { name: 'Today' }).closest('thead')!;
            expect(head.className).toContain('@max-2xs/panel:sr-only');
            expect(head.className).not.toMatch(/(^|\s)hidden(\s|$)/);
        });

        it('keeps the lowest figure and its day each whole, the day free to go under the figure', async () => {
            stub();
            renderHome();
            const panel = await settled();
            const [today, lowest] = within(row(panel, 'Account A')).getAllByRole('cell');
            expect(today!.className).toContain('whitespace-nowrap');
            expect(lowest!.className).not.toContain('whitespace-nowrap');
            const day = within(lowest!).getByText('Mar 15');
            expect(day.className).toContain('whitespace-nowrap');
            const figure = within(lowest!).getByText(formatCurrency(120, 'USD')).parentElement!;
            expect(figure.className).toContain('whitespace-nowrap');
            expect(figure).not.toContainElement(day);
        });
    });

    it('opens each account\'s register from its row', async () => {
        stub();
        renderHome();
        const panel = await settled();
        expect(within(row(panel, 'Account B')).getByRole('link', { name: 'Account B' }))
            .toHaveAttribute('href', `/ledgers/${LEDGER}/accounts/${CARD}`);
    });
});

describe('the chart: combined, or one account', () => {
    it('charts the accounts combined by default', async () => {
        stub();
        renderHome();
        const panel = await settled();

        expect(within(panel).getByText('Combined')).toBeInTheDocument();
        expect(within(panel).getByRole('img')).toHaveAttribute('aria-label',
            'Combined projected from scheduled items: lowest $30.00 on Mar 15, never below zero');
        // The ring marks the lowest point.
        expect(panel.querySelector('[data-lowest-point]')).toHaveAttribute('data-lowest-point', '2026-03-15');
    });

    it('charts the one account chosen, named, and still lists every account', async () => {
        const s = stub({ settings: { focusAccountId: CARD } });
        renderHome();
        const panel = await settled();

        expect(within(panel).getByText('Account B', { selector: 'div' })).toBeInTheDocument();
        expect(within(panel).queryByText('Combined')).toBeNull();
        expect(within(panel).getByRole('img')).toHaveAttribute('aria-label',
            'Account B projected from scheduled items: lowest -$90.00 on Mar 14, below zero from Mar 12');
        expect(rowNames(panel)).toEqual(['Account B', 'Account A']);
        // The choice is the widget's own: the whole scope is always what is read.
        expect(s.callsTo('GET', FORECAST).map((c) => c.search.has('accountId'))).toEqual([false]);
    });

    it('offers the accounts the projection lists, in its order, and saves the choice', async () => {
        const s = stub();
        renderHome();
        const user = userEvent.setup({ delay: null });
        const panel = await settled();

        await user.click(screen.getByRole('button', { name: 'Cash ahead actions' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Settings…' }));
        const settings = await screen.findByRole('group', { name: 'Cash ahead settings' });
        const select = within(settings).getByRole('combobox', { name: 'Account' });
        expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['Combined', 'Account B', 'Account A']);

        await user.selectOptions(select, CHECKING);
        // The preview follows the choice at once, with no new read.
        await waitFor(() => expect(within(panel).getByRole('img').getAttribute('aria-label')).toMatch(/^Account A projected/));
        expect(s.callsTo('GET', FORECAST)).toHaveLength(1);
        await user.click(within(settings).getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(s.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        const body = s.callsTo('PUT', /\/preferences\/dashboard$/)[0]!.body as { widgets: { key: string; settings: Record<string, unknown> }[] };
        expect(body.widgets.find((w) => w.key === 'cash-forecast')!.settings).toEqual({ days: 30, focusAccountId: CHECKING });
    });

    it('charts the accounts combined when the account chosen is not in the projection', async () => {
        stub({ settings: { focusAccountId: 'acct-gone' } });
        renderHome();
        const panel = await settled();
        expect(within(panel).getByRole('img').getAttribute('aria-label')).toMatch(/^Combined projected/);
        expect(rowNames(panel)).toEqual(['Account B', 'Account A']);
    });
});

describe('the look-ahead', () => {
    it.each([14, 30, 60])('asks for %i days and says so', async (days) => {
        const s = stub({ settings: { days } });
        renderHome();
        const panel = await settled();
        expect(within(panel).getByRole('heading', { level: 2 })).toHaveTextContent(`Cash ahead · ${days} days`);
        expect(s.callsTo('GET', FORECAST).map((c) => c.search.get('days'))).toEqual([String(days)]);
        expect(s.callsTo('GET', FORECAST)[0]!.search.get('today')).toBe('2026-03-12');
    });
});

describe('a scope across currencies with a missing rate', () => {
    const mixed = forecast({
        accounts: [{ ...card, currencyCode: 'EUR' }, checking], combined: null,
        rateUnavailable: [{ fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-03-12', reason: 'rate_unavailable' }],
    });

    it('prints each account in its own currency, and says why no combined chart is drawn', async () => {
        stub({ forecast: mixed });
        renderHome();
        const panel = await settled();

        expect(within(row(panel, 'Account A')).getAllByRole('cell')[0]).toHaveTextContent('$500.00');
        expect(within(row(panel, 'Account B')).getAllByRole('cell')[0]).toHaveTextContent(formatCurrency(-40, 'EUR'));
        expect(within(row(panel, 'Account B')).getAllByRole('cell')[0]!.textContent).not.toContain('$');
        // The missing rate, where the chart would be.
        expect(within(panel).getByText(/^No EUR to USD rate for Mar 12, 2026\./)).toBeInTheDocument();
        expect(within(panel).queryByRole('img')).toBeNull();
        expect(within(panel).queryByText('Combined')).toBeNull();
    });

    it('charts one account chosen, in its own currency', async () => {
        stub({ forecast: mixed, settings: { focusAccountId: CARD } });
        renderHome();
        const panel = await settled();
        expect(within(panel).getByRole('img').getAttribute('aria-label')).toContain(`lowest ${formatCurrency(-90, 'EUR')}`);
        expect(within(panel).queryByText(/^No EUR to USD rate/)).toBeNull();
    });
});

describe('a ledger with nothing to project', () => {
    const nothing = forecast({ accounts: [], combined: null });

    it('says so as Bills does, with Create reminder for an owner', async () => {
        stub({ forecast: nothing });
        renderHome();
        const panel = await widget();
        const line = await within(panel).findByText(/^No reminders yet\. Reminders turn recurring bills into one-click posts/);
        expect(within(line).getByRole('link', { name: 'Create reminder →' })).toHaveAttribute('href', `/ledgers/${LEDGER}/reminders`);
        expect(within(panel).queryByRole('table')).toBeNull();
        // The same words Bills uses, from the same component.
        const bills = (await screen.findByRole('heading', { level: 2, name: /^Bills & income/ })).closest('[id^="home-widget-"]') as HTMLElement;
        expect((await within(bills).findByText(/^No reminders yet\./)).textContent).toBe(line.textContent);
    });

    it('gives a viewer the sentence alone', async () => {
        stub({ forecast: nothing, role: 'viewer' });
        renderHome();
        const panel = await widget();
        const line = await within(panel).findByText(/^No reminders yet\./);
        expect(line).toHaveTextContent(/one-click posts\.$/);
        expect(within(panel).queryByRole('link', { name: 'Create reminder →' })).toBeNull();
    });

    it('does not say "No reminders yet" when an active reminder exists that has no account to place', async () => {
        stub({
            forecast: forecast({ accounts: [], combined: null, excluded: [{ reminderId: 'series-a', reason: 'no-lines' }] }),
            reminders: [series({ id: 'series-a', payee: 'Payee A' })],
        });
        renderHome();
        const panel = await widget();
        const note = await waitFor(() => {
            const p = panel.querySelector<HTMLElement>('[data-excluded]');
            expect(p).toHaveTextContent('1 reminder not projected: Payee A');
            return p!;
        });
        expect(note).toBeInTheDocument();
        expect(within(panel).queryByText(/No reminders yet/)).toBeNull();
    });
});

describe('where Cash ahead stands on the page', () => {
    it('is hidden by default, reads nothing, and waits in the tray', async () => {
        const s = homeStub({ accounts: ACCOUNTS });
        renderHome();
        // Settled: the default widgets are drawn.
        await screen.findByRole('heading', { level: 2, name: /^Cash flow/ });
        expect(screen.queryByRole('heading', { level: 2, name: /^Cash ahead/ })).toBeNull();
        expect(s.callsTo('GET', FORECAST)).toHaveLength(0);

        const user = userEvent.setup({ delay: null });
        await user.click(await customiseWhenRead());
        const tray = await screen.findByRole('region', { name: 'Hidden widgets' });
        expect(within(tray).getByText('Lowest projected balance per account.')).toBeInTheDocument();
        await user.click(within(tray).getByRole('button', { name: 'Add Cash ahead' }));
        expect(await screen.findByRole('group', { name: /^Cash ahead, side, \d+ of \d+$/ })).toBeInTheDocument();
    });

    it('is never counted in the header: an account going below zero and overdue reminders are advisory', async () => {
        stub({ forecast: forecast({ overdueOnDay1: 3 }) });
        renderHome();
        const panel = await settled();
        expect(within(row(panel, 'Account B')).getAllByRole('cell')[1]).toHaveTextContent(formatCurrency(-90, 'USD'));

        const day = await waitFor(() => {
            const d = screen.getAllByText('Thu, Mar 12', { selector: 'span' }).find((e) => e.parentElement?.tagName === 'P');
            if (!d) throw new Error('the sentence has not settled');
            return d.parentElement as HTMLElement;
        });
        expect(await within(day).findByText(/Nothing needs you today/)).toBeInTheDocument();
        expect(day.textContent).not.toMatch(/overdue|below zero|projected/);
    });

    it('offers no button but its menu', async () => {
        stub({ role: 'viewer' });
        renderHome();
        const panel = await settled();
        expect(within(panel).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Cash ahead actions']);
    });
});
