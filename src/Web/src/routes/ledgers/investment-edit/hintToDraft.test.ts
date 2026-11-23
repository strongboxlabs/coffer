import { describe, expect, it } from 'vitest';
import type { InvestmentRow } from '@/lib/types';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { hintToDraft } from './hintToDraft';

// The brokerage cash leg nets to ~0 for a reinvestment, so a prefill
// that seeded Amount from it would open an imported Reinvest with
// Amount = 0 (and so un-Acceptable). The Amount for the linked actions
// is shares × price, and that is what the prefill seeds.

const BROKERAGE = '11111111-1111-1111-1111-111111111111';
const SECURITY  = '99999999-9999-9999-9999-999999999999';

const header = {
    postedAt: '2026-05-29T00:00:00Z',
    payee: 'MONEY MARKET FUND A',
    memo: 'DIVIDEND REINVESTMENT',
    checkNumber: null,
};

function mkLeg(
    overrides: Partial<InvestmentRow> & { accountId: string; amount: number },
): InvestmentRow {
    return {
        ...NO_NOTES,
        kind: 'investment',
        id: crypto.randomUUID(),
        payee: header.payee,
        memo: header.memo,
        postedAt: header.postedAt,
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        balanceAfter: null,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
        origin: 'simplefin',
        isPending: false,
        investmentAction: null,
        externalId: null,
        counterpartyId: 'cp',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        checkNumber: null,
        tags: [],
        headerId: 'h',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: header.postedAt,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: true,
        securityId: null,
        securityTicker: null,
        securityName: null,
        quantity: null,
        unitPrice: null,
        postingRole: null,
        ingestActionHint: null,
        ingestSecurityId: null,
        ingestShares: null,
        ingestUnitPrice: null,
        ingestFee: null,
        ingestAmount: null,
        ingestSecurityTickerHint: null,
        categoryAccountId: null,
        categoryAccountName: null,
        categoryAccountType: null,
        transferAccountId: null,
        transferAccountName: null,
        transferAccountType: null,
        feeAmount: null,
        feeCategoryId: null,
        feeCategoryName: null,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: 'simplefin',
        isMergeWinner: false,
        importSource: null,
        derivedAction: null,
        accountPostingsOnHeader: 1,
        headerTotalPostings: 1,
        ...overrides,
    };
}

describe('hintToDraft', () => {
    it('prefers the stated total from the file over shares × price', () => {
        // The shape, from an OFX reinvest: the file states a TOTAL,
        // while a three-decimal unit count at the file's rounded two-decimal
        // price multiplies out to one cent less.
        // ADR-0073 D1 says those need not agree — the total is authoritative —
        // and before mig 228 nothing carried it, so opening the row rebuilt the
        // amount one cent adrift and Accept persisted that. A cent is enough to
        // make the row stop matching its own duplicate: merge candidates are
        // found by exact principal.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 0 })];
        const draft = hintToDraft(
            'dividend_reinvest', BROKERAGE, 'USD', header, legs,
            /* ingestSecurityId */ null,
            /* ingestShares */ 12.345,
            /* ingestUnitPrice */ 40.10,
            /* ingestFee */ null,
            /* ingestAmount */ 495.04,
        );
        expect(draft.amount).toBe(495.04);
        // …and the derived price follows the authoritative amount, not the
        // rounded one the file happened to print.
        expect(draft.shares).toBe(12.345);
    });

    it('still falls back to shares × price when the file stated no total', () => {
        // Every row imported before mig 228, and every provider that states
        // none. The reconstruction is a last resort, not the norm — but it must
        // still open with something rather than 0, which would block Accept.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 0 })];
        const draft = hintToDraft(
            'dividend_reinvest', BROKERAGE, 'USD', header, legs,
            /* ingestSecurityId */ null,
            /* ingestShares */ 12.345,
            /* ingestUnitPrice */ 40.10,
            /* ingestFee */ null,
            /* ingestAmount */ null,
        );
        expect(draft.amount).toBe(495.03);
    });

    it('seeds Reinvest Amount from shares × price (cash leg nets to ~0)', () => {
        // A reinvestment is cash-neutral on the brokerage leg.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 0 })];
        const draft = hintToDraft(
            'dividend_reinvest', BROKERAGE, 'USD', header, legs,
            /* ingestSecurityId */ null,
            /* ingestShares */ 1234.56,
            /* ingestUnitPrice */ 1,
            /* ingestFee */ null,
        );
        expect(draft.shares).toBe(1234.56);
        expect(draft.price).toBe(1);
        // Shares × price, not 0 from the cash leg.
        expect(draft.amount).toBe(1234.56);
    });

    it('seeds buy Amount = principal (real cash total minus fee)', () => {
        // Cash leg carries principal + fee, negated (shares × price, plus the
        // fee, going out); Amount is the fee-excluded principal.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: -1010 })];
        const draft = hintToDraft(
            'buy', BROKERAGE, 'USD', header, legs, SECURITY, 100, 10, 10,
        );
        expect(draft.amount).toBe(1000);
    });

    it('seeds buy Amount from the real settled total, not shares × rounded-price', () => {
        // OFX can give a three-decimal share count at a rounded two-decimal
        // price, with the trade settling cents away from shares × price.
        // Recomputing from shares × price would silently change the amount on
        // open — the real total (no fee here) is authoritative.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: -124.18 })];
        const draft = hintToDraft(
            'buy', BROKERAGE, 'USD', header, legs, SECURITY, 4.125, 30.10, null,
        );
        expect(draft.amount).toBe(124.18);
        // Price is derived from the authoritative amount (÷ |shares|, 6dp),
        // superseding the wire's rounded price so it reconciles to the settled total.
        expect(draft.price).toBe(
            Number.parseFloat((124.18 / 4.125).toFixed(6)));
        expect(draft.price! * 4.125).toBeCloseTo(124.18, 2);
        expect(draft.price).not.toBe(30.10);
    });

    it('seeds sell Amount as gross proceeds (cash net + fee)', () => {
        // A sell nets proceeds − fee into cash (shares × price, less the fee);
        // the Amount field is the gross proceeds.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 990 })];
        const draft = hintToDraft(
            'sell', BROKERAGE, 'USD', header, legs, SECURITY, 100, 10, 10,
        );
        expect(draft.amount).toBe(1000);
    });

    it('falls back to the signed cash leg when shares/price are absent', () => {
        // dividend_cash isn't a linked action — Amount is user-input,
        // seeded from the signed cash leg.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 42 })];
        const draft = hintToDraft(
            'dividend_cash', BROKERAGE, 'USD', header, legs, null, null, null, null,
        );
        expect(draft.amount).toBe(42);
    });
});

// ADR-0114 D15: an amount the draft derives is the app's own figure, rounded once at
// the brokerage currency's places, half away from zero; a total that rounds to zero
// there counts as none.
describe('hintToDraft at the brokerage currency\'s places', () => {
    const reinvest = (currency: string | null, shares: number, price: number, total: number | null = null) =>
        hintToDraft(
            'dividend_reinvest', BROKERAGE, currency, header, [mkLeg({ accountId: BROKERAGE, amount: 0 })],
            /* ingestSecurityId */ null,
            /* ingestShares */ shares,
            /* ingestUnitPrice */ price,
            /* ingestFee */ null,
            /* ingestAmount */ total,
        );

    it('derives shares × price at whole units on a whole-unit brokerage, a midpoint away from zero', () => {
        // Three units at a price ending in a half multiply out to a half unit.
        const draft = reinvest('JPY', 3, 333.5);
        expect(draft.amount).toBe(1001);
        // The price reconciles to the rounded amount, at six places: not money.
        expect(draft.price).toBe(Number.parseFloat((1001 / 3).toFixed(6)));
    });

    it('derives shares × price at three places on a three-place brokerage', () => {
        expect(reinvest('KWD', 2, 1.2345).amount).toBe(2.469);
    });

    it('rounds the file\'s stated total at the brokerage\'s places', () => {
        // A stated total with a half unit, on a whole-unit brokerage.
        expect(reinvest('JPY', 3, 333, 1234.5).amount).toBe(1235);
    });

    it('treats a stated total under half a whole unit as none, and falls back to shares × price', () => {
        // Present at two places, it rounds to zero at none: no total to prefer.
        expect(reinvest('JPY', 2, 500, 0.3).amount).toBe(1000);
        // The same residue is a total on a two-place brokerage.
        expect(reinvest('USD', 2, 500, 0.3).amount).toBe(0.3);
    });

    it('rounds nothing while the currency is not known', () => {
        expect(reinvest(null, 2, 1.2345).amount).toBe(2.469);
        expect(reinvest(null, 3, 333.5).amount).toBe(1000.5);
    });

    // The fee carrier is the file's figure as sent, finer than the currency when the
    // file was. The prefill rounds it once: rounding a 4-place cut of it again would
    // round twice, and here would land a cent above the file's figure.
    const buyWithFee = (currency: string | null, cash: number, fee: number) =>
        hintToDraft(
            'buy', BROKERAGE, currency, header, [mkLeg({ accountId: BROKERAGE, amount: cash })],
            SECURITY, /* ingestShares */ 10, /* ingestUnitPrice */ 25, fee,
        );

    it('rounds the fee prefill once at the brokerage\'s places', () => {
        const draft = buyWithFee('USD', -251, 1.00495);
        expect(draft.feeAmount).toBe(1);
        // The principal takes the fee the draft carries, so the two add back to the
        // cash the trade settled at.
        expect(draft.amount).toBe(250);
        expect(draft.amount! + draft.feeAmount!).toBe(251);
    });

    it('takes the principal from the rounded fee, so a midpoint fee still adds back to the cash', () => {
        // The fee rounds up to the next cent. A principal taken from the fee as sent
        // would round up too, and Amount plus Fee would be a cent over the cash.
        const draft = buyWithFee('USD', -251, 1.005);
        expect(draft.feeAmount).toBe(1.01);
        expect(draft.amount).toBe(249.99);
    });

    it('rounds the fee prefill at whole units and at three places', () => {
        expect(buyWithFee('JPY', -2600, 100.5).feeAmount).toBe(101);
        expect(buyWithFee('KWD', -251.5, 1.23456).feeAmount).toBe(1.235);
    });

    it('treats a fee under half a minor unit as none', () => {
        const draft = buyWithFee('USD', -250, 0.004);
        expect(draft.feeAmount).toBeNull();
        expect(draft.amount).toBe(250);
    });

    it('keeps the fee as sent while the currency is not known', () => {
        expect(buyWithFee(null, -251, 1.00495).feeAmount).toBe(1.00495);
    });
});
