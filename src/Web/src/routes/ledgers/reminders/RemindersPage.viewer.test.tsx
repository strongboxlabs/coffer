import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { stubFetch } from '@/lib/annotations/fetchStub';
import type { ReminderSummary } from '@/lib/types';

import { occurrence } from '../home/homeTestFixtures';
import { RemindersPage } from './RemindersPage';
import { validateRemindersSearch } from './remindersSearch';

// The Reminders page by role (ADR-0023 §L): a viewer reads the calendar and the list
// and is offered no control that writes, which the server refuses a viewer anyway; an
// owner or editor keeps every one. Through the real page, panels, router and API
// client, with the server mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const LEDGER = 'ledger-1';

const series = (id: string, payee: string): ReminderSummary => ({
    id, payee, memo: null, amount: -10, rrule: 'FREQ=MONTHLY', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-03-15', autoCommitDaysBefore: null, isActive: true, isLoanReminder: false,
    origin: 'manual', currencyCode: 'USD',
});

function renderPage(role: 'owner' | 'editor' | 'viewer', {
    search = '', reminders = [series('series-a', 'Payee A'), series('series-b', 'Payee B')], listCached = false,
}: {
    search?: string;
    reminders?: ReminderSummary[];
    /** The series list already read, as another view or widget of the ledger leaves it. */
    listCached?: boolean;
} = {}) {
    const stub = stubFetch()
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role }])
        .on('GET', /\/reminders$/, reminders)
        .on('GET', /\/reminders\/upcoming$/, [
            occurrence({ date: '2026-03-15', reminderId: 'series-a', payee: 'Payee A' }),
            occurrence({ date: '2026-03-10', reminderId: 'series-b', payee: 'Payee B', kind: 'skipped' }),
            occurrence({ date: '2026-03-05', reminderId: 'series-c', payee: 'Payee C', kind: 'scheduled', headerId: 'header-1' }),
        ])
        .on('GET', /\/schedules\/reminder-auto-post$/, {
            enabled: false, hourLocal: 5, minuteLocal: 0, timezone: null, lastRunAt: null, nextRunAt: null,
            consecutiveFailures: 0, lastError: null, lastFailureAt: null, disabledReason: null,
        });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    if (listCached) queryClient.setQueryData(['reminders', LEDGER], reminders);
    const root = createRootRoute();
    const router = createRouter({
        routeTree: root.addChildren([
            createRoute({
                getParentRoute: () => root, path: '/ledgers/$ledgerId/reminders',
                component: RemindersPage, validateSearch: validateRemindersSearch,
            }),
        ]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/reminders${search}`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    return { stub };
}

/** The calendar once its month and the reader's role have both arrived. */
async function settledCalendar(): Promise<HTMLElement> {
    // The breadcrumb names the ledger from the read that also carries the role.
    await screen.findByText('Synthetic');
    const calendar = await screen.findByRole('group', { name: /^Reminders in / });
    await within(calendar).findByText('● Payee A');
    return calendar;
}

/** The list view once its series have arrived. */
async function settledList(user: ReturnType<typeof userEvent.setup>): Promise<HTMLElement> {
    await screen.findByText('Synthetic');
    await user.click(screen.getByRole('tab', { name: 'List' }));
    const row = (await screen.findByText('Payee A', { selector: 'span' })).closest('li')!;
    return row;
}

const WRITES = ['New reminder', 'Edit', 'Skip next', 'Disable', 'Enable'];

describe('a viewer on the Reminders page', () => {
    it('reads the calendar, whose chips open and restore nothing', async () => {
        renderPage('viewer');
        const calendar = await settledCalendar();

        expect(within(calendar).getByText('⊘ Payee B')).toBeInTheDocument();
        expect(within(calendar).getByText('✓ Payee C')).toBeInTheDocument();
        expect(within(calendar).queryAllByRole('button')).toEqual([]);
        expect(calendar.textContent).not.toContain('click to edit');
    });

    it('is offered no auto-post schedule to change', async () => {
        renderPage('viewer');
        await settledCalendar();
        expect(screen.getByRole('heading', { level: 1, name: 'Reminders' })).toBeInTheDocument();
        expect(screen.queryByText('Due reminders post automatically each day')).toBeNull();
        expect(screen.queryByRole('checkbox')).toBeNull();
    });

    it('reads the list, whose rows offer no action', async () => {
        renderPage('viewer');
        const user = userEvent.setup();
        const row = await settledList(user);

        expect(within(row).getByText(/next Mar 15, 2026/)).toBeInTheDocument();
        expect(within(row).queryAllByRole('button')).toEqual([]);
        for (const name of WRITES) expect(screen.queryByRole('button', { name })).toBeNull();
    });

    it('is offered no New reminder on an empty list', async () => {
        renderPage('viewer', { reminders: [] });
        const user = userEvent.setup();
        await screen.findByText('Synthetic');
        await user.click(screen.getByRole('tab', { name: 'List' }));

        expect(await screen.findByText('No reminders yet.')).toBeInTheDocument();
        expect(screen.getByText('Reminders post recurring transactions on a schedule.')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'New reminder' })).toBeNull();
    });

    it('opens nothing for ?reminder=, and reads no series list for it', async () => {
        const { stub } = renderPage('viewer', { search: '?reminder=series-a' });
        const calendar = await settledCalendar();
        expect(within(calendar).getByText('● Payee A')).toBeInTheDocument();
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(stub.callsTo('GET', /\/reminders$/)).toHaveLength(0);
    });

    it('opens nothing for ?reminder= even with the series list already read', async () => {
        renderPage('viewer', { search: '?reminder=series-a', listCached: true });
        const calendar = await settledCalendar();
        expect(within(calendar).getByText('● Payee A')).toBeInTheDocument();
        expect(screen.queryByRole('dialog')).toBeNull();
    });
});

describe.each(['owner', 'editor'] as const)('an %s on the Reminders page', (role) => {
    it('keeps every chip\'s action and the auto-post schedule', async () => {
        renderPage(role);
        const calendar = await settledCalendar();

        expect(within(calendar).getByRole('button', { name: /Payee A/ })).toBeInTheDocument();
        expect(within(calendar).getByRole('button', { name: /Payee B/ })).toHaveAttribute('title', expect.stringContaining('restore it'));
        // A posted chip stays read-only for everyone.
        expect(within(calendar).queryByRole('button', { name: /Payee C/ })).toBeNull();
        expect(calendar.textContent).toContain('(click to edit + post)');
        expect(screen.getByText('Due reminders post automatically each day')).toBeInTheDocument();
    });

    it('keeps every row action and New reminder', async () => {
        renderPage(role);
        const user = userEvent.setup();
        const row = await settledList(user);

        expect(within(row).getAllByRole('button').map((b) => b.textContent)).toEqual(['Edit', 'Skip next', 'Disable']);
        expect(screen.getByRole('button', { name: 'New reminder' })).toBeInTheDocument();
    });
});
