import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    disconnectDrive,
    fetchDriveSyncStatus,
    setDriveEnabled,
    uploadAllToDrive,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import type { DriveSyncStatus } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Panel, PanelBody } from '@/components/ui/Panel';

import { ConnectDriveDialog } from './ConnectDriveDialog';

const DRIVE_KEY = ['admin-drive-sync'] as const;

/** Auto-sync is "behind" when it's on but there's no recent successful push. */
const STALE_AFTER_MS = 25 * 60 * 60 * 1000;

/**
 * Google Drive backup sync card (ADR-0062), shown under System → Backups.
 * Connect a Google account (OAuth redirect flow); turn on auto-push-with-each-
 * backup; sync (mirror) on demand; disconnect. The Drive folder mirrors the
 * local backup set — there is no separate Drive retention (ADR-0074). The
 * connected token is sealed under the master KEK server-side and never crosses
 * this boundary.
 */
export function GoogleDriveSyncCard() {
    const queryClient = useQueryClient();
    const statusQuery = useQuery({ queryKey: DRIVE_KEY, queryFn: fetchDriveSyncStatus });
    const status = statusQuery.data;

    const [connectOpen, setConnectOpen] = useState(false);
    const [disconnectOpen, setDisconnectOpen] = useState(false);
    const [actionError, setActionError] = useState<string | null>(null);
    const [returnNotice, setReturnNotice] = useState<{ ok: boolean; text: string } | null>(null);

    function refresh() {
        queryClient.invalidateQueries({ queryKey: DRIVE_KEY });
    }

    // The OAuth callback returns the browser here with ?drive=<result>. Surface
    // it, refetch status on success, and strip the param so a refresh is clean.
    useEffect(() => {
        const params = new URLSearchParams(window.location.search);
        const result = params.get('drive');
        if (!result) return;
        if (result === 'connected') {
            setReturnNotice({ ok: true, text: 'Google Drive connected.' });
            queryClient.invalidateQueries({ queryKey: DRIVE_KEY });
        } else if (result === 'denied') {
            setReturnNotice({ ok: false, text: 'Authorization was cancelled.' });
        } else {
            setReturnNotice({ ok: false, text: 'Connecting Google Drive failed. Please try again.' });
        }
        params.delete('drive');
        const qs = params.toString();
        window.history.replaceState(null, '', window.location.pathname + (qs ? `?${qs}` : ''));
    }, [queryClient]);

    const uploadMutation = useMutation({
        mutationFn: uploadAllToDrive,
        onSuccess: () => { setActionError(null); refresh(); },
        onError: (err) => setActionError(errorMessage(err, 'Upload failed.')),
    });

    const enableMutation = useMutation({
        mutationFn: setDriveEnabled,
        onSuccess: () => { setActionError(null); refresh(); },
        onError: (err) => setActionError(errorMessage(err, 'Could not change the setting.')),
    });

    const disconnectMutation = useMutation({
        mutationFn: disconnectDrive,
        onSuccess: () => { setActionError(null); setDisconnectOpen(false); refresh(); },
        onError: (err) => { setActionError(errorMessage(err, 'Disconnect failed.')); setDisconnectOpen(false); },
    });

    const connected = status?.connected ?? false;
    const busy = uploadMutation.isPending || enableMutation.isPending || disconnectMutation.isPending;

    return (
        <Panel>
            <PanelBody className="space-y-3">
                <div className="space-y-0.5">
                    <p className="text-sm font-medium">Google Drive backups</p>
                    <p className="text-xs text-text-muted">
                        Copy each backup off-box to a folder in your own Google Drive.
                        You connect with your own Google Cloud OAuth client; Coffer only
                        ever sees the files it creates. Each install uses its own
                        folder (named with the install ID below), so installs sharing
                        an account don't commingle.
                    </p>
                </div>

                {statusQuery.isPending ? (
                    <p className="text-sm text-text-subtle">Loading…</p>
                ) : statusQuery.isError ? (
                    <p role="alert" className="text-sm text-state-danger">
                        {errorMessage(statusQuery.error, 'Could not load Drive status.')}
                    </p>
                ) : connected && status ? (
                    <>
                        <dl className="space-y-1 text-xs">
                            <div className="flex gap-2">
                                <dt className="w-20 shrink-0 text-text-subtle">Account</dt>
                                <dd className="font-medium">{status.connectedEmail ?? 'Connected'}</dd>
                            </div>
                            <div className="flex gap-2">
                                <dt className="w-20 shrink-0 text-text-subtle">Folder</dt>
                                <dd className="font-medium">{status.folderName ?? 'Coffer Backups'}</dd>
                            </div>
                            {status.installId ? (
                                <div className="flex gap-2">
                                    <dt className="w-20 shrink-0 text-text-subtle">Install ID</dt>
                                    <dd className="font-mono font-medium">{status.installId}</dd>
                                </div>
                            ) : null}
                            <div className="flex gap-2">
                                <dt className="w-20 shrink-0 text-text-subtle">Last sync</dt>
                                <dd className="font-medium">
                                    {lastSyncText(status)}
                                    {isStale(status) ? (
                                        <span className="ml-2 text-state-warning">not synced recently</span>
                                    ) : null}
                                </dd>
                            </div>
                        </dl>
                        {status.lastSyncStatus === 'error' && status.lastSyncError ? (
                            <p role="alert" className="text-xs text-state-danger">{status.lastSyncError}</p>
                        ) : null}

                        <label className="flex items-center gap-2 text-sm font-medium">
                            <input
                                type="checkbox"
                                checked={status.enabled}
                                disabled={busy}
                                onChange={(e) => enableMutation.mutate(e.target.checked)}
                                className="h-4 w-4 rounded border-border text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                            />
                            <span>Keep Google Drive in sync after each new backup</span>
                        </label>

                        <p className="text-xs text-text-subtle">
                            The folder mirrors your local backups — it holds exactly what's in
                            the backup list above. How many are kept is set by your backup
                            schedule's retention; there is no separate Drive retention.
                        </p>

                        <div className="flex flex-wrap items-center gap-1.5">
                            <Button type="button" variant="primary" size="sm"
                                onClick={() => uploadMutation.mutate()} disabled={busy}
                                title="Make the Drive folder match your local backups now — upload missing, remove extras">
                                {uploadMutation.isPending ? 'Syncing…' : 'Sync to Drive now'}
                            </Button>
                            <Button type="button" variant="ghost" size="sm"
                                onClick={() => setDisconnectOpen(true)} disabled={busy}
                                className="text-state-danger hover:bg-state-danger-soft">
                                Disconnect
                            </Button>
                        </div>
                    </>
                ) : (
                    <div>
                        <Button type="button" variant="primary" size="sm" onClick={() => setConnectOpen(true)}>
                            Connect Google Drive
                        </Button>
                    </div>
                )}

                {returnNotice !== null ? (
                    <p
                        role={returnNotice.ok ? 'status' : 'alert'}
                        className={returnNotice.ok ? 'text-xs text-state-success' : 'text-xs text-state-danger'}
                    >
                        {returnNotice.text}
                    </p>
                ) : null}

                {actionError !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{actionError}</p>
                ) : null}
            </PanelBody>

            {connectOpen ? (
                <ConnectDriveDialog onClose={() => setConnectOpen(false)} />
            ) : null}

            <ConfirmDialog
                open={disconnectOpen}
                variant="danger"
                title="Disconnect Google Drive?"
                body="Coffer will forget the stored token and stop syncing. Backups already in Drive are left untouched. You can reconnect any time."
                confirmLabel="Disconnect"
                isConfirming={disconnectMutation.isPending}
                onConfirm={() => disconnectMutation.mutate()}
                onCancel={() => setDisconnectOpen(false)}
            />
        </Panel>
    );
}

function isStale(status: DriveSyncStatus): boolean {
    if (!status.enabled) return false;
    if (status.lastSyncStatus !== 'ok' || !status.lastSyncAt) return true;
    return Date.now() - new Date(status.lastSyncAt).getTime() > STALE_AFTER_MS;
}

function lastSyncText(status: { lastSyncAt: string | null; lastSyncStatus: string | null }): string {
    if (!status.lastSyncAt) return 'Never synced';
    const when = new Intl.DateTimeFormat(undefined, {
        month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit',
    }).format(new Date(status.lastSyncAt));
    return status.lastSyncStatus === 'error' ? `Failed · ${when}` : `Synced · ${when}`;
}
