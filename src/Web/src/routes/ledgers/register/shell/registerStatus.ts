import type { RegisterRowBase } from '@/lib/types/register';
import type { SelectionStatusFilter } from '@/lib/types/selection';

/**
 * Shared register row-status derivation (ADR-0030 reuse).
 *
 * These helpers were previously bank-only (they lived in
 * `bank/columns.ts`), but the status vocabulary + the future-dated
 * "scheduled" rule are register-wide concepts: the investment
 * register surfaces the same recon-status badge and the same
 * status-filter tabs. The helpers operate on `RegisterRowBase`
 * (the fields common to `BankRow` and `InvestmentRow` — `postedAt`,
 * `isPending`, `status`), so a single definition serves both pages
 * and the row renderers can't drift apart.
 *
 * Pure module (no React) so the row components can import it without
 * tripping the react-refresh "only export components" rule.
 */

export type StatusFilter =
    | 'all'
    | 'cleared'
    | 'uncleared'
    | 'reconciling'
    | 'scheduled'
    | 'needs_review'
    // ADR-0072 D1: the Hidden view. Unlike the other tabs (which
    // client-filter the visible payload), selecting this re-fetches the
    // register with `hidden=true`; the payload IS the hidden rows.
    | 'hidden';

/** The status views the register offers, in display order. Single source for
 *  the status dropdown's labels so they can't drift from the filter values. */
export const REGISTER_STATUS_VIEWS: ReadonlyArray<{ value: StatusFilter; label: string }> = [
    { value: 'all', label: 'All' },
    { value: 'cleared', label: 'Cleared' },
    { value: 'uncleared', label: 'Uncleared' },
    { value: 'reconciling', label: 'Reconciling' },
    { value: 'scheduled', label: 'Scheduled' },
    { value: 'needs_review', label: 'Needs review' },
    { value: 'hidden', label: 'Hidden' },
];

export type RowStatus =
    | 'cleared'
    | 'pending'
    | 'scheduled'
    | 'reconciling'
    | 'uncleared';

/**
 * Derive the row's display status from the row data + current date.
 *
 * Precedence: scheduled > pending > persisted reconciliation status.
 * A future-dated row reads as 'scheduled' regardless of its persisted
 * status (MD parity — you can't reconcile something that hasn't
 * posted yet). A pending row reads as 'pending' for the same reason.
 *
 * Persisted status comes from `txn.status` (migration 030 — the
 * normalized 3-state vocabulary uncleared / reconciling / cleared).
 */
export function resolveRowStatus(
    txn: RegisterRowBase,
    today: Date,
): RowStatus {
    if (isScheduled(txn, today)) return 'scheduled';
    if (txn.isPending) return 'pending';
    return txn.status;
}

/**
 * True iff this row's posted date is strictly after today's local
 * date. Date-only comparison — a transaction posted later today is
 * NOT scheduled, but one posted any time on a future calendar day is.
 */
export function isScheduled(txn: RegisterRowBase, today: Date): boolean {
    // Both sides as YYYY-MM-DD strings — posted_at as its UTC-anchored
    // calendar date (the date the user picked / saved), `today` as the
    // user's local calendar date. String compare avoids the date-
    // arithmetic landmines around DST + timezone offsets.
    const postedDate = txn.postedAt.slice(0, 10);
    const todayDate = `${today.getFullYear().toString().padStart(4, '0')}-${
        (today.getMonth() + 1).toString().padStart(2, '0')}-${
        today.getDate().toString().padStart(2, '0')}`;
    return postedDate > todayDate;
}

/** True when the row matches the active status filter. */
export function passesStatusFilter(
    txn: RegisterRowBase,
    filter: StatusFilter,
    today: Date,
): boolean {
    if (filter === 'all') return true;
    // The Hidden view fetches soft-hidden rows directly (hidden=true), so
    // the payload is already scoped; gate on the row flag defensively.
    if (filter === 'hidden') return txn.isHidden;
    // Needs-review is a separate dimension from the cleared/scheduled
    // status (it's the bank-feed review FLAG — migration 037, ADR-0031
    // Phase 3c — not a reconciliation state). A row passes this filter
    // iff it's awaiting Accept, independent of its resolved status.
    if (filter === 'needs_review') return txn.needsReview;
    const status = resolveRowStatus(txn, today);
    return status === filter;
}

/**
 * Map the UI status filter onto the server-side selection filter
 * (ADR-0024). The wire vocabulary (`SelectionStatusFilter`) now models
 * every UI tab — including `needs_review` (the bank-feed review flag,
 * which the bulk endpoint resolves via `txn_headers.needs_review`) — so
 * this is a direct pass-through. It stays as an explicit anti-corruption
 * boundary: if the UI ever adds a filter the wire can't express, the
 * mismatch surfaces here as a type error rather than a silently widened
 * select-all.
 */
export function toSelectionStatusFilter(
    filter: StatusFilter,
): SelectionStatusFilter {
    return filter;
}
