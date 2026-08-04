// Securities catalog API types (slice A3).

/**
 * Mirror of API `Coffer.Api.Contracts.SecuritySummaryDto` (slice A3).
 * One row of the Securities catalog: the security + the per-security
 * aggregates the table renders inline (total quantity, latest price).
 */
export interface SecuritySummary {
    id: string;
    ticker: string | null;
    cusip: string | null;
    name: string;
    assetClass: string | null;
    exchange: string | null;
    isActive: boolean;
    totalQuantity: number;
    latestPrice: number | null;
    /** ISO-8601 UTC date string; null when no price exists. */
    latestPriceAsOf: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SecurityDetailDto`. Full per-
 * security view for the Detail page hero + price history.
 */
export interface SecurityDetail {
    id: string;
    ticker: string | null;
    cusip: string | null;
    name: string;
    assetClass: string | null;
    exchange: string | null;
    isActive: boolean;
    totalQuantity: number;
    totalCostBasis: number;
    latestPrice: number | null;
    latestPriceAsOf: string | null;
    recentPrices: readonly SecurityPricePoint[];
    /** ADR-0054 D2: provider-symbol override (null → use ticker). */
    quoteSymbol: string | null;
    /** ADR-0054 D2: participates in automated price fetches. */
    autoPrice: boolean;
    /** ADR-0054 D2: is quoteSymbol a public ticker? false = feed-only. */
    quoteSymbolPublic: boolean;
    // Rich classification (ADR-0067).
    vehicleType: string | null;
    region: string | null;
    equitySize: string | null;
    equityStyle: string | null;
    fiDuration: string | null;
    fiCredit: string | null;
    taxCharacter: string | null;
    classificationSource: string | null;
    classificationConfidence: string | null;
}

export interface SecurityPricePoint {
    asOf: string;
    price: number;
    source: string | null;
}

/** Body of `POST /api/ledgers/{lid}/securities`. */
export interface CreateSecurityRequest {
    ticker?: string | null;
    cusip?: string | null;
    name: string;
    assetClass?: string | null;
    exchange?: string | null;
    /** ADR-0054 D2: provider-symbol override; null/empty → use ticker. */
    quoteSymbol?: string | null;
    /** ADR-0054 D2: auto-fetch prices (defaults true server-side). */
    autoPrice?: boolean;
    /** ADR-0054 D2: is quoteSymbol a public ticker (defaults true)? */
    quoteSymbolPublic?: boolean;
}

/** Body of `PATCH /api/ledgers/{lid}/securities/{sid}`. Every field is
 *  "leave alone" when omitted; empty-string clears the value (matches
 *  the override-style PATCH semantics elsewhere in the API). */
export interface PatchSecurityRequest {
    ticker?: string | null;
    cusip?: string | null;
    name?: string | null;
    assetClass?: string | null;
    exchange?: string | null;
    isActive?: boolean | null;
    /** ADR-0054 D2: null = leave alone; empty string = clear (→ use ticker). */
    quoteSymbol?: string | null;
    /** ADR-0054 D2: null = leave alone. */
    autoPrice?: boolean | null;
    /** ADR-0054 D2: null = leave alone. false requires a quote symbol. */
    quoteSymbolPublic?: boolean | null;
    // Rich classification (ADR-0067): omit = leave alone; '' clears (→ null).
    vehicleType?: string;
    region?: string;
    equitySize?: string;
    equityStyle?: string;
    fiDuration?: string;
    fiCredit?: string;
    taxCharacter?: string;
}

/** One multi-asset look-through sleeve (ADR-0067): percent (0-100) of the
 *  wrapper in an asset class + optional region. */
export interface SecurityComponent {
    assetClass: string;
    region: string | null;
    weight: number;
}

/** One row of `GET .../securities/{sid}/transactions`. */
export interface SecurityTransaction {
    headerId: string;
    accountId: string;
    accountName: string;
    postedAt: string;
    action: string | null;
    amount: number;
    quantity: number | null;
    unitPrice: number | null;
    payee: string | null;
}

export interface SecurityTransactionsPage {
    items: readonly SecurityTransaction[];
    cursorForOlder: string | null;
    /** Total count across all pages; same value on every page so the
     *  SPA can render "loaded / total" on the section badge. */
    totalCount: number;
}

/** One row of `GET .../securities/{sid}/prices`. Richer than
 *  `SecurityPricePoint` (the small embed on `SecurityDetail`) —
 *  carries the price id so the SPA can target edit / delete plus
 *  the full OHLC + volume band. */
export interface SecurityPriceRow {
    id: string;
    asOf: string;
    price: number;
    currencyCode: string;
    high: number | null;
    low: number | null;
    volume: number | null;
    /** Price origin: 'import' | 'fetch' | 'manual' | 'simplefin'. */
    source: string;
}

export interface SecurityPricesPage {
    items: readonly SecurityPriceRow[];
    cursorForOlder: string | null;
    totalCount: number;
}

/** Body of `POST .../securities/{sid}/prices`. */
export interface CreateSecurityPriceRequest {
    price: number;
    priceDate: string;
    currencyCode?: string | null;
    high?: number | null;
    low?: number | null;
    volume?: number | null;
}

/** Body of `PATCH .../securities/{sid}/prices/{priceId}`. */
export interface PatchSecurityPriceRequest {
    price?: number | null;
    priceDate?: string | null;
    currencyCode?: string | null;
    high?: number | null;
    low?: number | null;
    volume?: number | null;
}

/** Economic asset-class dropdown values — mirrors the DB CHECK in
 *  migration 150 (ADR-0067). Vehicle (etf / mutual_fund / …) is a
 *  separate dimension now (`vehicleType`), not an asset class. */
export const SECURITY_ASSET_CLASSES = [
    'equity',
    'fixed_income',
    'multi_asset',
    'cash',
    'real_assets',
    'alternative',
] as const;
