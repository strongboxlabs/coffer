// Category-management API types (Slice A). Categories ARE accounts
// (`accountType === 'category'`), but the manage-categories panel (the
// Categories tab of Structure) talks to the dedicated /categories endpoints, which carry the
// hierarchy + usage shape the tree needs. Create / rename / activate
// continue to reuse the accounts endpoints (./account.ts).

import type { RateUnavailable } from './conversion';

/**
 * Mirror of API `Coffer.Api.Contracts.CategoryNode`. One category in the
 * management tree, with the hierarchy pointer plus the usage counts the
 * tree shows and uses to gate Delete: `transactionCount` (legs posting
 * to it) and `childCount` (sub-categories). The server stays
 * authoritative on Delete; these counts only drive the UI affordance.
 */
export interface CategoryNode {
    id: string;
    name: string;
    categoryKind: string;
    parentId: string | null;
    isActive: boolean;
    isSystem: boolean;
    transactionCount: number;
    childCount: number;
    /** Signed sum of the category's own leg amounts, each converted into the
     *  ledger's reporting currency at its transaction's date (ADR-0114 D7).
     *  Expense categories net positive, income net negative (double-entry sign);
     *  the panel normalizes the sign per kind so both read as positive
     *  magnitudes under their section headers. Null when a leg needs a missing
     *  rate, named in `rateUnavailable` (ADR-0114 D9). */
    total: number | null;
    /** Absent when nothing is missing. */
    rateUnavailable?: RateUnavailable[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.CategoryListDto`, the body of
 * `GET /api/ledgers/{ledgerId}/categories`: every category, and the currency their
 * figures are in. A category has no currency of its own (ADR-0109 D9), so
 * `currencyCode` is the ledger's reporting currency, which every total is
 * converted into (ADR-0114 D7).
 */
export interface CategoryList {
    categories: CategoryNode[];
    currencyCode: string;
}

/** Body of `PATCH /api/ledgers/{ledgerId}/categories/{id}/parent`.
 *  `null` moves the category to the top level. */
export interface ReparentCategoryRequest {
    parentId: string | null;
}

/** Body of `POST /api/ledgers/{ledgerId}/categories/{id}/merge`.
 *  `dryRun` returns the counts that would move without writing. */
export interface MergeCategoryRequest {
    targetId: string;
    dryRun?: boolean;
}

/** Response of the merge endpoint — counts moved (or that would move,
 *  when `dryRun`). */
export interface MergeCategoryResponse {
    transactionsMoved: number;
    childrenReparented: number;
    dryRun: boolean;
    /** Budget targets of the source the destination kept, summed into its own where both
     *  held one for the same month. Zero on a dry run. */
    budgetTargetsMoved: number;
    /** Targets dropped because a category above them already held one for that month:
     *  anything beneath the destination (the source's sub-categories and its own) against
     *  the destination's, and what arrived against the destination's ancestors'. Zero on a
     *  dry run. */
    budgetTargetsDropped: number;
}
