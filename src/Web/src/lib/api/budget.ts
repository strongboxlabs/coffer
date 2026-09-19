import type { BudgetProgress, BudgetTransactionPage } from '../types/budget';
import { request } from './_request';

/**
 * One month's spend per category beside the mean of the trailing complete
 * months — the first REST caller of the reporting aggregation, which until now
 * was reachable only from the MCP tools.
 *
 * `month` is `yyyy-MM`; omitting it reports the current month. `windowMonths`
 * defaults to 3 server-side.
 */
export function fetchBudgetProgress(
    ledgerId: string,
    month?: string,
    windowMonths?: number,
): Promise<BudgetProgress> {
    const params = new URLSearchParams();
    if (month) params.set('month', month);
    if (windowMonths !== undefined) params.set('window', String(windowMonths));
    const qs = params.toString();
    return request<BudgetProgress>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/budget/progress${qs ? `?${qs}` : ''}`,
    );
}

/**
 * PUT /api/ledgers/{ledgerId}/budget/targets — set or replace one category's
 * target for one month. 204 on success.
 *
 * 422 codes worth handling by name: `budget-target-ancestor-conflict` and
 * `budget-target-descendant-conflict` (ADR-0099 D1a — a target may sit on a
 * category or on its descendants, never both). Both carry the NAME of the
 * category already holding one in the problem detail, which is the actionable
 * half: it may be several levels away and off screen.
 */
export function setBudgetTarget(
    ledgerId: string,
    body: { categoryId: string; month: string; amount: number },
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/budget/targets`,
        { method: 'PUT', body },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/budget/targets/{categoryId}?month=yyyy-MM —
 * return the category to its derived normal.
 *
 * 204 whether or not a row was there. "No target" is a legitimate destination,
 * so clearing an already-clear cell is success, not a 404.
 */
export function deleteBudgetTarget(
    ledgerId: string,
    categoryId: string,
    month: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/budget/targets/`
        + `${encodeURIComponent(categoryId)}?month=${encodeURIComponent(month)}`,
        { method: 'DELETE' },
    );
}

/** One entry of a bulk fill. */
export interface BudgetTargetEntry { categoryId: string; amount: number }

/** What a fill skipped, and why. `status` is `ancestor-has-target`,
 *  `descendant-has-target`, `not-a-category` or `category-not-in-ledger`. */
export interface BudgetTargetEntryResult {
    categoryId: string;
    status: string;
    conflictingCategoryName: string | null;
}

export interface FillBudgetTargetsResponse {
    applied: number;
    skipped: BudgetTargetEntryResult[];
}

/**
 * POST /api/ledgers/{ledgerId}/budget/targets/fill — many targets for one
 * month, in one transaction. Backs "copy last month" and "fill from average".
 *
 * Answers 200 with a count and the entries it SKIPPED rather than failing the
 * batch on the first conflict: refusing all thirty because one is illegal is
 * worse than applying twenty-nine and naming the one that was not. Show the
 * skipped list — dropping it silently is the failure mode this shape exists to
 * avoid.
 */
export function fillBudgetTargets(
    ledgerId: string,
    body: { month: string; entries: BudgetTargetEntry[] },
): Promise<FillBudgetTargetsResponse> {
    return request<FillBudgetTargetsResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/budget/targets/fill`,
        { method: 'POST', body },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/budget/transactions?month=&categoryId= — the
 * month's transactions for a category AND everything beneath it.
 *
 * The SUBTREE, not the direct postings: a budget row is a rollup, so its figure
 * already contains its descendants and a direct-only list would not add up to
 * the number the reader just clicked. The server walks the tree, so expanding a
 * row costs one request rather than one per descendant.
 */
export function fetchBudgetTransactions(
    ledgerId: string,
    categoryId: string,
    month: string,
    limit?: number,
): Promise<BudgetTransactionPage> {
    const params = new URLSearchParams({ month, categoryId });
    if (limit !== undefined) params.set('limit', String(limit));
    return request<BudgetTransactionPage>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/budget/transactions?${params.toString()}`,
    );
}
