import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    createBackup,
    deleteBackup,
    downloadBackup,
    fetchBackups,
    fetchBackupRetention,
    fetchBackupSchedule,
    pinBackup,
    saveBackupSchedule,
    setBackupRetention,
    unpinBackup,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import type { BackupRetention, BackupSummary } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { EmptyState } from '@/components/ui/EmptyState';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { ScheduleControl } from '@/components/ScheduleControl';

import { GoogleDriveSyncCard } from './components/GoogleDriveSyncCard';
import { RestoreBackupCard } from './components/RestoreBackupCard';
import { SetBackupPassphraseDialog } from './components/SetBackupPassphraseDialog';

const BACKUPS_KEY = ['admin-backups'] as const;
const SCHEDULE_KEY = ['admin-backup-schedule'] as const;

/**
 * Admin Backups panel (ADR-0060). Whole-DB encrypted backups: set the
 * passphrase, create / download / delete artifacts, and schedule a daily run.
 * Restore is the operator CLI (`coffer-api restore`) — there's no restore button
 * (it targets a fresh install before auth exists); Download is the bridge.
 */
export function BackupsPanel() {
    const queryClient = useQueryClient();

    const backupsQuery = useQuery({ queryKey: BACKUPS_KEY, queryFn: fetchBackups });
    // The schedule query doubles as the passphrase-configured source; the
    // ScheduleControl below shares this exact cache key so there's one fetch.
    const scheduleQuery = useQuery({ queryKey: SCHEDULE_KEY, queryFn: fetchBackupSchedule });
    const passphraseSet = scheduleQuery.data?.passphraseConfigured ?? false;

    const [passphraseOpen, setPassphraseOpen] = useState(false);
    const [deleteTarget, setDeleteTarget] = useState<BackupSummary | null>(null);
    const [actionError, setActionError] = useState<string | null>(null);

    const createMutation = useMutation({
        mutationFn: createBackup,
        onSuccess: () => {
            setActionError(null);
            queryClient.invalidateQueries({ queryKey: BACKUPS_KEY });
        },
        onError: (err) => setActionError(errorMessage(err, 'Backup failed.')),
    });

    const deleteMutation = useMutation({
        mutationFn: (id: string) => deleteBackup(id),
        onSuccess: () => {
            setActionError(null);
            queryClient.invalidateQueries({ queryKey: BACKUPS_KEY });
        },
        onError: (err) => setActionError(errorMessage(err, 'Delete failed.')),
    });

    const downloadMutation = useMutation({
        mutationFn: async (id: string) => {
            const blob = await downloadBackup(id);
            triggerDownload(blob, `${id}.cofferbak`);
        },
        onSuccess: () => setActionError(null),
        onError: (err) => setActionError(errorMessage(err, 'Download failed.')),
    });

    const pinMutation = useMutation({
        mutationFn: ({ id, pin }: { id: string; pin: boolean }) => (pin ? pinBackup(id) : unpinBackup(id)),
        onSuccess: () => {
            setActionError(null);
            queryClient.invalidateQueries({ queryKey: BACKUPS_KEY });
        },
        onError: (err) => setActionError(errorMessage(err, 'Could not change the pin.')),
    });

    const backups = backupsQuery.data ?? [];

    return (
        <section className="space-y-3">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Backups</h2>
                <p className="text-sm text-text-muted">
                    Encrypted whole-database backups. Set a passphrase, create one
                    on demand or schedule a daily run, download the artifact to keep
                    it off-box, or restore from one below. For headless disaster
                    recovery,{' '}
                    <code className="rounded bg-surface-muted px-1 py-0.5 text-[0.75rem]">
                        coffer-api restore
                    </code>{' '}
                    still works too.
                </p>
                <p className="text-xs text-text-subtle">
                    <strong className="font-medium text-text-muted">Retention</strong>{' '}
                    is configurable below and governs both these local backups and
                    the Google Drive mirror. Pin a backup or download it to keep it
                    beyond the policy.
                </p>
            </header>

            {/* Passphrase status + set/change */}
            <Panel>
                <PanelBody>
                    <div className="flex items-center justify-between gap-3">
                        <div className="space-y-0.5">
                            <p className="text-sm font-medium">Backup passphrase</p>
                            <p className="text-xs text-text-muted">
                                {passphraseSet
                                    ? 'Set. Used to encrypt every backup; required to restore.'
                                    : 'Not set. Required before creating or scheduling a backup.'}
                            </p>
                        </div>
                        <Button
                            type="button"
                            variant={passphraseSet ? 'ghost' : 'primary'}
                            size="sm"
                            onClick={() => setPassphraseOpen(true)}
                            disabled={scheduleQuery.isPending}
                        >
                            {passphraseSet ? 'Change' : 'Set passphrase'}
                        </Button>
                    </div>
                </PanelBody>
            </Panel>

            <div className="flex justify-end">
                <Button
                    type="button"
                    variant="primary"
                    size="sm"
                    onClick={() => createMutation.mutate()}
                    disabled={!passphraseSet || createMutation.isPending}
                    title={!passphraseSet ? 'Set a backup passphrase first.' : undefined}
                >
                    {createMutation.isPending ? 'Creating…' : '+ Create backup'}
                </Button>
            </div>

            {backupsQuery.isError ? (
                <Panel className="border-state-danger/40 bg-state-danger-soft">
                    <PanelBody>
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(backupsQuery.error, 'Could not load backups.')}
                        </p>
                    </PanelBody>
                </Panel>
            ) : null}

            {actionError !== null ? (
                <p role="alert" className="text-xs text-state-danger">{actionError}</p>
            ) : null}

            {backupsQuery.isPending ? (
                <Panel><PanelBody><p className="text-sm text-text-subtle">Loading…</p></PanelBody></Panel>
            ) : backups.length === 0 ? (
                <EmptyState
                    message="No backups yet."
                    hint={
                        passphraseSet ? (
                            <>Click <strong>+ Create backup</strong> to take one, or schedule a daily run below.</>
                        ) : (
                            <>Set a backup passphrase to get started.</>
                        )
                    }
                />
            ) : (
                <ul className="space-y-2">
                    {backups.map((b) => (
                        <BackupRow
                            key={b.id}
                            backup={b}
                            disabled={downloadMutation.isPending || deleteMutation.isPending || pinMutation.isPending}
                            onDownload={() => downloadMutation.mutate(b.id)}
                            onDelete={() => setDeleteTarget(b)}
                            onTogglePin={() => pinMutation.mutate({ id: b.id, pin: !b.pinned })}
                        />
                    ))}
                </ul>
            )}

            <Panel>
                <PanelBody>
                    <ScheduleControl
                        queryKey={SCHEDULE_KEY}
                        load={fetchBackupSchedule}
                        save={saveBackupSchedule}
                        label="Create a backup automatically each day"
                        note="pruned to your retention policy below"
                        canEnable={passphraseSet}
                        disabledHint="Set a backup passphrase first."
                    />
                </PanelBody>
            </Panel>

            <RetentionCard />

            <GoogleDriveSyncCard />

            <RestoreBackupCard />

            {passphraseOpen ? (
                <SetBackupPassphraseDialog
                    isRotate={passphraseSet}
                    onClose={() => setPassphraseOpen(false)}
                    onSaved={() => {
                        setPassphraseOpen(false);
                        queryClient.invalidateQueries({ queryKey: SCHEDULE_KEY });
                    }}
                />
            ) : null}

            <ConfirmDialog
                open={deleteTarget !== null}
                variant="danger"
                title="Delete backup?"
                body={
                    deleteTarget !== null
                        ? `Delete the backup from ${formatDateTime(deleteTarget.createdAtUtc)}? You can't undo this.`
                        : ''
                }
                confirmLabel="Delete"
                isConfirming={deleteMutation.isPending}
                onConfirm={() => {
                    if (deleteTarget === null) return;
                    deleteMutation.mutate(deleteTarget.id, { onSettled: () => setDeleteTarget(null) });
                }}
                onCancel={() => setDeleteTarget(null)}
            />
        </section>
    );
}

function BackupRow({
    backup, disabled, onDownload, onDelete, onTogglePin,
}: {
    backup: BackupSummary;
    disabled: boolean;
    onDownload: () => void;
    onDelete: () => void;
    onTogglePin: () => void;
}) {
    return (
        <li className="rounded border border-border bg-surface px-3 py-2.5">
            <div className="flex items-center justify-between gap-3">
                <div className="min-w-0 flex-1 space-y-0.5">
                    <p className="text-sm font-medium">
                        {formatDateTime(backup.createdAtUtc)}
                        {backup.pinned ? (
                            <span className="ml-2 text-xs font-normal text-accent" title="Never deleted by retention">
                                📌 Pinned
                            </span>
                        ) : null}
                    </p>
                    <p className="text-xs text-text-subtle">{formatBytes(backup.sizeBytes)}</p>
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={onTogglePin}
                        disabled={disabled}
                        title={backup.pinned
                            ? 'Allow retention to prune this backup'
                            : 'Never delete this backup (exclude from local + Drive retention)'}
                    >
                        {backup.pinned ? 'Unpin' : 'Pin'}
                    </Button>
                    <Button type="button" variant="ghost" size="sm" onClick={onDownload} disabled={disabled}>
                        Download
                    </Button>
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={onDelete}
                        disabled={disabled}
                        className="text-state-danger hover:bg-state-danger-soft"
                    >
                        Delete
                    </Button>
                </div>
            </div>
        </li>
    );
}

const RETENTION_KEY = ['admin-backup-retention'] as const;

/** Editable GFS retention (ADR-0074) — the single policy for local backups + the
 *  Drive mirror. Replaces the old fixed prose + the Drive card's tiers. */
function RetentionCard() {
    const queryClient = useQueryClient();
    const query = useQuery({ queryKey: RETENTION_KEY, queryFn: fetchBackupRetention });
    return (
        <Panel>
            <PanelBody className="space-y-2">
                <div className="space-y-0.5">
                    <p className="text-sm font-medium">Retention</p>
                    <p className="text-xs text-text-muted">
                        Keep daily backups for N days, then the newest of each week for
                        N weeks, then the newest of each month for N months; older ones
                        are pruned. This one policy governs your local backups and the
                        Google Drive mirror. Pinned backups are never pruned.
                    </p>
                </div>
                {query.isPending ? (
                    <p className="text-sm text-text-subtle">Loading…</p>
                ) : query.isError ? (
                    <p role="alert" className="text-sm text-state-danger">
                        {errorMessage(query.error, 'Could not load retention.')}
                    </p>
                ) : query.data ? (
                    <RetentionEditor
                        initial={query.data}
                        onSaved={() => queryClient.invalidateQueries({ queryKey: RETENTION_KEY })}
                    />
                ) : null}
            </PanelBody>
        </Panel>
    );
}

function RetentionEditor({ initial, onSaved }: { initial: BackupRetention; onSaved: () => void }) {
    const [daily, setDaily] = useState(initial.retentionDaily);
    const [weekly, setWeekly] = useState(initial.retentionWeekly);
    const [monthly, setMonthly] = useState(initial.retentionMonthly);
    const [error, setError] = useState<string | null>(null);

    const mutation = useMutation({
        mutationFn: () => setBackupRetention({
            retentionDaily: daily, retentionWeekly: weekly, retentionMonthly: monthly,
        }),
        onSuccess: () => { setError(null); onSaved(); },
        onError: (err) => setError(errorMessage(err, 'Could not save retention.')),
    });

    const changed = daily !== initial.retentionDaily
        || weekly !== initial.retentionWeekly
        || monthly !== initial.retentionMonthly;

    return (
        <div className="space-y-1">
            <div className="flex flex-wrap items-end gap-3 text-xs">
                <RetentionInput label="Daily (days)" value={daily} onChange={setDaily} disabled={mutation.isPending} />
                <RetentionInput label="Weekly (weeks)" value={weekly} onChange={setWeekly} disabled={mutation.isPending} />
                <RetentionInput label="Monthly (months)" value={monthly} onChange={setMonthly} disabled={mutation.isPending} />
                <Button type="button" variant="secondary" size="sm"
                    onClick={() => mutation.mutate()} disabled={!changed || mutation.isPending}>
                    {mutation.isPending ? 'Saving…' : 'Save'}
                </Button>
            </div>
            {error !== null ? <p role="alert" className="text-xs text-state-danger">{error}</p> : null}
        </div>
    );
}

function RetentionInput({ label, value, onChange, disabled }: {
    label: string; value: number; onChange: (v: number) => void; disabled: boolean;
}) {
    return (
        <label className="flex flex-col gap-1">
            <span className="text-text-subtle">{label}</span>
            <input
                type="number" min={0} max={3650} value={value} disabled={disabled}
                onChange={(e) => onChange(Math.max(0, Math.floor(Number(e.target.value) || 0)))}
                className="w-24 rounded border border-border bg-surface px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-50"
            />
        </label>
    );
}

function triggerDownload(blob: Blob, filename: string) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
}

function formatDateTime(iso: string): string {
    return new Intl.DateTimeFormat(undefined, {
        month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
    }).format(new Date(iso));
}

function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}
