import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { categoryChipVariant } from '@/lib/categoryChip';
import type { InvestmentRow } from '@/lib/types';

import { investmentStrategy } from './investmentStrategy';

/**
 * The investment register's category and fee chips: neutral for the system
 * Uncategorized category, a hashed colour for any other.
 */

const coloured = Array.from({ length: 50 }, (_, i) => `00000000-0000-0000-0000-0000000004${String(i).padStart(2, '0')}`)
    .filter((id) => categoryChipVariant('Uncategorized', 'category', id, false) !== 'default');
const SYSTEM_ID = coloured[0]!;
const OTHER_ID = coloured[1]!;

const row = (over: Partial<InvestmentRow>): InvestmentRow => ({
    ...NO_NOTES,
    kind: 'investment', id: 'leg-1', accountId: 'brokerage', payee: 'Payee One', amount: -10,
    postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared', isHidden: false,
    balanceAfter: 0, origin: 'manual', isPending: false, investmentAction: 'misc', externalId: null,
    currencyCode: 'USD',
    balanceCurrencyCode: 'USD',
    checkNumber: null, counterpartyId: 'cp', txnGroupId: null, legIndex: 0, counterpartyAccountId: null,
    counterpartyAccountName: null, counterpartyAccountType: null, headerId: 'h', clearedAt: null,
    clearedByUserId: null, createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null,
    needsReview: false, securityId: null, securityTicker: null, securityName: null, quantity: null,
    unitPrice: null, postingRole: 'income', ingestActionHint: null, ingestSecurityId: null,
    ingestShares: null, ingestUnitPrice: null, ingestFee: null, ingestAmount: null,
    ingestSecurityTickerHint: null, categoryAccountId: null, categoryAccountName: null,
    categoryAccountType: null, transferAccountId: null, transferAccountName: null,
    transferAccountType: null, feeAmount: null, feeCategoryId: null, feeCategoryName: null,
    providerRawPayload: null, headerAccountNetAmount: null, providerKey: null, isMergeWinner: false,
    importSource: null, derivedAction: null, accountPostingsOnHeader: 1, headerTotalPostings: 2,
    ...over,
});

const chipOf = (label: string) => screen.getByText(label).closest('span[class*="bg-"]');

describe('the investment register\'s Uncategorized chip', () => {
    it('is neutral as the category and as the fee category', () => {
        render(<>{investmentStrategy.renderSlot6(row({
            categoryAccountId: SYSTEM_ID, categoryAccountName: 'Uncategorized', categoryAccountType: 'category',
            feeCategoryId: SYSTEM_ID, feeCategoryName: 'Uncategorized fee', feeAmount: 1,
        }), undefined, new Set([SYSTEM_ID]))}</>);

        expect(chipOf('Uncategorized')).toHaveClass('bg-chip-neutral');
        expect(chipOf('Uncategorized fee')).toHaveClass('bg-chip-neutral');
    });

    it('keeps another category\'s colour', () => {
        render(<>{investmentStrategy.renderSlot6(row({
            categoryAccountId: OTHER_ID, categoryAccountName: 'Uncategorized', categoryAccountType: 'category',
        }), undefined, new Set([SYSTEM_ID]))}</>);

        expect(chipOf('Uncategorized')).not.toHaveClass('bg-chip-neutral');
    });
});
