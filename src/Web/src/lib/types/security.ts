// Securities catalog API types (slice A3).

import type { RateUnavailable } from './conversion';

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
    /** The currency `latestPrice` is in, and so the amount beside it (the price
     *  times `totalQuantity`), by the detail's rule (`SecurityDetail.priceCurrencyCode`). */
    priceCurrencyCode: string;
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
    /** FIFO cost basis of what is held, across the ledger, in `costBasisCurrencyCode`:
     *  a position kept in another currency converts lot by lot at each lot's
     *  acquisition date (ADR-0114 D7). Null when a rate is missing, named in
     *  `costBasisRateUnavailable` (ADR-0114 D9). */
    totalCostBasis: number | null;
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
    /** The currency `latestPrice` is in: the close it was read from; for a last
     *  trade's price, the security's newest price row's; with no price row,
     *  `holdingsCurrencyCode`. Only the price is in it: the totals below combine the
     *  holding accounts, so they are in the reporting currency (ADR-0114 D7). */
    priceCurrencyCode: string;
    /** The currency of the accounts that hold the security that day: the one they
     *  share, or the ordinal first when they span several; the ledger's reporting
     *  currency when none holds it. Only the fallback for `priceCurrencyCode`: no
     *  figure here sums in it. */
    holdingsCurrencyCode: string;
    /** The currency `totalCostBasis` is in: the ledger's reporting currency, because
     *  the basis combines every account holding the security. */
    costBasisCurrencyCode: string;
    /** Why `totalCostBasis` is null: each missing pair and day. Empty when valued. */
    costBasisRateUnavailable: RateUnavailable[];
    /** What is held at `latestPrice`, across the ledger, in `marketValueCurrencyCode`
     *  (ADR-0114 D7): each position from the price's currency into its brokerage's,
     *  then into the reporting currency, at the latest rate, rounded once by the server.
     *  Null with no current price (and no reason), or when a rate is missing, named in
     *  `marketValueRateUnavailable` (ADR-0114 D9). */
    marketValue: number | null;
    /** The ledger's reporting currency: the value combines the holding accounts. */
    marketValueCurrencyCode: string;
    /** Why `marketValue` is null when a rate is missing. Empty otherwise. */
    marketValueRateUnavailable: RateUnavailable[];
    /** `marketValue` less `totalCostBasis`, in `unrealizedGainCurrencyCode`; null when
     *  either is. */
    unrealizedGain: number | null;
    /** The ledger's reporting currency, as both terms of the gain are. */
    unrealizedGainCurrencyCode: string;
    /** Why `unrealizedGain` is null when a rate is missing: the value's and the basis's
     *  missing rates. Empty otherwise. */
    unrealizedGainRateUnavailable: RateUnavailable[];
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
    /** The currency of the account the row names: `amount` and `unitPrice` are in it.
     *  A closed brokerage's trades carry its own (ADR-0109 D9). */
    currencyCode: string;
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
    /** Price origin: 'import' | 'fetch' | 'manual' | 'simplefin' | 'trade'. */
    source: string;
    /** The row says a trade produced it and none currently does. Advisory: it
     *  may still be correct — a price left behind by a deleted trade is kept on
     *  purpose. Editing the row makes it `manual` and clears this. */
    unbacked: boolean;
}

export interface SecurityPricesPage {
    items: readonly SecurityPriceRow[];
    cursorForOlder: string | null;
    totalCount: number;
    /** The currency a new hand-entered price takes when none is given, for the editor
     *  to prefill (ADR-0114 D5): the latest price's currency, else the one currency
     *  every account holding the security is in. Null when neither answers. */
    defaultCurrencyCode: string | null;
}

/** Body of `POST .../securities/{sid}/prices`. */
export interface CreateSecurityPriceRequest {
    price: number;
    priceDate: string;
    /** ISO 4217 (ADR-0114 D5). Omitted or blank, the security's default
     *  (`SecurityPricesPage.defaultCurrencyCode`), else 422
     *  `security-price-currency-required`; not three characters once trimmed, 422
     *  `security-price-currency-invalid`. */
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
