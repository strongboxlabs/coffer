// Category-management API types (Slice A). Categories ARE accounts
// (`accountType === 'category'`), but the manage-categories Settings
// panel talks to the dedicated /categories endpoints, which carry the
// hierarchy + usage shape the tree needs. Create / rename / activate
// continue to reuse the accounts endpoints (./account.ts).

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
    /** Raw signed sum of the category's own leg amounts. Expense
     *  categories net positive, income net negative (double-entry sign);
     *  the panel normalizes the sign per kind so both read as positive
     *  magnitudes under their section headers. */
    total: number;
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
}
