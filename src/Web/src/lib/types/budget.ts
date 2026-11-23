// Spend-versus-typical, and the targets a person typed. Mirror of the API
// BudgetProgressDto.
//
// `typical` is the mean of the category's spend over the trailing complete
// months, or for a ledger younger than the window over only the months since its
// first transaction (`historyFrom`), derived on every read. It is deliberately not called `budget` — it
// describes history rather than a decision, so it moves when history moves (a
// backdated receipt, a late feed import, a category merge). The word has to
// carry that. `target` is the decided number; `mark` is whichever of the two
// the screen should judge the month against.
//
// Every figure is in the ledger's reporting currency (`currencyCode`), converted
// (ADR-0114 D7, D12): spend at each posting's date, a target typed in another
// currency at its month's last day, or for the current month at the latest rate. A
// figure that needs a missing rate is null and its `rateUnavailable` names each
// missing pair and day (ADR-0114 D9); the field is absent when nothing is missing.

import type { RateUnavailable } from './conversion';

export interface BudgetCategoryRow {
    categoryId: string;
    name: string;
    parentId: string | null;
    /** Spend in the reported month, positive magnitude; null when a rate is missing. */
    actual: number | null;
    /**
     * Mean spend over the trailing window, over only the months since the ledger's
     * first transaction when it is younger than the window (`historyFrom`), or null
     * when the category has no history in it. Null rather than 0 on purpose: a brand-new category
     * showing "typically 0" reads as infinitely over.
     */
    typical: number | null;
    /**
     * What a person typed for this category and month, or null when they have
     * not. Null is the ordinary case: ADR-0099 D1 makes PRESENCE of a stored
     * row the state, so most rows carry none and fall back to `typical`.
     */
    target: number | null;
    /**
     * The currency the stored target was typed in (ADR-0114 D12), or null when no
     * target is stored. This, not `target`, says a target is STORED: `target` is
     * null too when one is stored but needs a missing rate (ADR-0114 D9). Read it
     * through `hasStoredTarget`.
     */
    targetCurrencyCode: string | null;
    /**
     * The authoritative mark — where the bar's tick sits and what the hero's
     * ceiling sums. NEVER recompute this from the two fields above: per
     * ADR-0099 D1a a target and its ancestors are mutually exclusive, so an
     * untargeted row's mark is its own normal with each targeted DESCENDANT's
     * normal swapped for that descendant's number, which is a tree walk the
     * server has already done. On a row with nothing targeted beneath it,
     * `mark` and `typical` are identical — the common case.
     */
    mark: number | null;
    /**
     * Every missing rate a figure of this row needed: that figure is then null, and
     * this tells it apart from a `typical` that is null for having no history.
     * Absent when nothing is missing.
     */
    rateUnavailable?: RateUnavailable[];
}

/** One day's spend in one category. Sparse — no spend, no cell. */
export interface BudgetDailyCell {
    categoryId: string;
    /** Day of the month, 1-based. */
    day: number;
    /** Null, with `rateUnavailable`, when a posting of the day needs a missing rate. */
    amount: number | null;
    rateUnavailable?: RateUnavailable[];
}

export interface BudgetProgress {
    /** The reported month, `yyyy-MM`. */
    month: string;
    /** The trailing window requested, in complete months, and always that length: it
     *  does not shrink for a ledger younger than the window. `typical` averages all of
     *  it, or only the days since `historyFrom` when that is set; `usualSpan` turns the
     *  two into a label. */
    windowMonths: number;
    rows: BudgetCategoryRow[];
    actualTotal: number | null;
    typicalTotal: number | null;
    /** The ledger's reporting currency; every figure here is in it, converted. */
    currencyCode: string;
    daysInMonth: number;
    /**
     * How much of the month has happened. The solid line stops here; drawing a
     * cumulative line to the month's end when half of it has happened reads as
     * spending collapsing to zero.
     */
    elapsedDays: number;
    /** This month, per category per day. Accumulate client-side. */
    dailyActual: BudgetDailyCell[];
    /**
     * Mean day-of-month profile across the trailing window, each day divided by the
     * window's months in which the ledger already existed on that day (the window's
     * length unless `historyFrom` is set). Accumulated, it is the
     * baseline the shaded band is drawn around and the projection reads. Keyed by
     * each window month's own day, so a day can be past this month's last one; the
     * baseline counts it on that last day (`usualByDay`).
     */
    dailyNormal: BudgetDailyCell[];
    /**
     * The most recent month that has spending, `yyyy-MM`, or null. Only
     * populated when the requested month has none — a ledger whose imports lag
     * would otherwise show a wall of zeroes with no clue the data is elsewhere.
     */
    latestMonthWithSpending: string | null;
    /**
     * The part of `actualTotal` dated after the user's today: rows the register
     * shows as Scheduled. The month's spend counts them (ADR-0109 D10), so a screen
     * says "incl. this much scheduled", and a projection never falls below them,
     * each counted on its own date. Zero for a past month; null when a rate is
     * missing.
     */
    scheduledTotal: number | null;
    /**
     * The day of the ledger's first transaction, `yyyy-MM-dd`, when it falls after
     * the window's first day; null when the whole window is averaged. The window's
     * months before it are not averaged (each day of the month divides by the months
     * the ledger already existed on that day), so a label naming the window says
     * what was averaged instead (`usualSpan`).
     */
    historyFrom: string | null;
    /** Every missing rate a total here needed: that total is null. Absent when
     *  nothing is missing. */
    rateUnavailable?: RateUnavailable[];
}

/** One transaction line behind a budget row. Mirror of the API TransactionLine. */
export interface BudgetTransactionLine {
    headerId: string;
    postedAt: string;
    payee: string | null;
    accountId: string;
    accountName: string;
    counterpartyAccountId: string | null;
    /** The category this line was filed under. Full path from the server. */
    counterpartyAccountName: string | null;
    amount: number;
    memo: string | null;
    status: string;
    /** The line's own leg: unique per line, where a split's lines share the header
     *  and the account, so it is what names a line. */
    legId: string;
    /** The currency `amount` is in: the paying account's, since a category holds
     *  no money of its own. Lines in different currencies do not sum. */
    currencyCode: string;
}

export interface BudgetTransactionPage {
    lines: BudgetTransactionLine[];
    limit: number;
    offset: number;
    hasMore: boolean;
}
