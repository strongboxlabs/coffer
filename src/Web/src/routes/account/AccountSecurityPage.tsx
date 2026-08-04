import { useId, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearch } from '@tanstack/react-router';

import {
    addPasskey,
    deleteCredential,
    fetchCredentials,
    fetchRecoveryCodesStatus,
    regenerateRecoveryCodes,
    type CredentialSummary,
} from '@/lib/auth';
import {
    ApiError,
    createMcpToken,
    fetchMcpTokens,
    revokeMcpToken,
    type IssuedMcpToken,
    type McpTokenSummary,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { RecoveryCodes } from '@/components/RecoveryCodes';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

/**
 * `/account/security` — per-user passkey + recovery-code management
 * (ADR-0013 follow-through). Reached from the sidebar footer, and the
 * landing spot after a recovery-code sign-in (`?recovered` shows a banner
 * nudging the user to re-key, since the passkey they couldn't use is
 * likely dead — e.g. a restore onto a new RP id, ADR-0061).
 */
export function AccountSecurityPage() {
    const search = useSearch({ strict: false }) as { recovered?: boolean };

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            // No "All ledgers /" root (ADR-0090). This page is
                            // per-USER — your passkeys and recovery codes. It
                            // never belonged under the ledger list, any more
                            // than System settings did.
                            label: 'Security',
                        },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-3xl space-y-6 p-5">
                    <header>
                        <h1 className="text-xl font-semibold tracking-tight">Security</h1>
                        <p className="mt-0.5 text-sm text-text-muted">
                            Manage the passkeys, recovery codes, and connected apps for your account.
                        </p>
                    </header>

                    {search.recovered ? (
                        <p
                            role="status"
                            className="rounded border border-accent/40 bg-accent-soft px-3 py-2 text-sm text-text"
                        >
                            You signed in with a recovery code. If your passkey no
                            longer works (for example after restoring onto a new
                            address), add a new one below and remove the old.
                        </p>
                    ) : null}

                    <PasskeysSection />
                    <RecoveryCodesSection />
                    <ConnectedAppsSection />
                </div>
            </MainPane>
        </MainArea>
    );
}

function PasskeysSection() {
    const queryClient = useQueryClient();
    const nicknameId = useId();
    const [nickname, setNickname] = useState('');
    const [ceremonyStarted, setCeremonyStarted] = useState(false);
    const [pendingDelete, setPendingDelete] = useState<CredentialSummary | null>(null);

    const credentialsQuery = useQuery({
        queryKey: ['credentials'],
        queryFn: fetchCredentials,
    });

    // Shared with the recovery-codes section (same query key). Used to decide
    // whether removing the LAST passkey is safe — see blockLastDelete below.
    const recoveryStatusQuery = useQuery({
        queryKey: ['recovery-codes-status'],
        queryFn: fetchRecoveryCodesStatus,
    });

    const addMutation = useMutation({
        mutationFn: () => addPasskey(nickname.trim(), () => setCeremonyStarted(true)),
        onSettled: () => setCeremonyStarted(false),
        onSuccess: async () => {
            setNickname('');
            await queryClient.invalidateQueries({ queryKey: ['credentials'] });
        },
    });

    const deleteMutation = useMutation({
        mutationFn: (id: string) => deleteCredential(id),
        onSuccess: async () => {
            setPendingDelete(null);
            await queryClient.invalidateQueries({ queryKey: ['credentials'] });
        },
    });

    const credentials = credentialsQuery.data ?? [];
    // The last passkey can be removed only when a fallback login exists — unused
    // recovery codes — mirroring the API guard. This lets a user clear a dead
    // passkey (e.g. one left from a previous address) without locking themselves
    // out; with no recovery codes it stays disabled.
    const hasRecoveryFallback = (recoveryStatusQuery.data?.remaining ?? 0) > 0;
    const blockLastDelete = credentials.length <= 1 && !hasRecoveryFallback;

    const addError = addMutation.error ? errorMessage(addMutation.error, 'Could not add the passkey.') : null;
    const deleteError = deleteMutation.error
        ? errorMessage(deleteMutation.error, 'Could not remove the passkey.')
        : null;

    return (
        <section className="space-y-3 rounded border border-border bg-surface p-4">
            <header className="space-y-0.5">
                <h2 className="text-base font-semibold">Passkeys</h2>
                <p className="text-sm text-text-muted">
                    The authenticators you can sign in with. A passkey is tied to
                    the address it was registered on.
                </p>
            </header>

            {credentialsQuery.isLoading ? (
                <p className="text-sm text-text-muted">Loading…</p>
            ) : credentials.length === 0 ? (
                <p className="text-sm text-text-muted">No passkeys yet.</p>
            ) : (
                <ul className="divide-y divide-border rounded border border-border">
                    {credentials.map((c) => (
                        <li key={c.id} className="flex items-center gap-3 px-3 py-2 text-sm">
                            <span className="flex-1 truncate font-medium text-text">
                                {c.nickname}
                            </span>
                            <span className="text-[0.6875rem] text-text-muted">
                                added {new Date(c.createdAt).toLocaleDateString()}
                                {c.lastUsedAt
                                    ? ` · last used ${new Date(c.lastUsedAt).toLocaleDateString()}`
                                    : ' · never used'}
                            </span>
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                disabled={blockLastDelete || deleteMutation.isPending}
                                title={blockLastDelete ? "Add another passkey or generate recovery codes first — otherwise you'd be locked out." : undefined}
                                onClick={() => setPendingDelete(c)}
                            >
                                Remove
                            </Button>
                        </li>
                    ))}
                </ul>
            )}

            {deleteError ? (
                <p role="alert" className="text-sm text-state-danger">{deleteError}</p>
            ) : null}

            <div className="space-y-2 border-t border-border pt-3">
                <FieldLabel htmlFor={nicknameId}>Add a passkey</FieldLabel>
                <div className="flex gap-2">
                    <Input
                        id={nicknameId}
                        type="text"
                        placeholder="e.g. YubiKey 5, MacBook Touch ID"
                        value={nickname}
                        disabled={addMutation.isPending}
                        onChange={(event) => setNickname(event.target.value)}
                        className="flex-1"
                    />
                    <Button
                        type="button"
                        disabled={addMutation.isPending || nickname.trim().length === 0}
                        onClick={() => addMutation.mutate()}
                    >
                        {addMutation.isPending
                            ? ceremonyStarted
                                ? 'Tap your authenticator…'
                                : 'Starting…'
                            : 'Add'}
                    </Button>
                </div>
                {addError ? (
                    <p role="alert" className="text-sm text-state-danger">{addError}</p>
                ) : null}
            </div>

            <ConfirmDialog
                open={pendingDelete !== null}
                title="Remove this passkey?"
                body={
                    pendingDelete ? (
                        <>
                            <span className="font-medium text-text">{pendingDelete.nickname}</span>{' '}
                            will no longer be able to sign in. This can't be undone.
                            {credentials.length <= 1 ? (
                                <span className="mt-2 block text-state-warning">
                                    This is your only passkey — you'll sign in with a recovery
                                    code until you add a new one.
                                </span>
                            ) : null}
                        </>
                    ) : null
                }
                variant="danger"
                confirmLabel="Remove"
                isConfirming={deleteMutation.isPending}
                onConfirm={() => {
                    if (pendingDelete) deleteMutation.mutate(pendingDelete.id);
                }}
                onCancel={() => setPendingDelete(null)}
            />
        </section>
    );
}

function RecoveryCodesSection() {
    const statusQuery = useQuery({
        queryKey: ['recovery-codes-status'],
        queryFn: fetchRecoveryCodesStatus,
    });
    const queryClient = useQueryClient();
    const [freshCodes, setFreshCodes] = useState<string[] | null>(null);
    const [confirming, setConfirming] = useState(false);

    const regenerateMutation = useMutation({
        mutationFn: regenerateRecoveryCodes,
        onSuccess: async (codes) => {
            setConfirming(false);
            setFreshCodes(codes);
            await queryClient.invalidateQueries({ queryKey: ['recovery-codes-status'] });
        },
    });

    const status = statusQuery.data;
    const regenError = regenerateMutation.error
        ? errorMessage(regenerateMutation.error, 'Could not regenerate recovery codes.')
        : null;

    return (
        <section className="space-y-3 rounded border border-border bg-surface p-4">
            <header className="space-y-0.5">
                <h2 className="text-base font-semibold">Recovery codes</h2>
                <p className="text-sm text-text-muted">
                    Single-use codes to sign in if you lose your authenticator.
                </p>
            </header>

            <p className="text-sm text-text">
                {statusQuery.isLoading || status === undefined
                    ? 'Loading…'
                    : `${status.remaining} of ${status.total} remaining.`}
                {status && status.remaining <= 2 ? (
                    <span className="ml-1 text-state-warning">Running low — regenerate soon.</span>
                ) : null}
            </p>

            <Button
                type="button"
                variant="secondary"
                onClick={() => setConfirming(true)}
                disabled={regenerateMutation.isPending}
            >
                Regenerate codes
            </Button>

            {regenError ? (
                <p role="alert" className="text-sm text-state-danger">{regenError}</p>
            ) : null}

            <ConfirmDialog
                open={confirming}
                title="Regenerate recovery codes?"
                body="Your current codes stop working immediately. You'll get a fresh set to save."
                confirmLabel="Regenerate"
                isConfirming={regenerateMutation.isPending}
                onConfirm={() => regenerateMutation.mutate()}
                onCancel={() => setConfirming(false)}
            />

            {/* New codes shown once, in a modal with the same save affordances
                as first-run setup. */}
            <Modal
                open={freshCodes !== null}
                onClose={() => setFreshCodes(null)}
                titleId="fresh-recovery-codes-title"
                dismissOnBackdrop={false}
                className="max-w-xl"
            >
                <div className="p-5">
                    {freshCodes ? (
                        <RecoveryCodes codes={freshCodes} onAcknowledge={() => setFreshCodes(null)} />
                    ) : null}
                </div>
            </Modal>
        </section>
    );
}

/**
 * Connected apps (ADR-0063): mint / list / revoke the MCP bearer tokens an AI
 * client (Claude Desktop, etc.) uses to reach <c>/mcp</c>. The plaintext is
 * shown exactly once at creation — the same once-only treatment as recovery
 * codes. When MCP is disabled for the deployment the token API isn't mapped
 * (404), which we render as a quiet "turned off" state rather than an error.
 */
function ConnectedAppsSection() {
    const queryClient = useQueryClient();
    const nameId = useId();
    const urlId = useId();
    const [name, setName] = useState('');
    const [issued, setIssued] = useState<IssuedMcpToken | null>(null);
    const [pendingRevoke, setPendingRevoke] = useState<McpTokenSummary | null>(null);
    const [copied, setCopied] = useState<'url' | 'token' | null>(null);

    const tokensQuery = useQuery({
        queryKey: ['mcp-tokens'],
        queryFn: fetchMcpTokens,
        // A 404 means MCP is disabled for this deployment — an expected state,
        // not a transient failure, so don't retry it.
        retry: (count, error) =>
            !(error instanceof ApiError && error.status === 404) && count < 3,
    });

    const mcpDisabled = tokensQuery.error instanceof ApiError && tokensQuery.error.status === 404;

    const createMutation = useMutation({
        mutationFn: () => createMcpToken(name.trim()),
        onSuccess: async (token) => {
            setName('');
            setIssued(token);
            await queryClient.invalidateQueries({ queryKey: ['mcp-tokens'] });
        },
    });

    const revokeMutation = useMutation({
        mutationFn: (id: string) => revokeMcpToken(id),
        onSuccess: async () => {
            setPendingRevoke(null);
            await queryClient.invalidateQueries({ queryKey: ['mcp-tokens'] });
        },
    });

    const tokens = tokensQuery.data ?? [];
    const serverUrl = `${window.location.origin}/mcp`;

    const createError = createMutation.error
        ? errorMessage(createMutation.error, 'Could not create the token.')
        : null;
    const revokeError = revokeMutation.error
        ? errorMessage(revokeMutation.error, 'Could not revoke the token.')
        : null;

    async function copy(text: string, which: 'url' | 'token') {
        try {
            await navigator.clipboard.writeText(text);
            setCopied(which);
            window.setTimeout(() => setCopied((c) => (c === which ? null : c)), 1500);
        } catch {
            // Clipboard blocked (e.g. an insecure context) — the value stays
            // visible for manual copy; nothing else to do.
        }
    }

    return (
        <section className="space-y-3 rounded border border-border bg-surface p-4">
            <header className="space-y-0.5">
                <h2 className="text-base font-semibold">Connected apps</h2>
                <p className="text-sm text-text-muted">
                    Let AI assistants (Claude Desktop, etc.) build read-only reports over
                    your ledgers via MCP.
                </p>
            </header>

            {tokensQuery.isLoading ? (
                <p className="text-sm text-text-muted">Loading…</p>
            ) : mcpDisabled ? (
                <p className="text-sm text-text-muted">
                    MCP access is turned off for this deployment. An administrator can
                    enable it in the server configuration.
                </p>
            ) : (
                <>
                    <div className="space-y-1">
                        <FieldLabel htmlFor={urlId}>Server URL</FieldLabel>
                        <div className="flex gap-2">
                            <Input
                                id={urlId}
                                readOnly
                                value={serverUrl}
                                onFocus={(e) => e.currentTarget.select()}
                                className="flex-1 font-mono text-xs"
                            />
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                onClick={() => copy(serverUrl, 'url')}
                            >
                                {copied === 'url' ? 'Copied' : 'Copy'}
                            </Button>
                        </div>
                        <p className="text-[0.6875rem] text-text-muted">
                            Paste this into the app's connector configuration.
                        </p>
                    </div>

                    {tokens.length === 0 ? (
                        <p className="text-sm text-text-muted">No tokens yet.</p>
                    ) : (
                        <ul className="divide-y divide-border rounded border border-border">
                            {tokens.map((t) => (
                                <li key={t.id} className="flex items-center gap-3 px-3 py-2 text-sm">
                                    <span className="flex-1 truncate font-medium text-text">{t.name}</span>
                                    <span className="text-[0.6875rem] text-text-muted">
                                        added {new Date(t.createdAt).toLocaleDateString()}
                                        {t.lastUsedAt
                                            ? ` · last used ${new Date(t.lastUsedAt).toLocaleDateString()}`
                                            : ' · never used'}
                                        {t.expiresAt
                                            ? ` · expires ${new Date(t.expiresAt).toLocaleDateString()}`
                                            : ''}
                                    </span>
                                    <Button
                                        type="button"
                                        variant="secondary"
                                        size="sm"
                                        disabled={revokeMutation.isPending}
                                        onClick={() => setPendingRevoke(t)}
                                    >
                                        Revoke
                                    </Button>
                                </li>
                            ))}
                        </ul>
                    )}

                    {revokeError ? (
                        <p role="alert" className="text-sm text-state-danger">{revokeError}</p>
                    ) : null}

                    <div className="space-y-2 border-t border-border pt-3">
                        <FieldLabel htmlFor={nameId}>Generate a token</FieldLabel>
                        <div className="flex gap-2">
                            <Input
                                id={nameId}
                                type="text"
                                placeholder="e.g. Claude Desktop"
                                value={name}
                                disabled={createMutation.isPending}
                                onChange={(event) => setName(event.target.value)}
                                className="flex-1"
                            />
                            <Button
                                type="button"
                                disabled={createMutation.isPending || name.trim().length === 0}
                                onClick={() => createMutation.mutate()}
                            >
                                {createMutation.isPending ? 'Generating…' : 'Generate'}
                            </Button>
                        </div>
                        {createError ? (
                            <p role="alert" className="text-sm text-state-danger">{createError}</p>
                        ) : null}
                    </div>
                </>
            )}

            <ConfirmDialog
                open={pendingRevoke !== null}
                title="Revoke this token?"
                body={
                    pendingRevoke ? (
                        <>
                            <span className="font-medium text-text">{pendingRevoke.name}</span>{' '}
                            will stop working immediately and any app using it loses access.
                            This can't be undone.
                        </>
                    ) : null
                }
                variant="danger"
                confirmLabel="Revoke"
                isConfirming={revokeMutation.isPending}
                onConfirm={() => {
                    if (pendingRevoke) revokeMutation.mutate(pendingRevoke.id);
                }}
                onCancel={() => setPendingRevoke(null)}
            />

            {/* The plaintext token, shown exactly once. */}
            <Modal
                open={issued !== null}
                onClose={() => setIssued(null)}
                titleId="mcp-token-issued-title"
                dismissOnBackdrop={false}
                className="max-w-xl"
            >
                <div className="space-y-3 p-5">
                    <h3 id="mcp-token-issued-title" className="text-base font-semibold">
                        Copy your token now
                    </h3>
                    <p className="text-sm text-text-muted">
                        This is the only time you'll see it. Paste it into the app's
                        connector configuration. If you lose it, revoke it here and
                        generate a new one.
                    </p>
                    {issued ? (
                        <div className="flex gap-2">
                            <Input
                                readOnly
                                value={issued.token}
                                onFocus={(e) => e.currentTarget.select()}
                                className="flex-1 font-mono text-xs"
                            />
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                onClick={() => copy(issued.token, 'token')}
                            >
                                {copied === 'token' ? 'Copied' : 'Copy'}
                            </Button>
                        </div>
                    ) : null}
                    <div className="flex justify-end pt-1">
                        <Button type="button" onClick={() => setIssued(null)}>
                            Done
                        </Button>
                    </div>
                </div>
            </Modal>
        </section>
    );
}
