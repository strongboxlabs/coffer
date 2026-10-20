import { afterEach, describe, expect, it, vi } from 'vitest';

import { waitForRestoreOutcome, type RestoreOutcome } from './auth';

const APPLIED: RestoreOutcome = {
    outcome: 'applied',
    finishedAtUtc: '2026-10-09T18:57:01Z',
    previousDatabase: 'coffer_pre_restore_20261009T185701',
    error: null,
};

const json = (status: number, body?: unknown) =>
    new Response(body === undefined ? null : JSON.stringify(body), { status });
const refused = () => Promise.reject(new TypeError('Failed to fetch'));

/** Feed fetch a scripted sequence of answers; the last one repeats. */
function answers(...steps: Array<() => Promise<Response>>) {
    let i = 0;
    return vi.spyOn(globalThis, 'fetch').mockImplementation(() => steps[Math.min(i++, steps.length - 1)]());
}

const fast = { intervalMs: 1, notFoundGraceMs: 20, timeoutMs: 2_000 };

describe('waitForRestoreOutcome', () => {
    afterEach(() => vi.restoreAllMocks());

    it('returns the outcome the boot recorded for this attempt', async () => {
        const fetchSpy = answers(refused, () => Promise.resolve(json(200, APPLIED)));
        await expect(waitForRestoreOutcome('attempt-1', fast)).resolves.toEqual(APPLIED);
        expect(String(fetchSpy.mock.calls[0][0])).toBe('/api/restore-outcome/attempt-1');
    });

    it('does not read a 404 from the exiting process, followed by downtime, as "nothing recorded"', async () => {
        // The old process can answer once before it stops; then the server is down
        // for as long as the restore takes; then the new one answers with the outcome.
        answers(
            () => Promise.resolve(json(404)),
            refused, refused, refused,
            () => Promise.resolve(json(200, APPLIED)),
        );
        await expect(waitForRestoreOutcome('attempt-1', { ...fast, notFoundGraceMs: 1 })).resolves.toEqual(APPLIED);
    });

    it('reports nothing recorded only after the server keeps saying so', async () => {
        answers(refused, () => Promise.resolve(json(404)));
        await expect(waitForRestoreOutcome('attempt-1', fast)).resolves.toBeNull();
    });

    it('says the server never came back, rather than inventing an outcome', async () => {
        answers(refused);
        await expect(waitForRestoreOutcome('attempt-1', { ...fast, timeoutMs: 30 })).rejects.toThrow(/did not come back/i);
    });
});
