import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { LoginPage } from './LoginPage';
import { ApiError } from '@/lib/api';
import * as authModule from '@/lib/auth';

// LoginPage smoke tests. Behaviour we lock down:
//
//   * the form renders the username field
//   * empty / whitespace-only submission doesn't trigger the
//     ceremony (the submit button stays disabled)
//   * an ApiError from performLogin surfaces its `detail` in the
//     ARIA alert region
//   * a DOMException from the WebAuthn ceremony surfaces its
//     `.message` (covers the user-cancelled flow)
//
// We don't drive the full network round-trip here — performLogin is
// mocked. Network-level happy-path verification belongs in a future
// Playwright suite (engineering-standards §5 frontend UI row).

function renderLogin() {
    // Build a minimal in-memory router so the route hooks (useNavigate,
    // useSearch) the component depends on resolve to real
    // implementations. The route tree mirrors the real one's /login
    // slot. The cast on <RouterProvider router={...}> avoids a
    // module-augmentation type clash: the production Register
    // declaration types the app's router with a specific shape, and a
    // test router built without the full tree is technically a
    // different type even though it's structurally compatible at this
    // call site. Tests aren't production code — we accept the cast
    // at this seam.
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    const root = createRootRoute();
    const loginRoute = createRoute({
        getParentRoute: () => root,
        path: '/login',
        component: LoginPage,
        validateSearch: (search: Record<string, unknown>) => ({
            next: typeof search.next === 'string' ? search.next : undefined,
        }),
    });
    const router = createRouter({
        routeTree: root.addChildren([loginRoute]),
        history: createMemoryHistory({ initialEntries: ['/login'] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('LoginPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('renders the username field and disabled submit button initially', async () => {
        renderLogin();

        expect(await screen.findByLabelText(/username/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /sign in/i })).toBeDisabled();
    });

    it('keeps submit disabled for whitespace-only input', async () => {
        renderLogin();

        const user = userEvent.setup();
        const usernameField = await screen.findByLabelText(/username/i);
        await user.type(usernameField, '   ');

        expect(screen.getByRole('button', { name: /sign in/i })).toBeDisabled();
    });

    it('surfaces the API error detail when the ceremony rejects', async () => {
        const performLoginSpy = vi
            .spyOn(authModule, 'performLogin')
            .mockRejectedValue(new ApiError(401, 'Authentication failed.'));

        renderLogin();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.click(screen.getByRole('button', { name: /sign in/i }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('Authentication failed.');
        expect(performLoginSpy).toHaveBeenCalledWith('alice');
    });

    it('surfaces the DOMException message when the user cancels the WebAuthn ceremony', async () => {
        // navigator.credentials.get throws a DOMException with name
        // "NotAllowedError" when the user dismisses the platform UI;
        // @simplewebauthn/browser propagates it as-is.
        vi.spyOn(authModule, 'performLogin').mockRejectedValue(
            new DOMException('The operation either timed out or was not allowed.', 'NotAllowedError'),
        );

        renderLogin();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.click(screen.getByRole('button', { name: /sign in/i }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/operation either timed out or was not allowed/);
    });
});
