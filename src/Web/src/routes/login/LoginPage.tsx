import { useId, useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { LineChart } from 'lucide-react';

import { performLogin } from '@/lib/auth';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';

/**
 * Login page. Wires the WebAuthn ceremony from `lib/auth.ts` to a
 * minimal username form. ADR-0021 D.2: rendered inside a Panel,
 * branded with the Coffer wordmark + accent.
 *
 * Flow:
 *   1. User types username, submits the form.
 *   2. performLogin() handles begin → browser ceremony → complete.
 *   3. On success: invalidate the ['me'] query (so any cached
 *      auth state from a stale tab refetches) and navigate to the
 *      route the user originally tried to visit (?next=...) or to
 *      the landing page.
 *   4. On failure: render the human-readable error.
 *
 * Accessibility:
 *   - The username Input has type=text + autoComplete=username so
 *     password managers offer the right entry.
 *   - The error banner uses role="alert" so screen readers
 *     announce it when it appears.
 *   - Submit button stays disabled while the mutation is pending —
 *     prevents double-submit which would consume the challenge
 *     prematurely.
 */
export function LoginPage() {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false }) as { next?: string; returnUrl?: string };
    const usernameId = useId();

    const [username, setUsername] = useState('');

    const loginMutation = useMutation({
        mutationFn: (input: string) => performLogin(input),
        onSuccess: async () => {
            await queryClient.invalidateQueries({ queryKey: ['me'] });
            // returnUrl (set by the server's /oauth/authorize redirect) points at a
            // server endpoint, not an SPA route, so it needs a full navigation.
            // Guard against open redirects: same-origin relative paths only.
            if (typeof search.returnUrl === 'string' && isSafeReturnUrl(search.returnUrl)) {
                window.location.assign(search.returnUrl);
                return;
            }
            const target = typeof search.next === 'string' ? search.next : '/';
            navigate({ to: target });
        },
    });

    function handleSubmit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        const trimmed = username.trim();
        if (trimmed.length === 0) return;
        loginMutation.mutate(trimmed);
    }

    const loginError = loginMutation.error
        ? errorMessage(loginMutation.error, 'Authentication failed.')
        : null;

    // Only a same-origin relative path (e.g. /oauth/authorize?...). Rejects
    // protocol-relative (//evil) and backslash tricks so returnUrl can't become
    // an open redirect to another origin.
    function isSafeReturnUrl(url: string): boolean {
        return url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/\\');
    }

    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <div className="mb-6 flex items-center gap-2">
                <LineChart
                    className="h-5 w-5 text-accent"
                    strokeWidth={2.25}
                    aria-hidden
                />
                <span className="text-base font-bold tracking-tight">Coffer</span>
            </div>

            <Panel>
                <PanelBody className="space-y-5">
                    <header className="space-y-1">
                        <h1 className="text-lg font-semibold tracking-tight">
                            Sign in
                        </h1>
                        <p className="text-sm text-text-muted">
                            Authenticate with your registered passkey.
                        </p>
                    </header>

                    <form
                        onSubmit={handleSubmit}
                        className="space-y-4"
                        noValidate
                    >
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
                                disabled={loginMutation.isPending}
                                value={username}
                                onChange={(event) => setUsername(event.target.value)}
                            />
                        </div>

                        {loginError ? (
                            <p
                                role="alert"
                                className="rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger"
                            >
                                {loginError}
                            </p>
                        ) : null}

                        <Button
                            type="submit"
                            disabled={
                                loginMutation.isPending ||
                                username.trim().length === 0
                            }
                            className="w-full"
                        >
                            {loginMutation.isPending ? 'Signing in…' : 'Sign in'}
                        </Button>
                    </form>

                    <button
                        type="button"
                        onClick={() => navigate({ to: '/login/recovery' })}
                        className="text-[0.6875rem] text-text-muted hover:text-text"
                    >
                        Can't use your passkey? Use a recovery code
                    </button>
                </PanelBody>
            </Panel>
        </main>
    );
}
