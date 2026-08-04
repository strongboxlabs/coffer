import { useState } from 'react';

import { Button } from '@/components/ui/Button';

interface RecoveryCodesProps {
    /** Plaintext codes returned by the API. Shown to the user once and never again. */
    codes: readonly string[];
    /**
     * Called when the user clicks the acknowledge button after
     * (presumably) saving the codes. The parent decides what happens
     * next — typically a navigation.
     */
    onAcknowledge: () => void;
}

/**
 * Recovery codes display. Renders the plaintext codes prominently
 * with three save affordances — copy to clipboard, download .txt,
 * print — plus an explicit acknowledgement gate before the parent
 * can route the user away.
 *
 * Why the acknowledgement gate matters: the codes are returned
 * once by /api/auth/setup/{token}/complete and never appear again
 * (only an Argon2id hash is stored). If the user navigates away
 * without saving them — closes the tab, refreshes, clicks a link —
 * they're locked into "if I lose my authenticator I lose my
 * account." The disabled "Continue" button forces a moment of
 * attention.
 *
 * Save-affordance choices:
 *   - Copy: navigator.clipboard.writeText. Standard, works everywhere
 *     in a secure context.
 *   - Download: client-side Blob → anchor click → revoke. The codes
 *     never touch the server (they came from the server, but no
 *     round-trip for the file generation).
 *   - Print: window.print() with a print-only stylesheet that hides
 *     the surrounding chrome (buttons, acknowledgement) so the page
 *     prints as just the codes.
 *
 * The codes are kept only in this component's render output: no
 * localStorage, no analytics, no logging. The `onAcknowledge` prop
 * doesn't carry the codes — once the parent unmounts this component
 * the codes are gone from memory.
 */
export function RecoveryCodes({ codes, onAcknowledge }: RecoveryCodesProps) {
    const [acknowledged, setAcknowledged] = useState(false);
    const [copyState, setCopyState] = useState<'idle' | 'copied' | 'unsupported'>('idle');

    async function handleCopy() {
        // navigator.clipboard.writeText is the standard API; it
        // requires a secure context (HTTPS or localhost). If the
        // clipboard API isn't available (test env, older browser),
        // surface that to the user rather than silently failing.
        if (typeof navigator.clipboard?.writeText !== 'function') {
            setCopyState('unsupported');
            return;
        }
        try {
            await navigator.clipboard.writeText(codes.join('\n'));
            setCopyState('copied');
        } catch {
            // Permission denied or some other clipboard failure —
            // user can still copy manually from the visible list.
            setCopyState('unsupported');
        }
    }

    function handleDownload() {
        // Trailing newline is the POSIX-friendly convention — some
        // text tools choke on files that don't end in `\n`.
        const text = `${codes.join('\n')}\n`;
        const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = 'coffer-recovery-codes.txt';
        document.body.appendChild(anchor);
        anchor.click();
        document.body.removeChild(anchor);
        // Revoke on the next tick so Safari has a chance to honour
        // the download. Without the rAF, some browsers cancel the
        // download because the URL is gone before the click handler
        // resolves.
        requestAnimationFrame(() => URL.revokeObjectURL(url));
    }

    function handlePrint() {
        // The print-only stylesheet (see <style> below) hides every
        // element with data-print="hide" so the print preview shows
        // just the codes + heading. window.print() is synchronous in
        // most browsers — the user sees their browser's print dialog.
        window.print();
    }

    return (
        <section
            aria-labelledby="recovery-codes-heading"
            className="space-y-4"
            data-print="show"
        >
            {/* Print-only stylesheet: hide everything except the
                codes section and its children. We target the
                `data-print="hide"` attribute the chrome elements
                opt into; the heading + list have no attribute so
                they print untouched. */}
            <style>{`
                @media print {
                    body * { visibility: hidden; }
                    [data-print="show"], [data-print="show"] * { visibility: visible; }
                    [data-print="show"] { position: absolute; left: 0; top: 0; width: 100%; }
                    [data-print="hide"] { display: none !important; }
                }
            `}</style>
            <h2 id="recovery-codes-heading" className="text-lg font-semibold">
                Save your recovery codes
            </h2>
            <p className="text-sm text-text-muted" data-print="hide">
                These codes let you sign in if you lose access to your passkey.
                They will <strong>not</strong> be shown again. Save them in a
                password manager, print them, or write them down — somewhere
                separate from your authenticator.
            </p>

            <ul
                aria-label="Recovery codes"
                className="grid grid-cols-1 gap-2 rounded border border-border bg-surface-muted p-4 font-mono text-sm tabular-nums sm:grid-cols-2"
            >
                {codes.map((code) => (
                    <li key={code} className="select-all text-text">
                        {code}
                    </li>
                ))}
            </ul>

            <div className="flex flex-wrap items-center gap-3" data-print="hide">
                <Button type="button" variant="secondary" onClick={handleCopy}>
                    {copyState === 'copied' ? 'Copied' : 'Copy to clipboard'}
                </Button>
                <Button type="button" variant="secondary" onClick={handleDownload}>
                    Download .txt
                </Button>
                <Button type="button" variant="secondary" onClick={handlePrint}>
                    Print
                </Button>
                {copyState === 'unsupported' ? (
                    <span role="status" className="text-sm text-text-muted">
                        Clipboard unavailable — copy them manually above.
                    </span>
                ) : null}
            </div>

            <label
                className="flex items-start gap-2 text-sm text-text"
                data-print="hide"
            >
                <input
                    type="checkbox"
                    className="mt-1 size-4 rounded border-border accent-accent"
                    checked={acknowledged}
                    onChange={(event) => setAcknowledged(event.target.checked)}
                />
                <span>
                    I have saved my recovery codes in a safe place.
                </span>
            </label>

            <Button
                type="button"
                disabled={!acknowledged}
                onClick={onAcknowledge}
                className="w-full"
                data-print="hide"
            >
                Continue
            </Button>
        </section>
    );
}
