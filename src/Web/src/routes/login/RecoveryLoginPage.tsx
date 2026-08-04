import { useId, useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { LineChart } from 'lucide-react';

import { performRecoveryLogin } from '@/lib/auth';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';

/**
 * Recovery-code sign-in (ADR-0013). The fallback when no passkey can be
 * used — a lost authenticator, or (the case that motivated building this)
 * a restored database whose passkeys were bound to a different RP id
 * (ADR-0061), making every stored credential unusable on the new host.
 *
 * On success the session cookie is set and the user lands on
 * /account/security — not the dashboard — so they're prompted to register
 * a fresh passkey straight away (their old one likely no longer works).
 */
export function RecoveryLoginPage() {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const usernameId = useId();
    const codeId = useId();

    const [username, setUsername] = useState('');
    const [recoveryCode, setRecoveryCode] = useState('');

    const recoveryMutation = useMutation({
        mutationFn: () => performRecoveryLogin(username.trim(), recoveryCode.trim()),
        onSuccess: async () => {
            await queryClient.invalidateQueries({ queryKey: ['me'] });
            navigate({ to: '/account/security', search: { recovered: true } });
        },
    });

    function handleSubmit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        if (username.trim().length === 0 || recoveryCode.trim().length === 0) return;
        recoveryMutation.mutate();
    }

    const recoveryError = recoveryMutation.error
        ? errorMessage(recoveryMutation.error, 'Authentication failed.')
        : null;

    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <div className="mb-6 flex items-center gap-2">
                <LineChart className="h-5 w-5 text-accent" strokeWidth={2.25} aria-hidden />
                <span className="text-base font-bold tracking-tight">Coffer</span>
            </div>

            <Panel>
                <PanelBody className="space-y-5">
                    <header className="space-y-1">
                        <h1 className="text-lg font-semibold tracking-tight">
                            Use a recovery code
                        </h1>
                        <p className="text-sm text-text-muted">
                            Sign in with one of the single-use codes you saved
                            when you set up your account.
                        </p>
                    </header>

                    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
                        <div className="space-y-2">
                            <FieldLabel htmlFor={usernameId}>Username</FieldLabel>
                            <Input
                                id={usernameId}
                                type="text"
                                autoComplete="username"
                                autoCapitalize="off"
                                autoCorrect="off"
                                spellCheck={false}
                                required
                                disabled={recoveryMutation.isPending}
                                value={username}
                                onChange={(event) => setUsername(event.target.value)}
                            />
                        </div>

                        <div className="space-y-2">
                            <FieldLabel htmlFor={codeId}>Recovery code</FieldLabel>
                            <Input
                                id={codeId}
                                type="text"
                                autoComplete="one-time-code"
                                autoCapitalize="characters"
                                autoCorrect="off"
                                spellCheck={false}
                                placeholder="ABCDE-FGHJK"
                                required
                                disabled={recoveryMutation.isPending}
                                value={recoveryCode}
                                onChange={(event) => setRecoveryCode(event.target.value)}
                                className="font-mono tabular-nums"
                            />
                        </div>

                        {recoveryError ? (
                            <p
                                role="alert"
                                className="rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger"
                            >
                                {recoveryError}
                            </p>
                        ) : null}

                        <Button
                            type="submit"
                            disabled={
                                recoveryMutation.isPending ||
                                username.trim().length === 0 ||
                                recoveryCode.trim().length === 0
                            }
                            className="w-full"
                        >
                            {recoveryMutation.isPending ? 'Signing in…' : 'Sign in'}
                        </Button>
                    </form>

                    <button
                        type="button"
                        onClick={() => navigate({ to: '/login' })}
                        className="text-[0.6875rem] text-text-muted hover:text-text"
                    >
                        ← Back to passkey sign-in
                    </button>
                </PanelBody>
            </Panel>
        </main>
    );
}
