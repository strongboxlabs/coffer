import type { AccountSummary, BankRow, InvestmentRow } from '@/lib/types';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';

/**
 * Builders for the style guide's placeholder accounts and register rows, shared by its
 * sample sections. Each account and row is in the currency it is given, USD unless it
 * names another, so a sample can show a figure at its own currency's places (ADR-0114
 * D14, D15). Placeholder data only.
 */

/** The one ledger every sample belongs to. */
export const SAMPLE_LEDGER = 'styleguide';
/** The category a bank row faces unless it names another. */
export const SAMPLE_DINING = 'sg-dining';

export const sampleAccount = (
    id: string,
    name: string,
    accountType: AccountSummary['accountType'],
    currencyCode = 'USD',
): AccountSummary => ({
    id, name, accountType,
    ledgerId: SAMPLE_LEDGER, parentId: null,
    categoryKind: accountType === 'category' ? 'expense' : null,
    currencyCode, isActive: true, isSystem: false, feedConnectionId: null,
    needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
});

/** Placeholder ids, unique per row. The samples are static, so a re-render only remounts them. */
const nextId = (() => {
    let n = 0;
    return () => { n += 1; return n; };
})();

/** A bank register row: its Amount in `currencyCode`, its Balance in `balanceCurrencyCode`. */
export function sampleBankRow(over: Partial<BankRow> & { accountId: string }): BankRow {
    const n = nextId();
    return {
        ...NO_NOTES,
        kind: 'bank', id: `sg-leg-${n}`, payee: 'Corner Café', amount: -12,
        postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared',
        isHidden: false, currencyCode: 'USD', balanceAfter: 1988, balanceCurrencyCode: 'USD', origin: 'manual',
        isPending: false, externalId: null, checkNumber: null, counterpartyId: `sg-cp-${n}`,
        txnGroupId: null, legIndex: 0, counterpartyAccountId: SAMPLE_DINING,
        counterpartyAccountName: 'Food/Dining', counterpartyAccountType: 'category',
        headerId: `sg-hdr-${n}`, clearedAt: null, clearedByUserId: null,
        createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null,
        needsReview: false, providerRawPayload: null, headerAccountNetAmount: null,
        providerKey: null, isMergeWinner: false, importSource: null, derivedAction: null,
        accountPostingsOnHeader: 1, headerTotalPostings: 1,
        ...over,
    };
}

export function sampleInvestmentRow(over: Partial<InvestmentRow> & { accountId: string }): InvestmentRow {
    const b = sampleBankRow({ accountId: over.accountId });
    return {
        ...b,
        kind: 'investment', investmentAction: 'buy', securityId: null, securityTicker: null,
        securityName: null, quantity: null, unitPrice: null, postingRole: 'security',
        ingestActionHint: null, ingestSecurityId: null, ingestShares: null, ingestUnitPrice: null,
        ingestFee: null, ingestAmount: null, ingestSecurityTickerHint: null,
        categoryAccountId: null, categoryAccountName: null, categoryAccountType: null,
        transferAccountId: null, transferAccountName: null, transferAccountType: null,
        feeAmount: null, feeCategoryId: null, feeCategoryName: null,
        ...over,
    };
}
