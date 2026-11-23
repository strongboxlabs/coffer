import type { DemoJob } from '@/lib/api';
import type { ImportJobState } from '@/lib/api/import';
import type { LedgerSummary } from '@/lib/types';

/**
 * What the hub shows for a Demo build (ADR-0115 D9): preparing while the job runs,
 * failed when it failed (or the start was refused after the Demo was named), and
 * interrupted when the server lost a build this tab saw running. A succeeded build
 * shows no row of its own: it is an ordinary ledger row in the list.
 */
export type DemoRow =
    | { kind: 'preparing'; key: string; name: string; stepNumber: number; total: number; stepLabel: string | null }
    | { kind: 'failed'; key: string; name: string; message: string }
    | { kind: 'interrupted'; key: string; name: string; message: string };

/**
 * The last Demo job this tab saw, kept for the browser session so a build the server
 * forgot (a restart) can be named after the hub was left and opened again.
 * `ledgerIdsAtStart` tells a ledger the build made from one that was already there.
 */
export interface DemoBuildNote {
    jobId: string;
    ledgerName: string;
    ledgerIdsAtStart: readonly string[];
    lastState: ImportJobState;
}

export const DEMO_NOTE_KEY = 'coffer.demoJob';

/** The failed row's sentence: for a start refused after the Demo was named, and for a
 *  failed job the server sent no error text with. */
export function demoFailedText(name: string): string {
    return `Building ${name} failed and nothing was kept. Try again, or check the server logs.`;
}

/** The interrupted row's sentence: an interrupted build kept nothing, because its import
 *  never committed. */
export function demoInterruptedText(name: string): string {
    return `Building ${name} stopped because the server restarted. Nothing was kept.`;
}

/** The examples-incomplete line, naming the examples a Demo is missing. */
export function demoExamplesMissingText(names: readonly string[]): string {
    return `The history is complete, but some examples could not be added: ${names.join(', ')}.`;
}

/** The step line of a preparing row, "Step N of M — label". */
export function demoStepText(stepNumber: number, total: number, stepLabel: string | null): string {
    return stepLabel ? `Step ${stepNumber} of ${total} — ${stepLabel}` : `Step ${stepNumber} of ${total}`;
}

/** The preparing row's bar width, in whole percent of the steps reached. */
export function demoStepPercent(stepNumber: number, total: number): number {
    return total > 0 ? Math.round((stepNumber / total) * 100) : 0;
}

/** Ledger names compare as the server numbers them: trimmed, ignoring case. */
function sameName(a: string, b: string): boolean {
    return a.trim().toLowerCase() === b.trim().toLowerCase();
}

/**
 * A build this tab saw running that the server no longer holds: it answers with no
 * Demo job, or with another one.
 */
export function isLostBuild(note: DemoBuildNote | null, job: DemoJob | null): note is DemoBuildNote {
    return note !== null && note.lastState === 'running' && (job === null || job.jobId !== note.jobId);
}

/**
 * Whether a lost build's ledger is in the list after all: a ledger with its name that
 * was not there when it started. Then the build finished while the hub was not
 * looking, and nothing was interrupted.
 */
export function lostBuildArrived(note: DemoBuildNote, ledgers: readonly LedgerSummary[]): boolean {
    return ledgers.some((l) => sameName(l.name, note.ledgerName) && !note.ledgerIdsAtStart.includes(l.id));
}

function isNote(value: unknown): value is DemoBuildNote {
    if (typeof value !== 'object' || value === null) return false;
    const v = value as Record<string, unknown>;
    return typeof v.jobId === 'string'
        && typeof v.ledgerName === 'string'
        && Array.isArray(v.ledgerIdsAtStart) && v.ledgerIdsAtStart.every((id) => typeof id === 'string')
        && (v.lastState === 'running' || v.lastState === 'succeeded' || v.lastState === 'failed');
}

/** The stored note, or null when there is none, it is unreadable, or storage is unavailable. */
export function readDemoNote(): DemoBuildNote | null {
    try {
        const raw = sessionStorage.getItem(DEMO_NOTE_KEY);
        if (raw === null) return null;
        const parsed: unknown = JSON.parse(raw);
        return isNote(parsed) ? parsed : null;
    } catch {
        return null;
    }
}

/** Store the note, or forget it with null. Best effort: without storage the hub
 *  remembers the build only while it stays open. */
export function writeDemoNote(note: DemoBuildNote | null): void {
    try {
        if (note === null) sessionStorage.removeItem(DEMO_NOTE_KEY);
        else sessionStorage.setItem(DEMO_NOTE_KEY, JSON.stringify(note));
    } catch {
        // storage unavailable — the in-memory note still holds while the hub is open
    }
}
