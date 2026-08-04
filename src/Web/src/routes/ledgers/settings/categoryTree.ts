import type { CategoryNode } from '@/lib/types';

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
