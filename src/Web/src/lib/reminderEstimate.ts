import type { ReminderEstimate } from './types/reminder';

/**
 * Does this amount belong to a reminder series that estimates its amount (mig 220)?
 * Its `estimate` is present then, and wherever the amount is shown it is marked "≈",
 * whether or not an estimate could be made: with one, the amount IS that estimate;
 * without one, it is the amount entered, standing in until there is one. Either way
 * it is not a known figure. A fixed-amount series has no `estimate`, and a posted
 * occurrence carries none, its amount being what posted.
 *
 * Every surface that prints a reminder's amount asks this one question, so no
 * surface marks an amount that another leaves plain. Cash ahead's lowest reads the
 * forecast's own `estimated`, which the server sets by the same rule.
 */
export function isEstimating<T extends { estimate?: ReminderEstimate | null }>(
    of: T | null | undefined,
): of is T & { estimate: ReminderEstimate } {
    return of?.estimate != null;
}

/** What the "≈" says it stands for, as a title or an accessible name. */
export function estimateTitle(estimate: ReminderEstimate): string {
    if (estimate.amount === null) return 'Estimated: the amount entered is used until an estimate can be made';
    const n = estimate.availableSampleCount;
    return `Estimated from the last ${n} ${n === 1 ? 'occurrence' : 'occurrences'}`;
}
