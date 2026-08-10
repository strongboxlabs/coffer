import { describe, it, expect, beforeEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { SetupPage } from './SetupPage';
import { ApiError } from '@/lib/api';
import * as authModule from '@/lib/auth';
import type { SetupInfoResponse } from '@/lib/auth';

// Smoke tests for the setup ceremony page. Behaviour we lock down:
//
//   * /info fetches on mount; loading + error states render
//   * the form renders username + display name + passkey label
//   * the Demo opt-in is present and OFF by default (ADR-0088) — the
//     default path must not hand someone a ledger full of sample data
//   * no ledger picker is offered at all (ADR-0088)
//   * an ApiError from /complete surfaces its `.detail` in the alert
//   * a DOMException from the WebAuthn ceremony surfaces its message
//   * the button label transitions Create → Creating → Tap your
//     authenticator… as the ceremony lifecycle advances
//   * on success the page swaps to the RecoveryCodes view with the
//     codes the API returned

const TEST_TOKEN = 'test-bootstrap-token';

// /info carries no payload since ADR-0088; it exists to validate the token.
const DEFAULT_INFO: SetupInfoResponse = {};

function renderSetupRaw() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    const root = createRootRoute();
    const setupRoute = createRoute({
        getParentRoute: () => root,
        path: '/setup/$token',
        component: SetupPage,
    });
    // Stub the ledger hub at `/` so the post-acknowledge navigation has a
    // valid destination in the router tree (ADR-0088 — was /welcome).
    const hubRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <main>ledger hub</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([setupRoute, hubRoute]),
        history: createMemoryHistory({ initialEntries: [`/setup/${TEST_TOKEN}`] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/**
 * Render and advance past the ADR-0061 Create-vs-Restore choice into the
 * create-account form, which is what most of these tests exercise. Use
 * {@link renderSetupRaw} for tests that assert the choice / error / restore
 * screens directly.
 */
async function renderSetup() {
    const result = renderSetupRaw();
    fireEvent.click(await screen.findByRole('button', { name: /set up a new install/i }));
    return result;
}

describe('SetupPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        Object.defineProperty(navigator, 'clipboard', {
            configurable: true,
            value: { writeText: vi.fn(async () => undefined) },
        });
        // Default: /info resolves (token valid). Individual tests override
        // this when they need the rejection path.
        vi.spyOn(authModule, 'fetchSetupInfo').mockResolvedValue(DEFAULT_INFO);
    });

    it('renders the form once /info resolves, with submit enabled by the defaults', async () => {
        await renderSetup();

        // Username/display-name/passkey-label all visible after /info.
        expect(await screen.findByLabelText(/username/i)).toBeInTheDocument();
        expect(screen.getByLabelText(/display name/i)).toBeInTheDocument();
        expect(screen.getByLabelText(/passkey label/i)).toBeInTheDocument();

        // ADR-0088: no ledger picker at all. The old dropdown listed empty
        // placeholder ledgers and preselected one, which is how a fresh
        // install ended up owning a ledger with nothing in it.
        expect(screen.queryByRole('combobox')).not.toBeInTheDocument();

        // The Demo opt-in exists and is OFF by default — someone setting up
        // their own books must not acquire sample data by accident.
        const demo = screen.getByRole('checkbox', { name: /demo ledger/i });
        expect(demo).toBeInTheDocument();
        expect(demo).not.toBeChecked();

        // No fields filled yet — submit stays disabled.
        expect(
            screen.getByRole('button', { name: /create account/i }),
        ).toBeDisabled();
    });

    // ADR-0089: an email address is a perfectly good username. This used to be
    // rejected by a client-only ^[a-z0-9_-]{3,32}$ pattern that neither the API
    // nor the invite form enforced — so the first user was refused what every
    // invited user could already register.
    it('accepts an email address as the username', async () => {
        await renderSetup();

        const user = userEvent.setup();
        await user.type(
            await screen.findByLabelText(/username/i),
            'ada.reyes@example.com',
        );
        await user.type(screen.getByLabelText(/display name/i), 'Ada Reyes');
        await user.type(screen.getByLabelText(/passkey label/i), 'Coffer Dev');

        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
        expect(screen.getByLabelText(/username/i)).not.toHaveAttribute(
            'aria-invalid',
        );
        expect(
            screen.getByRole('button', { name: /create account/i }),
        ).toBeEnabled();
    });

    it('rejects a username containing a space, and says why', async () => {
        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'ada reyes');

        // Whitespace is one of the few things still refused: invisible padding
        // and indistinguishable copy/paste variants are a real hazard for a
        // login identifier. Submit is disabled, so the reason must be visible —
        // an unexplained disabled button reads as a broken app.
        const error = await screen.findByRole('alert');
        expect(error).toHaveTextContent(/spaces or invisible control characters/i);
        expect(screen.getByLabelText(/username/i)).toHaveAttribute(
            'aria-invalid',
            'true',
        );
        expect(
            screen.getByRole('button', { name: /create account/i }),
        ).toBeDisabled();
    });

    it('clears the username error once the space is removed', async () => {
        await renderSetup();

        const user = userEvent.setup();
        const field = await screen.findByLabelText(/username/i);
        await user.type(field, 'ada reyes');
        expect(await screen.findByRole('alert')).toBeInTheDocument();

        await user.clear(field);
        await user.type(field, 'ada.reyes@example.com');
        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
        expect(field).not.toHaveAttribute('aria-invalid');
    });

    it('surfaces a setup-link error when /info rejects', async () => {
        vi.spyOn(authModule, 'fetchSetupInfo').mockRejectedValue(
            new ApiError(401, 'Invalid or expired bootstrap token.'),
        );

        renderSetupRaw();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/invalid or expired/i);
        // The form is absent in the error state.
        expect(screen.queryByLabelText(/username/i)).not.toBeInTheDocument();
    });

    it('keeps submit disabled until every field has valid content', async () => {
        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        // Passkey label still empty — submit must stay disabled.
        expect(
            screen.getByRole('button', { name: /create account/i }),
        ).toBeDisabled();

        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        expect(
            screen.getByRole('button', { name: /create account/i }),
        ).toBeEnabled();
    });

    // Was: "marks the username invalid when it fails the regex", asserting that
    // `Alice` is rejected for containing uppercase. ADR-0089 makes that wrong
    // twice over — the charset is permissive, and case is folded in the database
    // by the username_ci collation rather than banned at the keyboard.
    it('accepts mixed case, since identity folds case in storage', async () => {
        await renderSetup();

        const user = userEvent.setup();
        const usernameInput = await screen.findByLabelText(/username/i);
        await user.type(usernameInput, 'Alice');
        expect(usernameInput).not.toHaveAttribute('aria-invalid');

        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        expect(
            screen.getByRole('button', { name: /create account/i }),
        ).toBeEnabled();
    });

    it('still enforces the minimum length', async () => {
        await renderSetup();

        const user = userEvent.setup();
        const usernameInput = await screen.findByLabelText(/username/i);
        await user.type(usernameInput, 'ab');
        expect(usernameInput).toHaveAttribute('aria-invalid', 'true');
        expect(await screen.findByRole('alert')).toHaveTextContent(/at least 3/i);

        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        expect(
            screen.getByRole('button', { name: /create account/i }),
        ).toBeDisabled();
    });

    it('surfaces the API error detail when the ceremony rejects', async () => {
        vi.spyOn(authModule, 'performSetup').mockRejectedValue(
            new ApiError(422, 'Username is already taken.', 'setup-username-taken'),
        );

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('button', { name: /create account/i }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('Username is already taken.');
    });

    it('surfaces the DOMException message when the user cancels WebAuthn', async () => {
        vi.spyOn(authModule, 'performSetup').mockRejectedValue(
            new DOMException(
                'The operation either timed out or was not allowed.',
                'NotAllowedError',
            ),
        );

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('button', { name: /create account/i }));

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/timed out or was not allowed/);
    });

    it('flips the submit label to "Tap or insert your authenticator…" once /begin returns', async () => {
        // performSetup is called with onCeremonyStart; we invoke that
        // callback synchronously and then leave the promise pending so
        // the test can observe the in-ceremony label.
        let resolveSetup: (value: never) => void = () => {};
        const setupSpy = vi
            .spyOn(authModule, 'performSetup')
            .mockImplementation((input) => {
                input.onCeremonyStart?.();
                return new Promise<never>((resolve) => {
                    resolveSetup = resolve;
                });
            });

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('button', { name: /create account/i }));

        await screen.findByRole('button', { name: /tap or insert your authenticator/i });
        expect(setupSpy).toHaveBeenCalledOnce();
        // Avoid leaving a dangling unresolved promise.
        resolveSetup(undefined as never);
    });

    it('shows recovery codes on success and gates Continue on acknowledgement', async () => {
        const codes = ['ABCD-1234', 'EFGH-5678'];
        vi.spyOn(authModule, 'performSetup').mockResolvedValue({
            userId: '00000000-0000-0000-0000-000000000010',
            username: 'alice',
            sessionId: '00000000-0000-0000-0000-000000000020',
            sessionExpiresAt: '2026-06-10T00:00:00Z',
            masterKeyBase64: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
            recoveryCodes: codes,
            ledgerId: '00000000-0000-0000-0000-000000000099',
            ledgerName: 'Demo',
        });

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('button', { name: /create account/i }));

        // The form is gone, replaced by the codes panel.
        expect(await screen.findByText(/save your recovery codes/i)).toBeInTheDocument();
        for (const code of codes) {
            expect(screen.getByText(code)).toBeInTheDocument();
        }

        // The success header names the seeded Demo ledger.
        expect(screen.getByText(/signed in as/i)).toHaveTextContent(/alice/i);
        expect(screen.getByText(/signed in as/i)).toHaveTextContent('Demo');

        // Continue is initially disabled until the user ticks the
        // acknowledgement checkbox. Scope the query to the codes panel —
        // the form's Demo checkbox is gone by now, but be explicit.
        const continueButton = screen.getByRole('button', { name: /continue/i });
        expect(continueButton).toBeDisabled();

        await user.click(screen.getByRole('checkbox'));
        expect(continueButton).toBeEnabled();
    });

    it('shows the master key after the recovery codes, then finishes (ADR-0092 D2)', async () => {
        // Two secrets, chained rather than shown together. Without this step a
        // first-time operator would have a key they had never seen, in a server-side
        // location they had no reason to look at.
        const masterKey = 'Zm9vYmFyYmF6cXV1eGZvb2JhcmJhenF1dXhmb28xMjM=';
        vi.spyOn(authModule, 'performSetup').mockResolvedValue({
            userId: '00000000-0000-0000-0000-000000000010',
            username: 'alice',
            sessionId: '00000000-0000-0000-0000-000000000020',
            sessionExpiresAt: '2026-06-10T00:00:00Z',
            masterKeyBase64: masterKey,
            recoveryCodes: ['ABCD-1234'],
            ledgerId: null,
            ledgerName: null,
        });

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('button', { name: /create account/i }));

        // Recovery codes first — the more severe secret, and the one-time one. The
        // master key must not be on screen competing for attention yet.
        expect(await screen.findByText(/save your recovery codes/i)).toBeInTheDocument();
        expect(screen.queryByText(masterKey)).not.toBeInTheDocument();

        await user.click(screen.getByRole('checkbox'));
        await user.click(screen.getByRole('button', { name: /continue/i }));

        // Then the master key, gated the same way.
        expect(await screen.findByText(/save your master key/i)).toBeInTheDocument();
        expect(screen.getByText(masterKey)).toBeInTheDocument();
        // Says plainly that it can be seen again — a false "last chance" is the kind
        // of warning operators learn to ignore.
        expect(screen.getByText(/see this again later/i)).toBeInTheDocument();

        const finish = screen.getByRole('button', { name: /finish setup/i });
        expect(finish).toBeDisabled();
        await user.click(screen.getByRole('checkbox'));
        expect(finish).toBeEnabled();
    });

    it('sends includeDemo=false when the box is left alone', async () => {
        const setupSpy = vi.spyOn(authModule, 'performSetup').mockResolvedValue({
            userId: '00000000-0000-0000-0000-000000000010',
            username: 'alice',
            sessionId: '00000000-0000-0000-0000-000000000020',
            sessionExpiresAt: '2026-06-10T00:00:00Z',
            masterKeyBase64: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
            recoveryCodes: ['ABCD-1234'],
            ledgerId: null,
            ledgerName: null,
        });

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('button', { name: /create account/i }));

        await waitFor(() => {
            expect(setupSpy).toHaveBeenCalled();
        });
        expect(setupSpy.mock.calls[0]![0].includeDemo).toBe(false);
    });

    it('sends includeDemo=true once the box is ticked', async () => {
        const setupSpy = vi.spyOn(authModule, 'performSetup').mockResolvedValue({
            userId: '00000000-0000-0000-0000-000000000010',
            username: 'alice',
            sessionId: '00000000-0000-0000-0000-000000000020',
            sessionExpiresAt: '2026-06-10T00:00:00Z',
            masterKeyBase64: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
            recoveryCodes: ['ABCD-1234'],
            ledgerId: '00000000-0000-0000-0000-000000000099',
            ledgerName: 'Demo',
        });

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('checkbox', { name: /demo ledger/i }));
        await user.click(screen.getByRole('button', { name: /create account/i }));

        await waitFor(() => {
            expect(setupSpy).toHaveBeenCalled();
        });
        expect(setupSpy.mock.calls[0]![0].includeDemo).toBe(true);
    });

    it('tells the user what is next when no ledger was seeded', async () => {
        vi.spyOn(authModule, 'performSetup').mockResolvedValue({
            userId: '00000000-0000-0000-0000-000000000010',
            username: 'alice',
            sessionId: '00000000-0000-0000-0000-000000000020',
            sessionExpiresAt: '2026-06-10T00:00:00Z',
            masterKeyBase64: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
            recoveryCodes: ['ABCD-1234'],
            ledgerId: null,
            ledgerName: null,
        });

        await renderSetup();

        const user = userEvent.setup();
        await user.type(await screen.findByLabelText(/username/i), 'alice');
        await user.type(screen.getByLabelText(/display name/i), 'Alice');
        await user.type(screen.getByLabelText(/passkey label/i), 'MacBook');
        await user.click(screen.getByRole('button', { name: /create account/i }));

        // Zero ledgers is the normal path now, so the success screen must
        // point somewhere rather than naming a ledger that doesn't exist.
        expect(
            await screen.findByText(/create a ledger or import one/i),
        ).toBeInTheDocument();
    });

    // --- Create-vs-Restore choice (ADR-0061) ---------------------------

    it('offers both create and restore on the choice screen', async () => {
        renderSetupRaw();

        expect(
            await screen.findByRole('button', { name: /set up a new install/i }),
        ).toBeInTheDocument();
        expect(
            screen.getByRole('button', { name: /restore from a backup/i }),
        ).toBeInTheDocument();
        // The create form is not shown until a choice is made.
        expect(screen.queryByLabelText(/username/i)).not.toBeInTheDocument();
    });

    it('shows the restore upload form when restore is chosen', async () => {
        renderSetupRaw();

        const user = userEvent.setup();
        await user.click(
            await screen.findByRole('button', { name: /restore from a backup/i }),
        );

        expect(await screen.findByLabelText(/backup file/i)).toBeInTheDocument();
        expect(screen.getByLabelText(/passphrase/i)).toBeInTheDocument();
        // Restore stays disabled until a file + passphrase are supplied.
        expect(screen.getByRole('button', { name: /^restore$/i })).toBeDisabled();
    });

    it('hands off to the restarting screen after a successful restore upload', async () => {
        const restoreSpy = vi
            .spyOn(authModule, 'restoreFromBackup')
            .mockResolvedValue(undefined);
        // Keep the post-restore poll from running (it would hit the network).
        vi.spyOn(authModule, 'waitForServerBack').mockImplementation(
            () => new Promise(() => {}),
        );

        renderSetupRaw();

        const user = userEvent.setup();
        await user.click(
            await screen.findByRole('button', { name: /restore from a backup/i }),
        );

        const file = new File(['ciphertext'], 'dr.cofferbak', {
            type: 'application/octet-stream',
        });
        await user.upload(screen.getByLabelText(/backup file/i), file);
        await user.type(screen.getByLabelText(/passphrase/i), 'correct horse');
        await user.click(screen.getByRole('button', { name: /^restore$/i }));

        await waitFor(() => {
            expect(restoreSpy).toHaveBeenCalledWith(TEST_TOKEN, file, 'correct horse');
        });
        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
    });
});
