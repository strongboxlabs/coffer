import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { UsersPanel } from './UsersPanel';
import * as apiModule from '@/lib/api';
import type { AdminUser } from '@/lib/api';

// Smoke tests for System → Users (ADR-0083):
//   * the sole enabled admin's "Remove admin" / "Disable" are locked (>=1-enabled-admin
//     guard, mirrored in the UI), while a second admin's are not.

const ADMIN: AdminUser = {
    id: 'u1', displayName: 'Alice', username: 'alice', isAdmin: true, isDisabled: false, ledgerCount: 2,
};
const SECOND_ADMIN: AdminUser = {
    id: 'u2', displayName: 'Bob', username: 'bob', isAdmin: true, isDisabled: false, ledgerCount: 1,
};

function renderPanel(users: AdminUser[]) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    vi.spyOn(apiModule, 'fetchAdminUsers').mockResolvedValue(users);
    vi.spyOn(apiModule, 'fetchAdminInvites').mockResolvedValue([]);
    return render(
        <QueryClientProvider client={queryClient}>
            <UsersPanel />
        </QueryClientProvider>,
    );
}

describe('UsersPanel', () => {
    beforeEach(() => vi.restoreAllMocks());

    it('locks the sole enabled admin (>=1-enabled-admin guard)', async () => {
        renderPanel([ADMIN]);
        expect(await screen.findByText('Alice')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Remove admin' })).toBeDisabled();
        expect(screen.getByRole('button', { name: 'Disable' })).toBeDisabled();
    });

    it('does not lock admins when more than one is enabled', async () => {
        renderPanel([ADMIN, SECOND_ADMIN]);
        expect(await screen.findByText('Alice')).toBeInTheDocument();
        for (const btn of screen.getAllByRole('button', { name: 'Remove admin' })) {
            expect(btn).not.toBeDisabled();
        }
        for (const btn of screen.getAllByRole('button', { name: 'Disable' })) {
            expect(btn).not.toBeDisabled();
        }
    });
});
