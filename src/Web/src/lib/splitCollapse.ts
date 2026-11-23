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
// No client-side inference of groups by scanning rows — the server is
// the source of truth, and group boundaries never slice a page
// (ADR-0019 entry-keyed pagination). Only the account that owns a split
// gets a group; on a line's other side each line is its own row, and the
// client never clusters them back together.

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
 * Does this rendered row carry the given header?
 *
 * Shared because focus is resolved by header ID rather than by index: the
 * windowed-register hook counts ENTRIES while a register renders ROWS (after
 * filtering by status and expanding groups), and the
 * two counts are not equal on either register. Resolving by id removes the
 * chance of comparing one space against the other.
 */
export function rowHasHeader<R extends RegisterRow>(
    row: DisplayRow<R>,
    headerId: string,
): boolean {
    if (row.kind === 'txn') return row.txn.headerId === headerId;
    if (row.kind === 'split-leg') return row.leg.headerId === headerId;
    return row.legs.some((l) => l.headerId === headerId);
}

/**
 * A register entry narrowed to a domain row shape `R` — the input to
 * {@link buildDisplayRows}. Mirrors the API
 * `RegisterEntryDto` union; a domain-scoped caller narrows its window to `R`
 * (e.g. `BankRow` / `InvestmentRow`) before piping it through.
 */
export type RegisterEntryOf<R extends RegisterRow = RegisterRow> =
    | { kind: 'txn'; txn: R; groupId: null; legs: null }
    | { kind: 'group'; txn: null; groupId: string; legs: R[] };

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
 * The one currency a split group's lines are in, or null when they are in more than
 * one. On a real account every line is in the account's currency. A category holds no
 * money of its own, so each of its lines is in the currency of the account that paid
 * it, and a category's split paid from accounts in different currencies has no single
 * currency (ADR-0114 D7).
 */
export function groupCurrency(legs: readonly RegisterRow[]): string | null {
    const first = legs[0]?.currencyCode;
    if (first === undefined) return null;
    return legs.every((leg) => leg.currencyCode === first) ? first : null;
}

/**
 * Net cash effect of a split group on its account, in {@link groupCurrency}. Sourced
 * from `headerAccountNetAmount` (ADR-0034 mig 098) — server-computed once
 * per (header, account) and projected onto every leg, so reading
 * `legs[0]` is sufficient. Falls back to summing leg amounts only
 * during transient ingest states where the trigger hasn't yet
 * populated the balance row.
 *
 * Null when the lines are in more than one currency: no figure adds currencies, and
 * the stored net adds the lines as they are (ADR-0114 D7). The caller shows the mixed
 * marker in place of a figure.
 */
export function groupAmount(legs: readonly RegisterRow[]): number | null {
    if (groupCurrency(legs) === null) return null;
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
    return lastLeg(legs)?.balanceAfter ?? null;
}

/**
 * The missing rates a null group balance needed (ADR-0114 D9): the LAST leg's, as
 * {@link groupBalanceAfter} reads that leg's balance. Undefined when none is missing.
 */
export function groupBalanceRateUnavailable(
    legs: readonly RegisterRow[],
): RegisterRow['balanceRateUnavailable'] {
    return lastLeg(legs)?.balanceRateUnavailable;
}

function lastLeg(legs: readonly RegisterRow[]): RegisterRow | undefined {
    let best: RegisterRow | undefined;
    for (const leg of legs) {
        if (best === undefined || leg.legIndex > best.legIndex) best = leg;
    }
    return best;
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
