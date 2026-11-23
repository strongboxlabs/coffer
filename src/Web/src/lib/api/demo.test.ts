import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

import { ApiError } from './_request';
import { fetchDemoJob, startDemoLedger } from './demo';

/**
 * Mocks `fetch`, not the api client: what this guards is on the wire. The Demo ends
 * on the browser's local date (ADR-0115 D1), so the date sent is the local calendar
 * day, not the UTC one.
 */
describe('Demo ledger api', () => {
    // The date tests pin a zone west of UTC, so the local and UTC dates differ whatever
    // zone the machine running them is in (in UTC they would be the same day).
    const zone = process.env.TZ;
    let calls: Array<{ url: string; init: RequestInit }>;

    function answer(response: () => Response) {
        vi.stubGlobal('fetch', vi.fn((url: string, init: RequestInit) => {
            calls.push({ url, init });
            return Promise.resolve(response());
        }));
    }

    beforeEach(() => {
        calls = [];
    });

    afterEach(() => {
        // Assigning undefined would store the string "undefined".
        if (zone === undefined) delete process.env.TZ;
        else process.env.TZ = zone;
        vi.useRealTimers();
        vi.unstubAllGlobals();
    });

    it('POSTs with the browser’s local date as today, and no body', async () => {
        // Late in the local evening: in a zone west of UTC the UTC date is already the
        // next day, and the Demo must still end on the date the user sees.
        process.env.TZ = 'America/Los_Angeles';
        vi.useFakeTimers({ toFake: ['Date'] });
        vi.setSystemTime(new Date(2026, 10, 18, 23, 30));
        answer(() => new Response(JSON.stringify({ jobId: 'j1' }), { status: 200 }));

        await startDemoLedger();

        expect(calls).toHaveLength(1);
        expect(calls[0].url).toBe('/api/ledgers/demo?today=2026-11-18');
        expect(calls[0].init.method).toBe('POST');
        expect(calls[0].init.body).toBeUndefined();
    });

    it('reads the date at call time, so a hub left open past midnight sends the new day', async () => {
        process.env.TZ = 'America/Los_Angeles';
        vi.useFakeTimers({ toFake: ['Date'] });
        answer(() => new Response(JSON.stringify({ jobId: 'j1' }), { status: 200 }));

        vi.setSystemTime(new Date(2026, 11, 31, 23, 59));
        await startDemoLedger();
        vi.setSystemTime(new Date(2027, 0, 1, 0, 1));
        await startDemoLedger();

        expect(calls.map((c) => c.url)).toEqual([
            '/api/ledgers/demo?today=2026-12-31',
            '/api/ledgers/demo?today=2027-01-01',
        ]);
    });

    it('returns null for a 204 job lookup, and the job otherwise', async () => {
        answer(() => new Response(null, { status: 204 }));
        expect(await fetchDemoJob()).toBeNull();
        expect(calls[0].url).toBe('/api/ledgers/demo/job');

        const job = { jobId: 'j1', state: 'running', ledgerName: 'Demo' };
        answer(() => new Response(JSON.stringify(job), { status: 200 }));
        expect(await fetchDemoJob()).toEqual(job);
    });

    it('carries the named Demo of a failed start on the ApiError', async () => {
        answer(() => new Response(JSON.stringify({
            detail: 'Building Demo 2 failed and nothing was kept. Try again, or check the server logs.',
            ledgerName: 'Demo 2',
        }), { status: 500, headers: { 'Content-Type': 'application/problem+json' } }));

        const error = await startDemoLedger().catch((e: unknown) => e);

        expect(error).toBeInstanceOf(ApiError);
        expect((error as ApiError).status).toBe(500);
        expect((error as ApiError).ledgerName).toBe('Demo 2');
    });

    it('leaves ledgerName undefined on a busy refusal, which names no Demo', async () => {
        answer(() => new Response(JSON.stringify({
            detail: 'An import is already running.',
            code: 'import-already-running',
        }), { status: 422, headers: { 'Content-Type': 'application/problem+json' } }));

        const error = await startDemoLedger().catch((e: unknown) => e);

        expect((error as ApiError).code).toBe('import-already-running');
        expect((error as ApiError).ledgerName).toBeUndefined();
    });
});
