// Portfolio reports (ADR-0063): returns over a window and allocation at an
// instant. Mirrors of the API ReturnsResult and AllocationResult. A figure that
// combines brokerages is in the ledger's reporting currency, converted; a figure of
// one brokerage stays in its own (ADR-0114 D7). Each result names its `currencyCode`.
// A figure that needs a missing rate is null, never a partial sum, and
// `rateUnavailable` names every missing pair and day (ADR-0114 D9).

import type { RateUnavailable } from './conversion';

/**
 * Time- and money-weighted return over one window. Both rates are ANNUALIZED
 * fractions (0.1 = 10% a year); a null rate always carries its reason. The
 * time-weighted rate covers only the time the scope was invested, so its span
 * travels with it and is shown wherever the rate is.
 */
export interface ReturnsResult {
    scope: 'ledger' | 'account';
    startDate: string;
    endDate: string;
    startValue: number | null;
    /** Cash plus securities at the window's end: the overview's investments value
     *  when the window ends at the user's today. */
    endValue: number | null;
    netContributions: number | null;
    /**
     * The window's change in value that contributions and withdrawals do not
     * explain: price movement, realized or not, plus dividends and interest, less
     * fees and investment expenses. Not annualized, and for the whole scope:
     * exactly `endValue − startValue − netContributions`. Measured from the start
     * value rather than from cost, so it is neither the FIFO realized gain nor the
     * unrealized gain against cost basis.
     */
    gain: number | null;
    moneyWeightedReturn: number | null;
    moneyWeightedUnavailableReason: string | null;
    timeWeightedReturn: number | null;
    timeWeightedUnavailableReason: string | null;
    timeWeightedCoveredYears: number | null;
    timeWeightedCoveredDays: number | null;
    contributionsIn: number | null;
    contributionsOut: number | null;
    /** The reporting currency at ledger scope, the brokerage's own at account scope.
     *  Both rates run over values converted into it. */
    currencyCode: string;
    rateUnavailable: RateUnavailable[];
}

/**
 * One allocation bucket: a dimension value, its market value and share. The value is
 * in `currencyCode`: the reporting currency, except an `account` bucket, which stays in
 * its brokerage's. `percent` is always the share of the converted total.
 */
export interface AllocationRow {
    bucket: string;
    marketValue: number | null;
    percent: number | null;
    currencyCode: string;
    rateUnavailable: RateUnavailable[];
}

/** A multi-asset fund with no component weights: look-through cannot split it. In
 *  the allocation's reporting currency. */
export interface UndecomposedMultiAsset {
    securityId: string;
    ticker: string | null;
    name: string;
    marketValue: number | null;
    percentOfTotal: number | null;
}

/** `totalMarketValue` is securities only; `excludedBrokerageCash` is the cash beside. */
export interface AllocationResult {
    buckets: AllocationRow[];
    totalMarketValue: number | null;
    asOf: string;
    excludedBrokerageCash: number | null;
    undecomposedMultiAssets: UndecomposedMultiAsset[];
    /** The ledger's reporting currency. */
    currencyCode: string;
    rateUnavailable: RateUnavailable[];
}

/** The allocation dimensions, spelled as the API and the MCP tool spell them. */
export type AllocationDimension = 'asset_class' | 'region' | 'vehicle' | 'security' | 'account';
