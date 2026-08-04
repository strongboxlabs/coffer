import type { RegisterRow } from '@/lib/types';

// Split-row UI model — pure functions, no I/O.
//
// Per ADR-0019 + the register API (post-migration 018 + the entry-keyed
// repository), the server already groups legs of multi-split MD
// transactions into `RegisterEntry` values of `kind: 'group'`. This
// module's job is to translate the server's entry stream into a flat
// list of UI rows, with expand state controlling whether group entries
// reveal their legs inline.
//
// The client-side grouping logic that previously inferred groups by
// scanning rows is no longer needed — the server is the source of
// truth, and group boundaries never slice a page (ADR-0019 entry-keyed
// pagination).

/**
 * A row in the rendered register — either a single transaction, a
 * collapsed split parent (representing 2+ legs), or one of the legs
 * revealed when a parent is expanded.
 *
 * Generic over the register-row shape (`R`), defaulting to the full
 * `RegisterRow` union. A domain-scoped caller (the bank register is
 * account-scoped, so its window is homogeneous `BankRow`) instantiates
 * `DisplayRow<BankRow>` after narrowing its entries on `kind`, so the
 * downstream row components receive the narrowed type without a coercive
 * cast (ADR-0030 §2).
 */
export type DisplayRow<R extends RegisterRow = RegisterRow> =
    | { kind: 'txn'; txn: R }
    | {
          kind: 'split-parent';
          groupId: string;
          legs: readonly R[];
          expanded: boolean;
      }
    | { kind: 'split-leg'; leg: R };

/**
 * A register entry narrowed to a domain row shape `R` — the shared input to
 * {@link regroupTargetSplits} and {@link buildDisplayRows}. Mirrors the API
 * `RegisterEntryDto` union; a domain-scoped caller narrows its window to `R`
 * (e.g. `BankRow` / `InvestmentRow`) before piping it through.
 */
export type RegisterEntryOf<R extends RegisterRow = RegisterRow> =
    | { kind: 'txn'; txn: R; groupId: null; legs: null }
    | { kind: 'group'; txn: null; groupId: string; legs: R[] };

/**
 * Cluster a contiguous run of ADR-0036 TARGET-split entries into one `group`
 * entry, so the register renders them as an expandable split-parent (like an
 * originating split). Shared by BOTH registers (ADR-0080) — a target split is
 * an ordinary bank-shape split that happens to land on some, but not all, of
 * a header's accounts.
 *
 * A target split is keyed by LEG id (not header), so its postings arrive as
 * separate `txn` entries. This folds a contiguous run sharing
 * `(headerId, accountId)` with `1 < accountPostingsOnHeader <
 * headerTotalPostings` into one group; everything else — originating groups,
 * single-posting targets, ordinary txns — passes through untouched. The
 * collapsed parent's numbers come from the shared {@link groupAmount} /
 * {@link groupBalanceAfter} / {@link canonicalLeg} helpers, so there is no
 * bespoke client aggregation.
 *
 * Runs client-side because it is cross-page: leg-keyed target entries can
 * straddle a server page, and this operates on the loaded window. Legs are
 * sorted `leg_index` ASC so an expanded cluster reads in posting order.
 */
export function regroupTargetSplits<R extends RegisterRow = RegisterRow>(
    entries: readonly RegisterEntryOf<R>[],
): RegisterEntryOf<R>[] {
    const out: RegisterEntryOf<R>[] = [];
    let i = 0;
    while (i < entries.length) {
        const entry = entries[i]!;
        // Only single-txn entries can be target-split members; existing
        // groups + non-target txns pass straight through.
        if (entry.kind !== 'txn') {
            out.push(entry);
            i += 1;
            continue;
        }
        const txn = entry.txn;
        const isTargetSplitMember =
            txn.accountPostingsOnHeader > 1
            && txn.accountPostingsOnHeader < txn.headerTotalPostings;
        if (!isTargetSplitMember) {
            out.push(entry);
            i += 1;
            continue;
        }
        // Collect the contiguous run of txn entries sharing this
        // (headerId, accountId). A header's target legs share posted_at/seq
        // so they arrive adjacent — a contiguous scan captures the cluster.
        const legs: R[] = [txn];
        let j = i + 1;
        while (j < entries.length) {
            const next = entries[j]!;
            if (
                next.kind !== 'txn'
                || next.txn.headerId !== txn.headerId
                || next.txn.accountId !== txn.accountId
            ) {
                break;
            }
            legs.push(next.txn);
            j += 1;
        }
        // A lone target leg (count says >1 but only one landed contiguously)
        // stays flat — never emit a one-leg split-parent.
        if (legs.length <= 1) {
            out.push(entry);
            i += 1;
            continue;
        }
        legs.sort((a, b) => a.legIndex - b.legIndex);
        out.push({ kind: 'group', txn: null, groupId: txn.headerId, legs });
        i = j;
    }
    return out;
}

/**
 * Translate a server-grouped entry stream into a flat `DisplayRow[]`.
 * Group entries emit a `split-parent` row; when the group is in
 * `expandedGroups`, the legs follow immediately after in the leg-index
 * order the server returned them.
 *
 * Generic over the row shape so a domain-scoped caller can pass a
 * `RegisterEntry`-shaped stream already narrowed to its domain (e.g.
 * `BankRow`) and get back `DisplayRow<BankRow>[]`.
 */
export function buildDisplayRows<R extends RegisterRow = RegisterRow>(
    entries: readonly RegisterEntryOf<R>[],
    expandedGroups: ReadonlySet<string>,
): DisplayRow<R>[] {
    const out: DisplayRow<R>[] = [];
    for (const entry of entries) {
        if (entry.kind === 'txn') {
            out.push({ kind: 'txn', txn: entry.txn });
            continue;
        }
        const expanded = expandedGroups.has(entry.groupId);
        out.push({
            kind: 'split-parent',
            groupId: entry.groupId,
            legs: entry.legs,
            expanded,
        });
        if (expanded) {
            for (const leg of entry.legs) {
                out.push({ kind: 'split-leg', leg });
            }
        }
    }
    return out;
}

/**
 * Net cash effect of a split group on its account. Sourced from
 * `headerAccountNetAmount` (ADR-0034 mig 098) — server-computed once
 * per (header, account) and projected onto every leg, so reading
 * `legs[0]` is sufficient. Falls back to summing leg amounts only
 * during transient ingest states where the trigger hasn't yet
 * populated the balance row.
 */
export function groupAmount(legs: readonly RegisterRow[]): number {
    const stored = legs[0]?.headerAccountNetAmount;
    if (stored !== undefined && stored !== null) return stored;
    let sum = 0;
    for (const leg of legs) sum += leg.amount;
    return sum;
}

/**
 * The balance_after for a split group is the balance after the LAST leg
 * (highest leg_index) was applied, which is the running balance the
 * register cares about for that posted_at.
 */
export function groupBalanceAfter(
    legs: readonly RegisterRow[],
): number | null {
    let best: RegisterRow | undefined;
    for (const leg of legs) {
        if (best === undefined || leg.legIndex > best.legIndex) best = leg;
    }
    return best?.balanceAfter ?? null;
}

/**
 * A canonical leg used for fields that are identical across the group
 * (date, payee, check#, status) — the server returns legs sorted by
 * leg_index ASC, so this is just legs[0].
 */
export function canonicalLeg<R extends RegisterRow>(
    legs: readonly R[],
): R {
    return legs[0]!;
}
