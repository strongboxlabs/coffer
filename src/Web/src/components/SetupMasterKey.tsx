import { useState } from 'react';

import { Button } from '@/components/ui/Button';

interface SetupMasterKeyProps {
    /** The install's master key, base64, from the setup response. */
    keyBase64: string;
    /** Called once the user has acknowledged saving it. The parent navigates. */
    onAcknowledge: () => void;
}

/**
 * First-run master-key display (ADR-0092 D2). Shown immediately after the recovery
 * codes, as the last step of setup.
 *
 * Why it exists: startup mints the key on a virgin install and writes it to a file
 * on the server. Without this step a first-time operator would have a key they have
 * never seen, in a location they don't know to look, and would only discover it
 * mattered when a cross-install migration needed it.
 *
 * Why the copy is calmer than the recovery-codes copy, deliberately: losing this
 * key costs three re-establishable secrets — bank-feed tokens, the stored backup
 * passphrase, the Drive connection — and nothing else. Ledger data, passkeys, and
 * backups do not depend on it, because a backup decrypts under its own passphrase.
 * Overstating it here would train the operator to treat this as the critical secret
 * when the recovery codes above and the backup passphrase actually are.
 *
 * And unlike the recovery codes, this one is **not** one-time: it can be viewed
 * again from System → Encryption behind a fresh passkey prompt. The gate below is a
 * moment of attention, not a last chance — and it says so, because a false "last
 * chance" is the kind of thing operators learn to distrust.
 */
export function SetupMasterKey({ keyBase64, onAcknowledge }: SetupMasterKeyProps) {
    const [acknowledged, setAcknowledged] = useState(false);
    const [copyState, setCopyState] = useState<'idle' | 'copied' | 'unsupported'>('idle');

    async function handleCopy() {
        try {
            await navigator.clipboard.writeText(keyBase64);
            setCopyState('copied');
        } catch {
            setCopyState('unsupported');
        }
    }

    function handleDownload() {
        // Client-side only — the key came from the server, but generating the file
        // needs no round-trip and shouldn't have one.
        //
        // Mirrors RecoveryCodes.handleDownload exactly, and the two details that look
        // superfluous are not: the anchor must be IN the document for Firefox to
        // honour the click, and the object URL must outlive the click for Safari, or
        // the download is cancelled before the handler resolves. Getting either wrong
        // produces a button that silently does nothing on those browsers.
        const blob = new Blob([`${keyBase64}\n`], { type: 'text/plain;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = 'coffer-master-key.txt';
        document.body.appendChild(anchor);
        anchor.click();
        document.body.removeChild(anchor);
        requestAnimationFrame(() => URL.revokeObjectURL(url));
    }

    return (
        <section aria-labelledby="master-key-heading" className="space-y-4">
            <h2 id="master-key-heading" className="text-lg font-semibold">
                Save your master key
            </h2>
            <p className="text-sm text-text-muted">
                This key wraps the secrets this install seals — bank-feed
                connections, your backup passphrase, and Google Drive. You need it to
                carry those to another install. Your ledger data, your passkeys, and
                your backups do <strong>not</strong> depend on it.
            </p>

            <code
                aria-label="Master key"
                className="block select-all break-all rounded border border-border bg-surface-muted p-4 font-mono text-sm text-text"
            >
                {keyBase64}
            </code>

            <div className="flex flex-wrap items-center gap-3">
                <Button type="button" variant="secondary" onClick={handleCopy}>
                    {copyState === 'copied' ? 'Copied' : 'Copy to clipboard'}
                </Button>
                <Button type="button" variant="secondary" onClick={handleDownload}>
                    Download .txt
                </Button>
                {copyState === 'unsupported' ? (
                    <span role="status" className="text-sm text-text-muted">
                        Clipboard unavailable — copy it manually above.
                    </span>
                ) : null}
            </div>

            <p className="text-xs text-text-muted">
                You can see this again later under System → Encryption, after confirming
                with your passkey. It's also stored on the server — but keep a copy
                somewhere else, since a server you've lost can't show it to you.
            </p>

            <label className="flex items-start gap-2 text-sm">
                <input
                    type="checkbox"
                    checked={acknowledged}
                    onChange={(e) => setAcknowledged(e.target.checked)}
                    className="mt-0.5"
                />
                <span>I've saved my master key somewhere safe.</span>
            </label>

            <Button type="button" disabled={!acknowledged} onClick={onAcknowledge}>
                Finish setup
            </Button>
        </section>
    );
}
