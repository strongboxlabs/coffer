// Payee typeahead — universal across account-type domains. The
// payee field exists on both bank and investment editors. The
// bank-feed-only recall panels (similar-payees, merge-candidates)
// live in [./bank.ts] since they only fire on needs_review rows.

import type { PayeeSuggestion } from '../types/payee';
import { request } from './_request';

/**
 * GET /api/ledgers/{ledgerId}/payees — the typeahead source for
 * the payee field. Ranked server-side by usage count then
 * recency; the SPA caches via TanStack Query and filters
 * client-side instead of making per-keystroke requests.
 */
export function fetchPayees(ledgerId: string): Promise<PayeeSuggestion[]> {
    return request<PayeeSuggestion[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/payees`,
    );
}
