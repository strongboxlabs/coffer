// Securities catalog endpoints (slice A3).

import type {
    SecuritySummary,
    SecurityDetail,
    CreateSecurityRequest,
    PatchSecurityRequest,
    SecurityTransactionsPage,
    SecurityPricesPage,
    CreateSecurityPriceRequest,
    PatchSecurityPriceRequest,
    SecurityComponent,
} from '../types/security';
import { request } from './_request';

/**
 * GET /api/ledgers/{ledgerId}/securities — full catalog row list,
 * optionally filtered by case-insensitive substring search on
 * ticker / cusip / name.
 *
 * 422 codes: `ledger-not-visible`.
 */
export function fetchSecurities(
    ledgerId: string,
    search?: string,
): Promise<SecuritySummary[]> {
    const qs = search && search.trim().length > 0
        ? `?q=${encodeURIComponent(search.trim())}`
        : '';
    return request<SecuritySummary[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities${qs}`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/securities/{securityId} — hero data +
 * most-recent 10 price points.
 *
 * 422 codes: `ledger-not-visible`, `security-not-in-ledger`.
 */
export function fetchSecurity(
    ledgerId: string,
    securityId: string,
): Promise<SecurityDetail> {
    return request<SecurityDetail>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}`,
    );
}

/**
 * POST /api/ledgers/{ledgerId}/securities — create a new security.
 * Returns `{ securityId }`.
 *
 * 422 codes: `ledger-not-visible`, `security-name-required`,
 * `security-asset-class-invalid`, `security-duplicate-ticker`,
 * `security-duplicate-cusip`.
 */
export function createSecurity(
    ledgerId: string,
    body: CreateSecurityRequest,
): Promise<{ securityId: string }> {
    return request<{ securityId: string }>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities`,
        { method: 'POST', body },
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/securities/{securityId} — partial
 * update. Omitted fields are left alone; empty-string clears.
 *
 * 422 codes: same as POST plus `security-not-in-ledger`.
 */
export function patchSecurity(
    ledgerId: string,
    securityId: string,
    body: PatchSecurityRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}`,
        { method: 'PATCH', body },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/securities/{securityId}/transactions —
 * cursor-paginated list of investment legs that reference this
 * security. The Detail page uses this for the "Recent transactions"
 * panel with a "Load more" affordance.
 */
export function fetchSecurityTransactions(
    ledgerId: string,
    securityId: string,
    options?: { cursor?: string | null; limit?: number },
): Promise<SecurityTransactionsPage> {
    const params = new URLSearchParams();
    if (options?.cursor) params.set('cursor', options.cursor);
    if (options?.limit !== undefined) params.set('limit', String(options.limit));
    const qs = params.toString().length > 0 ? `?${params.toString()}` : '';
    return request<SecurityTransactionsPage>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}/transactions${qs}`,
    );
}

/**
 * GET .../securities/{sid}/prices — cursor-paginated price list.
 * Backs the Detail page's collapsible Prices section.
 */
export function fetchSecurityPrices(
    ledgerId: string,
    securityId: string,
    options?: { cursor?: string | null; limit?: number },
): Promise<SecurityPricesPage> {
    const params = new URLSearchParams();
    if (options?.cursor) params.set('cursor', options.cursor);
    if (options?.limit !== undefined) params.set('limit', String(options.limit));
    const qs = params.toString().length > 0 ? `?${params.toString()}` : '';
    return request<SecurityPricesPage>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}/prices${qs}`,
    );
}

/** POST .../securities/{sid}/prices — append a new price.
 *  422 codes: `security-price-required`, `security-price-date-required`,
 *  `security-price-date-conflict` (PATCH the existing row instead),
 *  `security-price-high-low-invalid`. */
export function addSecurityPrice(
    ledgerId: string,
    securityId: string,
    body: CreateSecurityPriceRequest,
): Promise<{ priceId: string }> {
    return request<{ priceId: string }>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}/prices`,
        { method: 'POST', body },
    );
}

/** PATCH .../securities/{sid}/prices/{priceId}. */
export function patchSecurityPrice(
    ledgerId: string,
    securityId: string,
    priceId: string,
    body: PatchSecurityPriceRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}/prices/${encodeURIComponent(priceId)}`,
        { method: 'PATCH', body },
    );
}

/** GET .../securities/{sid}/components — multi-asset look-through sleeves. */
export function fetchSecurityComponents(
    ledgerId: string,
    securityId: string,
): Promise<SecurityComponent[]> {
    return request<SecurityComponent[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}/components`,
    );
}

/** PUT .../securities/{sid}/components — replace the whole look-through set.
 *  422 codes: `security-not-in-ledger`, `security-components-invalid`. */
export function replaceSecurityComponents(
    ledgerId: string,
    securityId: string,
    components: SecurityComponent[],
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}/components`,
        { method: 'PUT', body: { components } },
    );
}

/** DELETE .../securities/{sid}/prices/{priceId}. */
export function deleteSecurityPrice(
    ledgerId: string,
    securityId: string,
    priceId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/securities/${encodeURIComponent(securityId)}/prices/${encodeURIComponent(priceId)}`,
        { method: 'DELETE' },
    );
}
