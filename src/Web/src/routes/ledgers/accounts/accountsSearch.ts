/** The Accounts tab's search: `edit` opens that account's editor. */
export interface AccountsSearch {
    edit?: string;
}

/** The Accounts route's search, as the router keeps it: a non-empty id, or nothing. */
export function validateAccountsSearch(search: Record<string, unknown>): AccountsSearch {
    return typeof search.edit === 'string' && search.edit !== '' ? { edit: search.edit } : {};
}
