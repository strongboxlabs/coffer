import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    ApiError,
    fetchMasterKeyStatus,
    revealMasterKey,
    rotateMasterKey,
} from '@/lib/api';
import type { MasterKeyRotation } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';

const MASTER_KEY_STATUS_KEY = ['admin-master-key'] as const;

/** Typed phrase gating rotation. Rotation restarts the server and mints a key the
 *  operator must save, so it shouldn't be one stray click away. */
const ROTATE_CONFIRM_PHRASE = 'rotate';

/**
 * Admin master-key panel (ADR-0092 D2/D4). Shows which key the install runs,
 * reveals it on demand behind a fresh passkey assertion, and rotates it.
 *
 * Two deliberate framings here, both from the ADR:
 *
 *  - **"Migration key", not "recovery key".** Losing the master KEK costs three
 *    re-establishable secrets — bank-feed tokens, the stored backup passphrase,
 *    the Google Drive token — and nothing else. Ledger data and passkeys don't
 *    depend on it, and a backup decrypts under its own passphrase. Overstating the
 *    stakes would push operators toward treating this as the thing that stands
 *    between them and their data, which it isn't; the backup passphrase is.
 *
 *  - **Reveal is repeatable, not show-once.** Recovery codes are show-once because
 *    re-display is an authentication attack surface. This is an encryption key, and
 *    the only caller who can reach it already reads every ledger in plaintext
 *    through the normal UI. Show-once would only add a failure mode: a browser that
 *    dies before the human writes the key down.
 *
 * Key material lives in component state and nowhere else — never the query cache,
 * never a URL. It clears when the panel unmounts.
 */
export function MasterKeyPanel() {
    const queryClient = useQueryClient();
    /** True from the moment rotation commits until the restarted server answers with
     *  the new key id. Drives the polling and the "is it done?" confirmation. */
    const [awaitingRestart, setAwaitingRestart] = useState(false);

    const statusQuery = useQuery({
        queryKey: MASTER_KEY_STATUS_KEY,
        queryFn: fetchMasterKeyStatus,
        // Poll across the restart so the operator gets a definite "it's done" instead
        // of being told the page will break and left to guess when it's over.
        refetchInterval: awaitingRestart ? 1500 : false,
        refetchIntervalInBackground: true,
        retry: false,
    });

    // Revealed / newly-rotated key material. Component state only.
    const [revealed, setRevealed] = useState<string | null>(null);
    const [rotated, setRotated] = useState<MasterKeyRotation | null>(null);
    const [confirmText, setConfirmText] = useState('');
    const [copied, setCopied] = useState(false);
    /** Whether the key has been shown at least once this mount — drives the
     *  "Show again" label. Not the key itself, so it's safe to keep after hiding. */
    const [hasRevealed, setHasRevealed] = useState(false);

    // Belt-and-braces: drop key material on unmount rather than relying on React
    // to discard the state. Cheap, and makes the intent explicit.
    useEffect(() => () => {
        setRevealed(null);
        setRotated(null);
    }, []);

    const revealMutation = useMutation({
        mutationFn: revealMasterKey,
        onSuccess: (result) => {
            setRevealed(result.keyBase64);
            setHasRevealed(true);
            setCopied(false);
        },
    });

    const rotateMutation = useMutation({
        mutationFn: rotateMasterKey,
        onSuccess: (result) => {
            setRotated(result);
            setRevealed(null);
            setConfirmText('');
            // Drop the old status rather than invalidating: the server is going down,
            // so a refetch would fail and React Query would keep serving the PREVIOUS
            // id and fingerprint — leaving the header claiming v2 while the notice
            // says "rotated to v3". Better to show nothing than to show a key
            // identity that is no longer true.
            queryClient.removeQueries({ queryKey: MASTER_KEY_STATUS_KEY });
            setAwaitingRestart(true);
        },
    });

    const status = statusQuery.data;
    const visibleKey = rotated?.keyBase64 ?? revealed;

    // The restart is over once the server answers with the id rotation just minted.
    // That's the signal the operator actually wants: not "this page may break", but
    // "it's done, and it's running on the new key".
    const restartComplete = rotated !== null && status?.kekId === rotated.kekId;
    useEffect(() => {
        if (restartComplete) setAwaitingRestart(false);
    }, [restartComplete]);

    async function copyKey(value: string) {
        try {
            await navigator.clipboard.writeText(value);
            setCopied(true);
        } catch {
            // Clipboard blocked (insecure context, permissions). The key is
            // selectable on screen, so this is a nicety, not a failure.
            setCopied(false);
        }
    }

    return (
        <section className="space-y-3">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Master key</h2>
                <p className="text-sm text-text-muted">
                    Wraps this install's sealed secrets — bank-feed tokens, the stored
                    backup passphrase, and the Google Drive connection. It is what carries
                    those to another install; your ledger data, passkeys, and backups do
                    not depend on it. Back it up somewhere separate from the server.
                </p>
            </header>

            <Panel>
                <PanelBody>
                    {/* `rotated` overrides every loading and error state. Rotation takes
                        the server down, so the status query fails for a few seconds — and
                        this used to render the error branch instead of the body, which
                        HID THE NEWLY MINTED KEY before the operator had saved it. The one
                        copy on screen must not disappear because an unrelated fetch is
                        briefly failing. */}
                    {statusQuery.isPending && rotated === null ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : (statusQuery.isError || !status) && rotated === null ? (
                        <p className="text-sm text-state-danger">
                            {errorMessage(statusQuery.error, 'Could not load the master key status.')}
                        </p>
                    ) : (
                        <div className="space-y-4">
                            {/* ---- status: metadata only, no key material ---- */}
                            {/* Id and fingerprint answer "which key is this install on?",
                                which is what the status block is for. The file path used to
                                sit here too and doesn't belong: it's a path INSIDE the
                                container, so an operator can't act on it directly, and it
                                answers a question nobody is asking at a glance. It moved
                                down to the reveal block, where you're actually saving or
                                replacing the key and the location matters. */}
                            {status ? (
                                <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
                                    <dt className="text-text-muted">Key id</dt>
                                    <dd className="font-mono">{status.kekId}</dd>
                                    <dt className="text-text-muted">Fingerprint</dt>
                                    <dd className="break-all font-mono text-xs">{status.fingerprint}</dd>
                                </dl>
                            ) : (
                                // Mid-restart: no status to show. Showing the pre-rotation
                                // id and fingerprint here is what made the page contradict
                                // itself — header saying v2 while the notice said v3.
                                <p className="text-sm text-text-muted">
                                    Reconnecting to the server…
                                </p>
                            )}
                            <p className="text-xs text-text-muted">
                                The fingerprint identifies the key without revealing it — use it to
                                check whether a backup was sealed under this same key.
                            </p>

                            {/* ---- reveal ---- */}
                            <div className="space-y-2 border-t border-border pt-4">
                                <div className="flex flex-wrap items-center justify-between gap-3">
                                    <p className="text-sm font-medium">Show the key</p>
                                    <div className="flex flex-wrap gap-2">
                                        {/* While the key is on screen there is nothing to
                                            re-reveal, so offer only Hide. Once hidden, the
                                            label says "Show again" — a small signal that
                                            this is repeatable, unlike recovery codes. */}
                                        {visibleKey ? (
                                            <Button
                                                variant="secondary"
                                                onClick={() => {
                                                    setRevealed(null);
                                                    setRotated(null);
                                                    setCopied(false);
                                                }}
                                            >
                                                Hide
                                            </Button>
                                        ) : (
                                            <Button
                                                variant="secondary"
                                                onClick={() => revealMutation.mutate()}
                                                disabled={revealMutation.isPending}
                                            >
                                                {revealMutation.isPending
                                                    ? 'Confirming…'
                                                    : hasRevealed
                                                      ? 'Show again'
                                                      : 'Show key'}
                                            </Button>
                                        )}
                                    </div>
                                </div>
                                <p className="text-xs text-text-muted">
                                    Asks for your passkey first. Your session alone isn't enough —
                                    this confirms you're at the keyboard right now.
                                </p>

                                {visibleKey ? (
                                    <div className="space-y-2 rounded border border-accent bg-accent-soft p-3">
                                        <p className="text-xs font-semibold text-accent-soft-text">
                                            {rotated ? 'Your NEW master key' : 'Master key'} — save it
                                            somewhere safe
                                        </p>
                                        <code className="block select-all break-all font-mono text-sm text-text">
                                            {visibleKey}
                                        </code>
                                        {/* Where it lives, shown at the one moment it's
                                            actionable. Labelled as an in-container path
                                            because it is one — an operator reaching for it
                                            needs `docker compose exec`, not their own shell. */}
                                        {status ? (
                                            <p className="text-[0.6875rem] text-accent-soft-text">
                                                Stored in the container at{' '}
                                                <code className="break-all">{status.path}</code>
                                            </p>
                                        ) : null}
                                        <Button
                                            variant="secondary"
                                            onClick={() => copyKey(visibleKey)}
                                        >
                                            {copied ? 'Copied' : 'Copy'}
                                        </Button>
                                    </div>
                                ) : null}

                                {revealMutation.isError ? (
                                    <p className="text-xs text-state-danger">
                                        {errorMessage(revealMutation.error, 'Could not show the key.')}
                                    </p>
                                ) : null}
                            </div>

                            {/* ---- rotate ---- */}
                            <div className="space-y-2 border-t border-border pt-4">
                                <p className="text-sm font-medium">Rotate the key</p>
                                <p className="text-xs text-text-muted">
                                    Generates a new key and moves every stored secret onto it. Your data
                                    isn't re-encrypted and nothing is lost. It checks first and stops
                                    before changing anything if something can't be read. The previous key
                                    is kept alongside the new one, so a mistake is reversible, and the
                                    server restarts to pick it up.
                                </p>
                                {/* No "Check first" button, and no "new key id" input.

                                    The check was redundant: the server runs the dry run as
                                    rotation's FIRST step and refuses before touching anything
                                    (MasterKeyRotationCoordinator step 1). A separate button
                                    only previewed a list that doesn't change the decision —
                                    if it's safe you rotate, if it's blocked Rotate says so
                                    with remediation — while implying rotation might skip the
                                    check unless you remembered to press it.

                                    The id is advisory (only rotation reads it, and it re-wraps
                                    everything regardless), so choosing one was a decision with
                                    no consequence; its placeholder also reimplemented
                                    NextKekId client-side where the two could drift. */}

                                <label className="block text-xs text-text-muted">
                                    <span className="mb-1 block">
                                        Type “{ROTATE_CONFIRM_PHRASE}” to confirm
                                    </span>
                                    <Input
                                        value={confirmText}
                                        onChange={(e) => setConfirmText(e.target.value)}
                                        placeholder={ROTATE_CONFIRM_PHRASE}
                                        className="w-40"
                                    />
                                </label>

                                <Button
                                    onClick={() => rotateMutation.mutate()}
                                    disabled={
                                        rotateMutation.isPending
                                        || confirmText.trim().toLowerCase() !== ROTATE_CONFIRM_PHRASE
                                    }
                                >
                                    {rotateMutation.isPending ? 'Rotating…' : 'Rotate master key'}
                                </Button>

                                {/* A blocked rotation is the one failure an operator can
                                    actually act on, so it gets remediation rather than just
                                    the server's diagnostic. Everything else falls through to
                                    the plain message. Rotation stops before changing anything
                                    when it returns this, so there is nothing to undo. */}
                                {blockedError(rotateMutation.error) ? (
                                    <div className="space-y-1 rounded-md border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-xs text-text">
                                        <p className="font-semibold text-state-danger">
                                            Rotation stopped — nothing was changed.
                                        </p>
                                        <p>
                                            Something stored on this install can't be read with the
                                            current key, so moving to a new one would strand it. This
                                            almost always means a backup from another install was
                                            restored without its master key.
                                        </p>
                                        <p className="font-medium">To fix it, either:</p>
                                        <ul className="list-disc pl-4">
                                            <li>
                                                put that install's master key in place (Show key above
                                                tells you which key this one is using) and restart, then
                                                rotate again; or
                                            </li>
                                            <li>
                                                give up on the unreadable secrets — re-link your bank
                                                feeds, set a new backup passphrase, and reconnect Google
                                                Drive — then rotate again.
                                            </li>
                                        </ul>
                                        <p className="text-text-muted">
                                            Details: {blockedError(rotateMutation.error)}
                                        </p>
                                    </div>
                                ) : (
                                    <>
                                        {rotateMutation.isError ? (
                                            <p className="text-xs text-state-danger">
                                                {errorMessage(rotateMutation.error, 'Rotation failed.')}
                                            </p>
                                        ) : null}
                                    </>
                                )}

                                {rotated ? (
                                    <div className="space-y-1 rounded-md bg-state-warning-soft px-3 py-2 text-xs text-text">
                                        <p className="font-semibold">
                                            Rotated to “{rotated.kekId}”. Save the new key above before
                                            leaving this page.
                                        </p>
                                        {/* Same reasoning as the preview: name what moved, not
                                            the wrapping it moved inside. */}
                                        {rotatedItems(rotated).length > 0 ? (
                                            <p>
                                                Moved to the new key:{' '}
                                                {rotatedItems(rotated).join(', ')}.
                                            </p>
                                        ) : (
                                            <p>Nothing was stored under the old key, so only the key changed.</p>
                                        )}
                                        {/* No archive path here. It's an in-container location an
                                            operator can't act on directly, and it's in the rotation
                                            log if a mistake ever needs undoing — the reassurance is
                                            what matters at this moment, not the filename. */}
                                        {rotated.previousKeyArchivedAt ? (
                                            <p>
                                                Your previous key was kept alongside the new one, so a
                                                mistake is reversible.
                                            </p>
                                        ) : null}
                                        {/* Live progress, then a definite finish. The old copy
                                            said "this page will fail to load for a few seconds
                                            — that's expected", which tells an operator nothing
                                            they can use: not whether to act, not whether it
                                            worked, and not when it's over. The panel polls and
                                            answers those itself. */}
                                        {/* Explicit colour, not inherited. This line was the
                                            one reported as unreadable — dark-on-dark — and
                                            since it inherited its colour from an ancestor it
                                            was at the mercy of whatever that ancestor was.
                                            state-warning (#92400e) on state-warning-soft
                                            (#fef3c7) is a deliberate pairing from the token
                                            set, so it can't collapse.

                                            It also only shows for about half a second in
                                            practice — the restart is that quick — so it is
                                            written as a status line nobody needs to catch
                                            rather than information. The durable answer is the
                                            "Done" line below it. */}
                                        {awaitingRestart ? (
                                            <p className="font-medium text-state-warning">
                                                Restarting…
                                            </p>
                                        ) : restartComplete ? (
                                            <p className="font-medium text-state-success">
                                                ✓ Done — the server is back and running on “
                                                {rotated.kekId}”. Once you've saved the key above,
                                                there's nothing else to do.
                                            </p>
                                        ) : null}
                                    </div>
                                ) : null}
                            </div>
                        </div>
                    )}
                </PanelBody>
            </Panel>
        </section>
    );
}

/**
 * The dry run's counters, in the words the rest of the app uses.
 *
 * Deliberately does NOT surface a ledger-key count. "Ledger key" is an
 * implementation detail (the per-ledger LEK, ADR-0026) that an operator has no
 * concept of, and its count is not the count of anything they can see — a ledger
 * only has one once something in it needs sealing, which today means a bank-feed
 * connection. So describe what it protects, not the wrapping.
 */
function rotatedItems(rotated: MasterKeyRotation): string[] {
    const items: string[] = [];
    if (rotated.ledgersRotated > 0) {
        items.push(
            rotated.ledgersRotated === 1
                ? 'bank-feed connections in 1 ledger'
                : `bank-feed connections in ${rotated.ledgersRotated} ledgers`,
        );
    }
    if (rotated.backupPassphraseRotated) items.push('your backup passphrase');
    if (rotated.driveTokenRotated) items.push('your Google Drive connection');
    return items;
}

/**
 * The server's detail when rotation was refused because something doesn't open under
 * the current key, or null for any other error. That refusal is the one an operator
 * can remediate, so it earns its own explanation.
 */
function blockedError(error: unknown): string | null {
    return error instanceof ApiError && error.code === 'master-key-rotate-blocked'
        ? error.detail
        : null;
}

