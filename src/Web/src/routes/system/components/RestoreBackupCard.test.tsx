import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RestoreBackupCard } from './RestoreBackupCard';
import * as backupApi from '@/lib/api/backup';
import * as authApi from '@/lib/auth';
import * as authApiClient from '@/lib/api/auth';
import { ApiError } from '@/lib/api';

/** What the server answers when it accepts a restore. */
const ACCEPTED = { attemptId: 'attempt-1' };

/** What the boot recorded for an applied restore. */
const APPLIED: authApi.RestoreOutcome = {
    outcome: 'applied',
    finishedAtUtc: '2026-10-09T18:57:01Z',
    previousDatabase: 'coffer_pre_restore_20261009T185701',
    error: null,
};

// The card navigates to /login once the server is back. Mocked at the router
// so the assertion is on the navigation itself: without this the call would be
// swallowed and the whole point of the change — that the page moves on by
// itself — would go untested while every other test still passed.
const navigate = vi.fn();
vi.mock('@tanstack/react-router', () => ({
    useNavigate: () => navigate,
}));

// RestoreBackupCard (ADR-0071 D3): the authenticated-admin whole-DB restore.
// Locked down: the typed-confirmation gate, the KEK-mismatch acknowledge flow,
// and the post-success restarting notice.

function renderCard() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return {
        ...render(
            <QueryClientProvider client={qc}>
                <RestoreBackupCard />
            </QueryClientProvider>,
        ),
        // Exposed so a test can invalidate the backup list the way the backups
        // panel's create/delete mutations do.
        queryClient: qc,
    };
}

const backupFile = () => new File(['ciphertext'], 'db.cofferbak');

async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
    await user.upload(screen.getByLabelText(/backup file/i), backupFile());
    await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
    await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
}

describe('RestoreBackupCard', () => {
    beforeEach(() => sessionStorage.clear());

    beforeEach(() => {
        vi.restoreAllMocks();
        navigate.mockClear();
    });

    it('enables Restore only with a file, passphrase, and the exact confirm phrase', async () => {
        const user = userEvent.setup();
        renderCard();

        const button = screen.getByRole('button', { name: /restore database/i });
        expect(button).toBeDisabled();

        await user.upload(screen.getByLabelText(/backup file/i), backupFile());
        await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
        await user.type(screen.getByLabelText(/to confirm/i), 'not the phrase');
        expect(button).toBeDisabled();

        await user.clear(screen.getByLabelText(/to confirm/i));
        await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
        expect(button).toBeEnabled();
    });

    it('surfaces a KEK mismatch, requires acknowledgement, then proceeds', async () => {
        const spy = vi
            .spyOn(backupApi, 'restoreBackup')
            .mockRejectedValueOnce(
                new ApiError(422, 'This backup was sealed under a different Master KEK.', 'backup-kek-mismatch'),
            )
            .mockResolvedValueOnce(ACCEPTED);
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/different master kek/i)).toBeInTheDocument();
        // Blocked until the mismatch is acknowledged.
        expect(screen.getByRole('button', { name: /restore database/i })).toBeDisabled();

        await user.click(screen.getByRole('checkbox', { name: /restore anyway/i }));
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        expect(spy).toHaveBeenCalledTimes(2);
        expect(spy.mock.calls[1][3]).toBe(true);   // acknowledgeKekMismatch on the retry
    });

    it('accepts a source key instead of the acknowledgement, and passes it through', async () => {
        // ADR-0092 D4: supplying the source install's key means nothing gets
        // cleared, so there is nothing to acknowledge losing. The server enforces
        // the same rule and rejects a key that doesn't match the archive, so this
        // can't be used to skip the warning with a bogus value.
        const spy = vi
            .spyOn(backupApi, 'restoreBackup')
            .mockRejectedValueOnce(
                new ApiError(422, 'This backup was sealed under a different Master KEK.', 'backup-kek-mismatch'),
            )
            .mockResolvedValueOnce(ACCEPTED);
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));
        expect(await screen.findByText(/different master kek/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /restore database/i })).toBeDisabled();

        const sourceKey = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=';
        await user.type(screen.getByLabelText(/source install/i), sourceKey);

        // The acknowledgement checkbox gives way to the key field — they're
        // alternatives, not both.
        expect(screen.queryByRole('checkbox', { name: /restore anyway/i })).not.toBeInTheDocument();
        expect(screen.getByRole('button', { name: /restore database/i })).toBeEnabled();

        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        expect(spy.mock.calls[1][4]).toBe(sourceKey);   // forwarded to the API
    });

    it('shows the restarting notice on success', async () => {
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        // Held in the waiting phase so this test is about the notice appearing,
        // not about what it settles into — that is the tests below.
        vi.spyOn(authApi, 'waitForRestoreOutcome').mockReturnValue(new Promise(() => {}));
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        // It no longer promises a sign-out. Nothing in a restore revokes
        // sessions, so whether one is needed is not knowable at this point —
        // and it says only what it knows.
        expect(screen.getByText(/will say what happened when it is done/i)).toBeInTheDocument();
    });

    // --- a backup too big to send in one request (ADR-0101) ------------------

    /** A File whose reported size we control — Blob size is otherwise content. */
    function sized(name: string, size: number): File {
        const f = new File(['x'], name);
        Object.defineProperty(f, 'size', { value: size });
        return f;
    }

    const partName = (i: number, of: number) =>
        `coffer-20260929T031500000Z-0a1b2c3d.cofferbak.${String(i).padStart(3, '0')}-of-${String(of).padStart(3, '0')}`;

    it('sends a set of stored parts as parts, then restores by naming them', async () => {
        // The disaster-recovery shape: the install is gone, the backup came off
        // Google Drive as three files, and the proxy in front of the new install
        // refuses any body over 100 MB. Reassembling them locally first would ask
        // the operator to concatenate files by hand.
        const fromParts = vi
            .spyOn(backupApi, 'uploadRestoreFromParts')
            .mockResolvedValue(undefined);
        const restore = vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        const user = userEvent.setup();
        renderCard();

        await user.upload(screen.getByLabelText(/backup file/i), [
            sized(partName(2, 3), backupApi.BACKUP_PART_SIZE_FALLBACK),
            sized(partName(1, 3), backupApi.BACKUP_PART_SIZE_FALLBACK),
            sized(partName(3, 3), 1024),
        ]);
        await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
        await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        // Ordered before sending — the picker handed them over 2,1,3.
        expect(fromParts).toHaveBeenCalledTimes(1);
        expect(fromParts.mock.calls[0][0].map((f: File) => f.name)).toEqual([
            partName(1, 3), partName(2, 3), partName(3, 3),
        ]);
        // The restore NAMES the uploaded parts rather than carrying an archive.
        expect(restore.mock.calls[0][0]).toEqual({ uploadedParts: true });
    });

    it('refuses an incomplete set before anything is uploaded', async () => {
        const fromParts = vi
            .spyOn(backupApi, 'uploadRestoreFromParts')
            .mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderCard();

        await user.upload(screen.getByLabelText(/backup file/i), [
            sized(partName(1, 3), backupApi.BACKUP_PART_SIZE_FALLBACK),
            sized(partName(2, 3), backupApi.BACKUP_PART_SIZE_FALLBACK),
        ]);

        // Said here, by name, rather than discovered as a decrypt failure that
        // reads as a wrong passphrase.
        expect(await screen.findByRole('alert')).toHaveTextContent(
            /3 parts, but 2 were selected/i,
        );
        expect(screen.getByRole('button', { name: /restore database/i })).toBeDisabled();
        expect(fromParts).not.toHaveBeenCalled();
    });

    it('refuses a mixed selection rather than silently taking the first file', async () => {
        // Picking a whole backup AND a stray part is a slip, and the dangerous
        // reading of it is "restore the first one" — that starts a destructive
        // operation on something the operator did not choose.
        const restore = vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        const user = userEvent.setup();
        renderCard();

        await user.upload(screen.getByLabelText(/backup file/i), [
            sized('db.cofferbak', 1024),
            sized(partName(1, 2), backupApi.BACKUP_PART_SIZE_FALLBACK),
        ]);

        expect(await screen.findByRole('alert')).toHaveTextContent(
            /db\.cofferbak is not a backup part/i,
        );
        expect(screen.getByRole('button', { name: /restore database/i })).toBeDisabled();
        expect(restore).not.toHaveBeenCalled();
    });

    it('cuts at the size the SERVER is configured for, not a built-in one', async () => {
        // The whole point of Api:Backup:PartSizeMb being a setting is that
        // lowering it makes the browser send smaller pieces. This file is well
        // under the built-in fallback, so if the card ignored the server's
        // value it would go up whole and the setting would be decoration.
        vi.spyOn(backupApi, 'fetchRestoreLimits').mockResolvedValue({
            partSizeBytes: 1024 * 1024,
        });
        const inParts = vi.spyOn(backupApi, 'uploadRestoreInParts').mockResolvedValue(undefined);
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        const user = userEvent.setup();
        renderCard();

        // Wait for the configured size to land before choosing the file.
        expect(await screen.findByLabelText(/backup file/i)).toBeInTheDocument();
        await user.upload(
            screen.getByLabelText(/backup file/i), sized('db.cofferbak', 5 * 1024 * 1024));
        await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
        await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        expect(inParts).toHaveBeenCalledTimes(1);
        expect(inParts.mock.calls[0][1]).toBe(1024 * 1024);   // the server's size
    });

    it('splits a single oversize file rather than sending it whole', async () => {
        const inParts = vi
            .spyOn(backupApi, 'uploadRestoreInParts')
            .mockResolvedValue(undefined);
        const restore = vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        const user = userEvent.setup();
        renderCard();

        const big = sized('db.cofferbak', backupApi.BACKUP_PART_SIZE_FALLBACK + 1);
        await user.upload(screen.getByLabelText(/backup file/i), big);
        await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
        await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        expect(inParts).toHaveBeenCalledTimes(1);
        expect(restore.mock.calls[0][0]).toEqual({ uploadedParts: true });
    });

    it('sends a small file whole — nothing to work around', async () => {
        const inParts = vi
            .spyOn(backupApi, 'uploadRestoreInParts')
            .mockResolvedValue(undefined);
        const restore = vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        expect(inParts).not.toHaveBeenCalled();
        expect(restore.mock.calls[0][0]).toBeInstanceOf(File);
    });

    // --- the stored-backup picker tracks the list, without a reload ---------
    //
    // The card and the backups panel are separate components on one screen.
    // They read the SAME cache entry, so a backup created or deleted over
    // there has to change the picker here. It did not: the panel invalidated
    // "admin-backups" while this card queried "backups", so the picker showed
    // whatever was true when the page loaded.

    const aBackup = (id: string) => ({
        id,
        sizeBytes: 2048,
        createdAtUtc: '2026-06-23T03:15:00Z',
        pinned: false,
    });

    it('shows the picker when the first backup appears, with no reload', async () => {
        const fetch = vi.spyOn(backupApi, 'fetchBackups').mockResolvedValue([]);
        const { queryClient } = renderCard();

        // Anchored on the option text, not on the absence of a <select>: a
        // filter or any other dropdown elsewhere would satisfy a bare
        // "no combobox" assertion.
        expect(await screen.findByLabelText(/backup file/i)).toBeInTheDocument();
        expect(screen.queryByText(/choose a stored backup/i)).not.toBeInTheDocument();

        fetch.mockResolvedValue([aBackup('coffer-20260623T031500000Z-0a1b2c3d')]);
        await queryClient.invalidateQueries({ queryKey: backupApi.BACKUPS_QUERY_KEY });

        expect(await screen.findByText(/choose a stored backup/i)).toBeInTheDocument();
    });

    it('drops a selected backup that has since been deleted', async () => {
        const id = 'coffer-20260623T031500000Z-0a1b2c3d';
        const fetch = vi.spyOn(backupApi, 'fetchBackups').mockResolvedValue([aBackup(id)]);
        vi.spyOn(backupApi, 'validateRestoreKek').mockResolvedValue({
            hasFingerprint: true, compatible: true,
        });
        const user = userEvent.setup();
        const { queryClient } = renderCard();

        const select = await screen.findByRole('combobox');
        await user.selectOptions(select, id);
        await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
        await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
        expect(screen.getByRole('button', { name: /restore database/i })).toBeEnabled();

        // Deleted from the panel while this form sat filled in.
        fetch.mockResolvedValue([]);
        await queryClient.invalidateQueries({ queryKey: backupApi.BACKUPS_QUERY_KEY });

        // The selection goes with it. Leaving Restore armed and pointed at an
        // artifact that no longer exists is worse than a stale list: it offers
        // a destructive action that cannot work.
        await waitFor(() =>
            expect(screen.getByRole('button', { name: /restore database/i })).toBeDisabled());
    });

    // --- the restarting notice says what happened ---------------------------
    //
    // The first version stated "wait a moment, then sign in" and never changed, so a
    // restore that had already completed looked identical to one that had hung. A
    // later one asked /api/auth/me whether your session had survived, because a
    // restore used to bring every session back — which also handed back access revoked
    // after the backup. A restore now signs everyone out (FU-076), so the notice says
    // that, and what each person has to redo.

    it('says the restore completed, that everyone is signed out, and what was reset', async () => {
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        const outcome = vi.spyOn(authApi, 'waitForRestoreOutcome').mockResolvedValue(APPLIED);
        const me = vi.spyOn(authApiClient, 'fetchCurrentUser');
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restore complete/i)).toBeInTheDocument();
        // The outcome read is THIS attempt's, by the id the server handed back.
        expect(outcome).toHaveBeenCalledWith('attempt-1');
        expect(screen.getByText(/signs everyone out/i)).toBeInTheDocument();
        expect(screen.getByText(/AI connector has to be connected again/i)).toBeInTheDocument();
        expect(screen.getByText(/MCP tokens created before the restore/i)).toBeInTheDocument();
        expect(screen.getByText(/invites that had not been accepted/i)).toBeInTheDocument();
        // Where the way back is.
        expect(screen.getByText('coffer_pre_restore_20261009T185701')).toBeInTheDocument();
        // Nothing to ask: no session survives a restore.
        expect(me).not.toHaveBeenCalled();
        expect(screen.queryByText(/still signed in/i)).not.toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: /^sign in$/i }));
        expect(navigate).toHaveBeenCalledWith({ to: '/login' });
    });

    it('stops claiming progress if the server never answers', async () => {
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        vi.spyOn(authApi, 'waitForRestoreOutcome').mockRejectedValue(
            new Error('The server did not come back in time.'),
        );
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        // A restore that is genuinely stuck is not something this page can
        // resolve, so it says so rather than spinning a reassuring message —
        // and it must NOT claim completion.
        expect(await screen.findByText(/still restoring/i)).toBeInTheDocument();
        expect(screen.queryByText(/restore complete/i)).not.toBeInTheDocument();
        expect(navigate).not.toHaveBeenCalled();
    });

    // --- a failed restore is reported as failed -------------------------------
    //
    // Found on a real install (2026-10-09): the boot-time restore failed — logged,
    // database untouched — and this page said "Restore complete", because it
    // took "the server answers again" for success. The outcome is now read back.

    it('reports a failed restore as failed, and that nothing was changed', async () => {
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        vi.spyOn(authApi, 'waitForRestoreOutcome').mockResolvedValue({
            outcome: 'failed',
            finishedAtUtc: '2026-10-09T17:50:46Z',
            previousDatabase: null,
            error: 'database "coffer" is being accessed by other users',
        });
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restore failed — nothing was changed/i)).toBeInTheDocument();
        expect(screen.getByText(/being accessed by other users/i)).toBeInTheDocument();
        expect(screen.queryByText(/restore complete/i)).not.toBeInTheDocument();

        // Back to the form, to fix the cause and try again.
        await user.click(screen.getByRole('button', { name: /^back$/i }));
        expect(await screen.findByRole('button', { name: /restore database/i })).toBeInTheDocument();
    });

    it('reports an unknown outcome as unknown, never as success or failure', async () => {
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        vi.spyOn(authApi, 'waitForRestoreOutcome').mockResolvedValue({
            outcome: 'unknown',
            finishedAtUtc: '2026-10-09T18:57:01Z',
            previousDatabase: null,
            error: 'The swap reported an error and its result could not be checked.',
        });
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/could not tell whether this restore was applied/i)).toBeInTheDocument();
        expect(screen.getByText(/result could not be checked/i)).toBeInTheDocument();
        expect(screen.queryByText(/restore complete/i)).not.toBeInTheDocument();
        expect(screen.queryByText(/nothing was changed/i)).not.toBeInTheDocument();
    });

    it('resumes waiting for a restore after the page is reloaded mid-restore', async () => {
        // A restore of a large backup keeps the server down for minutes; a reload
        // in that time used to lose the attempt, and with it any way to learn the
        // outcome.
        sessionStorage.setItem('coffer.pendingRestoreAttempt', 'attempt-9');
        const outcome = vi.spyOn(authApi, 'waitForRestoreOutcome').mockResolvedValue(APPLIED);
        vi.spyOn(authApiClient, 'fetchCurrentUser').mockResolvedValue({
            id: 'u1', username: 'usera', displayName: 'User A', isAdmin: true,
        });
        renderCard();

        expect(await screen.findByText(/restore complete/i)).toBeInTheDocument();
        expect(outcome).toHaveBeenCalledWith('attempt-9');
        // Shown once; a later visit to the page starts fresh.
        expect(sessionStorage.getItem('coffer.pendingRestoreAttempt')).toBeNull();
    });

    it('never reports success when the server recorded no outcome', async () => {
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(ACCEPTED);
        vi.spyOn(authApi, 'waitForRestoreOutcome').mockResolvedValue(null);
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/could not confirm the restore/i)).toBeInTheDocument();
        expect(screen.getByText(/do not assume it worked/i)).toBeInTheDocument();
        expect(screen.queryByText(/restore complete/i)).not.toBeInTheDocument();
    });
});
