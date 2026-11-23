import { describe, expect, it } from 'vitest';

import {
    buildSections,
    formatTotal,
    moveDestinations,
    resolveMove,
    withAccountMoved,
    type FolderNode,
    type TreeNode,
} from './accountTree';
import { formatCurrency } from './money';
import type { AccountBalance, AccountFolder, AccountSubtotals, AccountSummary, RateUnavailable } from './types';

const LEDGER = 'ledger-1';

function account(id: string, name: string, overrides: Partial<AccountSummary> = {}): AccountSummary {
    return {
        id,
        name,
        ledgerId: LEDGER,
        parentId: null,
        accountType: 'bank',
        categoryKind: null,
        currencyCode: 'USD',
        isActive: true,
        isSystem: false,
        feedConnectionId: null,
        needsReviewCount: 0,
        holdingsAccountId: null,
        isTradeCommission: false,
        ...overrides,
    };
}

function folder(id: string, name: string, overrides: Partial<AccountFolder> = {}): AccountFolder {
    return { id, name, accountType: 'bank', parentId: null, accountIds: [], ...overrides };
}

function balances(...rows: [string, number, string?][]): Map<string, AccountBalance> {
    return new Map(rows.map(([accountId, balance, currencyCode = 'USD']) => [
        accountId,
        { accountId, balance, currencyCode },
    ]));
}

function names(nodes: readonly TreeNode[]): string[] {
    return nodes.map((n) => (n.kind === 'folder' ? `[${n.folder.name}]` : n.account.name));
}

function folderNamed(nodes: readonly TreeNode[], name: string): FolderNode {
    const found = nodes.find((n): n is FolderNode => n.kind === 'folder' && n.folder.name === name);
    if (!found) throw new Error(`no folder ${name}`);
    return found;
}

/** The server's totals: each type's and each folder's, in USD unless given. */
function subtotals(
    types: [string, number | null][],
    folders: [string, string[], number | null][],
    { currencyCode = 'USD', rateUnavailable = [] }: { currencyCode?: string; rateUnavailable?: RateUnavailable[] } = {},
): AccountSubtotals {
    return {
        currencyCode,
        types: types.map(([accountType, amount]) => ({
            accountType, amount, rateUnavailable: amount === null ? rateUnavailable : [],
        })),
        folders: folders.map(([folderId, accountIds, amount]) => ({
            folderId, accountIds, amount, rateUnavailable: amount === null ? rateUnavailable : [],
        })),
    };
}

describe('buildSections', () => {
    it('puts sub-folders first, then accounts, each by name, at every level', () => {
        const accounts = [
            account('a1', 'Zeta'),
            account('a2', 'Alpha'),
            account('a3', 'Inner b'),
            account('a4', 'Inner a'),
        ];
        const folders = [
            folder('f2', 'Second', { accountIds: ['a3', 'a4'] }),
            folder('f1', 'First'),
            folder('f3', 'Nested', { parentId: 'f2' }),
        ];
        const [section] = buildSections(accounts, folders, new Map(), undefined, false);
        expect(names(section!.children)).toEqual(['[First]', '[Second]', 'Alpha', 'Zeta']);
        expect(names(folderNamed(section!.children, 'Second').children)).toEqual([
            '[Nested]', 'Inner a', 'Inner b',
        ]);
    });

    it('takes a folder\'s and a section\'s total from the server, over every account beneath it', () => {
        const accounts = [
            account('top', 'Top'),
            account('child', 'Child'),
            account('grandchild', 'Grandchild'),
        ];
        const folders = [
            folder('outer', 'Outer', { accountIds: ['child'] }),
            folder('inner', 'Inner', { parentId: 'outer', accountIds: ['grandchild'] }),
        ];
        const [section] = buildSections(
            accounts,
            folders,
            balances(['top', 1], ['child', 20.1], ['grandchild', 300.2]),
            subtotals([['bank', 321.3]], [['outer', ['grandchild', 'child'], 320.3], ['inner', ['grandchild'], 300.2]]),
            false,
        );
        const outer = folderNamed(section!.children, 'Outer');
        const inner = folderNamed(outer.children, 'Inner');
        expect(inner.total.amount).toBe(300.2);
        expect(outer.total.amount).toBe(320.3);
        expect(section!.total.amount).toBe(321.3);
        expect(inner.depth).toBe(1);
        expect(inner.path).toEqual(['Outer', 'Inner']);
    });

    it('never adds the balances itself: accounts in two currencies show the server\'s converted total', () => {
        const accounts = [account('a', 'Home'), account('b', 'Abroad', { currencyCode: 'EUR' })];
        const folders = [folder('f', 'Both', { accountIds: ['a', 'b'] })];
        // A dollar balance and a euro one; the total is the server's conversion, not
        // their sum.
        const [section] = buildSections(
            accounts,
            folders,
            balances(['a', 10], ['b', 20, 'EUR']),
            subtotals([['bank', 40]], [['f', ['a', 'b'], 40]]),
            false,
        );
        const both = folderNamed(section!.children, 'Both');
        expect(both.total).toEqual({ amount: 40, currencyCode: 'USD', rateUnavailable: [] });
        expect(formatTotal(both.total)).toBe(formatCurrency(40, 'USD'));
        expect(formatTotal(section!.total)).toBe(formatCurrency(40, 'USD'));
    });

    it('reads a total that needs a missing rate "no rate", never the accounts that converted', () => {
        const missing: RateUnavailable = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-03-11', reason: 'rate_unavailable' };
        const accounts = [account('a', 'Home'), account('b', 'Abroad', { currencyCode: 'EUR' })];
        const folders = [folder('f', 'Both', { accountIds: ['a', 'b'] })];
        const [section] = buildSections(
            accounts,
            folders,
            balances(['a', 10], ['b', 20, 'EUR']),
            subtotals([['bank', null]], [['f', ['a', 'b'], null]], { rateUnavailable: [missing] }),
            false,
        );
        const both = folderNamed(section!.children, 'Both');
        expect(both.total.rateUnavailable).toEqual([missing]);
        expect(formatTotal(both.total)).toBe('no rate');
        expect(formatTotal(section!.total)).toBe('no rate');
    });

    it('shows no folder total while the accounts the server summed are not the folder\'s', () => {
        // An account moved into the folder here, and the server has not answered since.
        const accounts = [account('a', 'Home'), account('b', 'Moved in')];
        const folders = [folder('f', 'Box', { accountIds: ['a', 'b'] })];
        const [section] = buildSections(
            accounts, folders, balances(['a', 10], ['b', 5]), subtotals([['bank', 15]], [['f', ['a'], 10]]), false,
        );
        const box = folderNamed(section!.children, 'Box');
        expect(box.total.amount).toBeNull();
        expect(formatTotal(box.total)).toBe('');
        // The section's accounts did not change.
        expect(section!.total.amount).toBe(15);
    });

    it('shows no total while the server\'s totals load', () => {
        const accounts = [account('a', 'Home')];
        const folders = [folder('f', 'Box', { accountIds: ['a'] })];
        const [section] = buildSections(accounts, folders, balances(['a', 10]), undefined, false);
        expect(formatTotal(section!.total)).toBe('');
        expect(formatTotal(folderNamed(section!.children, 'Box').total)).toBe('');
    });

    it('draws an inactive account only when asked, whatever the totals', () => {
        const accounts = [
            account('live', 'Live'),
            account('gone', 'Gone', { isActive: false }),
        ];
        const folders = [folder('f', 'Box', { accountIds: ['live', 'gone'] })];
        const money = balances(['live', 10], ['gone', 5]);
        const totals = subtotals([['bank', 15]], [['f', ['live', 'gone'], 15]]);

        const [hidden] = buildSections(accounts, folders, money, totals, false);
        const box = folderNamed(hidden!.children, 'Box');
        expect(names(box.children)).toEqual(['Live']);
        expect(box.total.amount).toBe(15);
        expect(hidden!.total.amount).toBe(15);

        const [shown] = buildSections(accounts, folders, money, totals, true);
        expect(names(folderNamed(shown!.children, 'Box').children)).toEqual(['Gone', 'Live']);
        expect(folderNamed(shown!.children, 'Box').total.amount).toBe(15);
    });

    it('keeps liabilities negative, as the dashboard shows them', () => {
        const accounts = [account('card', 'Card', { accountType: 'credit_card' })];
        const [section] = buildSections(accounts, [], balances(['card', -1240.5]), subtotals([['credit_card', -1240.5]], []), false);
        expect(section!.label).toBe('Credit cards');
        expect(formatTotal(section!.total)).toBe(formatCurrency(-1240.5, 'USD'));
    });

    it('drops system rows and categories, and orders sections by account type', () => {
        const accounts = [
            account('loan', 'Mortgage', { accountType: 'loan' }),
            account('bank', 'Checking'),
            account('hold', 'Holdings', { accountType: 'holding', isSystem: true }),
            account('cat', 'Groceries', { accountType: 'category', categoryKind: 'expense' }),
        ];
        const sections = buildSections(accounts, [], new Map(), undefined, false);
        expect(sections.map((s) => s.label)).toEqual(['Banking', 'Loans']);
    });

    it('shows an empty folder with no figure, and a section that holds only one', () => {
        const sections = buildSections(
            [], [folder('f', 'Empty', { accountType: 'asset' })], new Map(), subtotals([], [['f', [], 0]]), false,
        );
        expect(sections.map((s) => s.label)).toEqual(['Assets']);
        expect(folderNamed(sections[0]!.children, 'Empty').total.amount).toBeNull();
        expect(formatTotal(sections[0]!.total)).toBe('');
    });

    it('puts a folder whose parent the list no longer has at the top level', () => {
        const sections = buildSections(
            [],
            [folder('orphan', 'Orphan', { parentId: 'gone' })],
            new Map(),
            undefined,
            false,
        );
        expect(names(sections[0]!.children)).toEqual(['[Orphan]']);
    });
});

describe('resolveMove', () => {
    const folders = [
        folder('outer', 'Outer', { accountIds: ['in-outer'] }),
        folder('inner', 'Inner', { parentId: 'outer' }),
        folder('card-folder', 'Cards', { accountType: 'credit_card' }),
    ];

    it('moves an account into a folder of its type, or back to the top level', () => {
        const item = { kind: 'account' as const, id: 'loose', accountType: 'bank' };
        expect(resolveMove(item, { kind: 'folder', folderId: 'inner' }, folders))
            .toEqual({ kind: 'move', parentId: 'inner' });
        const filed = { kind: 'account' as const, id: 'in-outer', accountType: 'bank' };
        expect(resolveMove(filed, { kind: 'section', accountType: 'bank' }, folders))
            .toEqual({ kind: 'move', parentId: null });
        expect(resolveMove(filed, { kind: 'folder', folderId: 'outer' }, folders))
            .toEqual({ kind: 'noop' });
    });

    it('refuses a target of another account type', () => {
        const item = { kind: 'account' as const, id: 'loose', accountType: 'bank' };
        expect(resolveMove(item, { kind: 'folder', folderId: 'card-folder' }, folders).kind).toBe('invalid');
        expect(resolveMove(item, { kind: 'section', accountType: 'loan' }, folders).kind).toBe('invalid');
    });

    it('refuses a folder dropped on itself or on anything beneath it', () => {
        const item = { kind: 'folder' as const, id: 'outer', accountType: 'bank' };
        expect(resolveMove(item, { kind: 'folder', folderId: 'outer' }, folders).kind).toBe('invalid');
        expect(resolveMove(item, { kind: 'folder', folderId: 'inner' }, folders).kind).toBe('invalid');
        const inner = { kind: 'folder' as const, id: 'inner', accountType: 'bank' };
        expect(resolveMove(inner, { kind: 'section', accountType: 'bank' }, folders))
            .toEqual({ kind: 'move', parentId: null });
    });

    it('lists every valid destination by path, and only those', () => {
        const extra = [...folders, folder('deep', 'Deep', { parentId: 'inner' })];
        const loose = { kind: 'account' as const, id: 'loose', accountType: 'bank' };
        expect(moveDestinations(loose, extra).map((d) => d.path)).toEqual([
            'Outer', 'Outer › Inner', 'Outer › Inner › Deep',
        ]);
        const inner = { kind: 'folder' as const, id: 'inner', accountType: 'bank' };
        // Not itself, not its own child, not the parent it is already in.
        expect(moveDestinations(inner, extra)).toEqual([]);
    });
});

describe('withAccountMoved', () => {
    it('takes the account out of its old folder and into the new one', () => {
        const moved = withAccountMoved(
            [folder('a', 'A', { accountIds: ['x', 'y'] }), folder('b', 'B')],
            'x',
            'b',
        );
        expect(moved.map((f) => f.accountIds)).toEqual([['y'], ['x']]);
        expect(withAccountMoved(moved, 'x', null).map((f) => f.accountIds)).toEqual([['y'], []]);
    });
});
