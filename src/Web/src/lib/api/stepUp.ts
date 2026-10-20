import { startAuthentication } from '@simplewebauthn/browser';

import { ApiError, request } from './_request';

// Fresh passkey tap for one sensitive action (2026-10-12 security audit, M2).
//
// The session cookie only proves someone signed in at some point in the last 30
// days. Actions that grant lasting access or replace data — adding or removing a
// passkey, new recovery codes, an MCP token, making someone an admin, a restore,
// a new backup passphrase, deleting a backup, turning MCP writes on — also need
// a human at an enrolled authenticator now. The server mints a challenge for the
// one action, the browser runs the ceremony, and the result rides in a header on
// the request it approves. One tap approves one request; nothing is cached.
//
// The tap must come from a passkey the user had BEFORE this sign-in, and must use
// the passkey's PIN or fingerprint/face check, not just a touch.

type AssertionOptionsJSON = Parameters<typeof startAuthentication>[0]['optionsJSON'];

/** The actions that need a tap. Mirrors the server's StepUpActions. */
export type StepUpAction =
    | 'passkey-add'
    | 'passkey-delete'
    | 'recovery-codes'
    | 'mcp-token'
    | 'grant-admin'
    | 'restore'
    | 'backup-passphrase'
    | 'backup-delete'
    | 'mcp-writes';

/** The header a tap travels in. */
export const STEP_UP_HEADER = 'X-Coffer-Step-Up';

interface StepUpBeginResponse {
    challengeId: string;
    assertionOptions: AssertionOptionsJSON;
}

/**
 * Run a passkey tap for `action` and return the headers that prove it, ready to
 * spread into a request. Throws an `ApiError` (e.g. no passkey older than this
 * sign-in) or a browser `DOMException` (`NotAllowedError` when the prompt is
 * dismissed); callers surface `.message`.
 */
export async function stepUpHeaders(action: StepUpAction): Promise<Record<string, string>> {
    const begin = await request<StepUpBeginResponse>('/api/auth/step-up/begin', {
        method: 'POST',
        body: { action },
    });
    const assertionResponse = await startAuthentication({ optionsJSON: begin.assertionOptions });
    const proof = JSON.stringify({ challengeId: begin.challengeId, assertionResponse });
    return { [STEP_UP_HEADER]: base64UrlEncode(proof) };
}

/** True when the server refused because the action needs a tap. */
export function isStepUpRequired(err: unknown): boolean {
    return err instanceof ApiError && err.code === 'step-up-required';
}

/**
 * Call `send` without a tap first, and only run one if the server asks for it.
 * For the one action a session may do untapped — adding a passkey right after
 * signing in with a recovery code — so that path doesn't prompt for a passkey the
 * user may not have.
 */
export async function withStepUpIfRequired<T>(
    action: StepUpAction,
    send: (headers: Record<string, string>) => Promise<T>,
): Promise<T> {
    try {
        return await send({});
    } catch (err) {
        if (!isStepUpRequired(err)) throw err;
        return send(await stepUpHeaders(action));
    }
}

function base64UrlEncode(text: string): string {
    const bytes = new TextEncoder().encode(text);
    let binary = '';
    for (const b of bytes) binary += String.fromCharCode(b);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
