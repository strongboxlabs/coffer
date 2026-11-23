// A ledger's benchmarks and exchange rates (ADR-0114 D1, D10).

import type {
    AddFxRateRequest,
    Benchmark,
    DerivedRate,
    FxRate,
    FxRatePage,
    FxRatesView,
} from '../types/marketSeries';
import { request } from './_request';

const base = (ledgerId: string) => `/api/ledgers/${encodeURIComponent(ledgerId)}`;

/** GET /api/ledgers/{ledgerId}/benchmarks — the ledger's benchmarks in order. Any member. */
export function fetchBenchmarks(ledgerId: string): Promise<Benchmark[]> {
    return request<Benchmark[]>(`${base(ledgerId)}/benchmarks`);
}

/**
 * POST /api/ledgers/{ledgerId}/benchmarks — add a fund or ETF symbol (trimmed and
 * upper-cased server-side). Owner or editor. 201 with the benchmark; its history is
 * fetched in the background.
 *
 * The check asks Yahoo, under the adding user's Yahoo opt-in. 422 codes, in the order
 * they are tried: `benchmark-symbol-invalid`, `benchmark-yahoo-off` (nothing sent),
 * `benchmark-already-added`, `benchmark-limit-reached` (three per ledger), then the
 * Yahoo check's own: `benchmark-is-index`, `benchmark-instrument-unsupported`,
 * `benchmark-no-adjusted-close`, `currency-missing`, `currency-unknown`,
 * `ticker-not-resolved`, `rate-limited`, `fetch-failed`, `parse-failed`, each with its
 * reason as the detail.
 */
export function addBenchmark(ledgerId: string, symbol: string): Promise<Benchmark> {
    return request<Benchmark>(`${base(ledgerId)}/benchmarks`, {
        method: 'POST',
        body: { symbol },
    });
}

/**
 * DELETE /api/ledgers/{ledgerId}/benchmarks/{benchmarkId} — 204. The rest close up;
 * the symbol's stored history is kept, so adding it back needs no fresh fetch, and a
 * saved report that picked it keeps its pick. 422 `benchmark-not-found`.
 */
export function removeBenchmark(ledgerId: string, benchmarkId: string): Promise<void> {
    return request<void>(`${base(ledgerId)}/benchmarks/${encodeURIComponent(benchmarkId)}`, {
        method: 'DELETE',
    });
}

/** GET /api/ledgers/{ledgerId}/fx-rates — the pairs the ledger needs and how far each
 *  is covered. Open to viewers. */
export function fetchFxRatesView(ledgerId: string): Promise<FxRatesView> {
    return request<FxRatesView>(`${base(ledgerId)}/fx-rates`);
}

/**
 * GET /api/ledgers/{ledgerId}/fx-rates/{from}/{to} — one pair's stored rates, newest
 * first. `before` (`yyyy-MM-dd`) continues from a page's `nextBefore`; `limit` is
 * 1..500, 100 by default. 422 `fx-rate-currency-invalid`, `fx-rate-date-invalid`.
 */
export function fetchFxRatePage(
    ledgerId: string,
    fromCurrency: string,
    toCurrency: string,
    { before, limit }: { before?: string; limit?: number } = {},
): Promise<FxRatePage> {
    const params = new URLSearchParams();
    if (before) params.set('before', before);
    if (limit !== undefined) params.set('limit', String(limit));
    const query = params.toString();
    return request<FxRatePage>(
        `${base(ledgerId)}/fx-rates/${encodeURIComponent(fromCurrency)}/${encodeURIComponent(toCurrency)}`
        + (query ? `?${query}` : ''),
    );
}

/**
 * POST /api/ledgers/{ledgerId}/fx-rates — type a rate for a day with none (ADR-0114
 * D10). Owner or editor. 201 with the stored rate, marked `manual`.
 *
 * 422 codes: `fx-rate-currency-invalid` (not a code, or from and to the same),
 * `fx-rate-date-invalid`, `fx-rate-date-in-future` (after the server's UTC today),
 * `fx-rate-invalid` (zero or less, or too large to store), `fx-pair-not-needed`,
 * `fx-rate-day-taken`.
 */
export function addFxRate(ledgerId: string, body: AddFxRateRequest): Promise<FxRate> {
    return request<FxRate>(`${base(ledgerId)}/fx-rates`, { method: 'POST', body });
}

/**
 * GET /api/ledgers/{ledgerId}/fx-rates/{from}/{to}/derived?date=yyyy-MM-dd&amount= — the
 * rate between two account currencies for `date`, derived through the reporting currency
 * (ADR-0114 D13, D14), and `amount` (in `from`) converted at it, rounded once at `to`'s
 * places. Open to viewers; it writes nothing. `amount` is left out when not given, and
 * the answer's `converted` is then null.
 *
 * 422 codes: `fx-rate-currency-invalid` (not a code, or from and to the same),
 * `fx-rate-date-invalid`. A missing rate is not a refusal: the answer names each
 * missing stored leg in `rateUnavailable`.
 */
export function fetchDerivedRate(
    ledgerId: string,
    fromCurrency: string,
    toCurrency: string,
    date: string,
    amount?: number,
): Promise<DerivedRate> {
    const params = new URLSearchParams({ date });
    if (amount !== undefined) params.set('amount', String(amount));
    return request<DerivedRate>(
        `${base(ledgerId)}/fx-rates/${encodeURIComponent(fromCurrency)}/${encodeURIComponent(toCurrency)}/derived`
        + `?${params.toString()}`,
    );
}
