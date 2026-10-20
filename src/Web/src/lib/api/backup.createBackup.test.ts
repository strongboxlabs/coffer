import { afterEach, describe, expect, it, vi } from 'vitest';

import { createBackup } from './backup';

// A manual backup runs on the server, not in its request (FU-065). The request used to
// carry the whole backup and died with it when a proxy cancelled it at its timeout
// (Cloudflare's is 100 seconds). Now POST answers 202 with a run id at once, and
// `createBackup` polls the run until it has finished, so the panel keeps one promise that
// resolves with the backup or rejects with why it failed.

function json(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
    });
}

function problem(status: number, code: string, detail: string): Response {
    return new Response(JSON.stringify({ status, detail, code }), {
        status,
        headers: { 'Content-Type': 'application/problem+json' },
    });
}

const RUN = 'a6f0c2b1-1111-4222-8333-944445555666';
const BACKUP = {
    id: 'coffer-20261016T101500000Z-0a1b2c3d',
    sizeBytes: 23 * 1024 * 1024,
    createdAtUtc: '2026-10-16T10:15:00Z',
    pinned: false,
};

function run(state: 'running' | 'succeeded' | 'failed', extra: object = {}) {
    return { runId: RUN, state, startedAtUtc: '2026-10-16T10:14:00Z', finishedAtUtc: null, backup: null, error: null, ...extra };
}

/** No real waiting between polls. */
const noWait = () => Promise.resolve();

afterEach(() => vi.restoreAllMocks());

describe('createBackup', () => {
    it('starts the run, polls while it runs, and resolves with the backup', async () => {
        const fetchSpy = vi.spyOn(globalThis, 'fetch')
            .mockResolvedValueOnce(json({ runId: RUN }, 202))
            .mockResolvedValueOnce(json(run('running')))
            .mockResolvedValueOnce(json(run('running')))
            .mockResolvedValueOnce(json(run('succeeded', { backup: BACKUP })));

        const created = await createBackup(noWait);

        expect(created).toEqual(BACKUP);
        expect(String(fetchSpy.mock.calls[0][0])).toBe('/api/admin/backups');
        expect(fetchSpy.mock.calls[0][1]?.method).toBe('POST');
        expect(String(fetchSpy.mock.calls[1][0])).toBe(`/api/admin/backups/runs/${RUN}`);
        expect(fetchSpy).toHaveBeenCalledTimes(4);
    });

    it('rejects with the reason the server recorded when the run fails', async () => {
        vi.spyOn(globalThis, 'fetch')
            .mockResolvedValueOnce(json({ runId: RUN }, 202))
            .mockResolvedValueOnce(json(run('failed', {
                error: 'Backup failed: a database operation failed. Check the server logs.',
            })));

        await expect(createBackup(noWait)).rejects.toThrow(/database operation failed/);
    });

    it('says the server restarted when the run is no longer known', async () => {
        // The run lives in the server's memory; a restart mid-backup forgets it. "Not found"
        // must not read as "it failed" or "it worked" — it reads as "go and look".
        vi.spyOn(globalThis, 'fetch')
            .mockResolvedValueOnce(json({ runId: RUN }, 202))
            .mockResolvedValueOnce(new Response(null, { status: 404 }));

        await expect(createBackup(noWait)).rejects.toThrow(/restarted while the backup was running/);
    });

    it('passes a refusal to start straight through, without polling', async () => {
        const fetchSpy = vi.spyOn(globalThis, 'fetch')
            .mockResolvedValueOnce(problem(422, 'backup-in-progress', 'A backup is already running.'));

        await expect(createBackup(noWait)).rejects.toThrow(/already running/);
        expect(fetchSpy).toHaveBeenCalledTimes(1);
    });
});
