import { useId, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { AlertTriangle } from 'lucide-react';

import { restoreBackup, validateRestoreKek, RESTORE_CONFIRM_PHRASE } from '@/lib/api/backup';
import { ApiError } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import type { BackupKekCheck } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';

/**
 * Restore-the-whole-database card (ADR-0071 D3). Upload a `.cofferbak` +
 * passphrase, type the exact confirmation phrase, and the server stages the
 * archive and restarts to apply it. Destructive: it replaces ALL users,
 * ledgers, and data across the deployment, and signs everyone out. A
 * cross-install KEK mismatch (D4) is surfaced as a warning the admin must
 * explicitly acknowledge.
 */
export function RestoreBackupCard() {
    const fileId = useId();
    const passId = useId();
    const confirmId = useId();

    const [file, setFile] = useState<File | null>(null);
    const [passphrase, setPassphrase] = useState('');
    const [confirm, setConfirm] = useState('');
    const [acknowledgeKek, setAcknowledgeKek] = useState(false);
    const [kekCheck, setKekCheck] = useState<BackupKekCheck | null>(null);
    const [restarting, setRestarting] = useState(false);

    const restoreMutation = useMutation({
        mutationFn: (vars: { file: File; passphrase: string; confirm: string; ack: boolean }) =>
            restoreBackup(vars.file, vars.passphrase, vars.confirm, vars.ack),
        onSuccess: () => setRestarting(true),
    });

    const kekMismatch =
        restoreMutation.error instanceof ApiError &&
        restoreMutation.error.code === 'backup-kek-mismatch';
    // The upfront header check (or the mid-restore 422) — either flags a KEK the
    // admin must acknowledge before the destructive restore.
    const needsKekAck = kekMismatch || (kekCheck !== null && !kekCheck.compatible);
    const errorText = restoreMutation.error
        ? errorMessage(restoreMutation.error, 'Restore failed.')
        : null;

    const confirmOk = confirm.trim().toLowerCase() === RESTORE_CONFIRM_PHRASE;
    const canSubmit =
        file !== null &&
        passphrase.length > 0 &&
        confirmOk &&
        (!needsKekAck || acknowledgeKek) &&
        !restoreMutation.isPending;

    if (restarting) {
        return (
            <Panel className="border-state-warning/40 bg-state-warning-soft">
                <PanelBody className="space-y-1">
                    <p className="text-sm font-medium">Restoring…</p>
                    <p className="text-sm text-text-muted">
                        The database is being restored and the app is restarting. Everyone has
                        been signed out. Wait a moment, then{' '}
                        <a href="/login" className="text-accent underline">sign in</a> with the
                        restored credentials.
                    </p>
                </PanelBody>
            </Panel>
        );
    }

    return (
        <Panel className="border-state-danger/40">
            <PanelBody className="space-y-4">
                <div className="space-y-1">
                    <h3 className="flex items-center gap-2 text-sm font-semibold text-state-danger">
                        <AlertTriangle className="h-4 w-4" aria-hidden />
                        Restore from a backup
                    </h3>
                    <p className="text-xs text-text-muted">
                        Replaces <strong>all users, ledgers, and data</strong> across the whole
                        deployment with the contents of a <code>.cofferbak</code> file, then
                        restarts and signs everyone out. Also the way to migrate from another
                        install. This cannot be undone.
                    </p>
                </div>

                <div className="space-y-1.5">
                    <FieldLabel htmlFor={fileId}>Backup file (.cofferbak)</FieldLabel>
                    <input
                        id={fileId}
                        type="file"
                        accept=".cofferbak"
                        onChange={(e) => {
                            const f = e.target.files?.[0] ?? null;
                            setFile(f);
                            restoreMutation.reset();
                            setAcknowledgeKek(false);
                            setKekCheck(null);
                            // Pre-flight the KEK compatibility from the header before
                            // the admin commits (ADR-0074). Best-effort: a failure
                            // just leaves the mid-restore check as the backstop.
                            if (f) {
                                void validateRestoreKek(f)
                                    .then(setKekCheck)
                                    .catch(() => setKekCheck(null));
                            }
                        }}
                        className="block w-full text-sm text-text file:mr-3 file:rounded file:border-0 file:bg-surface-hover file:px-3 file:py-1.5 file:text-sm file:font-medium file:text-text"
                    />
                </div>

                <div className="space-y-1.5">
                    <FieldLabel htmlFor={passId}>Passphrase</FieldLabel>
                    <Input
                        id={passId}
                        type="password"
                        autoComplete="off"
                        value={passphrase}
                        disabled={restoreMutation.isPending}
                        onChange={(e) => setPassphrase(e.target.value)}
                    />
                </div>

                <div className="space-y-1.5">
                    <FieldLabel htmlFor={confirmId}>
                        Type “{RESTORE_CONFIRM_PHRASE}” to confirm
                    </FieldLabel>
                    <Input
                        id={confirmId}
                        autoComplete="off"
                        placeholder={RESTORE_CONFIRM_PHRASE}
                        value={confirm}
                        disabled={restoreMutation.isPending}
                        onChange={(e) => setConfirm(e.target.value)}
                    />
                </div>

                {kekCheck?.compatible ? (
                    <p className="text-xs text-state-success">
                        ✓ This backup matches this install’s Master KEK — a clean restore.
                    </p>
                ) : null}

                {needsKekAck ? (
                    <div className="space-y-2 rounded border border-state-warning/40 bg-state-warning-soft p-3">
                        <p className="text-sm font-medium text-text">
                            {kekCheck !== null && !kekCheck.hasFingerprint
                                ? 'Older backup — the Master KEK can’t be verified.'
                                : 'This backup was sealed under a different Master KEK.'}
                        </p>
                        <p className="text-xs text-text-muted">
                            Data and passkeys will restore, but the backup passphrase and Google
                            Drive connection won’t carry over — you’ll re-set them afterward. For a
                            clean migration, set <code>COFFER_MASTER_KEK_BASE64</code> to the source
                            install’s value first.
                        </p>
                        <Checkbox
                            label="Restore anyway — I'll re-set the backup passphrase and reconnect Google Drive afterward."
                            checked={acknowledgeKek}
                            onChange={(e) => setAcknowledgeKek(e.target.checked)}
                        />
                    </div>
                ) : null}

                {errorText && !kekMismatch ? (
                    <p role="alert" className="text-sm text-state-danger">{errorText}</p>
                ) : null}

                <div className="flex justify-end">
                    <Button
                        type="button"
                        variant="primary"
                        disabled={!canSubmit}
                        className="bg-state-danger hover:bg-state-danger/90"
                        onClick={() =>
                            file &&
                            restoreMutation.mutate({ file, passphrase, confirm, ack: acknowledgeKek })
                        }
                    >
                        {restoreMutation.isPending ? 'Restoring…' : 'Restore database'}
                    </Button>
                </div>
            </PanelBody>
        </Panel>
    );
}
