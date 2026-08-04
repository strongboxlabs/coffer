import type { AccountSummary } from './types';

/**
 * Minimal shape {@link buildAccountPathMap} needs: an id, a display
 * name, and a parent pointer. Both `AccountSummary` and `CategoryNode`
 * satisfy it, so the same parent-chain walk serves the accounts list,
 * the register category chips, and the manage-categories tree without
 * duplicating the algorithm.
 */
export interface AccountPathNode {
    id: string;
    name: string;
    parentId: string | null;
}

/**
 * Build a slash-joined path for every account in a ledger, keyed
 * by id. Walks each account's `parentId` chain so a leaf like
 * `Groceries` under `Food` resolves to `Food/Groceries`.
 *
 * Mirrors the server-side `account_path()` Postgres function from
 * migration 021 (slash separator was the product decision there
 * too — `:` and `>` were rejected as more likely to collide with
 * names that contain them, see migration 021 lines 30–34).
 *
 * Why client-side at all: `AccountSummary` doesn't carry the path,
 * only `name` and `parentId`. Doing it client-side means we don't
 * need to widen the wire payload — the accounts list already in
 * memory is sufficient. O(N) over a few-hundred-row list,
 * memoised at the call site.
 */
export function buildAccountPathMap(
    accounts: readonly AccountPathNode[],
): Map<string, string> {
    const byId = new Map<string, AccountPathNode>(
        accounts.map((a) => [a.id, a]),
    );
    const paths = new Map<string, string>();

    function pathFor(id: string, seen: Set<string>): string {
        const cached = paths.get(id);
        if (cached !== undefined) return cached;
        const account = byId.get(id);
        if (account === undefined) return '';
        // Cycle guard — accounts shouldn't have cycles per schema,
        // but a corrupted import could; bail rather than recurse
        // forever.
        if (seen.has(id)) return account.name;
        seen.add(id);

        const parent = account.parentId !== null
            ? byId.get(account.parentId)
            : undefined;
        const path = parent !== undefined
            ? `${pathFor(parent.id, seen)}/${account.name}`
            : account.name;
        paths.set(id, path);
        return path;
    }

    for (const account of accounts) {
        pathFor(account.id, new Set());
    }
    return paths;
}

/**
 * Resolve an account id to its full slash path when the path map has it,
 * else fall back to the supplied display name. Safe when the id or the
 * map is absent (returns the name) — register chips may reference an
 * account that's path-less (a top-level account) or not yet loaded.
 * This is the one-liner the register row strategies call to upgrade a
 * bare category name (`Groceries`) to its chain (`Food/Groceries`).
 */
export function displayAccountPath(
    paths: ReadonlyMap<string, string> | undefined,
    id: string | null | undefined,
    name: string | null | undefined,
): string | null | undefined {
    if (id != null && paths !== undefined) return paths.get(id) ?? name;
    return name;
}

/**
 * Filter to accounts the user can use as a transaction
 * counterparty: any non-system, non-hidden account. Includes
 * categories (`accountType === 'category'`) AND real accounts
 * (bank/credit/etc.) so transfers between accounts work too.
 *
 * Sorted by full path so the Typeahead's pre-filter ordering
 * reads naturally (`Food/Groceries` next to `Food/Restaurants`,
 * etc.).
 */
export function pickableCounterparties(
    accounts: readonly AccountSummary[],
    pathMap: Map<string, string>,
): AccountSummary[] {
    return accounts
        .filter((a) => !a.isSystem && a.isActive)
        .slice()
        .sort((a, b) => {
            const pa = pathMap.get(a.id) ?? a.name;
            const pb = pathMap.get(b.id) ?? b.name;
            return pa.localeCompare(pb);
        });
}
