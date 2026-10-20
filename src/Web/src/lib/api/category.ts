// Category-management endpoints (Slice A) — the /categories REST
// surface behind the manage-categories Settings panel. Hierarchy ops
// only (list-with-usage, reparent, merge, delete); create / rename /
// activate reuse the accounts endpoints (./account.ts), since
// categories ARE accounts.

import type {
    CategoryNode,
    MergeCategoryRequest,
    MergeCategoryResponse,
    ReparentCategoryRequest,
} from '../types/category';
import { request } from './_request';

/**
 * GET /api/ledgers/{ledgerId}/categories — every category in the ledger
 * with its hierarchy pointer + usage counts (txn legs + child
 * categories), the source for the management tree. With
 * `includeInactive: true`, deactivated categories (e.g. merged-away
 * sources) are also returned, flagged via `CategoryNode.isActive`.
 */
export function fetchCategories(
    ledgerId: string,
    options?: { includeInactive?: boolean },
): Promise<CategoryNode[]> {
    const query = options?.includeInactive ? '?includeInactive=true' : '';
    return request<CategoryNode[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/categories${query}`,
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/categories/{categoryId}/parent — move a
 * category under a new parent (`parentId: null` = top level). 204 on
 * success (a no-op move is also success). 422 codes:
 * `account-not-in-ledger`, `account-not-a-category`, `account-is-system`,
 * `account-parent-invalid`, `category-cycle`.
 */
export function reparentCategory(
    ledgerId: string,
    categoryId: string,
    body: ReparentCategoryRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/categories/${encodeURIComponent(categoryId)}/parent`,
        { method: 'PATCH', body },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/categories/{categoryId}/merge — merge this
 * (source) category into `targetId`: repoint every leg, reparent the
 * source's children to the target, and deactivate the source
 * (reversible). Both must be the same kind. `dryRun: true` previews the
 * move counts without writing. 422 codes: `account-not-in-ledger`,
 * `account-not-a-category`, `category-kind-mismatch`, `category-merge-self`,
 * `account-is-system`.
 */
export function mergeCategory(
    ledgerId: string,
    categoryId: string,
    body: MergeCategoryRequest,
): Promise<MergeCategoryResponse> {
    return request<MergeCategoryResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/categories/${encodeURIComponent(categoryId)}/merge`,
        { method: 'POST', body },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/categories/{categoryId} — hard-delete a
 * category, allowed only when it has zero referencing legs and zero
 * children and is not system-managed. 204 on success; 422
 * `category-in-use` (merge it first) / `account-not-a-category` /
 * `account-is-system` / `account-not-in-ledger` otherwise.
 */
export function deleteCategory(
    ledgerId: string,
    categoryId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/categories/${encodeURIComponent(categoryId)}`,
        { method: 'DELETE' },
    );
}
