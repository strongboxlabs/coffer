// Quote-provider family types (ADR-0033). Mirrors the API's
// QuoteRunOutcome record shape — the SPA only reads the
// orchestrator's response envelope today; per-entry quote rows
// are persisted server-side and surfaced via the existing
// /holdings endpoint.

/**
 * Mirror of API `Coffer.Api.Quotes.QuoteRunOutcome`. Response
 * shape from `POST /api/ledgers/{id}/quotes/refresh`.
 *
 * `pricesInserted` + `pricesUpdated` lets the SPA render a
 * concrete "Refreshed 3 prices · 2 unchanged" toast rather than
 * a generic "done." `securitiesUnresolved` surfaces tickers no
 * provider returned data for (yellow pill on per-position chip
 * is a future polish).
 */
export interface QuoteRunOutcome {
    providerKeys: readonly string[];
    pricesInserted: number;
    pricesUpdated: number;
    securitiesUnresolved: readonly string[];
    errors: readonly QuoteError[];
}

/**
 * Mirror of API `Coffer.Api.Quotes.QuoteError`. One per-security
 * (or per-batch) failure surface. `code` is a stable short
 * identifier the SPA may eventually switch on for class-level
 * styling.
 */
export interface QuoteError {
    securityId: string | null;
    ticker: string;
    code: string;
    message: string;
}
