// WebAuthn ceremony helpers for both login (assertion) and setup
// (registration).
//
// We use @simplewebauthn/browser for the browser-side ceremonies —
// that library is the well-trodden path: it handles base64url
// encoding, the navigator.credentials API surface, and the
// spec-version drift that bare WebAuthn would otherwise require us
// to track.
//
// The server-side options come from Fido2NetLib, which serialises
// AssertionOptions / CredentialCreateOptions in the WebAuthn-spec
// shape that @simplewebauthn consumes. The two libraries agree on
// the wire format; this module is the seam between them.

import { startAuthentication, startRegistration } from '@simplewebauthn/browser';

import { ApiError, request, type InvitePreview } from './api';

// Derive the wire types from the library's own function signatures
// rather than depending on @simplewebauthn/types (deprecated at v11).
// If the library renames or reshapes any of these in a future major,
// the build catches it at this seam.
type AssertionOptionsJSON = Parameters<typeof startAuthentication>[0]['optionsJSON'];
type AssertionResponseJSON = Awaited<ReturnType<typeof startAuthentication>>;
type RegistrationOptionsJSON = Parameters<typeof startRegistration>[0]['optionsJSON'];
type RegistrationResponseJSON = Awaited<ReturnType<typeof startRegistration>>;

/** Mirror of API `LoginBeginRequest` (POST body shape). */
interface LoginBeginRequest {
    username: string;
}

/** Mirror of API `LoginBeginResponse`. */
interface LoginBeginResponse {
    challengeId: string;
    /**
     * Fido2NetLib's AssertionOptions serialized in the WebAuthn-spec
     * shape that @simplewebauthn/browser consumes. The two libraries
     * agree on the wire format.
     */
    options: AssertionOptionsJSON;
}

/** Mirror of API `LoginCompleteRequest`. */
interface LoginCompleteRequest {
    challengeId: string;
    assertionResponse: AssertionResponseJSON;
}

/** Mirror of API `LoginCompleteResponse`. */
export interface LoginCompleteResponse {
    userId: string;
    username: string;
    sessionId: string;
    sessionExpiresAt: string;
}

/**
 * Run the login ceremony end-to-end for the supplied username.
 *
 * Flow:
 *   1. POST /api/auth/login/begin → get challenge + assertion options.
 *   2. Browser WebAuthn ceremony (navigator.credentials.get under the
 *      hood, wrapped by @simplewebauthn/browser).
 *   3. POST /api/auth/login/complete → server verifies, sets the
 *      session cookie. The response body identifies the user; the
 *      caller usually doesn't need it directly (the next
 *      /api/auth/me roundtrip resolves identity) but it's returned
 *      so the UI can show a "Welcome back, $name" toast without
 *      waiting.
 *
 * Any thrown error here is either an `ApiError` (from the server) or
 * a DOMException / Error from the WebAuthn ceremony. The caller
 * surfaces the `.message` to the user; we don't try to localise or
 * categorise here beyond the typed-error path the API already gives
 * us.
 */
export async function performLogin(username: string): Promise<LoginCompleteResponse> {
    const begin = await request<LoginBeginResponse>('/api/auth/login/begin', {
        method: 'POST',
        body: { username } satisfies LoginBeginRequest,
    });

    const assertionResponse = await startAuthentication({
        optionsJSON: begin.options,
    });

    return request<LoginCompleteResponse>('/api/auth/login/complete', {
        method: 'POST',
        body: {
            challengeId: begin.challengeId,
            assertionResponse,
        } satisfies LoginCompleteRequest,
    });
}

/** Mirror of API `RecoveryLoginRequest`. */
interface RecoveryLoginRequest {
    username: string;
    recoveryCode: string;
}

/**
 * Sign in with a single-use recovery code instead of a passkey
 * (ADR-0013). The fallback when no passkey can be used — a lost
 * authenticator, or a restored DB whose passkeys were bound to a
 * different RP id (ADR-0061). On success the session cookie is set, same
 * as a normal login; the caller should then prompt the user to register
 * a fresh passkey.
 */
export async function performRecoveryLogin(
    username: string,
    recoveryCode: string,
): Promise<LoginCompleteResponse> {
    return request<LoginCompleteResponse>('/api/auth/login/recovery', {
        method: 'POST',
        body: { username, recoveryCode } satisfies RecoveryLoginRequest,
    });
}

// --- Setup (first-run registration) ----------------------------------

/**
 * Mirror of API `SetupInfoResponse`. Intentionally empty (ADR-0088) — /info
 * exists to validate the bootstrap token, and there is no ledger list to offer
 * on a fresh install.
 */
export type SetupInfoResponse = Record<string, never>;

/** Mirror of API `SetupBeginRequest`. */
interface SetupBeginRequest {
    username: string;
    displayName: string;
}

/** Mirror of API `SetupBeginResponse`. */
interface SetupBeginResponse {
    challengeId: string;
    options: RegistrationOptionsJSON;
}

/** Mirror of API `SetupCompleteRequest`. */
interface SetupCompleteRequest {
    challengeId: string;
    credentialNickname: string;
    attestationResponse: RegistrationResponseJSON;
    includeDemo: boolean;
}

/**
 * Mirror of API `SetupCompleteResponse`. <b>recoveryCodes</b> are
 * returned exactly once at registration time — the caller MUST
 * surface them to the user and acknowledge the display before
 * navigating away. There is no API to retrieve them again later.
 */
export interface SetupCompleteResponse {
    userId: string;
    username: string;
    sessionId: string;
    sessionExpiresAt: string;
    recoveryCodes: string[];
    /** Null unless a Demo ledger was requested and its seed succeeded (ADR-0088). */
    ledgerId: string | null;
    /** Null unless a Demo ledger was requested and its seed succeeded (ADR-0088). */
    ledgerName: string | null;
    /**
     * The install's master key, base64 (ADR-0092 D2). Startup minted it on this
     * virgin install; setup is where the operator is first shown it. Unlike
     * {@link recoveryCodes} this one CAN be retrieved again — System → Backups
     * reveals it behind a fresh passkey assertion — so the display gate is a moment
     * of attention rather than a last chance.
     */
    masterKeyBase64: string;
}

/** Input to {@link performSetup}. */
export interface SetupInput {
    /** Bootstrap token from the URL (the one-shot first-run secret). */
    token: string;
    /** Username to register; must be unique. */
    username: string;
    /** Human-readable display name (shown in the UI header etc.). */
    displayName: string;
    /**
     * Friendly label for the credential the user is about to enrol
     * (e.g. "MacBook Touch ID", "Yubikey 5"). Stored alongside the
     * credential so a future "manage your passkeys" screen can
     * disambiguate.
     */
    credentialNickname: string;
    /**
     * Also create a Demo ledger seeded with the bundled sample dataset
     * (ADR-0088). Setup creates no ledger otherwise — the user lands on the
     * hub and creates or imports one there.
     */
    includeDemo: boolean;
    /**
     * Optional callback fired right after /begin returns and the
     * WebAuthn ceremony is about to start. The setup page uses this
     * to swap the button label to "Tap your authenticator…" so the
     * user knows the biometric prompt is imminent (Windows Hello in
     * particular can be slow to appear or end up off-screen on
     * multi-monitor setups).
     */
    onCeremonyStart?: () => void;
}

/**
 * Run the first-run setup ceremony end-to-end.
 *
 * Flow:
 *   1. POST /api/auth/setup/{token}/begin with {username, displayName}.
 *      Server validates the bootstrap token (still valid, unconsumed),
 *      rejects duplicate username, and mints a registration challenge.
 *   2. Browser WebAuthn registration ceremony (navigator.credentials
 *      .create under the hood, wrapped by @simplewebauthn/browser).
 *   3. POST /api/auth/setup/{token}/complete with the attestation +
 *      a credential nickname. Server verifies the attestation,
 *      creates the user + credential + recovery codes inside a single
 *      transaction (also flips the bootstrap token's consumed_at),
 *      sets the session cookie, and returns the plaintext recovery
 *      codes ONCE.
 *
 * The caller MUST display the returned recoveryCodes to the user and
 * require an explicit acknowledgement before navigating away. The
 * codes never appear again (only an Argon2id hash is stored).
 */
export async function performSetup(input: SetupInput): Promise<SetupCompleteResponse> {
    const begin = await request<SetupBeginResponse>(
        `/api/auth/setup/${encodeURIComponent(input.token)}/begin`,
        {
            method: 'POST',
            body: {
                username: input.username,
                displayName: input.displayName,
            } satisfies SetupBeginRequest,
        },
    );

    // Notify the caller right before the browser's WebAuthn dialog
    // appears so the UI can show "Tap your authenticator…" instead of
    // a stale "Creating account…" while the user is staring at a
    // hidden Windows Hello prompt.
    input.onCeremonyStart?.();

    const attestationResponse = await startRegistration({
        optionsJSON: begin.options,
    });

    const completeBody: SetupCompleteRequest = {
        challengeId: begin.challengeId,
        credentialNickname: input.credentialNickname,
        attestationResponse,
        includeDemo: input.includeDemo,
    };

    return request<SetupCompleteResponse>(
        `/api/auth/setup/${encodeURIComponent(input.token)}/complete`,
        { method: 'POST', body: completeBody },
    );
}

// ── Invites (ADR-0083 slice B) — a scoped, repeatable clone of setup ──

interface InviteBeginRequest {
    username: string;
    displayName: string;
}
interface InviteBeginResponse {
    challengeId: string;
    options: RegistrationOptionsJSON;
}
interface InviteCompleteRequest {
    challengeId: string;
    credentialNickname: string;
    attestationResponse: RegistrationResponseJSON;
}

/** Mirror of API `InviteCompleteResponse`. */
export interface InviteCompleteResponse {
    userId: string;
    username: string;
    recoveryCodes: string[];
    ledgerId: string | null;
    ledgerName: string | null;
}

/** Mirror of API `InviteAcceptResponse`. */
export interface InviteAcceptResponse {
    ledgerId: string | null;
    ledgerName: string | null;
}

export interface InviteRedeemInput {
    token: string;
    username: string;
    displayName: string;
    credentialNickname: string;
    onCeremonyStart?: () => void;
}

/** GET /api/auth/invite/{token} — what the invite confers (validity + scope). */
export function fetchInvitePreview(token: string): Promise<InvitePreview> {
    return request<InvitePreview>(`/api/auth/invite/${encodeURIComponent(token)}`);
}

/**
 * Redeem an invite as a NEW user: the passkey-registration ceremony scoped to the
 * invite's pre-assigned ledger/role — a clone of {@link performSetup} without the
 * ledger picker. The caller MUST show the returned recoveryCodes once and gate
 * navigation on acknowledgement.
 */
export async function performInviteRedeem(input: InviteRedeemInput): Promise<InviteCompleteResponse> {
    const begin = await request<InviteBeginResponse>(
        `/api/auth/invite/${encodeURIComponent(input.token)}/begin`,
        {
            method: 'POST',
            body: { username: input.username, displayName: input.displayName } satisfies InviteBeginRequest,
        },
    );

    input.onCeremonyStart?.();
    const attestationResponse = await startRegistration({ optionsJSON: begin.options });

    return request<InviteCompleteResponse>(
        `/api/auth/invite/${encodeURIComponent(input.token)}/complete`,
        {
            method: 'POST',
            body: {
                challengeId: begin.challengeId,
                credentialNickname: input.credentialNickname,
                attestationResponse,
            } satisfies InviteCompleteRequest,
        },
    );
}

/** POST /api/auth/invite/{token}/accept — a signed-in user applies the invite's grant to themselves. */
export function performInviteAccept(token: string): Promise<InviteAcceptResponse> {
    return request<InviteAcceptResponse>(
        `/api/auth/invite/${encodeURIComponent(token)}/accept`,
        { method: 'POST' },
    );
}

/**
 * Fetch the public info for a bootstrap token (validity check + the
 * list of ledgers the new user could join). Used by the setup page on
 * mount so token-invalid surfaces immediately rather than after the
 * user fills the form.
 */
export async function fetchSetupInfo(token: string): Promise<SetupInfoResponse> {
    return request<SetupInfoResponse>(
        `/api/auth/setup/${encodeURIComponent(token)}/info`,
        { method: 'GET' },
    );
}

// --- Logout ----------------------------------------------------------

/**
 * POST /api/auth/logout — revoke the current session server-side
 * (the API flips `auth_sessions.revoked_at`) and clear the cookie
 * (the API responds with `Set-Cookie: coffer.session=; Max-Age=0`).
 *
 * The endpoint is anonymous on the API side by design: a user with
 * a stale cookie still wants logout to clear it without a 401 round
 * trip. The caller is responsible for invalidating any cached
 * authenticated state (the `['me']` query, anything keyed off the
 * user-id) and navigating away from protected routes.
 *
 * The browser can't observe the HttpOnly cookie directly, so we
 * don't try to manipulate it here. The API's Set-Cookie response
 * header is what actually clears it.
 */
export function performLogout(): Promise<void> {
    return request<void>('/api/auth/logout', { method: 'POST' });
}

// --- Bootstrap restore (ADR-0061) ------------------------------------

/**
 * Upload an encrypted backup to the restore branch of the bootstrap UI.
 * Multipart (the JSON `request` helper can't carry a file), bootstrap-
 * token-gated like the rest of setup. The server verifies the passphrase
 * opens the archive, stages it, and restarts; the next boot applies it.
 * Resolves when the server has accepted (status "restoring"); the caller
 * then polls {@link waitForServerBack}.
 */
export async function restoreFromBackup(
    token: string,
    archive: File,
    passphrase: string,
): Promise<void> {
    const form = new FormData();
    form.append('archive', archive);
    form.append('passphrase', passphrase);

    const response = await fetch(
        `/api/auth/setup/${encodeURIComponent(token)}/restore`,
        { method: 'POST', credentials: 'include', body: form },
    );
    if (!response.ok) {
        // Decode the ProblemDetails envelope the same way the JSON helper
        // does, so callers see the typed code (e.g. backup-passphrase-invalid).
        let detail = response.statusText || `HTTP ${response.status}`;
        let code: string | undefined;
        try {
            const body = (await response.json()) as { detail?: string; code?: string };
            if (typeof body.detail === 'string' && body.detail.length > 0) detail = body.detail;
            if (typeof body.code === 'string') code = body.code;
        } catch {
            // non-JSON body; keep the status fallback
        }
        throw new ApiError(response.status, detail, code);
    }
}

/**
 * Poll the anonymous liveness probe until the server answers (it goes
 * down then up across the restore restart). Resolves once it's back, or
 * rejects after {@link timeoutMs}. Used by the "restoring…" screen before
 * it sends the user to /login.
 */
export async function waitForServerBack(
    { timeoutMs = 120_000, intervalMs = 2_000 }: { timeoutMs?: number; intervalMs?: number } = {},
): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    // Give the process a moment to actually begin shutting down before the
    // first probe, so we don't see the pre-restart "up" and exit early.
    await delay(intervalMs);
    for (;;) {
        try {
            const resp = await fetch('/healthz', { method: 'GET', cache: 'no-store' });
            if (resp.ok) return;
        } catch {
            // connection refused while the server is restarting — keep waiting
        }
        if (Date.now() >= deadline) {
            throw new Error('The server did not come back in time. It may still be restoring.');
        }
        await delay(intervalMs);
    }
}

function delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

// --- Passkey + recovery-code self-service (ADR-0013) -----------------

/** Mirror of API `CredentialSummary`. */
export interface CredentialSummary {
    id: string;
    nickname: string;
    createdAt: string;
    lastUsedAt: string | null;
}

/** Mirror of API `RecoveryCodesStatusResponse`. */
export interface RecoveryCodesStatus {
    remaining: number;
    total: number;
}

interface RegisterBeginResponse {
    challengeId: string;
    options: RegistrationOptionsJSON;
}

interface RegisterCompleteRequest {
    challengeId: string;
    credentialNickname: string;
    attestationResponse: RegistrationResponseJSON;
}

/** List the current user's passkeys. */
export function fetchCredentials(): Promise<CredentialSummary[]> {
    return request<CredentialSummary[]>('/api/auth/credentials', { method: 'GET' });
}

/** Remove one of the current user's passkeys (the server refuses the last one). */
export function deleteCredential(id: string): Promise<void> {
    return request<void>(`/api/auth/credentials/${encodeURIComponent(id)}`, {
        method: 'DELETE',
    });
}

/**
 * Add a passkey to the current (already signed-in) user: register/begin →
 * browser ceremony → register/complete. {@link onCeremonyStart} fires
 * right before the authenticator prompt so the UI can hint "tap your
 * authenticator".
 */
export async function addPasskey(
    credentialNickname: string,
    onCeremonyStart?: () => void,
): Promise<CredentialSummary> {
    const begin = await request<RegisterBeginResponse>('/api/auth/register/begin', {
        method: 'POST',
    });

    onCeremonyStart?.();
    const attestationResponse = await startRegistration({ optionsJSON: begin.options });

    return request<CredentialSummary>('/api/auth/register/complete', {
        method: 'POST',
        body: {
            challengeId: begin.challengeId,
            credentialNickname,
            attestationResponse,
        } satisfies RegisterCompleteRequest,
    });
}

/** How many recovery codes remain for the current user. */
export function fetchRecoveryCodesStatus(): Promise<RecoveryCodesStatus> {
    return request<RecoveryCodesStatus>('/api/auth/recovery-codes', { method: 'GET' });
}

/**
 * Replace the current user's recovery-code set. Returns the fresh
 * plaintext codes ONCE — the caller must display them (the existing
 * RecoveryCodes component) and the old set is now invalid.
 */
export async function regenerateRecoveryCodes(): Promise<string[]> {
    const result = await request<{ recoveryCodes: string[] }>(
        '/api/auth/recovery-codes/regenerate',
        { method: 'POST' },
    );
    return result.recoveryCodes;
}
