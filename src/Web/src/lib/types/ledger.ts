// Ledger-level API response types.

import type { RateUnavailable } from './conversion';

/**
 * Mirror of API `Coffer.Api.Contracts.LedgerSummary`. The role is the
 * user's grant role on this ledger (`"owner"` today; the API may
 * add more granular roles later).
 */
export interface LedgerSummary {
    id: string;
    name: string;
    role: string;
    /** The ledger's reporting currency (ADR-0114 D7): what a figure that combines
     *  accounts converts into. Only an owner changes it (`updateLedger`). */
    currencyCode: string;
    /**
     * For a Demo whose examples were not all added, the names of those left out,
     * which the hub lists under its row (ADR-0115 D9). Null for every other ledger.
     * Only the list read (`fetchVisibleLedgers`) carries it.
     */
    demoExamplesMissing?: readonly string[] | null;
}

/**
 * Body of `PATCH /api/ledgers/{id}`: a rename, a reporting-currency change, or
 * both. Owner only. A field left out is left as it is; at least one is required.
 */
export interface UpdateLedgerRequest {
    name?: string;
    currencyCode?: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.ConsistencyMismatch`. The ids are carried
 * separately from `scope` so a repair targets exactly what was reported.
 */
export interface ConsistencyMismatch {
    scope: string;
    field: string;
    stored: number;
    expected: number;
    diff: number;
    accountId?: string | null;
    securityId?: string | null;
    headerId?: string | null;
    /** ISO date (`YYYY-MM-DD`) where the projection is per-(security, day). */
    priceDate?: string | null;
    /** What KIND of finding this is, where a projection has more than one: `value`
     *  (a stored row holds another figure), `missing` (the rule derives a figure and no
     *  row is stored), `extra` (a row is stored where the rule derives none: a running
     *  balance on a row the register skips, which the balances repair removes) or
     *  `unconfirmed` (`quote_currency`: a security with fetched prices whose currency
     *  Yahoo has not confirmed; its other kind, fetched prices in a currency other than
     *  the confirmed one, is `value`). Null where the projection has a single kind.
     *
     *  A `quote_currency` row carries 0 in `stored`, `expected` and `diff`; what
     *  differs is in `field`, in words. In a repair's `notRepaired` the reason is the
     *  code saying why the row was left: the Yahoo lookup's (`fetch-failed`,
     *  `rate-limited`, `currency-unknown`, …) or `quote-symbol-not-public`. */
    reason?: string | null;
    /** Whether the transaction `headerId` names is hidden (ADR-0072 D1): its register
     *  shows it only in the Hidden view, where its link opens. Set by `category_amounts`
     *  alone. */
    hidden?: boolean;
}

/** Mirror of API `Coffer.Api.Contracts.ProjectionConsistency`. */
export interface ProjectionConsistency {
    projection: string;
    healthy: boolean;
    checked: number;
    mismatchedCount: number;
    mismatches: ConsistencyMismatch[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.LedgerConsistencyReport`. Every projection
 * listed here has a repair, so the UI never reports a problem it cannot offer to
 * fix.
 */
export interface LedgerConsistencyReport {
    healthy: boolean;
    projections: ProjectionConsistency[];
    /** Advisory, not findings — see {@link UnbackedPriceAdvisory}. Takes no
     *  part in `healthy`. */
    unbackedPrices: UnbackedPriceAdvisory[];
    /** Findings with no repair — see {@link OversoldPosition}. Counted in
     *  `healthy`. */
    oversold: OversoldPosition[];
    /** Advisory, "Amounts with more decimals than their currency" — see
     *  {@link UnfitAmountAdvisory}. Takes no part in `healthy`. The server always sends
     *  it, empty when nothing is listed. */
    unfitAmounts?: UnfitAmountAdvisory[];
    /** Advisory, "Currencies Coffer no longer accepts" — see
     *  {@link RefusedCurrencyAdvisory}. Takes no part in `healthy`. The server always
     *  sends it, empty when nothing is listed. */
    refusedCurrencies?: RefusedCurrencyAdvisory[];
}

/** Where an {@link UnfitAmountAdvisory}'s figure lives, the place its link opens:
 *  the account's register at the transaction, the account's editor (opening balance),
 *  the loan's editor (loan terms), the budget at the month (target), the reminder.
 *  Mirror of API `UnfitAmountKinds`. */
export type UnfitAmountKind = 'transaction' | 'opening_balance' | 'loan_terms' | 'budget_target' | 'reminder';

/** Why a category line the Category amounts repair cannot rewrite is listed. Mirror of
 *  API `UnfitAmountReasons`: both lines are on categories; the posting has more than one
 *  real or category line. A reason names a shape, never signs or zeros: every posting of
 *  one real line and one category line is repaired, whatever its signs. */
export type UnfitAmountReason =
    | 'category-line-between-categories'
    | 'category-line-many-lines';

/**
 * Mirror of API `Coffer.Api.Contracts.UnfitAmountAdvisory` (ADR-0114 D15). One row of
 * the "Amounts with more decimals than their currency" advisory: where a stored figure
 * with more places than its currency allows lives, kept as it is, or a category line
 * the Category amounts repair cannot rewrite (`reason` set). The figure itself is not
 * sent. The line reads `account · date · payee` for a transaction,
 * `account · opening balance`, `loan · loan terms`, `category · month target` or
 * `reminder · reminder`.
 */
export interface UnfitAmountAdvisory {
    kind: UnfitAmountKind;
    /** The account the figure is on, which always exists: the register's account for a
     *  transaction (the real side, for a category line beside a real line; the category
     *  line's own category, for a posting between categories), the account, the loan,
     *  the target's category, or, for a reminder, the account of the template line that
     *  does not fit (never the series' source account, which can be unset). */
    accountId: string;
    account: string;
    /** The currency the figure is held to, that account's: the real side's for a
     *  category line beside a real line, the category's own for a posting between
     *  categories, the template line's account's for a reminder. */
    currencyCode: string;
    /** That currency's places. */
    places: number;
    headerId?: string | null;
    /** The transaction's posted date, `YYYY-MM-DD`. */
    date?: string | null;
    /** The transaction's or the reminder's payee. */
    payee?: string | null;
    /** The target's month, as its first day, `YYYY-MM-DD`. */
    month?: string | null;
    reminderId?: string | null;
    /** Null for a figure listed for its places. */
    reason?: UnfitAmountReason | null;
    /** A hidden transaction (ADR-0072 D1): its register shows it only in the Hidden
     *  view, where its link opens. The server always sends it, false for every kind but
     *  a transaction. */
    hidden?: boolean;
}

/** Whose currency a {@link RefusedCurrencyAdvisory} is. */
export type RefusedCurrencyKind = 'account' | 'reporting';

/**
 * Mirror of API `Coffer.Api.Contracts.RefusedCurrencyAdvisory` (ADR-0114 D15). One row of
 * the "Currencies Coffer no longer accepts" advisory: a stored code that is not an ISO
 * 4217 currency an account or a ledger can take. It keeps working at 2 places. The line
 * reads `account · code`, or "Reporting currency · code" for the ledger.
 */
export interface RefusedCurrencyAdvisory {
    kind: RefusedCurrencyKind;
    /** Null for the reporting currency. */
    accountId: string | null;
    account: string | null;
    currencyCode: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.ProjectionRepairResult`: what one projection's
 * repair rewrote (`repaired`, the check it worked from) and every projection checked
 * again once it had committed (`after`), the verdict the repair records. `after` is
 * null when that check did not finish: the rewrite stands and is recorded, and what
 * still disagrees is not known until the next check.
 */
export interface ProjectionRepairResult {
    projection: string;
    repaired: ProjectionConsistency;
    after: LedgerConsistencyReport | null;
    /** The rows of `repaired` the repair could not rewrite, each with `reason` the
     *  code saying why. Empty for every projection but `quote_currency`. */
    notRepaired: ConsistencyMismatch[];
}

/**
 * Mirror of API `LedgerConsistencyStateDto` — the last check's verdict as stored
 * (mig 242). `healthy` null means no current verdict (never checked, or after a
 * restore): "not checked yet", never unhealthy.
 */
export interface LedgerConsistencyState {
    lastCheckedAt: string | null;
    healthy: boolean | null;
    failingProjections: string[];
    lastRepairedAt: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.OversoldPosition` (FU-097). A position a
 * sale took more shares from than it held at the time: its gain is overstated by
 * those shares' proceeds. Nothing repairs it, because the missing purchase is not
 * in the data; the reader corrects the sale or enters the purchase.
 */
export interface OversoldPosition {
    /** The user-visible brokerage, whose register holds the sale. */
    accountId: string;
    account: string;
    securityId: string;
    security: string;
    sharesShort: number;
}

/**
 * Mirror of API `Coffer.Api.Contracts.UnbackedPriceAdvisory`. A security
 * holding prices that claim a trade produced them while no trade currently
 * does. Advisory: the price may still be right, and nothing records which
 * transaction wrote it, so it can only be inferred.
 */
export interface UnbackedPriceAdvisory {
    securityId: string;
    security: string;
    count: number;
    earliest: string;
    latest: string;
    /** What is held now, in the ledger's reporting currency: each position at its
     *  current price, or at its cost basis when it has none, converted at the latest
     *  rate and summed across the holding accounts (ADR-0114 D7); 0 once the position
     *  is sold, which sorts it last without hiding it. Null when a rate is missing,
     *  named in `rateUnavailable`; such a row sorts after every valued one. */
    holdingValue: number | null;
    /** The reporting currency `holdingValue` is in. */
    currencyCode: string;
    rateUnavailable: RateUnavailable[];
}
