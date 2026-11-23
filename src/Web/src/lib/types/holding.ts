// Portfolio / Holdings View API types (slice A1).

import type { RateUnavailable } from './conversion';

/**
 * Mirror of API `Coffer.Api.Contracts.HoldingsViewDto`. Portfolio View
 * payload for one investment account (slice A1.a). The brokerage is
 * the user-visible account; positions live on its system-managed
 * Holdings sibling (ADR-0019) — the endpoint resolves that link
 * server-side so callers only pass the brokerage id.
 */
export interface HoldingsViewDto {
    accountId: string;
    accountName: string;
    currencyCode: string;
    summary: PortfolioSummaryDto;
    positions: PositionDto[];
}

/**
 * Aggregate totals across all positions plus the brokerage's cash side, all in
 * `HoldingsViewDto.currencyCode`: a position priced in another currency counts at its
 * `accountValue`, converted at the latest rate (ADR-0114 D7). A figure that needs a
 * missing rate is null, with every figure derived from it, and `rateUnavailable`
 * names each (ADR-0114 D9); the field is absent when nothing is missing.
 */
export interface PortfolioSummaryDto {
    portfolioValue: number | null;
    costBasis: number | null;
    unrealizedGain: number | null;
    percentChange: number | null;
    cashBalance: number | null;
    total: number | null;
    rateUnavailable?: RateUnavailable[];
}

/**
 * One position in the investment account. `currentPrice` and the
 * derived `current*` fields are null when no `security_prices` row
 * exists for this security yet — manual-entry / pre-feed-integration
 * territory; the panel renders those positions with a dash placeholder
 * instead of a misleading zero amount.
 *
 * `costBasis` and `costPerShare` are in the account's currency, as its trades are.
 * `currentPrice` and `currentValue` are in the price's currency: the account's,
 * unless `priceCurrencyCode` names another. Such a position also carries
 * `accountValue`, its value converted into the account's currency at the latest rate
 * (ADR-0114 D7), and its `unrealizedGain` and `percentChange` compare that with the
 * cost basis. When the rate is missing those three are null and `rateUnavailable`
 * names it. The three added fields are absent for a position priced in the account's
 * currency.
 */
export interface PositionDto {
    securityId: string;
    ticker: string | null;
    name: string;
    assetClass: string | null;
    quantity: number;
    costBasis: number;
    costPerShare: number;
    currentPrice: number | null;
    priceAsOf: string | null;
    currentValue: number | null;
    unrealizedGain: number | null;
    percentChange: number | null;
    priceCurrencyCode?: string;
    accountValue?: number | null;
    rateUnavailable?: RateUnavailable[];
}
