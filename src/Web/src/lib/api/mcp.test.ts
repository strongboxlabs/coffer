import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

import {
    renameMcpConnection,
    renameMcpToken,
    renameOAuthGrant,
    revokeMcpConnection,
} from './mcp';

/**
 * These mock `fetch`, not the api client, on purpose: the MCP rename shipped double-encoded
 * (the client wrapped its body in JSON.stringify and request() stringified it again, so the
 * server received a JSON *string* and answered 400), and every test around it mocked the
 * client function, so none could see the wire. What crosses the wire is the only thing
 * worth asserting here.
 */
describe('mcp api client wire format', () => {
    let calls: Array<{ url: string; init: RequestInit }>;

    beforeEach(() => {
        calls = [];
        vi.stubGlobal('fetch', vi.fn((url: string, init: RequestInit) => {
            calls.push({ url, init });
            return Promise.resolve(new Response(null, { status: 204 }));
        }));
    });

    afterEach(() => {
        vi.unstubAllGlobals();
    });

    /** The JSON the server would parse — an object, never a string holding JSON. */
    function sentBody() {
        const parsed = JSON.parse(calls[0].init.body as string);
        expect(typeof parsed).not.toBe('string');
        return parsed;
    }

    it('renames a token with a JSON object', async () => {
        await renameMcpToken('t 1', 'Laptop');

        expect(calls[0].url).toBe('/api/account/mcp-tokens/t%201');
        expect(calls[0].init.method).toBe('PATCH');
        expect(sentBody()).toEqual({ name: 'Laptop' });
    });

    it('renames an OAuth connection with a JSON object, a blank name included', async () => {
        await renameOAuthGrant('g-1', '');

        expect(calls[0].url).toBe('/api/account/oauth-grants/g-1');
        expect(calls[0].init.method).toBe('PATCH');
        expect(sentBody()).toEqual({ name: '' });
    });

    it('renames anyone’s connection, addressed by kind and id', async () => {
        await renameMcpConnection('oauth', 'g-1', 'Phone');

        expect(calls[0].url).toBe('/api/admin/mcp/connections/oauth/g-1');
        expect(calls[0].init.method).toBe('PATCH');
        expect(sentBody()).toEqual({ name: 'Phone' });
    });

    it('revokes anyone’s connection, addressed by kind and id', async () => {
        await revokeMcpConnection('token', 'abc');

        expect(calls[0].url).toBe('/api/admin/mcp/connections/token/abc');
        expect(calls[0].init.method).toBe('DELETE');
        expect(calls[0].init.body).toBeUndefined();
    });
});
