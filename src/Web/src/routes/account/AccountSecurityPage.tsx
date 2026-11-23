import { useId, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { useSearch } from '@tanstack/react-router';

import {
    addPasskey,
    deleteCredential,
    fetchCredentials,
    fetchRecoveryCodesStatus,
    fetchSessions,
    regenerateRecoveryCodes,
    signOutOtherSessions,
    signOutSession,
    type CredentialSummary,
} from '@/lib/auth';
import {
    ApiError,
    createMcpToken,
    fetchMcpTokens,
    fetchOAuthGrants,
    renameMcpToken,
    renameOAuthGrant,
    revokeMcpToken,
    revokeOAuthGrant,
    type IssuedMcpToken,
    type McpTokenSummary,
    type OAuthGrantSummary,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { lastUsedText } from '@/lib/mcpConnection';
import { describeUserAgent } from '@/lib/userAgent';
import { RecoveryCodes } from '@/components/RecoveryCodes';
import { RenameConnectionForm } from '@/components/RenameConnectionForm';
import { useRenameFocus } from '@/components/renameFocus';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
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
                    <SessionsSection />
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
    // An added passkey clears the nickname, which leaves Add disabled with
    // focus on it; focus goes to the nickname field it waits on (ADR-0023 §L).
    const nicknameRef = useRef<HTMLInputElement>(null);
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
        // The list is read again without waiting on it, as for a made token:
        // awaiting it would keep the add pending through the read, with the
        // nickname cleared, Add disabled and the nickname field disabled too,
        // so focus would have nowhere to go until the read answered.
        onSuccess: () => {
            setNickname('');
            void queryClient.invalidateQueries({ queryKey: ['credentials'] });
        },
    });

    const deleteMutation = useMutation({
        mutationFn: (id: string) => deleteCredential(id),
        onSuccess: async () => {
            setPendingDelete(null);
            await queryClient.invalidateQueries({ queryKey: ['credentials'] });
            // The server ended every other session with it (FU-087).
            await queryClient.invalidateQueries({ queryKey: ['sessions'] });
        },
    });

    const credentials = credentialsQuery.data ?? [];
    // The last passkey can be removed only when a fallback login exists — unused
    // recovery codes — mirroring the API guard. This lets a user clear a dead
    // passkey (e.g. one left from a previous address) without locking themselves
    // out; with no recovery codes it stays disabled.
    const hasRecoveryFallback = (recoveryStatusQuery.data?.remaining ?? 0) > 0;
    const blockLastDelete = credentials.length <= 1 && !hasRecoveryFallback;
    // A removed passkey takes its row's Remove with it: focus goes to the next
    // row's Remove, the previous row's, or the nickname field that adds one, as
    // Add is disabled until a nickname is typed (ADR-0023 §L).
    const removeControl = useRowRemovalFocus(credentials.map((c) => c.id), nicknameRef);

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
                                ref={removeControl(c.id)}
                                type="button"
                                variant="secondary"
                                size="sm"
                                disabled={blockLastDelete}
                                pending={deleteMutation.isPending}
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
                        ref={nicknameRef}
                        id={nicknameId}
                        type="text"
                        placeholder="e.g. YubiKey 5, MacBook Touch ID"
                        value={nickname}
                        pending={addMutation.isPending}
                        onChange={(event) => setNickname(event.target.value)}
                        className="flex-1"
                    />
                    <Button
                        type="button"
                        disabled={nickname.trim().length === 0}
                        pending={addMutation.isPending}
                        fallbackFocusRef={nicknameRef}
                        onClick={() => addMutation.mutate()}
                        // Two stages, both laid out from the first render so Add
                        // never changes size between them (PendingLabel).
                        pendingLabel={(
                            <PendingLabel pending={ceremonyStarted} label="Starting…" pendingLabel="Tap your authenticator…" />
                        )}
                    >
                        Add
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
                            will no longer be able to sign in, and every other session on
                            your account is signed out. This can't be undone.
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

/**
 * The browsers and devices signed in to this account (ADR-0013's session list, FU-087),
 * and signing out one or all of the others (ADR-0105 D3): the way to
 * end a session you don't recognise or left signed in somewhere. Protective, so no
 * passkey tap — the worst a stolen session can do with it is sign the owner out.
 */
function SessionsSection() {
    const queryClient = useQueryClient();
    const [ended, setEnded] = useState<number | null>(null);
    const sessionsQuery = useQuery({ queryKey: ['sessions'], queryFn: fetchSessions });
    const signOutMutation = useMutation({
        mutationFn: signOutOtherSessions,
        onSuccess: async (count) => {
            setEnded(count);
            await queryClient.invalidateQueries({ queryKey: ['sessions'] });
        },
    });
    const signOutOneMutation = useMutation({
        mutationFn: (id: string) => signOutSession(id),
        onSuccess: async () => {
            await queryClient.invalidateQueries({ queryKey: ['sessions'] });
        },
    });
    const signOutError = signOutMutation.error
        ? errorMessage(signOutMutation.error, 'Could not sign out your other sessions.')
        : signOutOneMutation.error
          ? errorMessage(signOutOneMutation.error, 'Could not sign out that session.')
          : null;
    const sessions = sessionsQuery.data ?? [];
    // A session signed out leaves the list, and its Sign out with it: focus
    // goes to the next row's Sign out, else the previous row's, else the list
    // itself, which has no add control (ADR-0023 §L). This device's row has
    // none, so it is passed over, and keeps the list on the page.
    const listRef = useRef<HTMLUListElement | null>(null);
    const headingId = useId();
    const signOutControl = useRowRemovalFocus(sessions.map((s) => s.id), listRef);

    return (
        <section className="space-y-3 rounded border border-border bg-surface p-4">
            <header className="space-y-0.5">
                <h2 id={headingId} className="text-base font-semibold">Sessions</h2>
                <p className="text-sm text-text-muted">
                    The browsers and devices signed in to your account. Sign out any you
                    don't recognise.
                </p>
            </header>

            {sessionsQuery.isLoading ? (
                <p className="text-sm text-text-muted">Loading…</p>
            ) : sessionsQuery.isError ? (
                <p role="alert" className="text-sm text-state-danger">
                    {errorMessage(sessionsQuery.error, 'Could not load your sessions.')}
                </p>
            ) : (
                <ul
                    ref={listRef}
                    aria-labelledby={headingId}
                    tabIndex={-1}
                    className="divide-y divide-border rounded border border-border focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                    {sessions.map((s) => (
                        <li key={s.id} className="flex items-center gap-3 px-3 py-2 text-sm">
                            <span className="flex-1 truncate font-medium text-text" title={s.userAgent ?? undefined}>
                                {describeUserAgent(s.userAgent)}
                            </span>
                            <span className="text-[0.6875rem] text-text-muted">
                                signed in {new Date(s.createdAt).toLocaleDateString()}
                                {' · '}active {new Date(s.lastSeenAt).toLocaleString()}
                            </span>
                            {s.current ? (
                                <span className="text-[0.6875rem] font-medium text-accent">This device</span>
                            ) : (
                                <Button
                                    ref={signOutControl(s.id)}
                                    type="button"
                                    variant="secondary"
                                    size="sm"
                                    pending={signOutOneMutation.isPending}
                                    onClick={() => signOutOneMutation.mutate(s.id)}
                                >
                                    Sign out
                                </Button>
                            )}
                        </li>
                    ))}
                </ul>
            )}

            <Button
                type="button"
                variant="secondary"
                onClick={() => signOutMutation.mutate()}
                pending={signOutMutation.isPending}
                pendingLabel="Signing out…"
            >
                Sign out other sessions
            </Button>

            {ended !== null ? (
                <p role="status" className="text-sm text-text-muted">
                    {ended === 0
                        ? 'There were no other sessions.'
                        : `Signed out ${ended} other session${ended === 1 ? '' : 's'}.`}
                </p>
            ) : null}
            {signOutError ? (
                <p role="alert" className="text-sm text-state-danger">{signOutError}</p>
            ) : null}
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
        // The confirm hands over to the fresh codes while the mutation is still
        // pending. Regenerate codes is pending then, not disabled, so the
        // confirm gives focus back to it and the codes give it back on close
        // (ADR-0023 §L). The reads are not awaited: the codes are shown as soon
        // as the server has made them.
        onSuccess: (codes) => {
            setConfirming(false);
            setFreshCodes(codes);
            void queryClient.invalidateQueries({ queryKey: ['recovery-codes-status'] });
            // The server ended every other session with them (FU-087).
            void queryClient.invalidateQueries({ queryKey: ['sessions'] });
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
                pending={regenerateMutation.isPending}
            >
                Regenerate codes
            </Button>

            {regenError ? (
                <p role="alert" className="text-sm text-state-danger">{regenError}</p>
            ) : null}

            <ConfirmDialog
                open={confirming}
                title="Regenerate recovery codes?"
                body="Your current codes stop working immediately, and every other session on your account is signed out. You'll get a fresh set to save."
                confirmLabel="Regenerate"
                isConfirming={regenerateMutation.isPending}
                onConfirm={() => regenerateMutation.mutate()}
                onCancel={() => setConfirming(false)}
            />

            {/* New codes shown once, in a modal with the same save affordances
                as first-run setup. The dialog is named by the codes' own
                heading, which RecoveryCodes renders with this id. */}
            <Modal
                open={freshCodes !== null}
                onClose={() => setFreshCodes(null)}
                titleId="recovery-codes-heading"
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
    // Where focus goes when the token dialog closes. Generate opened it, but a
    // made token clears the name, which leaves Generate disabled until a new
    // one is typed, so it cannot take focus back; the name field it waits on
    // takes it instead (ADR-0023 §L).
    const nameRef = useRef<HTMLInputElement>(null);
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
        // The list is read again without waiting on it: the create settles once
        // the token is issued. Awaiting it would keep the create pending through
        // the read, and the name field disabled with it while "Copy your token
        // now" is open, so a dialog closed before the read answered would have
        // no field to give focus to.
        onSuccess: (token) => {
            setName('');
            setIssued(token);
            void queryClient.invalidateQueries({ queryKey: ['mcp-tokens'] });
        },
    });

    const revokeMutation = useMutation({
        mutationFn: (id: string) => revokeMcpToken(id),
        onSuccess: async () => {
            setPendingRevoke(null);
            await queryClient.invalidateQueries({ queryKey: ['mcp-tokens'] });
        },
    });

    // Apps connected by signing in through OAuth rather than with a pasted token (FU-087).
    // Same 404 as the tokens when MCP is off; the tokens query already says so.
    const grantsQuery = useQuery({
        queryKey: ['oauth-grants'],
        queryFn: fetchOAuthGrants,
        retry: (count, error) =>
            !(error instanceof ApiError && error.status === 404) && count < 3,
    });
    const [pendingRevokeGrant, setPendingRevokeGrant] = useState<OAuthGrantSummary | null>(null);
    const revokeGrantMutation = useMutation({
        mutationFn: (id: string) => revokeOAuthGrant(id),
        onSuccess: async () => {
            setPendingRevokeGrant(null);
            await queryClient.invalidateQueries({ queryKey: ['oauth-grants'] });
        },
    });
    const grants = grantsQuery.data ?? [];
    const revokeGrantError = revokeGrantMutation.error
        ? errorMessage(revokeGrantMutation.error, 'Could not revoke the app.')
        : null;

    // Naming a connection: one row at a time, a token or a sign-in, so one state says which.
    const [renaming, setRenaming] = useState<{ kind: 'token' | 'oauth'; id: string } | null>(null);
    const renameMutation = useMutation({
        mutationFn: (v: { kind: 'token' | 'oauth'; id: string; name: string }) =>
            v.kind === 'token' ? renameMcpToken(v.id, v.name) : renameOAuthGrant(v.id, v.name),
        onSuccess: async (_, v) => {
            setRenaming(null);
            await queryClient.invalidateQueries({
                queryKey: [v.kind === 'token' ? 'mcp-tokens' : 'oauth-grants'],
            });
        },
    });
    const renameError = renameMutation.error
        ? errorMessage(renameMutation.error, 'Could not rename it.')
        : null;
    // The rename form goes on a save that lands, Cancel or Escape: focus goes to
    // the row's Rename, drawn in its place, in either list (ADR-0023 §L).
    const renameFocus = useRenameFocus<string>();
    const renameKey = (kind: 'token' | 'oauth', id: string) => `${kind}:${id}`;
    function startRenaming(kind: 'token' | 'oauth', id: string) {
        renameMutation.reset();
        renameFocus.opened(renameKey(kind, id));
        setRenaming({ kind, id });
    }

    const tokens = tokensQuery.data ?? [];
    const serverUrl = `${window.location.origin}/mcp`;
    // A revoked token or app takes its row's Revoke with it: focus goes to the
    // next row's Revoke, the previous row's, or the panel's add control, the
    // token name field, as Generate is disabled until a name is typed (ADR-0023 §L).
    const revokeTokenControl = useRowRemovalFocus(tokens.map((t) => t.id), nameRef);
    const revokeGrantControl = useRowRemovalFocus(grants.map((g) => g.id), nameRef);

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
                                    {renaming?.kind === 'token' && renaming.id === t.id ? (
                                        <div className="flex-1">
                                            <RenameConnectionForm
                                                initial={t.name}
                                                label={`Name for ${t.name}`}
                                                placeholder={t.name}
                                                allowBlank={false}
                                                pending={renameMutation.isPending}
                                                error={renameError}
                                                onSave={(next) => renameMutation.mutate({ kind: 'token', id: t.id, name: next })}
                                                onCancel={() => setRenaming(null)}
                                                {...renameFocus.formRefs}
                                            />
                                        </div>
                                    ) : (
                                        <>
                                            <span className="flex-1 truncate font-medium text-text">{t.name}</span>
                                            <span className="text-[0.6875rem] text-text-muted">
                                                added {new Date(t.createdAt).toLocaleDateString()}
                                                {` · ${lastUsedText('token', t.lastUsedAt)}`}
                                                {t.expiresAt
                                                    ? ` · expires ${new Date(t.expiresAt).toLocaleDateString()}`
                                                    : ''}
                                            </span>
                                            <Button
                                                ref={renameFocus.renameRef(renameKey('token', t.id))}
                                                type="button"
                                                variant="secondary"
                                                size="sm"
                                                onClick={() => startRenaming('token', t.id)}
                                            >
                                                Rename
                                            </Button>
                                            <Button
                                                ref={revokeTokenControl(t.id)}
                                                type="button"
                                                variant="secondary"
                                                size="sm"
                                                pending={revokeMutation.isPending}
                                                onClick={() => setPendingRevoke(t)}
                                            >
                                                Revoke
                                            </Button>
                                        </>
                                    )}
                                </li>
                            ))}
                        </ul>
                    )}

                    {revokeError ? (
                        <p role="alert" className="text-sm text-state-danger">{revokeError}</p>
                    ) : null}

                    <div className="space-y-2 border-t border-border pt-3">
                        <h3 className="text-sm font-semibold">Apps you signed in to</h3>
                        <p className="text-[0.6875rem] text-text-muted">
                            Connected through your browser rather than with a token. Each
                            connection lasts 90 days; after that the app asks you again.
                        </p>
                        {grants.length === 0 ? (
                            <p className="text-sm text-text-muted">None.</p>
                        ) : (
                            <ul className="divide-y divide-border rounded border border-border">
                                {grants.map((g) => (
                                    <li key={g.id} className="flex items-center gap-3 px-3 py-2 text-sm">
                                        {renaming?.kind === 'oauth' && renaming.id === g.id ? (
                                            <div className="flex-1">
                                                <RenameConnectionForm
                                                    initial={g.label ?? ''}
                                                    label={`Name for ${g.label ?? g.appName}`}
                                                    placeholder={g.appName}
                                                    allowBlank
                                                    pending={renameMutation.isPending}
                                                    error={renameError}
                                                    onSave={(next) => renameMutation.mutate({ kind: 'oauth', id: g.id, name: next })}
                                                    onCancel={() => setRenaming(null)}
                                                    {...renameFocus.formRefs}
                                                />
                                            </div>
                                        ) : (
                                            <>
                                                <span className="flex-1 truncate font-medium text-text">
                                                    {g.label ?? g.appName}
                                                    {/* The app's own name stays visible once renamed: the
                                                        name is the person's word for it, but the app's is
                                                        what says which software this is. */}
                                                    {g.label ? (
                                                        <span className="ml-2 font-normal text-text-muted">
                                                            ({g.appName})
                                                        </span>
                                                    ) : null}
                                                </span>
                                                <span className="text-[0.6875rem] text-text-muted">
                                                    {g.canWrite ? 'can make changes · ' : 'read-only · '}
                                                    connected {new Date(g.connectedAt).toLocaleDateString()}
                                                    {` · ${lastUsedText('oauth', g.lastUsedAt)}`}
                                                    {' · until '}
                                                    {new Date(g.expiresAt).toLocaleDateString()}
                                                </span>
                                                <Button
                                                    ref={renameFocus.renameRef(renameKey('oauth', g.id))}
                                                    type="button"
                                                    variant="secondary"
                                                    size="sm"
                                                    onClick={() => startRenaming('oauth', g.id)}
                                                >
                                                    Rename
                                                </Button>
                                                <Button
                                                    ref={revokeGrantControl(g.id)}
                                                    type="button"
                                                    variant="secondary"
                                                    size="sm"
                                                    pending={revokeGrantMutation.isPending}
                                                    onClick={() => setPendingRevokeGrant(g)}
                                                >
                                                    Revoke
                                                </Button>
                                            </>
                                        )}
                                    </li>
                                ))}
                            </ul>
                        )}
                        {revokeGrantError ? (
                            <p role="alert" className="text-sm text-state-danger">{revokeGrantError}</p>
                        ) : null}
                    </div>

                    <div className="space-y-2 border-t border-border pt-3">
                        <FieldLabel htmlFor={nameId}>Generate a token</FieldLabel>
                        <div className="flex gap-2">
                            <Input
                                ref={nameRef}
                                id={nameId}
                                type="text"
                                placeholder="e.g. Claude Desktop"
                                value={name}
                                pending={createMutation.isPending}
                                onChange={(event) => setName(event.target.value)}
                                className="flex-1"
                            />
                            <Button
                                type="button"
                                disabled={name.trim().length === 0}
                                pending={createMutation.isPending}
                                pendingLabel="Generating…"
                                onClick={() => createMutation.mutate()}
                            >
                                Generate
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

            <ConfirmDialog
                open={pendingRevokeGrant !== null}
                title="Revoke this app?"
                body={
                    pendingRevokeGrant ? (
                        <>
                            <span className="font-medium text-text">
                                {pendingRevokeGrant.label ?? pendingRevokeGrant.appName}
                            </span>{' '}
                            loses access immediately. To use it again, connect it again from the app.
                        </>
                    ) : null
                }
                variant="danger"
                confirmLabel="Revoke"
                isConfirming={revokeGrantMutation.isPending}
                onConfirm={() => {
                    if (pendingRevokeGrant) revokeGrantMutation.mutate(pendingRevokeGrant.id);
                }}
                onCancel={() => setPendingRevokeGrant(null)}
            />

            {/* The plaintext token, shown exactly once. */}
            <Modal
                open={issued !== null}
                onClose={() => setIssued(null)}
                fallbackFocusRef={nameRef}
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
