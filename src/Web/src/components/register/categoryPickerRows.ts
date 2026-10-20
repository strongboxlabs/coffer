import type { AccountSummary } from '@/lib/types';

/**
 * Tree/path helpers for the category side of {@link AccountCategoryPicker}
 * (ADR-0043). Categories form a parentId tree; these turn a flat eligible list
 * into a root-first, indented, filtered row model and answer path queries like
 * `Bills/El`. Pure + framework-free so the picker stays presentational and the
 * matching rules are unit-tested directly.
 */

/** One rendered category row: the account + its indent depth (0 = root). */
export interface CategoryTreeRow {
    account: AccountSummary;
    depth: number;
}

/**
 * Lowercased root -> node name segments for a category, via its parentId
 * chain (cycle-guarded). `Food/Groceries` -> ['food', 'groceries'].
 */
export function categoryPathSegments(
    node: AccountSummary,
    byId: ReadonlyMap<string, AccountSummary>,
): string[] {
    const segs: string[] = [];
    const seen = new Set<string>();
    let cur: AccountSummary | undefined = node;
    while (cur !== undefined && !seen.has(cur.id)) {
        seen.add(cur.id);
        segs.unshift(cur.name.toLowerCase());
        cur = cur.parentId !== null ? byId.get(cur.parentId) : undefined;
    }
    return segs;
}

/**
 * Does a category whose root->leaf path is `segs` match `query`?
 *
 *  - empty query -> every category.
 *  - no `/` -> any path component contains the query (so typing a PARENT name
 *    reveals its subtree, and a leaf name reveals the leaf).
 *  - with `/` -> the query's segments must consecutively substring-match a run
 *    of path components. A trailing slash (`Bills/`) matches DESCENDANTS of that
 *    run; otherwise the run must END at this node (`Bills/El` navigates to
 *    Electricity, not to Bills itself).
 *
 * Substring, case-insensitive per segment.
 */
export function categoryPathMatches(segs: readonly string[], query: string): boolean {
    const q = query.trim().toLowerCase();
    if (q.length === 0) return true;
    if (!q.includes('/')) return segs.some((s) => s.includes(q));

    const rawSegs = q.split('/').map((s) => s.trim());
    const trailingSlash = rawSegs.length > 0 && rawSegs[rawSegs.length - 1] === '';
    const qSegs = rawSegs.filter((s) => s.length > 0);
    if (qSegs.length === 0) return true;                  // just "/"
    if (qSegs.length > segs.length) return false;

    for (let j = 0; j + qSegs.length <= segs.length; j++) {
        let ok = true;
        for (let i = 0; i < qSegs.length; i++) {
            if (!segs[j + i]!.includes(qSegs[i]!)) { ok = false; break; }
        }
        if (!ok) continue;
        const runEnd = j + qSegs.length - 1;
        // Trailing slash -> a node BELOW the matched run (its descendants);
        // otherwise the run must end exactly at this node.
        if (trailingSlash) {
            if (segs.length - 1 > runEnd) return true;
        } else if (runEnd === segs.length - 1) {
            return true;
        }
    }
    return false;
}

/**
 * Tree-ordered, filtered rows for a forest of eligible categories (already
 * narrowed to ONE kind by the caller). Roots are categories with no eligible
 * parent; children nest under them. A node is INCLUDED when it matches OR any
 * descendant matches — so an ancestor shows (as pickable context) above a
 * deeper match. DFS order, siblings alpha, `depth` = indent level.
 */
export function buildCategoryTreeRows(
    categories: readonly AccountSummary[],
    byId: ReadonlyMap<string, AccountSummary>,
    query: string,
): CategoryTreeRow[] {
    if (categories.length === 0) return [];
    const eligibleIds = new Set(categories.map((c) => c.id));

    const childrenOf = new Map<string | null, AccountSummary[]>();
    for (const c of categories) {
        // A category whose parent isn't in this eligible set roots the forest
        // here (e.g. an expense child of an income parent, or a filtered-out
        // parent) — never dropped, just re-parented to the top.
        const parentKey =
            c.parentId !== null && eligibleIds.has(c.parentId) ? c.parentId : null;
        const arr = childrenOf.get(parentKey);
        if (arr) arr.push(c);
        else childrenOf.set(parentKey, [c]);
    }
    for (const arr of childrenOf.values()) {
        arr.sort((a, b) => a.name.localeCompare(b.name));
    }

    const matchById = new Map<string, boolean>();
    for (const c of categories) {
        matchById.set(c.id, categoryPathMatches(categoryPathSegments(c, byId), query));
    }

    // Show a node if it or any descendant matches (memoised DFS).
    const showById = new Map<string, boolean>();
    const computeShow = (node: AccountSummary): boolean => {
        const cached = showById.get(node.id);
        if (cached !== undefined) return cached;
        let show = matchById.get(node.id) ?? false;
        for (const child of childrenOf.get(node.id) ?? []) {
            if (computeShow(child)) show = true;
        }
        showById.set(node.id, show);
        return show;
    };
    for (const c of categories) computeShow(c);

    const rows: CategoryTreeRow[] = [];
    const dfs = (node: AccountSummary, depth: number): void => {
        if (!showById.get(node.id)) return;
        rows.push({ account: node, depth });
        for (const child of childrenOf.get(node.id) ?? []) dfs(child, depth + 1);
    };
    for (const root of childrenOf.get(null) ?? []) dfs(root, 0);
    return rows;
}
