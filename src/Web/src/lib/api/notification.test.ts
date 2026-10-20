import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

import {
    createNotificationSubscriber,
    createLedgerNotificationSubscriber,
} from './notification';

/**
 * These mock `fetch`, not the api client, on purpose.
 *
 * Every notification mutation shipped double-encoded: the client wrapped its body in
 * JSON.stringify and `request()` stringifies again, so the server received a JSON
 * *string* where it expected an object and answered 400. Nothing in the suite caught
 * it, because the panel tests mock these very functions — the wire format was
 * untestable by construction. Asserting on what crosses the wire is the only version
 * of this test that could have failed.
 */
describe('notification api client wire format', () => {
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

    function sentBody() {
        const raw = calls[0].init.body;
        expect(typeof raw).toBe('string');
        const parsed = JSON.parse(raw as string);
        // A double-encoded body parses to a STRING, not an object — that is the
        // failure this whole file exists to catch.
        expect(typeof parsed).not.toBe('string');
        return parsed;
    }

    it('sends an admin subscriber as a JSON object', async () => {
        await createNotificationSubscriber({
            subscriberKey: 'healthchecks',
            url: 'https://hc.invalid/ping',
            minSeverity: 'info',
        });

        expect(sentBody()).toEqual({
            subscriberKey: 'healthchecks',
            url: 'https://hc.invalid/ping',
            minSeverity: 'info',
        });
    });

    it('sends a ledger subscriber as a JSON object', async () => {
        await createLedgerNotificationSubscriber('00000000-0000-0000-0000-000000000010', {
            subscriberKey: 'webhook',
            url: 'https://example.invalid/hook',
            minSeverity: 'warning',
        });

        expect(calls[0].url).toContain('/notifications/subscribers');
        expect(sentBody()).toEqual({
            subscriberKey: 'webhook',
            url: 'https://example.invalid/hook',
            minSeverity: 'warning',
        });
    });
});
