import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';

import { ApiError, setBackupPassphrase } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Modal } from '@/components/ui/Modal';

/** Mirror of the server minimum (AdminBackupsEndpoints.MinPassphraseLength). */
const MIN_LENGTH = 8;

/**
 * Set / rotate the backup passphrase (ADR-0060). Two fields (passphrase + confirm)
 * with a prominent warning. The server seals it under the master KEK; the cleartext
 * is never stored. Rotating affects FUTURE backups only — existing artifacts still
 * need the previous passphrase, which the dialog calls out.
 *
 * The warning used to say the passphrase "cannot be recovered". That was never quite
 * true — the server unseals it on every scheduled backup — and since ADR-0092 D7 it
 * is plainly false: an admin can look it up behind a passkey prompt. The copy now
 * draws the line where it actually falls, which is losing the *server*, because a
 * warning an operator can catch out is one they stop believing.
 */
export function SetBackupPassphraseDialog({
    isRotate,
    onClose,
    onSaved,
}: {
    isRotate: boolean;
    onClose: () => void;
    onSaved: () => void;
}) {
    const [passphrase, setPassphrase] = useState('');
    const [confirm, setConfirm] = useState('');

    const mutation = useMutation({
        mutationFn: () => setBackupPassphrase(passphrase),
        onSuccess: () => onSaved(),
    });

    const tooShort = passphrase.length > 0 && passphrase.length < MIN_LENGTH;
    const mismatch = confirm.length > 0 && confirm !== passphrase;
    const canSubmit = passphrase.length >= MIN_LENGTH && confirm === passphrase;

    function handleSubmit(e: React.FormEvent) {
        e.preventDefault();
        if (!canSubmit || mutation.isPending) return;
        mutation.mutate();
    }

    const serverInvalid = mutation.error instanceof ApiError
        && mutation.error.code === 'backup-passphrase-invalid';

    return (
        <Modal open onClose={onClose} titleId="set-passphrase-title" className="max-w-md">
            <form onSubmit={handleSubmit}>
                <header className="border-b border-border px-4 py-3">
                    <h2 id="set-passphrase-title" className="text-base font-semibold">
                        {isRotate ? 'Change backup passphrase' : 'Set backup passphrase'}
                    </h2>
                </header>
                <div className="space-y-3 p-4">
                    <div
                        role="note"
                        className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs text-text"
                    >
                        This passphrase encrypts every backup — without it, your backups
                        are unreadable. While this install is running you can look it up
                        again under System → Backups, but a server you've <strong>lost</strong>
                        can't tell you anything. Store it somewhere separate.
                        {isRotate ? (
                            <span className="mt-1 block">
                                Changing it affects <strong>future</strong> backups only;
                                existing backups still require the previous passphrase.
                            </span>
                        ) : null}
                    </div>

                    <div className="flex flex-col gap-1 text-xs">
                        <FieldLabel htmlFor="backup-passphrase">Passphrase</FieldLabel>
                        <input
                            id="backup-passphrase"
                            type="password"
                            value={passphrase}
                            onChange={(e) => setPassphrase(e.target.value)}
                            autoFocus
                            autoComplete="new-password"
                            aria-invalid={tooShort}
                            className="rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                        <span className="text-[0.6875rem] text-text-subtle">
                            At least {MIN_LENGTH} characters.
                        </span>
                    </div>

                    <div className="flex flex-col gap-1 text-xs">
                        <FieldLabel htmlFor="backup-passphrase-confirm">Confirm passphrase</FieldLabel>
                        <input
                            id="backup-passphrase-confirm"
                            type="password"
                            value={confirm}
                            onChange={(e) => setConfirm(e.target.value)}
                            autoComplete="new-password"
                            aria-invalid={mismatch}
                            className="rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                        {mismatch ? (
                            <span role="alert" className="text-[0.6875rem] text-state-danger">
                                Passphrases don't match.
                            </span>
                        ) : null}
                    </div>

                    {mutation.isError ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {serverInvalid
                                ? `Passphrase must be at least ${MIN_LENGTH} characters.`
                                : errorMessage(mutation.error, 'Could not set the passphrase.')}
                        </p>
                    ) : null}
                </div>
                <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        disabled={mutation.isPending}>
                        Cancel
                    </Button>
                    <Button type="submit" variant="primary" size="sm"
                        disabled={!canSubmit || mutation.isPending}>
                        {mutation.isPending ? 'Saving…' : isRotate ? 'Change' : 'Set passphrase'}
                    </Button>
                </footer>
            </form>
        </Modal>
    );
}
