import type {
    DashboardPrefsV2,
    QuoteProvider,
    QuotesPrefs,
    StoredDashboardPrefs,
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

/** GET /preferences/dashboard — the stored home-screen layout, whatever its version,
 *  with the row's `updatedAt` (null and an empty document when nothing is stored). */
export function fetchDashboardPrefs(ledgerId: string): Promise<StoredDashboardPrefs> {
    return request<StoredDashboardPrefs>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/preferences/dashboard`,
    );
}

/**
 * PUT /preferences/dashboard — replace the layout if nobody saved since `updatedAt`
 * (null: nothing stored when it was read). A stale save is 422
 * `dashboard-prefs-stale` and writes nothing. Answers the saved document with the
 * row's new `updatedAt`, which the next save echoes.
 */
export function saveDashboardPrefs(
    ledgerId: string,
    prefs: DashboardPrefsV2,
    updatedAt: string | null,
): Promise<StoredDashboardPrefs> {
    return request<StoredDashboardPrefs>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/preferences/dashboard`,
        { method: 'PUT', body: { ...prefs, updatedAt } },
    );
}

/** DELETE /preferences/dashboard — reset to the default layout. 204. */
export function deleteDashboardPrefs(ledgerId: string): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/preferences/dashboard`,
        { method: 'DELETE' },
    );
}

