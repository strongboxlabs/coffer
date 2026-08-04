import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';

import { startDriveConnect } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';

/**
 * Connect a Google account for Drive backup sync (ADR-0062 §④a) via the OAuth
 * authorization-code redirect flow. The admin pastes their own Google Cloud
 * **Web application** OAuth client id + secret; Coffer returns a Google consent
 * URL and we navigate the browser to it. Google redirects back to Coffer's
 * callback, which seals the token + provisions the folder and returns here.
 */
export function ConnectDriveDialog({ onClose }: { onClose: () => void }) {
    const [clientId, setClientId] = useState('');
    const [clientSecret, setClientSecret] = useState('');

    const startMutation = useMutation({
        mutationFn: () => startDriveConnect(clientId.trim(), clientSecret.trim()),
        onSuccess: ({ authorizationUrl }) => {
            // Hand off to Google; the callback brings the browser back to
            // System → Backups with a ?drive= result the card reads.
            window.location.assign(authorizationUrl);
        },
    });

    const canStart = clientId.trim().length > 0 && clientSecret.trim().length > 0;

    function handleSubmit(e: React.FormEvent) {
        e.preventDefault();
        if (!canStart || startMutation.isPending) return;
        startMutation.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="connect-drive-title" className="max-w-md">
            <header className="border-b border-border px-4 py-3">
                <h2 id="connect-drive-title" className="text-base font-semibold">
                    Connect Google Drive
                </h2>
            </header>
            <form onSubmit={handleSubmit}>
                <div className="space-y-3 p-4">
                    <p className="text-sm text-text-muted">
                        Paste the OAuth client ID and secret from your own Google
                        Cloud project (client type{' '}
                        <strong className="font-medium">Web application</strong>,
                        scope <code className="rounded bg-surface-muted px-1 py-0.5 text-[0.75rem]">drive.file</code>).
                        Coffer stores them sealed under the master key and only ever
                        sees the files it creates. You'll be sent to Google to
                        approve, then returned here.
                    </p>
                    <div className="space-y-1">
                        <FieldLabel htmlFor="drive-client-id">Client ID</FieldLabel>
                        <Input
                            id="drive-client-id"
                            value={clientId}
                            onChange={(e) => setClientId(e.target.value)}
                            autoComplete="off"
                            spellCheck={false}
                            placeholder="xxxxx.apps.googleusercontent.com"
                        />
                    </div>
                    <div className="space-y-1">
                        <FieldLabel htmlFor="drive-client-secret">Client secret</FieldLabel>
                        <Input
                            id="drive-client-secret"
                            type="password"
                            value={clientSecret}
                            onChange={(e) => setClientSecret(e.target.value)}
                            autoComplete="off"
                            spellCheck={false}
                        />
                    </div>
                    {startMutation.isError ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {errorMessage(startMutation.error, 'Could not start the connection.')}
                        </p>
                    ) : null}
                </div>
                <footer className="flex justify-end gap-2 border-t border-border px-4 py-3">
                    <Button type="button" variant="ghost" size="sm" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" variant="primary" size="sm" disabled={!canStart || startMutation.isPending}>
                        {startMutation.isPending ? 'Starting…' : 'Continue to Google'}
                    </Button>
                </footer>
            </form>
        </Modal>
    );
}
