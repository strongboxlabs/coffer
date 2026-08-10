import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { MasterKeyPanel } from './MasterKeyPanel';
import * as masterKeyApi from '@/lib/api/masterKey';
import { ApiError } from '@/lib/api';

// MasterKeyPanel (ADR-0092 D2/D4). The assertions worth having here are about
// what does and doesn't put key material on screen, and that rotation can't be
// triggered by a stray click.

const FIXTURE_KEY = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=';

function renderPanel() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={qc}>
            <MasterKeyPanel />
        </QueryClientProvider>,
    );
}

function stubStatus() {
    return vi.spyOn(masterKeyApi, 'fetchMasterKeyStatus').mockResolvedValue({
        kekId: 'v1',
        path: '/app/data/master.key',
        fingerprint: 'A1B2C3D4E5F60718293A4B5C6D7E8F90',
    });
}

describe('MasterKeyPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('shows metadata but no key material on load', async () => {
        stubStatus();
        renderPanel();

        expect(await screen.findByText('v1')).toBeInTheDocument();
        expect(screen.getByText('A1B2C3D4E5F60718293A4B5C6D7E8F90')).toBeInTheDocument();
        // The point of the fingerprint: identifies the key without revealing it.
        expect(screen.queryByText(FIXTURE_KEY)).not.toBeInTheDocument();
        // The file path is NOT here. It's a path inside the container, so it's not
        // something an operator can act on at a glance, and it doesn't help answer
        // "which key is this install on?" — it shows up when revealing instead.
        expect(screen.queryByText('/app/data/master.key')).not.toBeInTheDocument();
    });

    it('shows where the key lives only when the key is on screen', async () => {
        stubStatus();
        vi.spyOn(masterKeyApi, 'revealMasterKey')
            .mockResolvedValue({ keyBase64: FIXTURE_KEY, kekId: 'v1' });
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        expect(screen.queryByText('/app/data/master.key')).not.toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: /show key/i }));

        // Location is actionable exactly when you're saving or replacing the key.
        expect(await screen.findByText('/app/data/master.key')).toBeInTheDocument();
        expect(screen.getByText(/stored in the container/i)).toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: /^hide$/i }));
        expect(screen.queryByText('/app/data/master.key')).not.toBeInTheDocument();
    });

    it('offers no new-key-id field — the server increments', async () => {
        // An advisory label nothing depends on isn't a decision worth putting in front
        // of an operator mid-rotation, and the placeholder used to reimplement
        // NextKekId client-side where it could drift.
        stubStatus();
        renderPanel();

        await screen.findByText('v1');
        expect(screen.queryByLabelText(/new key id/i)).not.toBeInTheDocument();
    });

    it('reveals the key only after the ceremony, and can hide it again', async () => {
        stubStatus();
        const reveal = vi
            .spyOn(masterKeyApi, 'revealMasterKey')
            .mockResolvedValue({ keyBase64: FIXTURE_KEY, kekId: 'v1' });
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        expect(screen.queryByText(FIXTURE_KEY)).not.toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: /show key/i }));

        expect(await screen.findByText(FIXTURE_KEY)).toBeInTheDocument();
        expect(reveal).toHaveBeenCalledTimes(1);

        await user.click(screen.getByRole('button', { name: /^hide$/i }));
        expect(screen.queryByText(FIXTURE_KEY)).not.toBeInTheDocument();
    });

    it('is repeatable — not show-once', async () => {
        // ADR-0092 D2: an admin who can already read every ledger gains nothing
        // from re-display, while show-once would strand an operator whose browser
        // died before they wrote the key down.
        stubStatus();
        vi.spyOn(masterKeyApi, 'revealMasterKey')
            .mockResolvedValue({ keyBase64: FIXTURE_KEY, kekId: 'v1' });
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        await user.click(screen.getByRole('button', { name: /show key/i }));
        await screen.findByText(FIXTURE_KEY);
        await user.click(screen.getByRole('button', { name: /^hide$/i }));
        await user.click(screen.getByRole('button', { name: /show again/i }));

        expect(await screen.findByText(FIXTURE_KEY)).toBeInTheDocument();
    });

    it('surfaces a dismissed passkey prompt without showing a key', async () => {
        stubStatus();
        vi.spyOn(masterKeyApi, 'revealMasterKey')
            .mockRejectedValue(new Error('The operation either timed out or was not allowed.'));
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        await user.click(screen.getByRole('button', { name: /show key/i }));

        expect(await screen.findByText(/timed out or was not allowed/i)).toBeInTheDocument();
        expect(screen.queryByText(FIXTURE_KEY)).not.toBeInTheDocument();
    });

    it('explains when the account has no usable passkey for this domain', async () => {
        stubStatus();
        vi.spyOn(masterKeyApi, 'revealMasterKey').mockRejectedValue(
            new ApiError(
                422,
                'No passkey registered for this domain is available to confirm your identity.',
                'master-key-no-credentials',
            ),
        );
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        await user.click(screen.getByRole('button', { name: /show key/i }));

        expect(await screen.findByText(/no passkey registered for this domain/i)).toBeInTheDocument();
    });

    // --- rotation ----------------------------------------------------------

    it('gates rotation behind the typed phrase', async () => {
        stubStatus();
        const rotate = vi.spyOn(masterKeyApi, 'rotateMasterKey');
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        const button = screen.getByRole('button', { name: /rotate master key/i });
        expect(button).toBeDisabled();

        await user.type(screen.getByLabelText(/to confirm/i), 'yes');
        expect(button).toBeDisabled();

        await user.clear(screen.getByLabelText(/to confirm/i));
        await user.type(screen.getByLabelText(/to confirm/i), 'rotate');
        expect(button).toBeEnabled();
        expect(rotate).not.toHaveBeenCalled();
    });

    it('offers no separate check step — rotation checks itself', async () => {
        // The server runs the dry run as rotation's first step and refuses before
        // touching anything, so a "Check first" button only previewed a list that
        // didn't change the decision, while implying the check was opt-in.
        stubStatus();
        renderPanel();

        await screen.findByText('v1');
        expect(screen.queryByRole('button', { name: /check first/i })).not.toBeInTheDocument();
        // …and the description says the check happens anyway.
        expect(screen.getByText(/checks first and stops before changing anything/i))
            .toBeInTheDocument();
    });

    it('names what moved in operator terms, not API counters', async () => {
        // Was "Re-wrapped 3 ledger keys, the backup passphrase" — jargon, and a bare
        // count of something an operator has no concept of.
        stubStatus();
        vi.spyOn(masterKeyApi, 'rotateMasterKey').mockResolvedValue({
            keyBase64: 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=',
            kekId: 'v2',
            ledgersRotated: 3,
            backupPassphraseRotated: true,
            driveTokenRotated: false,
            previousKeyArchivedAt: '/app/data/master.key.20260807T120000Z.bak',
            restartPending: true,
        });
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        await user.type(screen.getByLabelText(/to confirm/i), 'rotate');
        await user.click(screen.getByRole('button', { name: /rotate master key/i }));

        expect(await screen.findByText(/bank-feed connections in 3 ledgers/i)).toBeInTheDocument();
        expect(screen.getByText(/your backup passphrase/i)).toBeInTheDocument();
        expect(screen.queryByText(/ledger key/i)).not.toBeInTheDocument();
        expect(screen.queryByText(/re-wrapped/i)).not.toBeInTheDocument();
        // The archive PATH is not shown — it's an in-container location the operator
        // can't act on. The reassurance is what matters here.
        expect(screen.queryByText(/master\.key\.20260807/)).not.toBeInTheDocument();
        expect(screen.getByText(/previous key was kept alongside/i)).toBeInTheDocument();
    });

    it('explains how to remediate a blocked rotation', async () => {
        // The one rotation failure an operator can act on. Previously it surfaced only
        // the server's diagnostic, which says what is wrong but not what to do.
        stubStatus();
        vi.spyOn(masterKeyApi, 'rotateMasterKey').mockRejectedValue(
            new ApiError(
                422,
                "Google Drive OAuth token does not open under the current KEK (id 'v1').",
                'master-key-rotate-blocked',
            ),
        );
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        await user.type(screen.getByLabelText(/to confirm/i), 'rotate');
        await user.click(screen.getByRole('button', { name: /rotate master key/i }));

        expect(await screen.findByText(/nothing was changed/i)).toBeInTheDocument();
        expect(screen.getByText(/restored without its master key/i)).toBeInTheDocument();
        expect(screen.getByText(/re-link your bank feeds/i)).toBeInTheDocument();
        // The server's diagnostic is kept, but as detail rather than as the whole story.
        expect(screen.getByText(/does not open under the current kek/i)).toBeInTheDocument();
    });

    it('keeps the new key on screen while the server is down, then confirms it is done', async () => {
        // Three failures in one flow, all real:
        //  1. The status query fails during the restart, and the panel's error branch
        //     replaced the whole body — HIDING the newly minted key before the operator
        //     had saved it. The one copy on screen must survive an unrelated fetch
        //     failing.
        //  2. React Query kept serving the PRE-rotation id, so the header claimed v1
        //     while the notice said v2.
        //  3. "This page will fail to load for a few seconds" told the operator nothing
        //     useful: not whether to act, not whether it worked, not when it's over.
        const newKey = 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=';
        const status = vi.spyOn(masterKeyApi, 'fetchMasterKeyStatus');
        // Loaded once, then the server goes away, then it returns on the NEW key.
        status.mockResolvedValueOnce({
            kekId: 'v1', path: '/app/data/master.key', fingerprint: 'AAAA',
        });
        vi.spyOn(masterKeyApi, 'rotateMasterKey').mockResolvedValue({
            keyBase64: newKey,
            kekId: 'v2',
            ledgersRotated: 2,
            backupPassphraseRotated: true,
            driveTokenRotated: false,
            previousKeyArchivedAt: '/app/data/master.key.20260806T120000Z.bak',
            restartPending: true,
        });
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        // From here the server is unreachable.
        status.mockRejectedValue(new Error('Failed to fetch'));

        await user.type(screen.getByLabelText(/to confirm/i), 'rotate');
        await user.click(screen.getByRole('button', { name: /rotate master key/i }));

        // (1) The key is on screen and stays there despite the failing status query.
        expect(await screen.findByText(newKey)).toBeInTheDocument();
        expect(screen.getByText(/rotated to .v2./i)).toBeInTheDocument();
        expect(screen.getByText(/previous key was kept alongside/i)).toBeInTheDocument();
        // (2) No stale identity: the pre-rotation id must not still be claimed.
        expect(screen.queryByText('v1')).not.toBeInTheDocument();
        expect(screen.getByText(/reconnecting to the server/i)).toBeInTheDocument();
        // (3) Progress, not a warning about the page breaking.
        // Short, and explicitly coloured rather than inheriting — it was reported
        // unreadable (dark on dark), and in practice it's on screen for about half a
        // second, so it's a status flicker, not information anyone must catch.
        const restarting = screen.getByText(/^Restarting…$/);
        expect(restarting).toBeInTheDocument();
        expect(restarting).toHaveClass('text-state-warning');

        // The server returns on the new key; the panel notices by itself.
        status.mockResolvedValue({
            kekId: 'v2', path: '/app/data/master.key', fingerprint: 'BBBB',
        });

        expect(await screen.findByText(/the server is back and running/i, undefined, { timeout: 5000 }))
            .toBeInTheDocument();
        // And the key is still there to be saved.
        expect(screen.getByText(newKey)).toBeInTheDocument();
    });

    it('reports a blocked rotation and shows no key', async () => {
        stubStatus();
        vi.spyOn(masterKeyApi, 'rotateMasterKey').mockRejectedValue(
            new ApiError(
                422,
                "Google Drive OAuth token does not open under the current KEK (id 'v1').",
                'master-key-rotate-blocked',
            ),
        );
        const user = userEvent.setup();
        renderPanel();

        await screen.findByText('v1');
        await user.type(screen.getByLabelText(/to confirm/i), 'rotate');
        await user.click(screen.getByRole('button', { name: /rotate master key/i }));

        expect(await screen.findByText(/does not open under the current kek/i)).toBeInTheDocument();
        expect(screen.queryByText(FIXTURE_KEY)).not.toBeInTheDocument();
    });
});
