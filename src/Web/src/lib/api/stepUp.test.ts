import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from './_request';
import { STEP_UP_HEADER, stepUpHeaders, withStepUpIfRequired } from './stepUp';

// The passkey tap sensitive actions need (2026-10-12 security audit, M2): begin a
// ceremony for the one action, run it, and send the result in a header.

const assertion = { id: 'cred-1', rawId: 'cred-1', type: 'public-key', response: {} };

vi.mock('@simplewebauthn/browser', () => ({
    startAuthentication: vi.fn(async () => assertion),
}));

function json(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
    });
}

function decodeHeader(value: string): unknown {
    const b64 = value.replace(/-/g, '+').replace(/_/g, '/');
    const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);
    return JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(padded), (c) => c.charCodeAt(0))));
}

afterEach(() => vi.restoreAllMocks());

describe('stepUpHeaders', () => {
    it('begins a tap for the action and returns the proof as a header', async () => {
        const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
            json({ challengeId: 'ch-1', assertionOptions: { challenge: 'x' } }),
        );

        const headers = await stepUpHeaders('backup-delete');

        expect(String(fetchSpy.mock.calls[0][0])).toBe('/api/auth/step-up/begin');
        expect(JSON.parse(String(fetchSpy.mock.calls[0][1]?.body))).toEqual({ action: 'backup-delete' });
        expect(decodeHeader(headers[STEP_UP_HEADER])).toEqual({
            challengeId: 'ch-1',
            assertionResponse: assertion,
        });
    });
});

describe('withStepUpIfRequired', () => {
    it('sends without a tap first, and taps only when the server asks', async () => {
        vi.spyOn(globalThis, 'fetch').mockResolvedValue(
            json({ challengeId: 'ch-2', assertionOptions: { challenge: 'x' } }),
        );
        const send = vi
            .fn<(headers: Record<string, string>) => Promise<string>>()
            .mockRejectedValueOnce(new ApiError(422, 'Confirm with your passkey', 'step-up-required'))
            .mockResolvedValueOnce('done');

        await expect(withStepUpIfRequired('passkey-add', send)).resolves.toBe('done');

        expect(send).toHaveBeenCalledTimes(2);
        expect(send.mock.calls[0][0]).toEqual({});
        expect(Object.keys(send.mock.calls[1][0])).toEqual([STEP_UP_HEADER]);
    });

    it('does not tap when the first attempt succeeds', async () => {
        const fetchSpy = vi.spyOn(globalThis, 'fetch');
        const send = vi.fn(async () => 'done');

        await expect(withStepUpIfRequired('mcp-writes', send)).resolves.toBe('done');

        expect(send).toHaveBeenCalledTimes(1);
        expect(fetchSpy).not.toHaveBeenCalled();
    });

    it('passes any other failure straight through without tapping', async () => {
        const fetchSpy = vi.spyOn(globalThis, 'fetch');
        const failure = new ApiError(422, 'No such user', 'user-not-found');
        const send = vi.fn(async () => {
            throw failure;
        });

        await expect(withStepUpIfRequired('grant-admin', send)).rejects.toBe(failure);

        expect(send).toHaveBeenCalledTimes(1);
        expect(fetchSpy).not.toHaveBeenCalled();
    });
});
