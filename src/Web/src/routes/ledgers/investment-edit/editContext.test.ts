import { describe, expect, it } from 'vitest';

import { ownMemo, ownTags } from '@/lib/annotations/notes';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { classifySimpleFinDescription } from '@/lib/ingest/simpleFinDescriptionClassifier';
import type { InvestmentRow, LedgerInvestmentAction } from '@/lib/types';

import { investmentEditContext } from './editContext';
import { hintToDraft } from './hintToDraft';
import { legsToDraft } from './legsToDraft';

/**
 * Characterisation: the investment editor's edit context is what the investment
 * register built inline when it opened a row. `registerInline` below is that
 * construction as the register had it, kept here as the reference, with the one
 * field added since (the role of the leg an inherited value is on); each case
 * asserts the extracted function returns the same, and pins the fields a save
 * depends on (the tax date, the inherited notes, the review state, the hint).
 */
function registerInline(editingHeaderId: string, accountId: string, legs: InvestmentRow[] | undefined) {
    if (!legs || legs.length === 0) return null;
    const canonical = legs.find((l) => l.accountId === accountId) ?? legs[0]!;
    const headerFields = {
        postedAt: canonical.postedAt,
        transactedAt: canonical.transactedAt,
        payee: canonical.payee,
        memo: ownMemo(canonical),
        checkNumber: canonical.checkNumber,
        tags: ownTags(canonical),
    };
    const inherited = {
        memo: canonical.inheritedMemo,
        tags: canonical.inheritedTags,
        fromAccountId: canonical.inheritFromAccountId,
        fromRole: legs.find((l) => l.accountId === canonical.inheritFromAccountId)?.postingRole ?? null,
    };
    if (canonical.investmentAction === null) {
        const isFeedImported = canonical.origin !== 'manual';
        const hasHint = canonical.ingestActionHint !== null;
        if (!hasHint && !isFeedImported) return null;
        const seedAction = canonical.ingestActionHint as LedgerInvestmentAction | null;
        const draft = hintToDraft(
            seedAction, accountId, legs.find((l) => l.accountId === accountId)?.currencyCode ?? null,
            headerFields, legs, canonical.ingestSecurityId,
            canonical.ingestShares, canonical.ingestUnitPrice, canonical.ingestFee, canonical.ingestAmount,
        );
        const persistedHint = canonical.ingestSecurityTickerHint;
        const fallbackHint = persistedHint === null && canonical.providerKey === 'simplefin'
            ? classifySimpleFinDescription(canonical.payee).tickerHint
            : null;
        const resolvedHint = persistedHint ?? fallbackHint;
        const providerSecurityHint = resolvedHint && canonical.providerKey
            ? { providerKey: canonical.providerKey, providerSecurityId: resolvedHint }
            : undefined;
        return { headerId: editingHeaderId, initialDraft: draft, providerSecurityHint, needsReview: canonical.needsReview, inherited };
    }
    const draft = legsToDraft(canonical.investmentAction as LedgerInvestmentAction, accountId, headerFields, legs);
    return { headerId: editingHeaderId, initialDraft: draft, needsReview: canonical.needsReview, inherited };
}

const BROKERAGE = '11111111-1111-1111-1111-111111111111';
const UNCATEGORIZED = '22222222-2222-2222-2222-222222222222';
const INCOME_CAT = '33333333-3333-3333-3333-333333333333';
const OTHER_SIDE = '44444444-4444-4444-4444-444444444444';
const SECURITY = '99999999-9999-9999-9999-999999999999';
const HEADER = '00000000-0000-0000-0000-000000000aaa';

function leg(over: Partial<InvestmentRow> & { accountId: string; amount: number }): InvestmentRow {
    return {
        ...NO_NOTES,
        kind: 'investment', id: `leg-${over.accountId.slice(0, 4)}`, payee: 'Payee One', memo: null,
        postedAt: '2026-05-15T00:00:00Z', transactedAt: '2026-05-14T00:00:00Z', status: 'uncleared',
        isHidden: false, balanceAfter: null, origin: 'manual', isPending: false,
        currencyCode: 'USD', balanceCurrencyCode: 'USD',
        investmentAction: null, externalId: null, checkNumber: null, counterpartyId: 'cp',
        txnGroupId: null, legIndex: 0, counterpartyAccountId: null, counterpartyAccountName: null,
        counterpartyAccountType: null, tags: [], headerId: HEADER, clearedAt: null,
        clearedByUserId: null, createdAt: '2026-05-15T00:00:00Z', onlineMatchFitid: null,
        onlineMatchFiId: null, needsReview: false, securityId: null, securityTicker: null,
        securityName: null, quantity: null, unitPrice: null, postingRole: null,
        ingestActionHint: null, ingestSecurityId: null, ingestShares: null, ingestUnitPrice: null,
        ingestFee: null, ingestAmount: null, ingestSecurityTickerHint: null,
        categoryAccountId: null, categoryAccountName: null, categoryAccountType: null,
        transferAccountId: null, transferAccountName: null, transferAccountType: null,
        feeAmount: null, feeCategoryId: null, feeCategoryName: null, providerRawPayload: null,
        headerAccountNetAmount: null, providerKey: null, isMergeWinner: false, importSource: null,
        derivedAction: null, accountPostingsOnHeader: 1, headerTotalPostings: 2,
        ...over,
    };
}

const INHERITING = {
    memo: 'Other side memo', memoInherited: true, tags: ['Tag A'], tagsInherited: true,
    inheritedMemo: 'Other side memo', inheritedTags: ['Tag A'], inheritFromAccountId: OTHER_SIDE,
};

function bothAgree(legs: InvestmentRow[] | undefined) {
    const got = investmentEditContext(HEADER, BROKERAGE, legs);
    expect(got).toEqual(registerInline(HEADER, BROKERAGE, legs));
    return got;
}

describe('investmentEditContext', () => {
    it('is null with no legs yet, and with an empty leg set', () => {
        expect(bothAgree(undefined)).toBeNull();
        expect(bothAgree([])).toBeNull();
    });

    it('is null for a manual bank-shape row', () => {
        expect(bothAgree([
            leg({ accountId: BROKERAGE, amount: -20 }),
            leg({ accountId: UNCATEGORIZED, amount: 20, legIndex: 1 }),
        ])).toBeNull();
    });

    it('seeds a feed row with a hint from the hint, carrying the provider\'s ticker', () => {
        const feed = {
            origin: 'simplefin', providerKey: 'simplefin', needsReview: true,
            ingestActionHint: 'buy', ingestSecurityId: SECURITY, ingestShares: 2,
            ingestUnitPrice: 10, ingestAmount: -20, ingestSecurityTickerHint: 'TICKA',
        };
        const got = bothAgree([
            leg({ accountId: UNCATEGORIZED, amount: 20, legIndex: 1, ...feed }),
            leg({ accountId: BROKERAGE, amount: -20, ...feed, ...INHERITING }),
        ]);

        expect(got).not.toBeNull();
        expect(got!.initialDraft.action).toBe('buy');
        expect(got!.initialDraft.securityId).toBe(SECURITY);
        expect(got!.initialDraft.transactedAt).toBe('2026-05-14');
        expect(got!.providerSecurityHint).toEqual({ providerKey: 'simplefin', providerSecurityId: 'TICKA' });
        expect(got!.needsReview).toBe(true);
        // The brokerage's own notes are blank while it inherits; the editor
        // shows the other side's as placeholder.
        expect(got!.initialDraft.memo).toBe('');
        expect(got!.initialDraft.tags).toEqual([]);
        expect(got!.inherited).toEqual({
            memo: 'Other side memo', tags: ['Tag A'], fromAccountId: OTHER_SIDE, fromRole: null,
        });
    });

    it('opens a feed row without a hint with a blank action and no provider ticker', () => {
        const feed = { origin: 'simplefin', providerKey: 'simplefin', needsReview: true };
        const got = bothAgree([
            leg({ accountId: BROKERAGE, amount: -20, ...feed }),
            leg({ accountId: UNCATEGORIZED, amount: 20, legIndex: 1, ...feed }),
        ]);

        expect(got).not.toBeNull();
        expect(got!.initialDraft.action).toBeNull();
        expect(got!.providerSecurityHint).toBeUndefined();
        expect(got!.initialDraft.transactedAt).toBe('2026-05-14');
        expect(got!.needsReview).toBe(true);
        expect(got!.inherited).toEqual({ memo: null, tags: [], fromAccountId: null, fromRole: null });
    });

    it('names the role of the leg an inherited value is on: a feed row\'s leg has none', () => {
        const xfr = { investmentAction: 'transfer', headerTotalPostings: 1 };
        const event = bothAgree([
            leg({ accountId: OTHER_SIDE, amount: -50, legIndex: 1, postingRole: 'transfer', ...xfr }),
            leg({ accountId: BROKERAGE, amount: 50, postingRole: 'transfer', ...xfr, ...INHERITING }),
        ]);
        expect(event!.inherited).toEqual({
            memo: 'Other side memo', tags: ['Tag A'], fromAccountId: OTHER_SIDE, fromRole: 'transfer',
        });

        const feed = { origin: 'simplefin', providerKey: 'simplefin', needsReview: true, headerTotalPostings: 1 };
        const fed = bothAgree([
            leg({ accountId: BROKERAGE, amount: 50, ...feed, ...INHERITING }),
            leg({ accountId: OTHER_SIDE, amount: -50, legIndex: 1, ...feed }),
        ]);
        expect(fed!.inherited.fromRole).toBeNull();
    });

    it('inverts investment-shape legs, reading the brokerage\'s leg first', () => {
        const event = { investmentAction: 'dividend_cash', needsReview: false, headerTotalPostings: 2 };
        const got = bothAgree([
            leg({ accountId: INCOME_CAT, amount: -15, legIndex: 1, postingRole: 'income', ...event }),
            leg({
                accountId: BROKERAGE, amount: 15, postingRole: 'income', securityId: SECURITY,
                memo: 'Own memo', tags: ['Tag B'], ...event,
            }),
        ]);

        expect(got).not.toBeNull();
        expect(got!.initialDraft.action).toBe('dividend_cash');
        expect(got!.initialDraft.memo).toBe('Own memo');
        expect(got!.initialDraft.tags).toEqual(['Tag B']);
        expect(got!.initialDraft.transactedAt).toBe('2026-05-14');
        expect('providerSecurityHint' in got!).toBe(false);
        expect(got!.needsReview).toBe(false);
    });
});
