// Category-management endpoints (Slice A) — the /categories REST
// surface behind the manage-categories panel (the Categories tab of Structure). Hierarchy ops
// only (list-with-usage, reparent, merge, delete); create / rename /
// activate reuse the accounts endpoints (./account.ts), since
// categories ARE accounts.

import type {
    CategoryList,
    MergeCategoryRequest,
    MergeCategoryResponse,
    ReparentCategoryRequest,
} from '../types/category';
import { request } from './_request';

/**
 * GET /api/ledgers/{ledgerId}/categories — every category in the ledger
 * with its hierarchy pointer + usage counts (txn legs + child
 * categories), the source for the management tree, and the ledger's
 * display currency its figures are formatted in. With
 * `includeInactive: true`, deactivated categories (e.g. merged-away
 * sources) are also returned, flagged via `CategoryNode.isActive`.
 */
export function fetchCategories(
    ledgerId: string,
    options?: { includeInactive?: boolean },
): Promise<CategoryList> {
    const query = options?.includeInactive ? '?includeInactive=true' : '';
    return request<CategoryList>(
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
 * source's children to the target, and deactivate the source (the row is
 * kept; reactivating it brings back an empty category, and nothing moves
 * back). The kinds may differ (ADR-0017): a cross-kind merge is how a
 * category is reclassified. The target must not sit beneath the source.
 * `dryRun: true` previews the move counts without writing. The response also
 * says how many budget targets moved and how many were dropped (ADR-0099;
 * both zero on a dry run). 422 codes: `account-not-in-ledger`,
 * `account-not-a-category`, `category-merge-self`, `account-is-system`,
 * `category-cycle`, and `rate-unavailable` (ADR-0114 D12): both categories hold a
 * target for a month in different currencies and the rate is missing, so nothing
 * merged. Its detail names the month and the pair, and the error's
 * `rateUnavailable` carries the pair and day.
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
