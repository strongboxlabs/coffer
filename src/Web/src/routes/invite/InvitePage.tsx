import { useId, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from '@tanstack/react-router';

import {
    fetchInvitePreview,
    performInviteRedeem,
    performInviteAccept,
    type InviteCompleteResponse,
} from '@/lib/auth';
import { fetchCurrentUser } from '@/lib/api';
import type { InvitePreview } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { usernameProblem } from '@/lib/username';
import { BrandHeader } from '@/components/ui/BrandHeader';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { RecoveryCodes } from '@/components/RecoveryCodes';

/** One-line description of what an invite confers. */
function describeInvite(preview: InvitePreview): string {
    if (preview.ledgerName && preview.role) {
        return `You've been invited to “${preview.ledgerName}” as ${preview.role}.`;
    }
    if (preview.grantsAdmin) return "You've been invited to Coffer as an administrator.";
    return "You've been invited to Coffer.";
}

const Shell = ({ children }: { children: React.ReactNode }) => (
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
        <BrandHeader />
        {children}
    </main>
);

/**
 * `/invite/$token` — accept an invite link (ADR-0083 slice B). Public, like `/setup`:
 * the token is the credential. A signed-in visitor accepts (the grant is applied to
 * their account); a new visitor runs the passkey-registration ceremony scoped to the
 * invite's ledger/role, then saves their recovery codes before continuing.
 */
export function InvitePage() {
    const { token } = useParams({ strict: false }) as { token: string };

    const previewQuery = useQuery({
        queryKey: ['invite-preview', token],
        queryFn: () => fetchInvitePreview(token),
        retry: false,
    });
    // fetchCurrentUser 401s when signed-out; treat any error as "not signed in".
    const meQuery = useQuery({ queryKey: ['me'], queryFn: fetchCurrentUser, retry: false });

    if (previewQuery.isPending || meQuery.isPending) {
        return (
            <Shell>
                <Panel>
                    <PanelBody>
                        <p className="text-sm text-text-muted">Checking your invite…</p>
                    </PanelBody>
                </Panel>
            </Shell>
        );
    }

    if (previewQuery.isError) {
        return (
            <Shell>
                <Panel>
                    <PanelBody className="space-y-3">
                        <h1 className="text-lg font-semibold tracking-tight">Invite unavailable</h1>
                        <p
                            role="alert"
                            className="rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger"
                        >
                            {errorMessage(previewQuery.error, 'This invite link is invalid, already used, or expired.')}
                        </p>
                        <p className="text-sm text-text-muted">Ask whoever invited you for a fresh link.</p>
                    </PanelBody>
                </Panel>
            </Shell>
        );
    }

    return meQuery.data
        ? <AcceptInvite token={token} preview={previewQuery.data} />
        : <RedeemInvite token={token} preview={previewQuery.data} />;
}

/** Signed-in visitor: apply the invite's grant to the current account. */
function AcceptInvite({ token, preview }: { token: string; preview: InvitePreview }) {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const accept = useMutation({
        mutationFn: () => performInviteAccept(token),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['ledgers'] });
            queryClient.invalidateQueries({ queryKey: ['me'] });
            navigate({ to: '/' });
        },
    });

    return (
        <Shell>
            <Panel>
                <PanelBody className="space-y-4">
                    <h1 className="text-lg font-semibold tracking-tight">Accept invite</h1>
                    <p className="text-sm text-text">{describeInvite(preview)}</p>
                    <p className="text-sm text-text-muted">
                        You're signed in — accepting adds this to your current account.
                    </p>
                    {accept.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(accept.error, 'Could not accept the invite.')}
                        </p>
                    ) : null}
                    <Button
                        className="w-full"
                        disabled={accept.isPending}
                        onClick={() => accept.mutate()}
                    >
                        {accept.isPending ? 'Accepting…' : 'Accept invitation'}
                    </Button>
                </PanelBody>
            </Panel>
        </Shell>
    );
}

/** New visitor: register a passkey and land with the invite's grant. */
function RedeemInvite({ token, preview }: { token: string; preview: InvitePreview }) {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const usernameId = useId();
    const displayNameId = useId();
    const nicknameId = useId();

    const [username, setUsername] = useState('');
    const [displayName, setDisplayName] = useState('');
    const [nickname, setNickname] = useState('This device');
    const [ceremony, setCeremony] = useState(false);
    const [result, setResult] = useState<InviteCompleteResponse | null>(null);

    // Shared with setup via @/lib/username so the two entry points state the
    // same rule (ADR-0089).
    const usernameError = usernameProblem(username);
    const usernameShowsError = username.length > 0 && usernameError !== null;
    const displayNameValid = displayName.trim().length > 0;

    const redeem = useMutation({
        mutationFn: () =>
            performInviteRedeem({
                token,
                username: username.trim(),
                displayName: displayName.trim(),
                credentialNickname: nickname.trim() || 'This device',
                onCeremonyStart: () => setCeremony(true),
            }),
        onSettled: () => setCeremony(false),
        onSuccess: (data) => setResult(data),
    });

    function handleSubmit(event: FormEvent) {
        event.preventDefault();
        redeem.mutate();
    }

    // Once redeemed the session cookie is set → show recovery codes, then route in.
    if (result) {
        return (
            <Shell>
                <Panel>
                    <PanelBody>
                        <RecoveryCodes
                            codes={result.recoveryCodes}
                            onAcknowledge={() => {
                                queryClient.invalidateQueries({ queryKey: ['ledgers'] });
                                queryClient.invalidateQueries({ queryKey: ['me'] });
                                navigate({ to: '/' });
                            }}
                        />
                    </PanelBody>
                </Panel>
            </Shell>
        );
    }

    return (
        <Shell>
            <Panel>
                <PanelBody className="space-y-4">
                    <div className="space-y-1">
                        <h1 className="text-lg font-semibold tracking-tight">Accept your invite</h1>
                        <p className="text-sm text-text">{describeInvite(preview)}</p>
                        <p className="text-sm text-text-muted">
                            Create your account with a passkey — no password.
                        </p>
                    </div>

                    <form onSubmit={handleSubmit} className="space-y-3">
                        <div className="space-y-1">
                            <FieldLabel htmlFor={usernameId}>Username</FieldLabel>
                            <Input
                                id={usernameId}
                                value={username}
                                onChange={(e) => setUsername(e.target.value)}
                                autoComplete="username"
                                required
                                aria-invalid={usernameShowsError ? true : undefined}
                                aria-describedby={
                                    usernameShowsError
                                        ? `${usernameId}-error`
                                        : `${usernameId}-hint`
                                }
                            />
                            {/* Same rule and same wording as setup (ADR-0089).
                                This form used to accept anything non-empty and
                                surface the server's refusal only after the
                                WebAuthn ceremony. */}
                            {usernameShowsError ? (
                                <p
                                    id={`${usernameId}-error`}
                                    role="alert"
                                    className="text-[0.6875rem] text-state-danger"
                                >
                                    {usernameError}
                                </p>
                            ) : (
                                <p
                                    id={`${usernameId}-hint`}
                                    className="text-[0.6875rem] text-text-muted"
                                >
                                    An email address or a handle. Capitalisation
                                    doesn't matter.
                                </p>
                            )}
                        </div>
                        <div className="space-y-1">
                            <FieldLabel htmlFor={displayNameId}>Display name</FieldLabel>
                            <Input
                                id={displayNameId}
                                value={displayName}
                                onChange={(e) => setDisplayName(e.target.value)}
                                required
                            />
                        </div>
                        <div className="space-y-1">
                            <FieldLabel htmlFor={nicknameId}>Passkey name</FieldLabel>
                            <Input
                                id={nicknameId}
                                value={nickname}
                                onChange={(e) => setNickname(e.target.value)}
                            />
                        </div>

                        {redeem.isError ? (
                            <p role="alert" className="text-sm text-state-danger">
                                {errorMessage(redeem.error, 'Could not accept the invite.')}
                            </p>
                        ) : null}

                        <Button
                            type="submit"
                            className="w-full"
                            disabled={
                                redeem.isPending ||
                                usernameError !== null ||
                                !displayNameValid
                            }
                        >
                            {ceremony
                                ? 'Tap your authenticator…'
                                : redeem.isPending
                                    ? 'Creating account…'
                                    : 'Create account'}
                        </Button>
                    </form>
                </PanelBody>
            </Panel>
        </Shell>
    );
}
