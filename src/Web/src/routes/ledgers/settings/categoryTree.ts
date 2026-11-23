import type { AccountSummary, CategoryNode } from '@/lib/types';

/** Minimal hierarchy shape — id + parent pointer. Both CategoryNode and
 *  AccountSummary satisfy it, so the descendant walk serves either. */
export type HierarchyNode = { id: string; parentId: string | null };

// Pure tree helpers for the manage-categories panel (Slice A). Kept
// separate from the panel + dialogs so the rendering and the
// hierarchy math can be unit-tested independently.

export interface CategoryTreeNode {
    node: CategoryNode;
    depth: number;
    children: CategoryTreeNode[];
}

/**
 * Build a depth-annotated forest from a flat category list (already
 * scoped to one kind by the caller). A node is a root when its parent
 * is null OR its parent is absent from this set (e.g. the parent is the
 * other kind, or was filtered out). Siblings are name-sorted
 * (locale-aware, case-insensitive) at every level.
 */
export function buildForest(nodes: readonly CategoryNode[]): CategoryTreeNode[] {
    const present = new Set(nodes.map((n) => n.id));
    const childrenOf = new Map<string | null, CategoryNode[]>();
    for (const n of nodes) {
        // A parent outside this set anchors the node as a root (null bucket).
        const key = n.parentId !== null && present.has(n.parentId) ? n.parentId : null;
        const bucket = childrenOf.get(key);
        if (bucket) bucket.push(n);
        else childrenOf.set(key, [n]);
    }
    const byName = (a: CategoryNode, b: CategoryNode) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
    const build = (parentId: string | null, depth: number): CategoryTreeNode[] =>
        (childrenOf.get(parentId) ?? [])
            .slice()
            .sort(byName)
            .map((n) => ({ node: n, depth, children: build(n.id, depth + 1) }));
    return build(null, 0);
}

/**
 * Pre-order flatten for row rendering — a parent immediately precedes
 * its descendants, each row carrying its depth (for indentation).
 */
export function flattenForest(forest: readonly CategoryTreeNode[]): CategoryTreeNode[] {
    const out: CategoryTreeNode[] = [];
    const walk = (nodes: readonly CategoryTreeNode[]) => {
        for (const n of nodes) {
            out.push(n);
            walk(n.children);
        }
    };
    walk(forest);
    return out;
}

/**
 * The ids a search keeps: every category whose own name contains `query` (already trimmed
 * and lower-cased, and not empty), and every ancestor of one, so a match is never shown
 * without the path that leads to it. A category whose parent is not in `nodes` ends its walk.
 */
export function categoryIdsMatching(nodes: readonly CategoryNode[], query: string): Set<string> {
    const byId = new Map(nodes.map((n) => [n.id, n] as const));
    const keep = new Set<string>();
    for (const n of nodes) {
        if (!n.name.toLowerCase().includes(query)) continue;
        let cur: CategoryNode | undefined = n;
        while (cur !== undefined && !keep.has(cur.id)) {
            keep.add(cur.id);
            cur = cur.parentId !== null ? byId.get(cur.parentId) : undefined;
        }
    }
    return keep;
}

/**
 * Merge targets for a category, which — unlike parent targets — are NOT narrowed by kind.
 *
 * A merge across kinds is now the ONLY way to reclassify a category (ADR-0017: the kind of an
 * existing category is immutable, because flipping it silently moves every posting in it
 * between the spending and income totals for all of its history). Filtering the target list by
 * kind would leave no route at all. The merge is the better mechanism for exactly the reasons
 * the flip is not: it states how many transactions and children move, and it deactivates the
 * source rather than destroying it (reactivating that gives back an empty category; nothing
 * moves back).
 *
 * An eligible target is an active category other than the source and what is beneath it: the
 * source's children are reparented to the target, so a target beneath it would end up under
 * itself. The picker leaves out what it can see beneath the source (a category under an
 * inactive one, which the accounts read leaves out, is not seen); the server refuses the rest
 * (`category-cycle`) and the dialog shows its message.
 */
export function eligibleMergeTarget(
    excludeIds: ReadonlySet<string>,
): (a: AccountSummary) => boolean {
    return (a) =>
        a.accountType === 'category'
        && a.isActive
        && !excludeIds.has(a.id);
}

/**
 * Whether there is anything to merge this category into. The row menu and the merge dialog ask
 * the same question, through this, so the menu offers Merge exactly when the dialog has a target
 * (the dialog still builds the exclusion set itself, for the picker, from the same two helpers).
 */
export function canMergeAway(nodeId: string, accounts: readonly AccountSummary[]): boolean {
    const excluded = collectDescendantIds(nodeId, accounts);
    excluded.add(nodeId);
    return accounts.some(eligibleMergeTarget(excluded));
}

/**
 * Every descendant id of `rootId` (excluding itself). Used to keep a
 * reparent / merge from selecting its own subtree — which the server
 * also rejects (cycle), but pre-filtering keeps the picker honest.
 */
export function collectDescendantIds(
    rootId: string,
    all: readonly HierarchyNode[],
): Set<string> {
    const childrenOf = new Map<string, HierarchyNode[]>();
    for (const n of all) {
        if (n.parentId !== null) {
            const bucket = childrenOf.get(n.parentId);
            if (bucket) bucket.push(n);
            else childrenOf.set(n.parentId, [n]);
        }
    }
    const out = new Set<string>();
    const stack = [rootId];
    while (stack.length > 0) {
        const id = stack.pop() as string;
        for (const child of childrenOf.get(id) ?? []) {
            if (!out.has(child.id)) {
                out.add(child.id);
                stack.push(child.id);
            }
        }
    }
    return out;
}
