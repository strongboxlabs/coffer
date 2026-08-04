// Payee typeahead API types.

/**
 * Mirror of API `Coffer.Api.Contracts.PayeeSuggestion`. One row of
 * the typeahead source served by `GET /api/ledgers/{id}/payees` —
 * resolved payee text plus enough context for the SPA to render
 * "you've used this N times, last on …" if desired. Server already
 * ranks count-desc then last-used-desc; the SPA just renders the
 * filtered prefix of the response.
 */
export interface PayeeSuggestion {
    name: string;
    count: number;
    /** ISO-8601 UTC timestamp string. */
    lastUsedAt: string;
}
