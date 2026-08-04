import { useState } from 'react';

import { Modal } from '@/components/ui/Modal';
import { Button } from '@/components/ui/Button';

/**
 * Shows a freshly-minted invite link once (ADR-0083 slice B) — the token is never
 * shown again. Shared by the owner (Members panel) and admin (Users tab) surfaces.
 */
export function InviteLinkModal({ token, onClose }: { token: string | null; onClose: () => void }) {
    const [copied, setCopied] = useState(false);
    if (token === null) return null;

    const url = `${window.location.origin}/invite/${token}`;
    async function copy() {
        try {
            await navigator.clipboard.writeText(url);
            setCopied(true);
        } catch {
            // Clipboard unavailable — the URL is selectable for manual copy.
        }
    }

    return (
        <Modal open onClose={onClose} ariaLabel="Invite link" className="max-w-lg">
            <header className="border-b border-border px-4 py-3">
                <h2 className="text-base font-semibold">Invite link</h2>
            </header>
            <div className="space-y-3 p-4">
                <p className="text-sm text-text-muted">
                    Share this one-time link with the person you're inviting. It works once, expires in
                    7 days, and won't be shown again — copy it now. You can revoke it below until it's used.
                </p>
                <div className="flex items-center gap-2">
                    <code className="min-w-0 flex-1 select-all truncate rounded border border-border bg-surface-muted px-2 py-1 text-xs">
                        {url}
                    </code>
                    <Button type="button" variant="secondary" size="sm" onClick={copy}>
                        {copied ? 'Copied' : 'Copy'}
                    </Button>
                </div>
            </div>
            <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                <Button type="button" variant="primary" size="sm" onClick={onClose}>
                    Done
                </Button>
            </footer>
        </Modal>
    );
}
