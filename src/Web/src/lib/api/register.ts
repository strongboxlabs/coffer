// Register read endpoint + universal mutations (recon-status,
// delete). The read surface is universal across account-type
// domains; recon-status and delete also apply to any row
// regardless of domain. Bank-specific create/PATCH live in
// [./bank.ts]; investment-specific in [./investment.ts] (lands
// with A4.c.3). See ADR-0030.

import type {
    DeleteTransactionResponse,
    HeaderBalanceDto,
    IndexBucketDto,
    InvestmentRow,
    RegisterPage,
    SetReconStatusRequest,
} from '../types/register';
import { request } from './_request';

/** Direction values for the cursor (mirror of `RegisterRepository.DirectionBefore/After`). */
export type RegisterDirection = 'before' | 'after';

/** Server-side status values (mig 164). The UI's `all` = omit; `hidden` = the
 *  separate `hidden` flag, not this. */
export type RegisterServerStatus =
    'cleared' | 'uncleared' | 'reconciling' | 'scheduled' | 'needs_review';

/**
 * Server-side register filter (mig 164). Mirror of the API's `RegisterFilter`
 * query params. Every field optional; omitted ⇒ no-op. Pushed into
 * `register_entry_keys` so the windowed keyset cursor walks only matching
 * entries — the reason filtering can't be client-side (it only sees the loaded
 * window). Dates are `YYYY-MM-DD`.
 */
export interface RegisterFilterArgs {
    search?: string;
    dateFrom?: string;
    dateTo?: string;
    amountMin?: number;
    amountMax?: number;
    securityId?: string;
    tag?: string;
    categoryId?: string;
    status?: RegisterServerStatus;
    /** Caller's LOCAL calendar date (YYYY-MM-DD) so "scheduled" matches the
     *  user's date, not the server's UTC one. */
    today?: string;
}

/** True when any *user* filter dimension is set. Excludes `status` (owned by
 *  the status tabs) and `today` (set by the controller), so toggling a status
 *  tab never lights up the filter chrome. One definition — the popover's
 *  active styling and both register pages' match-count share it (no drift). */
export function isRegisterFilterActive(f: RegisterFilterArgs): boolean {
    return !!(
        f.search
        || f.dateFrom
        || f.dateTo
        || f.amountMin !== undefined
        || f.amountMax !== undefined
        || f.categoryId
        || f.tag
        || f.securityId
    );
}

/** Per-status entry counts (mig 165) for the status dropdown's badges. Mirror
 *  of the API's `RegisterStatusCounts`. Every count respects the active
 *  NON-status filter; `needsReview` overlaps the recon buckets (separate
 *  dimension), so the buckets don't sum to `all`. */
export interface RegisterStatusCounts {
    all: number;
    cleared: number;
    uncleared: number;
    reconciling: number;
    scheduled: number;
    needsReview: number;
    hidden: number;
}

/** Serialize a {@link RegisterFilterArgs} onto a query string (shared by the
 *  register page + the scroll-track buckets so both filter identically). */
function appendFilterParams(params: URLSearchParams, filter?: RegisterFilterArgs): void {
    if (!filter) return;
    if (filter.search) params.set('search', filter.search);
    if (filter.dateFrom) params.set('date_from', filter.dateFrom);
    if (filter.dateTo) params.set('date_to', filter.dateTo);
    if (filter.amountMin !== undefined) params.set('amount_min', String(filter.amountMin));
    if (filter.amountMax !== undefined) params.set('amount_max', String(filter.amountMax));
    if (filter.securityId) params.set('security_id', filter.securityId);
    if (filter.tag) params.set('tag', filter.tag);
    if (filter.categoryId) params.set('category_id', filter.categoryId);
    if (filter.status) params.set('status', filter.status);
    if (filter.today) params.set('today', filter.today);
}

export interface FetchRegisterArgs {
    ledgerId: string;
    /** Optional account scope. Omit for ledger-wide reads. */
    accountId?: string;
    /** Opaque continuation token from a prior page's
     *  `cursorForOlder` (when direction='before') or
     *  `cursorForNewer` (when direction='after'). Omitted on the
     *  canonical first page. */
    cursor?: string;
    /** Direction the cursor walks the timeline. Defaults to
     *  `'before'` server-side. */
    direction?: RegisterDirection;
    /** Header id to anchor on. When supplied, ignores `cursor`
     *  and returns a page with this header as the first entry
     *  (entry[0]) followed by strictly-older entries. Used by the
     *  "Show other side" navigation arrival path. */
    startingAtHeaderId?: string;
    /** Page size. Server clamps to [1, 500]; default 100 when omitted. */
    limit?: number;
    /** When true, returns soft-hidden rows instead of the visible
     *  register (ADR-0072 D1 — the Hidden view). Defaults to false. */
    hidden?: boolean;
    /** Server-side filter (mig 164). */
    filter?: RegisterFilterArgs;
    /** Column sort (mig 166). Omitted ⇒ the server default (date, desc).
     *  Changing it resets the windowed register — a new order needs a fresh
     *  keyset walk. Display-order only; never changes which entries match. */
    sort?: { column: string; dir: 'asc' | 'desc' };
}

/**
 * GET /api/ledgers/{ledgerId}/transactions — one page of the
 * register. Three call shapes (sliding-window pagination, migration 031):
 *
 * - **Initial / most-recent page**: omit cursor + direction +
 *   startingAtHeaderId. Returns the most-recent `limit` entries.
 * - **Continuation in either direction**: pass `cursor` + `direction`.
 *   `'before'` walks toward older entries; `'after'` walks toward newer.
 *   Result is always time-DESC ordered regardless of direction.
 * - **Focus arrival**: pass `startingAtHeaderId`. Returns a page
 *   anchored at that header (entry[0]) followed by older entries.
 *
 * Surfaces the API's 422 codes verbatim via `ApiError`:
 *   * `ledger-not-visible`         — caller has no grant on this ledger.
 *   * `account-not-in-ledger`      — supplied accountId is in another ledger.
 *   * `register-limit-invalid`     — supplied limit outside [1, 500].
 *   * `register-direction-invalid` — direction is neither 'before' nor 'after'.
 */
export function fetchRegister(args: FetchRegisterArgs): Promise<RegisterPage> {
    const params = new URLSearchParams();
    if (args.accountId !== undefined) params.set('account_id', args.accountId);
    if (args.cursor !== undefined) params.set('cursor', args.cursor);
    if (args.direction !== undefined) params.set('direction', args.direction);
    if (args.startingAtHeaderId !== undefined)
        params.set('starting_at', args.startingAtHeaderId);
    if (args.limit !== undefined) params.set('limit', String(args.limit));
    if (args.hidden) params.set('hidden', 'true');
    appendFilterParams(params, args.filter);
    if (args.sort) {
        params.set('sort', args.sort.column);
        params.set('dir', args.sort.dir);
    }
    const query = params.toString();
    const suffix = query.length > 0 ? `?${query}` : '';
    return request<RegisterPage>(
        `/api/ledgers/${encodeURIComponent(args.ledgerId)}/transactions${suffix}`,
    );
}

/**
 * PUT /api/ledgers/{ledgerId}/transactions/{headerId}/recon-status —
 * set the reconciliation state on one header. Universal across
 * domains. The server manages the paired audit columns
 * (cleared_at / cleared_by_user_id) to keep the DB CHECK satisfied;
 * clients just declare the desired new state.
 *
 * 422 codes:
 *   * `transaction-recon-status-invalid` — status not one of
 *     uncleared / reconciling / cleared.
 *   * `transaction-not-in-ledger`        — supplied headerId is in
 *     another ledger.
 */
export function setReconStatus(
    ledgerId: string,
    headerId: string,
    body: SetReconStatusRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}/recon-status`,
        { method: 'PUT', body },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/transactions/{headerId} — remove a
 * row from the user-visible register. Universal across domains. The
 * server picks hard-delete (manual entries, `external_id IS NULL`)
 * vs soft-hide (any feed / import-keyed row) and returns which
 * branch ran in the response.
 *
 * 422 codes:
 *   * `transaction-not-in-ledger` — supplied headerId is in another ledger.
 */
export function deleteTransaction(
    ledgerId: string,
    headerId: string,
): Promise<DeleteTransactionResponse> {
    return request<DeleteTransactionResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}`,
        { method: 'DELETE' },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/transactions/{headerId}/unhide (ADR-0072
 * D2) — un-hide a single soft-hidden transaction so it returns to the
 * register. Idempotent (204 whether or not it was hidden).
 *
 * 422 codes: `transaction-not-in-ledger`.
 */
export function unhideTransaction(
    ledgerId: string,
    headerId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}/unhide`,
        { method: 'POST' },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/transactions/{headerId}/move-account
 * (ADR-0072 D3) — move one bank-shape transaction from its source
 * account to another real account. Surfaces the guard codes verbatim:
 *   * `transaction-not-in-ledger`, `transaction-header-is-investment`,
 *     `transaction-source-account-mismatch`,
 *     `transaction-move-target-invalid`,
 *     `transaction-move-target-same-as-source`,
 *     `transaction-move-split-to-investment`,
 *     `transaction-move-collision`.
 */
export function moveTransactionToAccount(
    ledgerId: string,
    headerId: string,
    sourceAccountId: string,
    targetAccountId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}/move-account`,
        { method: 'POST', body: { sourceAccountId, targetAccountId } },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/transactions/index-buckets?account_id=...
 * — one bucket per month-with-activity for the account's register,
 * ordered most-recent first. Drives the SPA's date-aware scroll-track
 * (the custom replacement for the native browser scrollbar — Google
 * Photos pattern).
 *
 * The result is small (≤ months-in-account-lifetime, typically a few
 * hundred at most) and stable across saves, so callers should cache it
 * via TanStack Query keyed on `(ledgerId, accountId, filterFingerprint)`
 * and invalidate when register mutations land.
 */
export function fetchIndexBuckets(
    ledgerId: string,
    accountId: string,
    opts?: { hidden?: boolean; filter?: RegisterFilterArgs },
): Promise<IndexBucketDto[]> {
    const params = new URLSearchParams({ account_id: accountId });
    if (opts?.hidden) params.set('hidden', 'true');
    appendFilterParams(params, opts?.filter);
    return request<IndexBucketDto[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/index-buckets?${params.toString()}`,
    );
}

/**
 * GET /api/ledgers/{ledgerId}/transactions/status-counts?account_id=...
 * — per-status entry counts for the status dropdown's badges. The endpoint
 * buckets across every status itself, so `status` is never sent (stripped
 * here defensively); the other filter dimensions narrow every count. Cache
 * keyed on `(ledgerId, accountId, non-status filter)`.
 */
export function fetchStatusCounts(
    ledgerId: string,
    accountId: string,
    opts?: { filter?: RegisterFilterArgs },
): Promise<RegisterStatusCounts> {
    const params = new URLSearchParams({ account_id: accountId });
    appendFilterParams(
        params,
        opts?.filter ? { ...opts.filter, status: undefined } : undefined,
    );
    return request<RegisterStatusCounts>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/status-counts?${params.toString()}`,
    );
}

/**
 * POST /api/ledgers/{ledgerId}/transactions/balances?account_id=...
 * — bulk fetch of (balance_after, net_amount) for the given header
 * ids on a specific account. Used by the SPA's after-save in-place
 * refresh path: fetch fresh balances for every header currently in
 * the register window, patch them via `register.mutateEntries`,
 * leave the rendered rows in place (no virtuoso data swap, no
 * scroll jump).
 *
 * Empty headerIds returns []. Missing headers (deleted or not on
 * this account) are silently absent from the response.
 */
export function fetchBalancesForHeaders(
    ledgerId: string,
    accountId: string,
    headerIds: readonly string[],
): Promise<HeaderBalanceDto[]> {
    return request<HeaderBalanceDto[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/balances?account_id=${encodeURIComponent(accountId)}`,
        { method: 'POST', body: { headerIds } },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/transactions/{headerId}/legs —
 * full leg set for a single header across ALL accounts. Used by
 * the investment editor on re-open so legsToDraft can read the
 * off-account legs (income category, transfer destination, fee
 * category) — the register page only loads the legs scoped to the
 * account it's displaying.
 *
 * Returns all-`InvestmentRow` (ADR-0030 §2): the API projects every
 * leg of this header — including the off-account bank-domain category /
 * transfer / fee legs — with the full investment shape, because its
 * only caller is the investment editor's `legsToDraft`, which reads
 * `postingRole` / `securityId` / `quantity` off every leg regardless
 * of the owning account's domain.
 */
export function fetchHeaderLegs(
    ledgerId: string,
    headerId: string,
): Promise<InvestmentRow[]> {
    return request<InvestmentRow[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/transactions/${encodeURIComponent(headerId)}/legs`,
    );
}
