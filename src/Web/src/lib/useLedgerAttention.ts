import { useQuery } from '@tanstack/react-query';

import { fetchLedgerAttention } from '@/lib/api';
import type { LedgerAttention } from '@/lib/types/notification';

/**
 * Which of my ledgers needs me (FU-049) — the one source behind every navigation
 * indicator.
 *
 * Its own module rather than living beside `AttentionDot`, for the reason
 * `settingsTabs.ts` gives: a file that exports both components and non-components
 * breaks react-refresh's boundary, and the rail is not a thing to lose hot reload on.
 */

/** Severities that reach an indicator. Anything lower is not a problem. */
export type AttentionSeverity = 'warning' | 'critical';

/**
 * Narrow the wire's open `string` to the two values an indicator can draw.
 *
 * The DTO is a string because severity is a database TEXT column with a CHECK, and
 * mirroring it as a union in TypeScript would be a second place to edit that the
 * compiler cannot keep honest. Anything unexpected renders as nothing at all, which is
 * the safe direction: a new severity nobody has taught the UI about should not paint a
 * dot in whichever colour the fallback happens to be.
 */
function toSeverity(value: string | undefined): AttentionSeverity | null {
    return value === 'critical' || value === 'warning' ? value : null;
}

export interface LedgerAttentionView {
    /** Per ledger, for the surfaces that name one. */
    byLedger: Map<string, LedgerAttention>;
    /** The aggregate behind the Coffer wordmark: critical anywhere outranks warning. */
    worst: AttentionSeverity | null;
    isPending: boolean;
    /** The severity to draw for one ledger, already narrowed. */
    severityFor: (ledgerId: string | undefined) => AttentionSeverity | null;
    /** How many distinct subjects are outstanding on one ledger. */
    countFor: (ledgerId: string | undefined) => number | undefined;
}

/**
 * One request, however many indicators are on screen.
 *
 * Every consumer shares this query key, so React Query serves four surfaces from a
 * single fetch. The alternative the follow-up warns about — one fetch per ledger —
 * would put N requests behind every page load, because the navigation renders on every
 * page.
 */
export function useLedgerAttention(): LedgerAttentionView {
    const query = useQuery({
        queryKey: ['ledger-attention'],
        queryFn: fetchLedgerAttention,
        // A standing problem does not change minute to minute, and this rides along
        // with every navigation. Refetching on focus is what keeps it honest after a
        // repair, without polling for one.
        staleTime: 60_000,
    });

    const rows: LedgerAttention[] = query.data ?? [];
    const byLedger = new Map(rows.map((r) => [r.ledgerId, r]));

    // Computed here rather than at each call site so the rail and the rows cannot
    // disagree about which severity is worse.
    const worst: AttentionSeverity | null = rows.some((r) => r.severity === 'critical')
        ? 'critical'
        : rows.some((r) => r.severity === 'warning')
          ? 'warning'
          : null;

    return {
        byLedger,
        worst,
        isPending: query.isPending,
        severityFor: (ledgerId) =>
            toSeverity(ledgerId ? byLedger.get(ledgerId)?.severity : undefined),
        // Paired with severityFor: a count is only ever drawn beside a dot, so a count
        // for a severity the UI will not draw would be a number with nothing to
        // qualify it.
        countFor: (ledgerId) =>
            ledgerId && toSeverity(byLedger.get(ledgerId)?.severity) !== null
                ? byLedger.get(ledgerId)?.count
                : undefined,
    };
}
