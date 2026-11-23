// The hub's Demo ledger (ADR-0115 D2, D3): add one, and find its build again. A Demo
// is built as an ordinary background Moneydance import, so its status is an import
// job's status with what the hub shows beside it.

import type { ImportJob } from './import';
import { request } from './_request';
import { todayQuery } from './_today';

/** `import` until the import commits, then `examples` while the examples are added. */
export type DemoJobPhase = 'import' | 'examples';

/** A Demo build's status: the import job's fields, plus its name and readable step. */
export interface DemoJob extends ImportJob {
    /** The Demo's name, chosen by the server when the build started ("Demo", "Demo 2"…). */
    ledgerName: string;
    phase: DemoJobPhase;
    /** The step in progress, counted from 1, while running; `total` once finished. */
    stepNumber: number;
    /** The readable label of the step in progress while running; null once finished. */
    stepLabel: string | null;
}

/**
 * POST /api/ledgers/demo — start building a new Demo that ends on the caller's local
 * date (the register's `today`, so the Demo's last day is never Scheduled).
 *
 * Refusals: 422 `import-already-running` (another import is running, or the server
 * is reading another export), with no `ledgerName`; 422 `demo-date-in-future` (this
 * computer's clock is ahead of the server's by more than a day); 422
 * `import-parse-failed` (not enough memory to read it) and 500 (the Demo could not be
 * built), each carrying the chosen name as `ledgerName` on the ApiError.
 */
export function startDemoLedger(): Promise<DemoJob> {
    return request<DemoJob>(`/api/ledgers/demo${todayQuery()}`, { method: 'POST' });
}

/**
 * GET /api/ledgers/demo/job — the caller's latest Demo build, running or finished;
 * null when the server holds none (none started, another import since, or a restart).
 */
export async function fetchDemoJob(): Promise<DemoJob | null> {
    const job = await request<DemoJob | undefined>('/api/ledgers/demo/job');
    return job ?? null;
}
