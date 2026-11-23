import type { LucideIcon } from 'lucide-react';

import { accountTypeMeta, accountTypeOrder } from '@/lib/accountTypes';
import { NO_RATE_LABEL } from '@/lib/conversion';
import { formatCurrency } from '@/lib/money';
import type {
    AccountBalance, AccountFolder, AccountSubtotals, AccountSummary, RateUnavailable,
} from '@/lib/types';

// The sidebar's account tree (FU-006, FU-052): one section per account type,
// folders nested to any depth inside it, and a figure on every row.
//
// Pure, so the rules live in one place the tests can reach without a DOM:
//
//   * At every level, sub-folders first, then accounts, each by name.
//   * A folder's figure rolls up EVERY descendant account, inactive ones
//     included and whether or not they are on screen (ADR-0085: is_active is a
//     presentation flag, and aggregates never gate on it). A section heading's
//     figure is the same rule over every account of its type, which is also
//     how the dashboard's subtotal is built.
//   * A folder's and a section's figure combine accounts, so they are the
//     server's (GET …/accounts/subtotals): every account converted into the
//     ledger's reporting currency and summed there (ADR-0114 D7). Nothing here
//     adds balances, which would add currencies. A total that needs a missing
//     rate reads "no rate" (ADR-0114 D9). A folder's figure is shown only while
//     the accounts the server summed are the accounts the folder holds here, so
//     a move the server has not answered yet never shows the old total.
//   * The server's list is flat. A parent id it no longer has, which a stale
//     cache can produce for a moment, puts the folder at the top level rather
//     than dropping it and every account inside it.

export const PATH_SEPARATOR = ' › ';

export interface Total {
    /** Null while it is not known (loading, nothing to cover, or the accounts it
     *  covers have moved since the server summed them), or when a rate is missing. */
    amount: number | null;
    /** The ledger's reporting currency; null while the total is not known. */
    currencyCode: string | null;
    /** Each missing rate the total needs (ADR-0114 D9); empty when none is. */
    rateUnavailable: readonly RateUnavailable[];
}

const UNKNOWN: Total = { amount: null, currencyCode: null, rateUnavailable: [] };

export interface AccountNode {
    kind: 'account';
    account: AccountSummary;
    depth: number;
    /** Folder names from the top of the section down to this account's folder. */
    folderPath: readonly string[];
    balance: AccountBalance | undefined;
}

export interface FolderNode {
    kind: 'folder';
    folder: AccountFolder;
    depth: number;
    /** Folder names from the top of the section down to and including this one. */
    path: readonly string[];
    children: readonly TreeNode[];
    total: Total;
}

export type TreeNode = AccountNode | FolderNode;

export interface TypeSection {
    accountType: string;
    label: string;
    icon: LucideIcon;
    total: Total;
    children: readonly TreeNode[];
}

const byName = (a: string, b: string) =>
    a.localeCompare(b, undefined, { sensitivity: 'base' });

/** Accounts the rail lists at all: no system rows, no categories. */
export function isRailAccount(account: AccountSummary): boolean {
    return !account.isSystem && account.accountType !== 'category';
}

/** A total as the server sent it. */
function totalFrom(
    subtotal: { amount: number | null; rateUnavailable: readonly RateUnavailable[] },
    currencyCode: string,
): Total {
    return { amount: subtotal.amount, currencyCode, rateUnavailable: subtotal.rateUnavailable };
}

/** A section's total: the server's for its type, or unknown while it has none. */
export function sectionTotal(accountType: string, subtotals: AccountSubtotals | undefined): Total {
    const sent = subtotals?.types.find((t) => t.accountType === accountType);
    return sent === undefined || subtotals === undefined ? UNKNOWN : totalFrom(sent, subtotals.currencyCode);
}

/**
 * A folder's total: the server's, while the accounts it summed are `accountIds`, the
 * accounts beneath the folder as this tree has them. Unknown when they differ (a move
 * not yet answered), and for a folder with nothing beneath it, which has no figure.
 */
export function folderTotal(
    folderId: string,
    accountIds: readonly string[],
    subtotals: AccountSubtotals | undefined,
): Total {
    if (accountIds.length === 0 || subtotals === undefined) return UNKNOWN;
    const sent = subtotals.folders.find((f) => f.folderId === folderId);
    if (sent === undefined || !sameIds(sent.accountIds, accountIds)) return UNKNOWN;
    return totalFrom(sent, subtotals.currencyCode);
}

function sameIds(a: readonly string[], b: readonly string[]): boolean {
    if (a.length !== b.length) return false;
    const set = new Set(a);
    return b.every((id) => set.has(id));
}

/**
 * A total as the rail prints it: the amount, as the dashboard formats one; "no rate"
 * when it needs a missing rate; empty while it is not known.
 */
export function formatTotal(total: Total): string {
    if (total.rateUnavailable.length > 0) return NO_RATE_LABEL;
    if (total.amount === null || total.currencyCode === null) return '';
    return formatCurrency(total.amount, total.currencyCode);
}

/** A folder's parent, with a parent the list no longer has read as the top level. */
function parentOf(folder: AccountFolder, byId: ReadonlyMap<string, AccountFolder>): string | null {
    return folder.parentId !== null && byId.has(folder.parentId) ? folder.parentId : null;
}

/** The folder names from the top of its section down to `folderId`, inclusive. */
export function folderPath(
    folderId: string,
    byId: ReadonlyMap<string, AccountFolder>,
): string[] {
    const names: string[] = [];
    const seen = new Set<string>();
    let cur = byId.get(folderId);
    while (cur !== undefined && !seen.has(cur.id)) {
        seen.add(cur.id);
        names.unshift(cur.name);
        const parent = parentOf(cur, byId);
        cur = parent === null ? undefined : byId.get(parent);
    }
    return names;
}

/** `folderId` and every folder beneath it. */
export function selfAndDescendants(
    folderId: string,
    folders: readonly AccountFolder[],
): Set<string> {
    const byId = new Map(folders.map((f) => [f.id, f]));
    const out = new Set<string>([folderId]);
    let grew = true;
    while (grew) {
        grew = false;
        for (const f of folders) {
            const parent = parentOf(f, byId);
            if (parent !== null && out.has(parent) && !out.has(f.id)) {
                out.add(f.id);
                grew = true;
            }
        }
    }
    return out;
}

/** The folder an account is in, or null at the top level. */
export function folderOfAccount(
    accountId: string,
    folders: readonly AccountFolder[],
): AccountFolder | null {
    return folders.find((f) => f.accountIds.includes(accountId)) ?? null;
}

/** Something being moved. */
export interface MoveItem {
    kind: 'account' | 'folder';
    id: string;
    accountType: string;
}

/** Where it is going: into a folder, or the top level of a type's section. */
export type MoveTarget =
    | { kind: 'folder'; folderId: string }
    | { kind: 'section'; accountType: string };

export type MoveOutcome =
    | { kind: 'invalid' }
    | { kind: 'noop' }
    | { kind: 'move'; parentId: string | null };

/**
 * What a move would do: `invalid` for a target of another type, or a folder
 * dropped on itself or anything beneath it; `noop` when it is already there;
 * otherwise the new parent (null = top level).
 */
export function resolveMove(
    item: MoveItem,
    target: MoveTarget,
    folders: readonly AccountFolder[],
): MoveOutcome {
    const byId = new Map(folders.map((f) => [f.id, f]));
    let targetType: string;
    let parentId: string | null;
    if (target.kind === 'section') {
        targetType = target.accountType;
        parentId = null;
    } else {
        const folder = byId.get(target.folderId);
        if (folder === undefined) return { kind: 'invalid' };
        targetType = folder.accountType;
        parentId = folder.id;
    }
    if (targetType !== item.accountType) return { kind: 'invalid' };

    if (item.kind === 'folder') {
        if (parentId !== null && selfAndDescendants(item.id, folders).has(parentId)) {
            return { kind: 'invalid' };
        }
        const self = byId.get(item.id);
        const current = self === undefined ? null : parentOf(self, byId);
        return current === parentId ? { kind: 'noop' } : { kind: 'move', parentId };
    }
    const current = folderOfAccount(item.id, folders)?.id ?? null;
    return current === parentId ? { kind: 'noop' } : { kind: 'move', parentId };
}

/** The folders a move could go into, by path, for the menu's "Move to" items. */
export function moveDestinations(
    item: MoveItem,
    folders: readonly AccountFolder[],
): { folderId: string; path: string }[] {
    const byId = new Map(folders.map((f) => [f.id, f]));
    return folders
        .filter((f) => resolveMove(item, { kind: 'folder', folderId: f.id }, folders).kind === 'move')
        .map((f) => ({ folderId: f.id, path: folderPath(f.id, byId).join(PATH_SEPARATOR) }))
        .sort((a, b) => byName(a.path, b.path));
}

/** The optimistic cache edit for an account move. */
export function withAccountMoved(
    folders: readonly AccountFolder[],
    accountId: string,
    folderId: string | null,
): AccountFolder[] {
    return folders.map((f) => {
        const without = f.accountIds.filter((id) => id !== accountId);
        return f.id === folderId
            ? { ...f, accountIds: [...without, accountId] }
            : { ...f, accountIds: without };
    });
}

/** The optimistic cache edit for a folder move. */
export function withFolderMoved(
    folders: readonly AccountFolder[],
    folderId: string,
    parentId: string | null,
): AccountFolder[] {
    return folders.map((f) => (f.id === folderId ? { ...f, parentId } : f));
}

/**
 * Build the sections. `accounts` should include inactive ones, because the
 * figures need them; `showInactive` decides only which account rows are drawn.
 * A section appears when it has a row to draw: an account on screen, or a
 * folder (an empty folder is still somewhere to put an account). `balances` are each
 * account's own figure; `subtotals` the server's totals, undefined while they load.
 */
export function buildSections(
    accounts: readonly AccountSummary[],
    folders: readonly AccountFolder[],
    balances: ReadonlyMap<string, AccountBalance>,
    subtotals: AccountSubtotals | undefined,
    showInactive: boolean,
): TypeSection[] {
    const byId = new Map(folders.map((f) => [f.id, f]));
    const railAccounts = accounts.filter(isRailAccount);

    const folderOf = new Map<string, string>();
    for (const f of folders) for (const id of f.accountIds) folderOf.set(id, f.id);

    const childFolders = new Map<string | null, AccountFolder[]>();
    for (const f of folders) {
        const key = parentOf(f, byId);
        const list = childFolders.get(key);
        if (list) list.push(f);
        else childFolders.set(key, [f]);
    }

    const types = new Set<string>();
    for (const a of railAccounts) types.add(a.accountType);
    for (const f of folders) types.add(f.accountType);

    const sections: TypeSection[] = [];
    for (const type of types) {
        const typeAccounts = railAccounts
            .filter((a) => a.accountType === type)
            .sort((a, b) => byName(a.name, b.name));

        const accountNodes = (
            folderId: string | null,
            depth: number,
            path: readonly string[],
        ): AccountNode[] =>
            typeAccounts
                .filter((a) => {
                    const own = folderOf.get(a.id);
                    const placed = own !== undefined && byId.has(own) ? own : null;
                    return placed === folderId && (showInactive || a.isActive);
                })
                .map((a) => ({
                    kind: 'account',
                    account: a,
                    depth,
                    folderPath: path,
                    balance: balances.get(a.id),
                }));

        // Guards a cycle a stale cache could briefly hold; the server refuses one.
        const visited = new Set<string>();
        // One level's rows: its sub-folders (each with its own rows), then its accounts.
        const level = (
            parentId: string | null,
            depth: number,
            path: readonly string[],
        ): TreeNode[] => {
            const subFolders = (childFolders.get(parentId) ?? [])
                .filter((f) => f.accountType === type && !visited.has(f.id))
                .sort((a, b) => byName(a.name, b.name));
            const folderNodes: FolderNode[] = subFolders.map((f) => {
                visited.add(f.id);
                const ownPath = [...path, f.name];
                const children = level(f.id, depth + 1, ownPath);
                return {
                    kind: 'folder',
                    folder: f,
                    depth,
                    path: ownPath,
                    children,
                    total: folderTotal(f.id, descendantAccountIds(f.id, folders, byId), subtotals),
                };
            });
            return [...folderNodes, ...accountNodes(parentId, depth, path)];
        };

        const children = level(null, 0, []);
        if (children.length === 0) continue;
        const meta = accountTypeMeta(type);
        sections.push({
            accountType: type,
            label: meta.label,
            icon: meta.icon,
            total: sectionTotal(type, subtotals),
            children,
        });
    }
    return sections.sort((a, b) => accountTypeOrder(a.accountType) - accountTypeOrder(b.accountType));
}

/** Every account anywhere beneath a folder, as the server lists them. */
function descendantAccountIds(
    folderId: string,
    folders: readonly AccountFolder[],
    byId: ReadonlyMap<string, AccountFolder>,
): string[] {
    return [...selfAndDescendants(folderId, folders)].flatMap((id) => byId.get(id)?.accountIds ?? []);
}
