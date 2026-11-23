import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import type { RefObject } from 'react';

import { EditorDialog } from '@/components/ui/EditorDialog';
import { stubFetch } from '@/lib/annotations/fetchStub';
import type { ReminderSummary } from '@/lib/types';

import { RemindersPage } from './RemindersPage';
import { validateRemindersSearch } from './remindersSearch';

// `?reminder=<id>` on the Reminders page, through the real page, router and API
// client, with the server mocked at fetch. The series editor is stood in for by
// its dialog shell, so what is tested is which series opens, when, and what
// closing it does. Every name is invented.

vi.mock('./ReminderEditorDialog', () => ({
    ReminderEditorDialog: (props: { reminderId: string; onClose: () => void; fallbackFocusRef?: RefObject<HTMLElement | null> }) => (
        <EditorDialog ariaLabel="Edit reminder" title="Edit reminder" onClose={props.onClose} fallbackFocusRef={props.fallbackFocusRef}>
            <p>Editing {props.reminderId}</p>
        </EditorDialog>
    ),
}));
vi.mock('./RemindersUpcomingPanel', () => ({ RemindersUpcomingPanel: () => <p>Upcoming</p> }));

afterEach(() => {
    vi.unstubAllGlobals();
});

const LEDGER = 'ledger-1';

const series = (id: string, payee: string): ReminderSummary => ({
    id, payee, memo: null, amount: -10, rrule: 'FREQ=MONTHLY', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-04-01', autoCommitDaysBefore: null, isActive: true, isLoanReminder: false,
    origin: 'manual', currencyCode: 'USD',
});

function renderPage(search: string) {
    const stub = stubFetch()
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role: 'owner' }])
        .on('GET', /\/reminders$/, [series('series-a', 'Payee A'), series('series-b', 'Payee B')])
        .on('GET', /\/schedules\/reminder-auto-post$/, {
            enabled: false, hourLocal: 5, minuteLocal: 0, timezone: null, lastRunAt: null, nextRunAt: null,
            consecutiveFailures: 0, lastError: null, lastFailureAt: null, disabledReason: null,
        });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
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
    return { stub, router, queryClient };
}

describe('?reminder= on the Reminders page', () => {
    it('opens that series\' editor', async () => {
        renderPage('?reminder=series-b');
        const dialog = await screen.findByRole('dialog', { name: 'Edit reminder' });
        expect(dialog).toHaveTextContent('Editing series-b');
    });

    it('clears the param when the editor closes, and gives focus to the view showing', async () => {
        const { router } = renderPage('?reminder=series-a');
        const user = userEvent.setup();
        await screen.findByRole('dialog', { name: 'Edit reminder' });

        await user.click(screen.getByRole('button', { name: 'Close' }));
        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit reminder' })).toBeNull());
        expect(router.state.location.search).toEqual({});
        // Nothing on the page opened it, so focus does not fall to the page.
        expect(screen.getByRole('tab', { name: 'Upcoming' })).toHaveFocus();
        // Back to the same entry, with nothing left to reopen it.
        expect(router.state.location.href).toBe(`/ledgers/${LEDGER}/reminders`);
    });

    it('opens nothing and says nothing for an id that names no series', async () => {
        const { queryClient } = renderPage('?reminder=series-gone');
        // Settled: the series list, which the id is looked up in, has arrived.
        await waitFor(() => expect(queryClient.getQueryState(['reminders', LEDGER])?.status).toBe('success'));
        expect(screen.getByRole('heading', { level: 1, name: 'Reminders' })).toBeInTheDocument();
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(screen.queryByRole('alert')).toBeNull();
    });

    it('reads no series list when no reminder is named', async () => {
        const { stub } = renderPage('');
        expect(await screen.findByText('Upcoming', { selector: 'p' })).toBeInTheDocument();
        expect(stub.callsTo('GET', /\/reminders$/)).toHaveLength(0);
        expect(screen.queryByRole('dialog')).toBeNull();
    });
});
