import { todayInputValue } from '../dates';

/**
 * The user's local date, as the register sends it (`today=YYYY-MM-DD`), for every
 * call that reads a CURRENT value: balances, net worth, holdings, prices. Not a loan
 * reminder's split, which is priced as of the payment's own date, never the
 * caller's (ADR-0078). The server cuts each of them exactly where the register's
 * Scheduled rows begin, so a row the register shows as Scheduled is in none of
 * them, and counts in all of them the moment the user's date reaches it.
 *
 * Read at call time rather than memoised, so a page left open across midnight asks
 * for the new day on its next fetch.
 */
export function todayQuery(): string {
    return `?today=${encodeURIComponent(todayInputValue())}`;
}

/** The same date for a URL that already carries a query string. */
export function withToday(params: URLSearchParams): URLSearchParams {
    params.set('today', todayInputValue());
    return params;
}
