import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { ImportLedgerPage } from './ImportLedgerPage';
import * as importApi from '@/lib/api/import';

// ImportLedgerPage (ADR-0071 D2): the new-ledger-from-Moneydance wizard.
// Locked down: analyze → preview, run → complete, failure surfaces the error.

function renderImport() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    const root = createRootRoute();
    const importRoute = createRoute({
        getParentRoute: () => root,
        path: '/imports/moneydance',
        component: ImportLedgerPage,
    });
    const ledgerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>ledger</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([importRoute, ledgerRoute]),
        history: createMemoryHistory({ initialEntries: ['/imports/moneydance'] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

const PREVIEW: importApi.ImportPreview = {
    exporter: 'Moneydance 2023',
    build: 5000,
    exportDate: 20260101,
    totalItems: 1234,
    counts: [
        { objType: 'txn', count: 1000 },
        { objType: 'acct', count: 42 },
    ],
};

function makeFile() {
    return new File(['{"metadata":{},"all_items":[]}'], 'export.json', {
        type: 'application/json',
    });
}

describe('ImportLedgerPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('shows the per-type preview after analyzing a file', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue(PREVIEW);
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));

        expect(await screen.findByText('txn')).toBeInTheDocument();
        expect(screen.getByText('1,000')).toBeInTheDocument();
        expect(screen.getByLabelText(/new ledger name/i)).toBeInTheDocument();
    });

    it('runs the import and shows completion with an Open ledger action', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue(PREVIEW);
        vi.spyOn(importApi, 'startMoneydanceImport').mockResolvedValue({
            jobId: 'job-1', state: 'running', completed: 0, total: 9, step: null, ledgerId: null, error: null,
        });
        vi.spyOn(importApi, 'fetchImportJob').mockResolvedValue({
            jobId: 'job-1', state: 'succeeded', completed: 9, total: 9, step: null,
            ledgerId: '00000000-0000-0000-0000-0000000000aa', error: null,
        });
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        await user.type(await screen.findByLabelText(/new ledger name/i), 'Imported');
        await user.click(screen.getByRole('button', { name: /create ledger/i }));

        expect(await screen.findByText(/import complete/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /open ledger/i })).toBeEnabled();
    });

    it('surfaces a failed import with a Start over action', async () => {
        vi.spyOn(importApi, 'previewMoneydanceImport').mockResolvedValue(PREVIEW);
        vi.spyOn(importApi, 'startMoneydanceImport').mockResolvedValue({
            jobId: 'job-2', state: 'running', completed: 0, total: 9, step: null, ledgerId: null, error: null,
        });
        vi.spyOn(importApi, 'fetchImportJob').mockResolvedValue({
            jobId: 'job-2', state: 'failed', completed: 3, total: 9, step: null,
            ledgerId: null, error: 'Ledger name already taken.',
        });
        const user = userEvent.setup();
        renderImport();

        await user.upload(await screen.findByLabelText(/moneydance export/i), makeFile());
        await user.click(screen.getByRole('button', { name: /analyze export/i }));
        await user.type(await screen.findByLabelText(/new ledger name/i), 'Dupe');
        await user.click(screen.getByRole('button', { name: /create ledger/i }));

        expect(await screen.findByText(/ledger name already taken/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /start over/i })).toBeInTheDocument();
    });
});
