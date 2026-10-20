// Spend-versus-typical, and the targets a person typed. Mirror of the API
// BudgetProgressDto.
//
// `typical` is the mean of the category's spend over the trailing complete
// months, derived on every read. It is deliberately not called `budget` — it
// describes history rather than a decision, so it moves when history moves (a
// backdated receipt, a late feed import, a category merge). The word has to
// carry that. `target` is the decided number; `mark` is whichever of the two
// the screen should judge the month against.

export interface BudgetCategoryRow {
    categoryId: string;
    name: string;
    parentId: string | null;
    /** Spend in the reported month, positive magnitude. */
    actual: number;
    /**
     * Mean spend over the trailing window, or null when the category has no
     * history in it. Null rather than 0 on purpose: a brand-new category
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
     * The authoritative mark — where the bar's tick sits and what the hero's
     * ceiling sums. NEVER recompute this from the two fields above: per
     * ADR-0099 D1a a target and its ancestors are mutually exclusive, so an
     * untargeted row's mark is its own normal with each targeted DESCENDANT's
     * normal swapped for that descendant's number, which is a tree walk the
     * server has already done. On a row with nothing targeted beneath it,
     * `mark` and `typical` are identical — the common case.
     */
    mark: number | null;
}

/** One day's spend in one category. Sparse — no spend, no cell. */
export interface BudgetDailyCell {
    categoryId: string;
    /** Day of the month, 1-based. */
    day: number;
    amount: number;
}

export interface BudgetProgress {
    /** The reported month, `yyyy-MM`. */
    month: string;
    /** How many trailing complete months `typical` averages over. */
    windowMonths: number;
    rows: BudgetCategoryRow[];
    actualTotal: number;
    typicalTotal: number | null;
    currencyCode: string;
    /** Accounts span more than one currency — totals are summed without FX. */
    mixedCurrency: boolean;
    daysInMonth: number;
    /**
     * How much of the month has happened. The solid line stops here; drawing a
     * cumulative line to the month's end when half of it has happened reads as
     * spending collapsing to zero.
     */
    elapsedDays: number;
    /** This month, per category per day. Accumulate client-side. */
    dailyActual: BudgetDailyCell[];
    /** Mean day-of-month profile across the trailing window. */
    dailyNormal: BudgetDailyCell[];
    /**
     * The most recent month that has spending, `yyyy-MM`, or null. Only
     * populated when the requested month has none — a ledger whose imports lag
     * would otherwise show a wall of zeroes with no clue the data is elsewhere.
     */
    latestMonthWithSpending: string | null;
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
}

export interface BudgetTransactionPage {
    lines: BudgetTransactionLine[];
    limit: number;
    offset: number;
    hasMore: boolean;
}
