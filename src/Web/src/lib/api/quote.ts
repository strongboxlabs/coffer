// Quote-provider family endpoints (ADR-0033). Mirrors the
// per-family parallel structure — separate API client file per
// provider family.

import type { QuoteRunOutcome } from '../types/quote';
import { request } from './_request';

/**
 * POST /api/ledgers/{ledgerId}/quotes/refresh — fan out to every
 * registered pull-capable quote provider for the ledger. Today:
 * just SimpleFinHoldingsQuoteProvider (extracts from the
 * SimpleFIN ingest orchestrator's stored raw payloads — no
 * external HTTP, no rate limits). When Yahoo / other pull
 * providers ship they slot in via DI; this endpoint doesn't
 * change.
 *
 * Returns a typed outcome the SPA can render in a "Refresh
 * complete" toast (per-provider counts, unresolved securities,
 * per-security errors).
 *
 * 422 codes:
 *   * `ledger-not-visible` — caller has no grant on the ledger.
 */
export function refreshQuotes(ledgerId: string): Promise<QuoteRunOutcome> {
    return request<QuoteRunOutcome>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/quotes/refresh`,
        { method: 'POST' },
    );
}
