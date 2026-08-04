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

import { RecoveryLoginPage } from './RecoveryLoginPage';
import { ApiError } from '@/lib/api';
import * as authModule from '@/lib/auth';

// RecoveryLoginPage smoke tests (ADR-0013). Behaviour locked down:
//   * username + recovery-code fields render; submit gates on both
//   * an ApiError from performRecoveryLogin surfaces in the alert
//   * on success it routes to /account/security with ?recovered

function renderRecovery() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    const root = createRootRoute();
    const recoveryRoute = createRoute({
        getParentRoute: () => root,
        path: '/login/recovery',
        component: RecoveryLoginPage,
    });
    const securityRoute = createRoute({
        getParentRoute: () => root,
        path: '/account/security',
        component: () => <main>security page</main>,
        validateSearch: (search: Record<string, unknown>): { recovered?: boolean } =>
            search.recovered === true ? { recovered: true } : {},
    });
    const loginRoute = createRoute({
        getParentRoute: () => root,
        path: '/login',
        component: () => <main>login page</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([recoveryRoute, securityRoute, loginRoute]),
        history: createMemoryHistory({ initialEntries: ['/login/recovery'] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('RecoveryLoginPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('gates submit until both username and code are filled', async () => {
        renderRecovery();
        const user = userEvent.setup();

        const submit = await screen.findByRole('button', { name: /sign in/i });
        expect(submit).toBeDisabled();

        await user.type(await screen.findByLabelText(/username/i), 'alice');
        expect(submit).toBeDisabled();

        await user.type(screen.getByLabelText(/recovery code/i), 'ABCDE-FGHJK');
        expect(submit).toBeEnabled();
    });

    it('surfaces the API error detail when the code is rejected', async () => {
        vi.spyOn(authModule, 'performRecoveryLogin').mockRejectedValue(
            new ApiError(401, 'Authentication failed.'),
        );

        renderRecovery();
        const user = userEvent.setup();

        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/recovery code/i), 'WRONG-CODE0');
        await user.click(screen.getByRole('button', { name: /sign in/i }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/authentication failed/i);
    });

    it('routes to the security page on success', async () => {
        vi.spyOn(authModule, 'performRecoveryLogin').mockResolvedValue({
            userId: '00000000-0000-0000-0000-000000000010',
            username: 'alice',
            sessionId: '00000000-0000-0000-0000-000000000020',
            sessionExpiresAt: '2026-06-10T00:00:00Z',
        });

        renderRecovery();
        const user = userEvent.setup();

        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/recovery code/i), 'ABCDE-FGHJK');
        await user.click(screen.getByRole('button', { name: /sign in/i }));

        expect(await screen.findByText('security page')).toBeInTheDocument();
    });
});
