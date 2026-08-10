import { startAuthentication } from '@simplewebauthn/browser';

import { request } from './_request';
import type {
    MasterKeyStatus,
    MasterKeyReveal,
    MasterKeyRotation,
} from '../types/masterKey';

// Admin master-KEK surface (ADR-0092 D2/D4, /api/admin/master-key).
//
// Both the reveal and the rotation require a FRESH passkey assertion on top of
// the admin session: the cookie only proves an admin authenticated some time in
// the last 30 days, while the assertion proves a human with an enrolled
// authenticator is present right now. That is what turns a stolen still-valid
// cookie into a dead end.
//
// The ceremony is deliberately NOT cached or reused here. Each call runs its own
// begin → navigator.credentials.get → complete round trip, because the server
// consumes the challenge on use and authorizes exactly one response with it.

type AssertionOptionsJSON = Parameters<typeof startAuthentication>[0]['optionsJSON'];
type AssertionResponseJSON = Awaited<ReturnType<typeof startAuthentication>>;

interface RevealBeginResponse {
    challengeId: string;
    assertionOptions: AssertionOptionsJSON;
}

/**
 * Metadata only — id, file path, and a non-reversible fingerprint. Safe to fetch
 * on panel load; carries no key material, so it needs no ceremony.
 */
export function fetchMasterKeyStatus(): Promise<MasterKeyStatus> {
    return request<MasterKeyStatus>('/api/admin/master-key');
}

/**
 * Run the re-auth ceremony and return the key.
 *
 * Throws either an `ApiError` (401 on a failed/replayed ceremony, 422 when the
 * account has no usable passkey for this domain) or a DOMException from the
 * browser ceremony — most commonly `NotAllowedError` when the user dismisses the
 * prompt. Callers surface `.message`.
 */
export async function revealMasterKey(): Promise<MasterKeyReveal> {
    const begin = await request<RevealBeginResponse>(
        '/api/admin/master-key/reveal/begin',
        { method: 'POST' },
    );

    const assertionResponse: AssertionResponseJSON = await startAuthentication({
        optionsJSON: begin.assertionOptions,
    });

    return request<MasterKeyReveal>('/api/admin/master-key/reveal', {
        method: 'POST',
        body: { challengeId: begin.challengeId, assertionResponse },
    });
}

/**
 * Rotate: generate a new key server-side, re-wrap everything onto it, swap the
 * key file, and return the new key (shown once — the operator must save it).
 *
 * The server restarts immediately afterwards to load the new key, so the next
 * request from this page will fail until it comes back. `restartPending` in the
 * response says so; the caller should tell the user to expect a brief reconnect
 * rather than treat it as an error.
 */
export async function rotateMasterKey(): Promise<MasterKeyRotation> {
    // Same ceremony endpoint as reveal — rotation both writes new key material
    // and hands it back, so it is at least as sensitive.
    const begin = await request<RevealBeginResponse>(
        '/api/admin/master-key/reveal/begin',
        { method: 'POST' },
    );

    const assertionResponse: AssertionResponseJSON = await startAuthentication({
        optionsJSON: begin.assertionOptions,
    });

    return request<MasterKeyRotation>('/api/admin/master-key/rotate', {
        method: 'POST',
        body: { challengeId: begin.challengeId, assertionResponse },
    });
}
