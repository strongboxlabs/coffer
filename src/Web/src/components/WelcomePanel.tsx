import { useState } from 'react';

import { Button } from '@/components/ui/Button';

interface WelcomePanelProps {
    /** The install's master key, base64, from the one-time setup response. */
    keyBase64: string;
    /** True when setup seeded a Demo ledger, so the next step differs (ADR-0088). */
    hasLedger: boolean;
    /** Leave the welcome screen for the ledger hub. */
    onContinue: () => void;
}

/**
 * Post-setup welcome screen (ADR-0095, amending ADR-0092 D2).
 *
 * The master key used to be the last **step of setup**, behind its own
 * acknowledgement checkbox and a "Finish setup" button. That put it in the wrong
 * place for two reasons. It cannot be *explained* at that moment — there are no
 * sealed secrets yet, no bank feed, no backup passphrase, no Drive connection, and
 * no backup to restore anywhere, so the operator is asked to file away a secret that
 * currently protects nothing. And gating progress on it ranked it alongside the
 * recovery codes, which are one-time and genuinely unrecoverable, when losing this
 * key costs three re-establishable secrets and no data at all.
 *
 * So setup now ends at the recovery codes — one thing to save, the one that cannot
 * be recovered — and this screen follows it. Here the key comes with the reason it
 * exists and next to the thing that actually makes recovery work: a backup and its
 * passphrase. No checkbox: this is advice at the moment it is true, and the key stays
 * viewable under System → Encryption, so a gate would be theatre.
 */
export function WelcomePanel({ keyBase64, hasLedger, onContinue }: WelcomePanelProps) {
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
        // the download is cancelled before the handler resolves.
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
        <section aria-labelledby="welcome-heading" className="space-y-6">
            <h2 id="welcome-heading" className="text-lg font-semibold">
                You're set up
            </h2>

            <div className="space-y-3">
                <h3 className="text-sm font-semibold">Save your master key</h3>
                <p className="text-sm text-text-muted">
                    This install generated a key that wraps the secrets it seals —
                    bank-feed connections, your backup passphrase, and Google Drive.
                    Keeping a copy means those come across if you ever move to another
                    machine. Your ledger data, your passkeys and your backups do{' '}
                    <strong>not</strong> depend on it: a backup decrypts under its own
                    passphrase, so losing this key costs you three reconnections, not
                    your books.
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
                    Not urgent, and not a last chance — it's on the server and you can
                    read it again under System → Encryption after confirming with your
                    passkey. Keep a copy elsewhere, though: a server you've lost can't
                    show it to you.
                </p>
            </div>

            <div className="space-y-2 border-t border-border pt-4">
                <h3 className="text-sm font-semibold">Then set up backups</h3>
                <p className="text-sm text-text-muted">
                    This is the one that protects your data. Under{' '}
                    <strong>System → Backups</strong>, set a passphrase and turn on the
                    daily schedule, then download an artifact to keep off this machine.
                    A backup file plus its passphrase is all a restore needs — on this
                    install or a new one.
                </p>
            </div>

            <div className="space-y-2 border-t border-border pt-4">
                <h3 className="text-sm font-semibold">
                    {hasLedger ? 'Have a look around' : 'Add your first ledger'}
                </h3>
                <p className="text-sm text-text-muted">
                    {hasLedger ? (
                        <>
                            The Demo ledger is populated with a worked example — open a
                            register or the investments view to see the shape of things.
                            Delete it whenever you like.
                        </>
                    ) : (
                        <>
                            Create an empty ledger, or import a Moneydance export to
                            bring your history across with accounts, splits, investment
                            lots and cost basis intact.
                        </>
                    )}
                </p>
            </div>

            <Button type="button" onClick={onContinue}>
                {hasLedger ? 'Go to my ledgers' : 'Get started'}
            </Button>
        </section>
    );
}
