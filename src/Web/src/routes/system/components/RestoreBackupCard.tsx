import { useEffect, useId, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { useMutation, useQuery } from '@tanstack/react-query';
import { AlertTriangle } from 'lucide-react';

import {
    restoreBackup,
    validateRestoreKek,
    uploadRestoreInParts,
    uploadRestoreFromParts,
    orderBackupParts,
    parseBackupPartName,
    backupPartCount,
    fetchRestoreLimits,
    BACKUPS_QUERY_KEY,
    BACKUP_PART_SIZE_FALLBACK,
    RESTORE_CONFIRM_PHRASE,
    fetchBackups,
} from '@/lib/api/backup';
import { ApiError } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { waitForServerBack } from '@/lib/auth';
import { fetchCurrentUser } from '@/lib/api/auth';
import type { BackupKekCheck } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { formatLedgerDateTime } from '@/lib/dates';
import { formatBytes } from '@/lib/format';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';

/** What the page knows about a restore in progress. */
type RestoreState =
    | { phase: 'waiting' }
    /** Server answered, and the session still works — no sign-in needed. */
    | { phase: 'done'; signedIn: true; username: string }
    /** Server answered; the restored database has no session for this cookie. */
    | { phase: 'done'; signedIn: false }
    | { phase: 'timedOut' };

/**
 * Shown from the moment a restore is accepted until the server is back.
 *
 * Two things this gets wrong if it is written carelessly, and both were got
 * wrong here first.
 *
 * It must SAY the restore finished. The first version stated "wait a moment,
 * then sign in" and never changed, so a completed restore looked exactly like
 * a hung one. The second version polled and then redirected — which is a
 * silent acknowledgement: arriving somewhere else is not the same as being
 * told the thing you asked for worked. A restore replaces every row in the
 * database; it is the last operation that should leave you inferring the
 * outcome from a change of scenery.
 *
 * And it must not ASSUME you were signed out. Nothing in the restore revokes
 * sessions — "everyone is signed out" is a consequence of the database being
 * replaced, not an action anyone takes. Restore a backup of THIS install taken
 * while you were signed in, which is what rolling back means, and your session
 * row comes back with everything else: you are still signed in and a login
 * screen is an interruption with nothing behind it. Restore a backup from a
 * different install, or one older than your session, and you are not. The page
 * cannot tell which from the client, so it asks: /api/auth/me either answers or
 * 401s, and the notice says what is actually true.
 */
function RestoringNotice() {
    const navigate = useNavigate();
    const [state, setState] = useState<RestoreState>({ phase: 'waiting' });

    useEffect(() => {
        let cancelled = false;
        waitForServerBack()
            .then(async () => {
                if (cancelled) return;
                try {
                    const me = await fetchCurrentUser();
                    if (!cancelled) setState({ phase: 'done', signedIn: true, username: me.username });
                } catch {
                    // 401 — the restored database has no session for this cookie.
                    if (!cancelled) setState({ phase: 'done', signedIn: false });
                }
            })
            .catch(() => {
                if (!cancelled) setState({ phase: 'timedOut' });
            });
        return () => {
            cancelled = true;
        };
    }, []);

    if (state.phase === 'timedOut') {
        return (
            <Panel className="border-state-warning/40 bg-state-warning-soft">
                <PanelBody className="space-y-1">
                    <p className="text-sm font-medium">Still restoring</p>
                    <p className="text-sm text-text-muted">
                        The server has not answered yet. A large restore can take a while,
                        and it is still running there whatever this page shows. Reload in a
                        moment.
                    </p>
                </PanelBody>
            </Panel>
        );
    }

    if (state.phase === 'waiting') {
        return (
            <Panel className="border-state-warning/40 bg-state-warning-soft">
                <PanelBody className="space-y-1">
                    <p className="text-sm font-medium">Restoring…</p>
                    <p className="text-sm text-text-muted">
                        The database is being replaced and the app is restarting. This page
                        will say so when it is done.
                    </p>
                </PanelBody>
            </Panel>
        );
    }

    return (
        <Panel className="border-state-success/40 bg-state-success-soft">
            <PanelBody className="space-y-2">
                <p className="text-sm font-medium text-state-success">Restore complete</p>
                {state.signedIn ? (
                    <>
                        <p className="text-sm text-text-muted">
                            The database was replaced and the app restarted. You are still
                            signed in as <strong>{state.username}</strong> — the restored
                            database has your session in it, so there is nothing to sign in
                            to again.
                        </p>
                        <p className="text-sm text-text-muted">
                            Everything on screen is from before the restore.
                        </p>
                        {/* A full reload, not a cache invalidation. Every row in
                            the database was just replaced, so what is stale is
                            not only the query cache: route loader data and any
                            component holding an id from the old database are
                            stale too. Invalidating queries left the page
                            looking unchanged — which is indistinguishable from
                            a button that does nothing. */}
                        <Button
                            type="button"
                            variant="secondary"
                            onClick={() => window.location.reload()}
                        >
                            Show the restored data
                        </Button>
                    </>
                ) : (
                    <>
                        <p className="text-sm text-text-muted">
                            The database was replaced and the app restarted. This backup
                            does not contain your session, so you will need to sign in —
                            with the credentials from the backup, which may not be the ones
                            you just used.
                        </p>
                        <Button
                            type="button"
                            variant="primary"
                            onClick={() => void navigate({ to: '/login' })}
                        >
                            Sign in
                        </Button>
                    </>
                )}
            </PanelBody>
        </Panel>
    );
}

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
    const storedId = useId();
    const passId = useId();
    const confirmId = useId();

    /** The artifact to restore. A stored backup is named, not uploaded: it is
     *  already on the server, and sending it back is a round trip that a CDN may
     *  simply refuse — Cloudflare caps request bodies at 100 MB on Free and Pro,
     *  and a real .cofferbak here runs to 141 MB.
     *
     *  `{ parts }` is the off-host case: a backup over the configured part size sits in Google
     *  Drive as several files (ADR-0101), and this takes them exactly as they
     *  were downloaded rather than asking anyone to concatenate files by hand. */
    const [source, setSource] =
        useState<File | { backupId: string } | { parts: File[] } | null>(null);
    /** Why a picked set of files is not a restorable backup — wrong count, mixed
     *  backups, a file that is not a part at all. Caught by name, before any
     *  upload: the alternative is a decrypt failure that reads as a wrong
     *  passphrase. */
    const [pickError, setPickError] = useState<string | null>(null);
    const [passphrase, setPassphrase] = useState('');
    const [confirm, setConfirm] = useState('');
    const [acknowledgeKek, setAcknowledgeKek] = useState(false);
    const [kekCheck, setKekCheck] = useState<BackupKekCheck | null>(null);
    const [restarting, setRestarting] = useState(false);
    /** Source install's master key for the adopt path (ADR-0092 D4). Component
     *  state only — key material never goes in a cache or a URL. */
    const [sourceKey, setSourceKey] = useState('');
    /** Bytes sent so far when an upload is going out in parts (ADR-0101). Null
     *  while nothing is uploading — a small file never shows a progress line. */
    const [sentBytes, setSentBytes] = useState<number | null>(null);

    // This install's own backups. Restoring one of these needs no upload at all.
    // Keyed the same as the backups panel (BACKUPS_QUERY_KEY), so creating or
    // deleting one there updates this list without a reload.
    const storedQuery = useQuery({ queryKey: BACKUPS_QUERY_KEY, queryFn: fetchBackups });
    const stored = storedQuery.data ?? [];

    // Drop a selection the list no longer contains. Deleting the chosen backup
    // would otherwise leave this card holding its id with Restore still
    // enabled — pointed at an artifact that is gone. The server would refuse
    // it, but offering a destructive action that cannot work is the part that
    // is wrong.
    const selectedId =
        source !== null && 'backupId' in source ? source.backupId : null;
    useEffect(() => {
        if (selectedId === null || storedQuery.data === undefined) return;
        if (!storedQuery.data.some((b) => b.id === selectedId)) pickSource(null);
        // pickSource is stable enough for this: it only closes over setters.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [selectedId, storedQuery.data]);

    // The size to cut an upload into, from the server (Api:Backup:PartSizeMb).
    // Read rather than assumed: an operator who lowered it for a stricter proxy
    // has to have the browser actually honour it, or the setting does nothing
    // for the path it exists for. The fallback only covers the first frame.
    const limitsQuery = useQuery({ queryKey: ['restore-limits'], queryFn: fetchRestoreLimits });
    const partSize = limitsQuery.data?.partSizeBytes ?? BACKUP_PART_SIZE_FALLBACK;

    /** Choosing a source resets everything derived from the previous one — a
     *  stale KEK verdict from another artifact is worse than none. */
    function pickSource(next: File | { backupId: string } | { parts: File[] } | null) {
        setSource(next);
        setPickError(null);
        restoreMutation.reset();
        setSentBytes(null);
        setAcknowledgeKek(false);
        setKekCheck(null);
        // Pre-flight the KEK compatibility from the header before the admin
        // commits (ADR-0074). Best-effort: a failure just leaves the mid-restore
        // check as the backstop. The header is in the FIRST part, so a split
        // backup pre-flights from that one — the same 8 KB slice as any file.
        const probe =
            next !== null && 'parts' in next ? next.parts[0] : next;
        if (probe) {
            void validateRestoreKek(probe)
                .then(setKekCheck)
                .catch(() => setKekCheck(null));
        }
    }

    /** Turn a file-picker selection into a source: one whole backup, or a
     *  complete set of parts. */
    function pickFiles(files: FileList | null) {
        const picked = files ? [...files] : [];
        if (picked.length === 0) return pickSource(null);
        if (picked.length === 1 && parseBackupPartName(picked[0].name) === null)
            return pickSource(picked[0]);

        const result = orderBackupParts(picked);
        if ('error' in result) {
            pickSource(null);
            setPickError(result.error);
            return;
        }
        pickSource(result);
    }

    const restoreMutation = useMutation({
        mutationFn: async (vars: {
            file: File | { backupId: string } | { parts: File[] };
            passphrase: string;
            confirm: string;
            ack: boolean;
            sourceKey: string;
        }) => {
            // Anything too big to send in one request goes up in pieces first,
            // and the restore then names those pieces (ADR-0101). Sending it
            // whole would be refused by the proxy in front of Coffer before a
            // byte reached the app — on Cloudflare Free and Pro at 100 MB, with
            // no setting to raise.
            const needsParts =
                'parts' in vars.file ||
                (vars.file instanceof File && vars.file.size > partSize);

            let source: typeof vars.file | { uploadedParts: true } = vars.file;
            if (needsParts) {
                setSentBytes(0);
                try {
                    if ('parts' in vars.file) {
                        await uploadRestoreFromParts(vars.file.parts, setSentBytes);
                    } else {
                        await uploadRestoreInParts(vars.file as File, partSize, setSentBytes);
                    }
                } finally {
                    setSentBytes(null);
                }
                source = { uploadedParts: true };
            }
            return restoreBackup(
                source as File | { backupId: string } | { uploadedParts: true },
                vars.passphrase,
                vars.confirm,
                vars.ack,
                vars.sourceKey,
            );
        },
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
    // A supplied source key stands in for the acknowledgement: the point of it is
    // that nothing gets cleared, so there's nothing to acknowledge losing. The
    // server applies the same rule, and rejects a key that doesn't match the
    // archive — so this can't be used to skip the warning with a bogus value.
    const kekResolved = acknowledgeKek || sourceKey.trim().length > 0;
    const canSubmit =
        source !== null &&
        passphrase.length > 0 &&
        confirmOk &&
        (!needsKekAck || kekResolved) &&
        !restoreMutation.isPending;

    if (restarting) {
        return <RestoringNotice />;
    }

    return (
        <Panel className="border-state-danger/40">
            <PanelBody className="space-y-4">
                <div className="space-y-1">
                    <h3 className="flex items-center gap-2 text-sm font-semibold text-state-danger">
                        <AlertTriangle className="size-icon-md" aria-hidden />
                        Restore from a backup
                    </h3>
                    <p className="text-xs text-text-muted">
                        Replaces <strong>all users, ledgers, and data</strong> across the whole
                        deployment with the contents of a <code>.cofferbak</code> file, then
                        restarts and signs everyone out. Also the way to migrate from another
                        install. This cannot be undone.
                    </p>
                </div>

                {stored.length > 0 ? (
                    <div className="space-y-1.5">
                        <FieldLabel htmlFor={storedId}>
                            A backup this install already has
                        </FieldLabel>
                        <select
                            id={storedId}
                            className="block w-full rounded border border-border bg-surface px-2 py-1.5 text-sm text-text"
                            value={
                                source !== null && 'backupId' in source
                                    ? source.backupId
                                    : ''
                            }
                            onChange={(e) =>
                                pickSource(e.target.value ? { backupId: e.target.value } : null)
                            }
                        >
                            <option value="">— choose a stored backup —</option>
                            {stored.map((b) => (
                                <option key={b.id} value={b.id}>
                                    {formatLedgerDateTime(b.createdAtUtc)} · {formatBytes(b.sizeBytes)}
                                </option>
                            ))}
                        </select>
                        {/* The reason this exists, said once and plainly. */}
                        <p className="text-xs text-text-muted">
                            Nothing is uploaded — the file is already on this server.
                            Restoring a large backup by upload can fail before it
                            arrives, because a proxy or CDN in front of Coffer may
                            refuse a body that size.
                        </p>
                    </div>
                ) : null}

                <div className="space-y-1.5">
                    <FieldLabel htmlFor={fileId}>
                        {stored.length > 0
                            ? '…or upload one (.cofferbak)'
                            : 'Backup file (.cofferbak)'}
                    </FieldLabel>
                    {/* No accept filter: a part's extension is .003-of-005, which
                        cannot be enumerated, and a filter that hides the very files
                        an operator came here with is worse than none. Names are
                        validated on selection instead, by the picker. */}
                    <input
                        id={fileId}
                        type="file"
                        multiple
                        onChange={(e) => pickFiles(e.target.files)}
                        className="block w-full text-sm text-text file:mr-3 file:rounded file:border-0 file:bg-surface-hover file:px-3 file:py-1.5 file:text-sm file:font-medium file:text-text"
                    />
                    {/* Said before they commit, not after a failure: a large upload
                        is the case that breaks, and knowing it will be split is the
                        difference between a slow restore and a mystifying one. */}
                    {source instanceof File && source.size > partSize ? (
                        <p className="text-xs text-text-muted">
                            {formatBytes(source.size)} — sent in{' '}
                            {backupPartCount(source.size, partSize)} parts of{' '}
                            {formatBytes(partSize)}, so a proxy that caps request
                            size does not refuse it.
                        </p>
                    ) : null}
                    {source !== null && 'parts' in source ? (
                        <p className="text-xs text-text-muted">
                            {source.parts.length} parts ·{' '}
                            {formatBytes(source.parts.reduce((n, f) => n + f.size, 0))} —
                            selected in the shape they were stored, and sent one at a
                            time.
                        </p>
                    ) : null}
                    {pickError ? (
                        <p role="alert" className="text-xs text-state-danger">{pickError}</p>
                    ) : null}
                    {/* A backup kept off-host is several files once it is over the
                        part size; whoever reaches for it is unlikely to know that
                        in advance, so the form says it rather than waiting to
                        reject a single part. */}
                    <p className="text-xs text-text-muted">
                        A large backup is stored as several
                        <code className="mx-1">.cofferbak.001-of-003</code>
                        files. Select all of them together.
                    </p>
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
                            Data and passkeys will restore intact either way. Paste the source
                            install’s master key below and its sealed secrets carry over too;
                            leave it empty and they’re cleared — bank feeds, the backup
                            passphrase, and the Google Drive connection all need re-establishing.
                        </p>
                        {/* The master key lives on its own tab now (ADR-0092), so point at
                            it: this warning is exactly when an operator wants to compare
                            fingerprints, and they shouldn't have to go looking. */}
                        <p className="text-xs text-text-muted">
                            <a href="/system?tab=encryption" className="text-accent underline">
                                System → Encryption
                            </a>{' '}
                            shows this install’s key and its fingerprint, if you need to check
                            which one you’re on.
                        </p>

                        {/* The clean-migration path (ADR-0092 D4). Offered right here
                            rather than as separate documentation, because this warning
                            is the moment the operator learns they need it. The server
                            checks it against the archive's fingerprint before anything
                            destructive runs, so a wrong paste is refused up front. */}
                        <label className="block space-y-1">
                            <span className="text-xs font-medium text-text">
                                Source install’s master key (optional)
                            </span>
                            <Input
                                value={sourceKey}
                                onChange={(e) => setSourceKey(e.target.value)}
                                placeholder="44-character base64, ending in ="
                                autoComplete="off"
                                spellCheck={false}
                                className="font-mono text-xs"
                            />
                        </label>

                        {sourceKey.trim().length === 0 ? (
                            <Checkbox
                                label="Restore anyway — I'll re-link my bank feeds, set a new backup passphrase, and reconnect Google Drive afterward."
                                checked={acknowledgeKek}
                                onChange={(e) => setAcknowledgeKek(e.target.checked)}
                            />
                        ) : (
                            <p className="text-xs text-text-muted">
                                With a matching key supplied, nothing is cleared — no
                                acknowledgement needed.
                            </p>
                        )}
                    </div>
                ) : null}

                {sentBytes !== null && source !== null && !('backupId' in source) ? (
                    <p className="text-xs text-text-muted" role="status">
                        Uploading — {formatBytes(sentBytes)} of{' '}
                        {formatBytes(
                            source instanceof File
                                ? source.size
                                : source.parts.reduce((n, f) => n + f.size, 0),
                        )}{' '}
                        sent. Nothing is replaced until every part has arrived.
                    </p>
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
                            source &&
                            restoreMutation.mutate({
                                file: source,
                                passphrase,
                                confirm,
                                ack: acknowledgeKek,
                                sourceKey,
                            })
                        }
                    >
                        {restoreMutation.isPending ? 'Restoring…' : 'Restore database'}
                    </Button>
                </div>
            </PanelBody>
        </Panel>
    );
}
