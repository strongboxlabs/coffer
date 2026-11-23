import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

import { ApiError, request } from './_request';

/**
 * Mocks `fetch`, not the api client: the failure this guards is on the wire, and a test
 * that mocks the client function can never see it.
 */
describe('request body handling', () => {
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

    it('serializes an object body once', async () => {
        await request('/api/x', { method: 'PATCH', body: { label: 'Laptop' } });

        expect(JSON.parse(calls[0].init.body as string)).toEqual({ label: 'Laptop' });
    });

    it('refuses a body that is already a string, and sends nothing', async () => {
        await expect(
            request('/api/x', { method: 'PATCH', body: JSON.stringify({ label: 'Laptop' }) }),
        ).rejects.toThrow(/already a string/);

        expect(calls).toHaveLength(0);
    });
});

describe('a refusal that names a missing rate', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    function refuse(body: unknown) {
        vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify(body), {
            status: 422,
            headers: { 'Content-Type': 'application/problem+json' },
        }))));
    }

    it('carries the rateUnavailable extension beside the code', async () => {
        const missing = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-01-31', reason: 'rate_unavailable' };
        refuse({ detail: 'No EUR to USD rate for January 2026.', code: 'rate-unavailable', rateUnavailable: missing });

        const error = await request('/api/x', { method: 'POST', body: {} }).catch((e: unknown) => e);

        expect(error).toBeInstanceOf(ApiError);
        expect((error as ApiError).code).toBe('rate-unavailable');
        expect((error as ApiError).rateUnavailable).toEqual(missing);
    });

    it('leaves it undefined on any other refusal, and on a malformed member', async () => {
        refuse({ detail: 'Nope.', code: 'category-cycle', rateUnavailable: { fromCurrency: 'EUR' } });

        const error = await request('/api/x', { method: 'POST', body: {} }).catch((e: unknown) => e);

        expect((error as ApiError).code).toBe('category-cycle');
        expect((error as ApiError).rateUnavailable).toBeUndefined();
        expect((error as ApiError).rateUnavailableLegs).toEqual([]);
    });

    it('carries a list of missing stored legs, as a share move names them, and drops a malformed one (ADR-0114 D13)', async () => {
        const legs = [
            { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-02-02', reason: 'rate_unavailable' },
            { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-02-02', reason: 'rate_unavailable' },
        ];
        refuse({ detail: 'No rate.', code: 'rate-unavailable', rateUnavailable: [...legs, { fromCurrency: 'EUR' }] });

        const error = await request('/api/x', { method: 'POST', body: {} }).catch((e: unknown) => e);

        expect((error as ApiError).rateUnavailableLegs).toEqual(legs);
        expect((error as ApiError).rateUnavailable).toBeUndefined();
    });
});
