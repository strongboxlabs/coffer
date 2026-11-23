import { afterEach, describe, expect, it, vi } from 'vitest';

import { request, requestBlob, requestMultipart } from '@/lib/api/_request';
import { fetchReportPins } from '@/lib/api/report';

import { hasEmptyPathSegment, settledEmptySegmentRequests, stubFetch, takeEmptySegmentRequests } from './fetchStub';

describe('hasEmptyPathSegment', () => {
    it.each([
        '/api/ledgers//reports/pins',
        '/api/ledgers/l1/transactions/',
        'http://localhost/api/ledgers//accounts',
        '/api/ledgers//reports/pins?from=2026-01-01',
    ])('finds the empty segment in %s', (raw) => {
        expect(hasEmptyPathSegment(raw)).toBe(true);
    });

    it.each([
        '/api/ledgers/l1/reports/pins',
        'http://localhost/api/ledgers/l1/accounts',
        '/api/auth/login/begin?returnUrl=http://localhost/oauth/authorize',
        '/api/ledgers/l1/register#row//2',
        '/',
        'http://localhost/',
    ])('finds none in %s', (raw) => {
        expect(hasEmptyPathSegment(raw)).toBe(false);
    });
});

describe('stubFetch', () => {
    afterEach(() => vi.unstubAllGlobals());

    it('refuses a request with an empty path segment, naming it, and holds it for the suite to fail', async () => {
        const stub = stubFetch().on('GET', /\/reports\/pins$/, []);

        await expect(fetch('/api/ledgers//reports/pins')).rejects.toThrow('GET /api/ledgers//reports/pins');

        // Taken here, so this test, which meant to send it, does not fail for it.
        expect(takeEmptySegmentRequests()).toEqual(['GET /api/ledgers//reports/pins']);
        expect(stub.calls.map((c) => c.path)).toEqual(['/api/ledgers//reports/pins']);
    });

    it('is not the only fetch that refuses one: a request no test routed is refused too', async () => {
        await expect(fetch('/api/ledgers//accounts', { method: 'post' })).rejects.toThrow('POST /api/ledgers//accounts');

        expect(takeEmptySegmentRequests()).toEqual(['POST /api/ledgers//accounts']);
    });

    it('answers a whole path as routed', async () => {
        stubFetch().on('GET', /\/reports\/pins$/, []);

        const response = await fetch('/api/ledgers/l1/reports/pins');

        expect(await response.json()).toEqual([]);
        expect(takeEmptySegmentRequests()).toEqual([]);
    });
});

// A test that installs a fetch of its own replaces the environment's, and the
// check with it. The API client's request functions check the path themselves
// in the suite (vitest.setup.ts), so whatever fetch a test installs, a request
// the client builds with an empty segment is refused before it is sent.
describe('the API client, under a fetch the test installed', () => {
    afterEach(() => {
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    const answer = async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });

    it('refuses one before it reaches a fetch stubbed in', async () => {
        const own = vi.fn(answer);
        vi.stubGlobal('fetch', own);

        await expect(fetchReportPins('')).rejects.toThrow('GET /api/ledgers//reports/pins');

        expect(own).not.toHaveBeenCalled();
        expect(takeEmptySegmentRequests()).toEqual(['GET /api/ledgers//reports/pins']);
    });

    it('refuses one before it reaches a fetch spied on, however the client sends it', async () => {
        const own = vi.spyOn(globalThis, 'fetch').mockImplementation(answer);

        await expect(request('/api/ledgers//accounts/a1', { method: 'DELETE' })).rejects.toThrow('DELETE /api/ledgers//accounts/a1');
        await expect(requestBlob('/api/backups//download')).rejects.toThrow('GET /api/backups//download');
        await expect(requestMultipart('/api/ledgers//imports', new FormData())).rejects.toThrow('POST /api/ledgers//imports');

        expect(own).not.toHaveBeenCalled();
        expect(takeEmptySegmentRequests()).toEqual([
            'DELETE /api/ledgers//accounts/a1',
            'GET /api/backups//download',
            'POST /api/ledgers//imports',
        ]);
    });

    it('sends a whole path on to the fetch the test installed', async () => {
        const own = vi.fn(answer);
        vi.stubGlobal('fetch', own);

        await expect(fetchReportPins('ledger-1')).resolves.toEqual([]);

        expect(own).toHaveBeenCalledTimes(1);
        expect(takeEmptySegmentRequests()).toEqual([]);
    });
});

// The suite reports what was refused after each test, and after a file's last
// one (vitest.setup.ts). It takes them a turn of the event loop late.
describe('settledEmptySegmentRequests', () => {
    it('takes a request sent a turn after it was asked for, so it is reported with the test that sent it', async () => {
        setTimeout(() => {
            void fetch('/api/ledgers//sent-late').catch(() => {});
        }, 0);

        expect(await settledEmptySegmentRequests()).toEqual(['GET /api/ledgers//sent-late']);
    });

    it('takes its turn on the real clock, so a test that left fake timers on does not hold it up', async () => {
        vi.useFakeTimers();
        try {
            expect(await settledEmptySegmentRequests()).toEqual([]);
        } finally {
            vi.useRealTimers();
        }
    });
});
