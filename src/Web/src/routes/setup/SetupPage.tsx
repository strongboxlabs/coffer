import { useEffect, useId, useState, type FormEvent } from 'react';
import {
    useMutation,
    useQuery,
    useQueryClient,
} from '@tanstack/react-query';
import { useNavigate, useParams } from '@tanstack/react-router';

import {
    fetchSetupInfo,
    performSetup,
    restoreFromBackup,
    waitForServerBack,
    type SetupCompleteResponse,
    type SetupInfoResponse,
} from '@/lib/auth';
import { errorMessage } from '@/lib/errorMessage';
import { usernameProblem } from '@/lib/username';
import { RecoveryCodes } from '@/components/RecoveryCodes';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { BrandHeader } from '@/components/ui/BrandHeader';

// Username rules live in @/lib/username so this form and the invite form can't
// drift apart the way they had (ADR-0089).

/**
 * First-run setup page. Three states drive the render:
 *
 *   1. Loading /info — the token is being validated.
 *   2. /info failed — bootstrap token invalid / expired; surface and
 *      stop.
 *   3. /info succeeded — show the form.
 *
 * After /complete returns, the recovery-codes panel takes over and
 * (acknowledgement gate) routes onward to the ledger hub.
 *
 * Setup does NOT choose a ledger (ADR-0088). It creates the user and passkey,
 * plus a Demo ledger if the box is ticked; the hub is the post-setup home and
 * carries both "New ledger" and "Import from Moneydance". Zero ledgers is a
 * supported landing — the hub renders an empty state with both CTAs.
 *
 * Flow (drawn in ADR-0013, expanded by ADR-0022 follow-up, ADR-0088):
 *   1. Operator finds the bootstrap token in API logs — or in the
 *      dev-up-docker.sh output — and opens `/setup/{token}`.
 *   2. Page mounts; GET /info validates the token.
 *   3. User fills username, display name, passkey label; optionally
 *      ticks "Include a Demo ledger".
 *   4. performSetup() runs /begin → WebAuthn ceremony → /complete.
 *      The page shows "Tap or insert your authenticator…" once /begin
 *      has returned so the user knows the prompt is imminent (a USB
 *      security key is inserted then tapped; a platform one just tapped).
 *   5. /complete sets the cookie + returns recovery codes, and the Demo
 *      ledger if one was seeded. RecoveryCodes panel gates onward
 *      navigation behind explicit user acknowledgement.
 *   6. After acknowledgement, navigate to `/` — the ledger hub.
 */
export function SetupPage() {
    const { token } = useParams({ strict: false }) as { token: string };
    const infoQuery = useQuery<SetupInfoResponse, Error>({
        queryKey: ['setup-info', token],
        queryFn: () => fetchSetupInfo(token),
        retry: false,
    });

    if (infoQuery.isLoading) {
        return (
            <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
                <BrandHeader />
                <Panel>
                    <PanelBody>
                        <p className="text-sm text-text-muted">
                            Verifying setup link…
                        </p>
                    </PanelBody>
                </Panel>
            </main>
        );
    }

    if (infoQuery.error !== null && infoQuery.error !== undefined) {
        return (
            <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
                <BrandHeader />
                <Panel>
                    <PanelBody className="space-y-3">
                        <h1 className="text-lg font-semibold tracking-tight">
                            Setup link unavailable
                        </h1>
                        <p
                            role="alert"
                            className="rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger"
                        >
                            {errorMessage(
                                infoQuery.error,
                                'The setup link is invalid, expired, or already consumed.',
                            )}
                        </p>
                        <p className="text-sm text-text-muted">
                            Ask the operator to mint a fresh bootstrap token
                            from the API logs.
                        </p>
                    </PanelBody>
                </Panel>
            </main>
        );
    }

    // infoQuery's value is empty (ADR-0088); it is awaited purely so an invalid
    // or expired bootstrap token surfaces before the form renders.
    return <SetupChooser token={token} />;
}

/**
 * First decision on a fresh install (ADR-0061): start clean, or restore a
 * backup. Both are gated by the same one-shot bootstrap token; the choice
 * only routes to the create ceremony or the restore upload.
 */
function SetupChooser({ token }: { token: string }) {
    const [mode, setMode] = useState<'choose' | 'create' | 'restore'>('choose');

    if (mode === 'create') {
        return <SetupForm token={token} onBack={() => setMode('choose')} />;
    }
    if (mode === 'restore') {
        return <RestoreFlow token={token} onBack={() => setMode('choose')} />;
    }

    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <BrandHeader />
            <Panel>
                <PanelBody className="space-y-5">
                    <header className="space-y-1">
                        <h1 className="text-lg font-semibold tracking-tight">
                            Welcome to Coffer
                        </h1>
                        <p className="text-sm text-text-muted">
                            Set up a new install, or restore from a backup. This
                            link works once.
                        </p>
                    </header>

                    <div className="space-y-3">
                        <button
                            type="button"
                            onClick={() => setMode('create')}
                            className="w-full rounded border border-border bg-surface px-4 py-3 text-left transition hover:border-accent hover:bg-surface-muted"
                        >
                            <span className="block text-sm font-medium text-text">
                                Set up a new install
                            </span>
                            <span className="block text-[0.6875rem] text-text-muted">
                                Create the first user + ledger and register a passkey.
                            </span>
                        </button>

                        <button
                            type="button"
                            onClick={() => setMode('restore')}
                            className="w-full rounded border border-border bg-surface px-4 py-3 text-left transition hover:border-accent hover:bg-surface-muted"
                        >
                            <span className="block text-sm font-medium text-text">
                                Restore from a backup
                            </span>
                            <span className="block text-[0.6875rem] text-text-muted">
                                Upload an encrypted <code>.cofferbak</code> and its
                                passphrase. Replaces the database, then restarts.
                            </span>
                        </button>
                    </div>
                </PanelBody>
            </Panel>
        </main>
    );
}

interface SetupFormProps {
    token: string;
    onBack: () => void;
}

function SetupForm({ token, onBack }: SetupFormProps) {
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    const usernameId = useId();
    const displayNameId = useId();
    const credentialNicknameId = useId();

    const [username, setUsername] = useState('');
    const [displayName, setDisplayName] = useState('');
    const [credentialNickname, setCredentialNickname] = useState('');

    // Demo opt-in (ADR-0088). Unchecked by default: someone setting up their
    // own books shouldn't acquire a ledger full of sample data by accident.
    const [includeDemo, setIncludeDemo] = useState(false);

    const [ceremonyStarted, setCeremonyStarted] = useState(false);
    const [result, setResult] = useState<SetupCompleteResponse | null>(null);

    const setupMutation = useMutation({
        mutationFn: performSetup,
        onMutate: () => {
            setCeremonyStarted(false);
        },
        onError: () => {
            // A WebAuthn cancellation or attestation failure resets the
            // "ceremony in progress" indicator so the button label
            // returns to "Create account."
            setCeremonyStarted(false);
        },
        onSuccess: async (response) => {
            await queryClient.invalidateQueries({ queryKey: ['me'] });
            setResult(response);
        },
    });

    const usernameError = usernameProblem(username);
    const usernameValid = usernameError === null;
    const displayNameValid = displayName.trim().length > 0;
    const credentialNicknameValid = credentialNickname.trim().length > 0;

    // Only complain once there's something to complain about — an empty field on
    // first paint isn't an error yet.
    const usernameShowsError = username.length > 0 && !usernameValid;

    function handleSubmit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        if (!usernameValid || !displayNameValid || !credentialNicknameValid) {
            return;
        }

        setupMutation.mutate({
            token,
            username: username.trim(),
            displayName: displayName.trim(),
            credentialNickname: credentialNickname.trim(),
            includeDemo,
            onCeremonyStart: () => setCeremonyStarted(true),
        });
    }

    // Result + acknowledgement path
    // `!= null`, not `!== null`: an undefined response would slip past a
    // null-only check and throw on `result.username` while rendering. Now that
    // ledgerId/ledgerName are legitimately nullable (ADR-0088), the success
    // screen has to be defensive about the payload rather than assume it.
    if (result != null) {
        return (
            <main className="mx-auto max-w-xl px-6 py-12">
                <BrandHeader />
                <Panel>
                    <PanelBody className="space-y-6">
                        <header className="space-y-1">
                            <h1 className="text-lg font-semibold tracking-tight">
                                Account created
                            </h1>
                            <p className="text-sm text-text-muted">
                                Signed in as{' '}
                                <span className="font-medium text-text">
                                    {result.username}
                                </span>
                                .{' '}
                                {result.ledgerName !== null ? (
                                    <>
                                        Seeded the{' '}
                                        <span className="font-medium text-text">
                                            {result.ledgerName}
                                        </span>{' '}
                                        ledger.
                                    </>
                                ) : (
                                    <>
                                        Next, create a ledger or import one from
                                        Moneydance.
                                    </>
                                )}
                            </p>
                        </header>
                        <RecoveryCodes
                            codes={result.recoveryCodes}
                            onAcknowledge={() => navigate({ to: '/' })}
                        />
                    </PanelBody>
                </Panel>
            </main>
        );
    }

    const setupError = setupMutation.error
        ? errorMessage(setupMutation.error, 'Account setup failed.')
        : null;
    const submitDisabled =
        setupMutation.isPending ||
        !usernameValid ||
        !displayNameValid ||
        !credentialNicknameValid;

    const buttonLabel = setupMutation.isPending
        ? ceremonyStarted
            ? 'Tap or insert your authenticator…'
            : 'Creating account…'
        : 'Create account';

    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <BrandHeader />
            <Panel>
                <PanelBody className="space-y-5">
                    <header className="space-y-1">
                        <button
                            type="button"
                            onClick={onBack}
                            disabled={setupMutation.isPending}
                            className="text-[0.6875rem] text-text-muted hover:text-text disabled:opacity-50"
                        >
                            ← Back
                        </button>
                        <h1 className="text-lg font-semibold tracking-tight">
                            Set up your account
                        </h1>
                        <p className="text-sm text-text-muted">
                            Choose your username and register a passkey. This
                            link works once.
                        </p>
                    </header>

                    <form
                        onSubmit={handleSubmit}
                        className="space-y-4"
                        noValidate
                    >
                        <div className="space-y-1.5">
                            <FieldLabel htmlFor={usernameId}>Username</FieldLabel>
                            <Input
                                id={usernameId}
                                type="text"
                                autoComplete="username"
                                autoCapitalize="off"
                                autoCorrect="off"
                                spellCheck={false}
                                required
                                disabled={setupMutation.isPending}
                                value={username}
                                onChange={(event) =>
                                    setUsername(event.target.value)
                                }
                                aria-invalid={usernameShowsError ? true : undefined}
                                aria-describedby={
                                    usernameShowsError
                                        ? `${usernameId}-error`
                                        : `${usernameId}-hint`
                                }
                            />
                            {usernameShowsError ? (
                                // Say WHY, next to the field. "Create account" is
                                // disabled until every field is valid, and an
                                // unexplained disabled button is indistinguishable
                                // from a broken one.
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
                                    An email address or a handle — whatever you'd
                                    rather sign in with. Capitalisation doesn't
                                    matter.
                                </p>
                            )}
                        </div>

                        <div className="space-y-1.5">
                            <FieldLabel htmlFor={displayNameId}>Display name</FieldLabel>
                            <Input
                                id={displayNameId}
                                type="text"
                                autoComplete="name"
                                required
                                disabled={setupMutation.isPending}
                                value={displayName}
                                onChange={(event) =>
                                    setDisplayName(event.target.value)
                                }
                                aria-describedby={`${displayNameId}-hint`}
                            />
                            <p
                                id={`${displayNameId}-hint`}
                                className="text-[0.6875rem] text-text-muted"
                            >
                                Shown in the app header. Any characters are
                                fine.
                            </p>
                        </div>

                        <div className="space-y-1.5">
                            <FieldLabel htmlFor={credentialNicknameId}>
                                Passkey label
                            </FieldLabel>
                            <Input
                                id={credentialNicknameId}
                                type="text"
                                autoComplete="off"
                                placeholder="e.g. MacBook Touch ID"
                                required
                                disabled={setupMutation.isPending}
                                value={credentialNickname}
                                onChange={(event) =>
                                    setCredentialNickname(event.target.value)
                                }
                                aria-describedby={`${credentialNicknameId}-hint`}
                            />
                            <p
                                id={`${credentialNicknameId}-hint`}
                                className="text-[0.6875rem] text-text-muted"
                            >
                                A nickname for the credential you're enrolling.
                                Helps if you add more devices later.
                            </p>
                        </div>

                        <fieldset
                            className="space-y-2"
                            disabled={setupMutation.isPending}
                        >
                            <legend className="text-sm font-medium">
                                Sample data
                            </legend>
                            <label className="flex items-start gap-2 text-sm">
                                <input
                                    type="checkbox"
                                    checked={includeDemo}
                                    onChange={(event) =>
                                        setIncludeDemo(event.target.checked)
                                    }
                                    className="mt-1 size-4 accent-accent"
                                />
                                <span className="flex-1 space-y-1">
                                    <span className="block">
                                        Include a Demo ledger
                                    </span>
                                    <span className="block text-[0.6875rem] text-text-muted">
                                        A worked example with accounts,
                                        categories and transactions, to explore
                                        before you commit your own data. You can
                                        delete it any time.
                                    </span>
                                </span>
                            </label>
                            <p className="text-[0.6875rem] text-text-muted">
                                Either way, you'll land on your ledger list next,
                                where you can create a ledger or import one from
                                Moneydance.
                            </p>
                        </fieldset>

                        {setupError ? (
                            <p
                                role="alert"
                                className="rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger"
                            >
                                {setupError}
                            </p>
                        ) : null}

                        <Button
                            type="submit"
                            disabled={submitDisabled}
                            className="w-full"
                        >
                            {buttonLabel}
                        </Button>
                    </form>
                </PanelBody>
            </Panel>
        </main>
    );
}

/**
 * Restore branch of the bootstrap UI (ADR-0061). Upload an encrypted
 * backup + passphrase; on accept, the server stages it and restarts, so
 * we hand off to the "restoring…" screen which polls until the server is
 * back and then sends the user to /login (the restored credentials, not
 * a freshly-created one).
 */
function RestoreFlow({ token, onBack }: { token: string; onBack: () => void }) {
    const fileId = useId();
    const passphraseId = useId();
    const [file, setFile] = useState<File | null>(null);
    const [passphrase, setPassphrase] = useState('');
    const [restarting, setRestarting] = useState(false);

    const restoreMutation = useMutation({
        mutationFn: () => restoreFromBackup(token, file!, passphrase),
        onSuccess: () => setRestarting(true),
    });

    if (restarting) {
        return <RestoringScreen />;
    }

    const restoreError = restoreMutation.error
        ? errorMessage(restoreMutation.error, 'Restore failed.')
        : null;
    const submitDisabled = restoreMutation.isPending || file === null || passphrase.length === 0;

    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <BrandHeader />
            <Panel>
                <PanelBody className="space-y-5">
                    <header className="space-y-1">
                        <button
                            type="button"
                            onClick={onBack}
                            disabled={restoreMutation.isPending}
                            className="text-[0.6875rem] text-text-muted hover:text-text disabled:opacity-50"
                        >
                            ← Back
                        </button>
                        <h1 className="text-lg font-semibold tracking-tight">
                            Restore from a backup
                        </h1>
                        <p className="text-sm text-text-muted">
                            Replaces this database with the backup, then restarts.
                            Sign in afterwards with the credentials from the
                            backup.
                        </p>
                    </header>

                    <form
                        onSubmit={(event) => {
                            event.preventDefault();
                            if (!submitDisabled) restoreMutation.mutate();
                        }}
                        className="space-y-4"
                        noValidate
                    >
                        <div className="space-y-1.5">
                            <FieldLabel htmlFor={fileId}>Backup file</FieldLabel>
                            <input
                                id={fileId}
                                type="file"
                                accept=".cofferbak,application/octet-stream"
                                disabled={restoreMutation.isPending}
                                onChange={(event) =>
                                    setFile(event.target.files?.[0] ?? null)
                                }
                                className="block w-full text-sm text-text file:mr-3 file:rounded file:border file:border-border file:bg-surface-muted file:px-3 file:py-1.5 file:text-sm"
                            />
                        </div>

                        <div className="space-y-1.5">
                            <FieldLabel htmlFor={passphraseId}>Passphrase</FieldLabel>
                            <Input
                                id={passphraseId}
                                type="password"
                                autoComplete="off"
                                required
                                disabled={restoreMutation.isPending}
                                value={passphrase}
                                onChange={(event) => setPassphrase(event.target.value)}
                            />
                            <p className="text-[0.6875rem] text-text-muted">
                                The passphrase the backup was encrypted with. It's
                                verified before anything is changed.
                            </p>
                        </div>

                        {restoreError ? (
                            <p
                                role="alert"
                                className="rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger"
                            >
                                {restoreError}
                            </p>
                        ) : null}

                        <Button type="submit" disabled={submitDisabled} className="w-full">
                            {restoreMutation.isPending ? 'Uploading…' : 'Restore'}
                        </Button>
                    </form>
                </PanelBody>
            </Panel>
        </main>
    );
}

/**
 * Shown after the server accepts a restore: it's applying the backup and
 * restarting. Polls the liveness probe until it answers, then routes to
 * /login. On timeout, offers a manual link rather than spinning forever.
 */
function RestoringScreen() {
    const navigate = useNavigate();
    const [timedOut, setTimedOut] = useState(false);

    useEffect(() => {
        let cancelled = false;
        waitForServerBack()
            .then(() => {
                if (!cancelled) navigate({ to: '/login' });
            })
            .catch(() => {
                if (!cancelled) setTimedOut(true);
            });
        return () => {
            cancelled = true;
        };
    }, [navigate]);

    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <BrandHeader />
            <Panel>
                <PanelBody className="space-y-3">
                    <h1 className="text-lg font-semibold tracking-tight">
                        {timedOut ? 'Still restoring…' : 'Restoring…'}
                    </h1>
                    <p className="text-sm text-text-muted">
                        {timedOut
                            ? 'The restore is taking longer than expected. It may still be running — try the sign-in page in a moment.'
                            : 'Applying the backup and restarting. This page will reconnect automatically.'}
                    </p>
                    {timedOut ? (
                        <Button
                            type="button"
                            onClick={() => navigate({ to: '/login' })}
                            className="w-full"
                        >
                            Go to sign in
                        </Button>
                    ) : null}
                </PanelBody>
            </Panel>
        </main>
    );
}

