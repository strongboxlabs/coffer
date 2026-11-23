// Ledger overview aggregate (ADR-0056 slice 1). Mirror of the API
// LedgerOverviewDto — the home screen's balance-derived figures.
//
// Every figure that combines accounts is in the ledger's reporting currency
// (`currencyCode`), converted (ADR-0114 D7). A figure that needs a missing rate is
// null and `rateUnavailable` names each missing pair and day (ADR-0114 D9). Each
// account row stays in its own currency.

import type { RateUnavailable } from './conversion';

export interface OverviewAccount {
    id: string;
    name: string;
    /** bank | cash | credit_card | investment | asset | liability | loan */
    accountType: string;
    /** The currency `balance` is in: the account's own, never converted. */
    currencyCode: string;
    /**
     * Current balance; liabilities are negative. Null when a position priced in
     * another currency needs a missing rate, named in `rateUnavailable`.
     */
    balance: number | null;
    /** False for a closed account, which still counts in net worth (ADR-0085). */
    isActive: boolean;
    rateUnavailable: RateUnavailable[];
}

export interface OverviewAccountGroup {
    accountType: string;
    /**
     * The type's accounts converted into the ledger's reporting currency
     * (`LedgerOverview.currencyCode`) and summed; null when a rate is missing.
     */
    subtotal: number | null;
    accounts: OverviewAccount[];
    rateUnavailable: RateUnavailable[];
}

export interface PortfolioRollup {
    /** Holdings only. */
    value: number | null;
    /** Converted lot by lot at each lot's acquisition date. */
    costBasis: number | null;
    unrealizedGain: number | null;
    percentChange: number | null;
    /** The brokerages' cash beside the holdings: `investmentsValue − value`. */
    cashBalance: number | null;
    /** The ledger's reporting currency; every investment figure is in it. */
    currencyCode: string;
    rateUnavailable: RateUnavailable[];
}

/**
 * Net worth at the end of `since`, a month-end before today. `noHistory` is set,
 * with both figures null, when `since` precedes the ledger's first dated fact.
 * Both figures are also null when `since`'s rate is missing, named in
 * `rateUnavailable`.
 */
export interface OverviewComparison {
    since: string;
    netWorth: { then: number | null; change: number | null };
    noHistory: boolean;
    rateUnavailable: RateUnavailable[];
}

export interface LedgerOverview {
    netWorth: number | null;
    totalAssets: number | null;
    totalLiabilities: number | null;
    investmentsValue: number | null;
    /** The ledger's reporting currency (ADR-0114 D7). */
    currencyCode: string;
    accountGroups: OverviewAccountGroup[];
    portfolio: PortfolioRollup;
    /** Every missing rate a total above needed. */
    rateUnavailable: RateUnavailable[];
    /** Present only when the request named a `since` month-end. */
    comparison?: OverviewComparison | null;
}

/** One point of the net-worth series: net worth at the end of a period. */
export interface NetWorthHistoryPoint {
    asOf: string;
    /** Converted at the point's own day's rate; null when one is missing. */
    netWorth: number | null;
    /** Held positions with no price at all that day, valued at cost. */
    unpricedSecurityCount: number;
    /** The point precedes the ledger's first dated fact: not a net worth. */
    noHistory: boolean;
    rateUnavailable: RateUnavailable[];
}

export interface NetWorthHistory {
    fromUtc: string;
    toUtc: string;
    interval: string;
    /** The ledger's reporting currency. */
    currencyCode: string;
    points: NetWorthHistoryPoint[];
}
