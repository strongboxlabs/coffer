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
    /** Securities whose currency a Yahoo read confirmed for the first time this run
     *  (ADR-0114 D5). */
    currenciesConfirmed: number;
    /** Fetched price rows re-stamped to the currency Yahoo named. */
    rowsRestamped: number;
    /** Exchange-rate days stored this run (ADR-0114 D11), new days only. */
    ratesInserted: number;
    /** Benchmark growth days stored this run, new days only. */
    benchmarkDaysInserted: number;
}

/**
 * Mirror of API `Coffer.Api.Quotes.QuoteError`. One per-security
 * (or per-batch) failure surface. `code` is a stable short
 * identifier the SPA may eventually switch on for class-level
 * styling.
 *
 * A market-series error (ADR-0114 D11) has `securityId` null and `ticker` the
 * benchmark symbol or the pair as `FROM/TO`; its codes include `currency-missing`,
 * `currency-unknown`, `currency-invalid`, `market-series-exception`,
 * `ledger-write-busy`, `benchmark-currency-changed`, `fx-fetch-failed`,
 * `fx-parse-failed`, `fx-base-url-invalid` and the Yahoo codes.
 */
export interface QuoteError {
    securityId: string | null;
    ticker: string;
    code: string;
    message: string;
    /** The source that raised it: `simplefin-holdings`, `yahoo` or `market-series`. */
    providerKey: string | null;
    /** The feed's id for the account a SimpleFIN holdings error is about. */
    feedAccountId: string | null;
}
