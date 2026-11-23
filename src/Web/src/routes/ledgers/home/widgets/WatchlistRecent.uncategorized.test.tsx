import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';

import { categoryChipVariant } from '@/lib/categoryChip';

import { account, bankRow, CHECKING, homeStub, layoutWith, renderHome, txnEntry } from '../homeTestFixtures';

/**
 * The Watchlist's dot and Recent transactions' category chip: the system
 * Uncategorized category is neutral in both, and a person's own category of the
 * same name keeps its colour. Through the real page, mocked at fetch, so the
 * system flag comes from the accounts read as it does in the app. Every name and
 * figure is invented.
 */

// Ids that hash to a colour under the name, so only the system flag can make
// one neutral.
const coloured = Array.from({ length: 50 }, (_, i) => `00000000-0000-0000-0000-0000000004${String(i).padStart(2, '0')}`)
    .filter((id) => categoryChipVariant('Uncategorized', 'category', id, false) !== 'default');
const SYSTEM_ID = coloured[0]!;
const OWN_ID = coloured[1]!;
const PARENT_ID = 'cat-parent';

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const panel = (title: RegExp) => screen.findByRole('heading', { level: 2, name: title })
    .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

describe('Uncategorized in the Watchlist and Recent transactions', () => {
    it('is the neutral dot and chip for the system category, and an own category of that name keeps its colour', async () => {
        homeStub({
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
                account({ id: SYSTEM_ID, name: 'Uncategorized', accountType: 'category', categoryKind: 'expense', isSystem: true }),
                account({ id: PARENT_ID, name: 'Category P', accountType: 'category', categoryKind: 'expense' }),
                account({ id: OWN_ID, name: 'Uncategorized', accountType: 'category', categoryKind: 'expense', parentId: PARENT_ID }),
            ],
            prefs: layoutWith({
                watchlist: { visible: true, settings: { categoryIds: [SYSTEM_ID, OWN_ID] } },
                'recent-transactions': { visible: true, settings: {} },
            }),
            entries: [
                txnEntry(bankRow({ id: 'leg-s', headerId: 'h-s', payee: 'Payee S', amount: -12,
                    counterpartyAccountId: SYSTEM_ID, counterpartyAccountName: 'Uncategorized' })),
                txnEntry(bankRow({ id: 'leg-o', headerId: 'h-o', payee: 'Payee O', amount: -8,
                    counterpartyAccountId: OWN_ID, counterpartyAccountName: 'Uncategorized' })),
            ],
        });
        renderHome();

        // Watchlist rows: the dot beside each watched category's path.
        const watchlist = await panel(/^Watchlist/);
        const watchedSystem = (await within(watchlist).findByText('Uncategorized')).closest('a')!;
        const watchedOwn = (await within(watchlist).findByText('Category P/Uncategorized')).closest('a')!;
        expect(watchedSystem.querySelector('.size-dot-sm')).toHaveClass('bg-chip-neutral-text');
        expect(watchedOwn.querySelector('.size-dot-sm')).not.toBeNull();
        expect(watchedOwn.querySelector('.size-dot-sm')).not.toHaveClass('bg-chip-neutral-text');

        // Recent rows: the register's category chip, titled with its path.
        const recent = await panel(/^Recent transactions/);
        const recentSystem = (await within(recent).findByText('Payee S')).closest('a')!;
        const recentOwn = (await within(recent).findByText('Payee O')).closest('a')!;
        expect(within(recentSystem).getByTitle('Uncategorized')).toHaveClass('bg-chip-neutral');
        expect(within(recentOwn).getByTitle('Category P/Uncategorized')).not.toHaveClass('bg-chip-neutral');
    });
});
