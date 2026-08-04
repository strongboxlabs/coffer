import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { GeneralPanel } from './GeneralPanel';
import * as apiModule from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';

// GeneralPanel — rename + delete (ADR-0020). Locked down:
//   * owner sees enabled rename/delete; Save calls renameLedger
//   * a non-owner (viewer) has both disabled
//   * delete is gated behind a typed-name confirmation before it fires

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const settingsRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <GeneralPanel ledgerId={LEDGER_ID} />,
    });
    const landingRoute = createRoute({
        getParentRoute: () => root,
        path: '/landing-stub',
        component: () => <main>landing</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([settingsRoute, landingRoute]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

function mockLedger(role: string) {
    const ledgers: LedgerSummary[] = [{ id: LEDGER_ID, name: 'Personal', role }];
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
}

describe('GeneralPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('lets an owner rename the ledger', async () => {
        mockLedger('owner');
        const renameSpy = vi.spyOn(apiModule, 'renameLedger').mockResolvedValue(undefined);
        renderPanel();
        const user = userEvent.setup();

        const input = await screen.findByLabelText(/^name$/i);
        await waitFor(() => expect(input).not.toBeDisabled());
        await user.clear(input);
        await user.type(input, 'Renamed');
        await user.click(screen.getByRole('button', { name: /^rename$/i }));

        await waitFor(() => expect(renameSpy).toHaveBeenCalledWith(LEDGER_ID, 'Renamed'));
    });

    it('disables rename + delete for a non-owner', async () => {
        mockLedger('viewer');
        renderPanel();

        const input = await screen.findByLabelText(/^name$/i);
        expect(input).toBeDisabled();
        expect(screen.getByRole('button', { name: /^rename$/i })).toBeDisabled();
        expect(screen.getByRole('button', { name: /delete ledger/i })).toBeDisabled();
    });

    it('gates delete behind a typed-name confirmation', async () => {
        mockLedger('owner');
        const deleteSpy = vi.spyOn(apiModule, 'deleteLedger').mockResolvedValue(undefined);
        renderPanel();
        const user = userEvent.setup();

        // Open the dialog (only the danger-zone button exists at this point).
        await user.click(await screen.findByRole('button', { name: /delete ledger/i }));

        // Everything below is scoped to the modal so it doesn't collide with
        // the rename input / danger-zone button on the page behind it.
        const dialog = await screen.findByRole('dialog');
        const confirm = within(dialog).getByRole('button', { name: /^delete ledger$/i });
        const typeBox = within(dialog).getByRole('textbox');

        // Gated: confirm is disabled until the ledger name is typed exactly.
        expect(confirm).toBeDisabled();
        await user.type(typeBox, 'Personal');
        await waitFor(() => expect(confirm).toBeEnabled());
        await user.click(confirm);

        await waitFor(() => expect(deleteSpy).toHaveBeenCalledWith(LEDGER_ID));
    });
});
