import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';

import { categoryChipVariant } from '@/lib/categoryChip';

import { account, budget, CHECKING, homeStub, renderHome } from '../homeTestFixtures';

/** This month's over-the-mark rows: the system Uncategorized category's dot is neutral. */

const coloured = Array.from({ length: 50 }, (_, i) => `00000000-0000-0000-0000-0000000004${String(i).padStart(2, '0')}`)
    .filter((id) => categoryChipVariant('Uncategorized', 'category', id, false) !== 'default');
const SYSTEM_ID = coloured[0]!;
const OWN_ID = coloured[1]!;

afterEach(() => {
    vi.unstubAllGlobals();
});

describe('This month\'s Uncategorized dot', () => {
    it('is neutral for the system category, and another category keeps its colour', async () => {
        homeStub({
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
                account({ id: SYSTEM_ID, name: 'Uncategorized', accountType: 'category', categoryKind: 'expense', isSystem: true }),
                account({ id: OWN_ID, name: 'Own category', accountType: 'category', categoryKind: 'expense' }),
            ],
            budget: budget({
                actualTotal: 100,
                rows: [
                    { categoryId: SYSTEM_ID, name: 'Uncategorized', parentId: null, actual: 60, typical: 10, target: null, targetCurrencyCode: null, mark: 10 },
                    { categoryId: OWN_ID, name: 'Own category', parentId: null, actual: 40, typical: 10, target: null, targetCurrencyCode: null, mark: 10 },
                ],
            }),
        });
        renderHome();

        const system = await screen.findByRole('link', { name: /^Uncategorized/ });
        const own = await screen.findByRole('link', { name: /^Own category/ });
        expect(system.querySelector('.size-dot-sm')).toHaveClass('bg-chip-neutral-text');
        expect(own.querySelector('.size-dot-sm')).not.toBeNull();
        expect(own.querySelector('.size-dot-sm')).not.toHaveClass('bg-chip-neutral-text');
    });
});
