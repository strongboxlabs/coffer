import type {
    DashboardPrefs,
    QuoteProvider,
    QuotesPrefs,
} from '../types/preference';
import { request } from './_request';

/** GET /quote-providers — catalog of opt-in external quote providers. */
export function fetchQuoteProviders(ledgerId: string): Promise<QuoteProvider[]> {
    return request<QuoteProvider[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/quote-providers`,
    );
}

/** GET /preferences/quotes — this ledger's enabled providers (defaulted). */
export function fetchQuotesPrefs(ledgerId: string): Promise<QuotesPrefs> {
    return request<QuotesPrefs>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/preferences/quotes`,
    );
}

/** PUT /preferences/quotes — replace the enabled-providers set. */
export function saveQuotesPrefs(
    ledgerId: string,
    prefs: QuotesPrefs,
): Promise<QuotesPrefs> {
    return request<QuotesPrefs>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/preferences/quotes`,
        { method: 'PUT', body: prefs },
    );
}

/** GET /preferences/dashboard — the Overview layout (empty = default). */
export function fetchDashboardPrefs(ledgerId: string): Promise<DashboardPrefs> {
    return request<DashboardPrefs>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/preferences/dashboard`,
    );
}

/** PUT /preferences/dashboard — replace the Overview layout. */
export function saveDashboardPrefs(
    ledgerId: string,
    prefs: DashboardPrefs,
): Promise<DashboardPrefs> {
    return request<DashboardPrefs>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/preferences/dashboard`,
        { method: 'PUT', body: prefs },
    );
}

