// Portfolio / Holdings View API types (slice A1).

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

/** Aggregate totals across all positions plus the brokerage's cash side. */
export interface PortfolioSummaryDto {
    portfolioValue: number;
    costBasis: number;
    unrealizedGain: number;
    percentChange: number;
    cashBalance: number;
    total: number;
}

/**
 * One position in the investment account. `currentPrice` and the
 * derived `current*` fields are null when no `security_prices` row
 * exists for this security yet — manual-entry / pre-feed-integration
 * territory; the panel renders those positions with a dash placeholder
 * instead of a misleading $0.
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
}
