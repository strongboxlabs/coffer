import { describe, it, expect } from 'vitest';
import {
    draftToCreateRequest,
    draftToPatchRequest,
    seedTransactedAt,
    validate,
    type InvestmentTxnDraft,
} from './validation';

/**
 * Per-action validation tests against the ADR-0029 matrix. Each test
 * exercises one action's required-field set + at least one rejection
 * path. The pure-function design lets these run without rendering
 * anything.
 */

const BROKERAGE = '00000000-0000-0000-0000-0000000000aa';
const SECURITY  = '00000000-0000-0000-0000-0000000000bb';
const CATEGORY  = '00000000-0000-0000-0000-0000000000cc';
const TRANSFER  = '00000000-0000-0000-0000-0000000000dd';
const FEE_CAT   = '00000000-0000-0000-0000-0000000000ee';

function emptyDraft(): InvestmentTxnDraft {
    return {
        brokerageAccountId: BROKERAGE,
        postedAt: '2026-05-21',
        transactedAt: '',
        action: null,
        payee: '',
        memo: '',
        checkNumber: '',
        securityId: null,
        shares: null,
        price: null,
        amount: null,
        categoryAccountId: null,
        transferAccountId: null,
        feeAccountId: null,
        feeAmount: null,
        tags: [],
    };
}

describe('tax date — payload builders', () => {
    // A draft that carries no tax date makes every PATCH omit it, and the
    // investment PATCH is wholesale-replace — an omitted field CLEARS the
    // stored value. Editing an unrelated field on a transaction with a distinct
    // tax date would silently destroy that tax date, so these pin that the
    // builders carry it.
    const buy = (): InvestmentTxnDraft => ({
        ...emptyDraft(),
        action: 'buy',
        securityId: SECURITY,
        shares: 10,
        price: 100,
    });

    it('sends the posted date when no distinct tax date is set', () => {
        const req = draftToCreateRequest(buy());
        expect(req.transactedAt).toBe(new Date('2026-05-21').toISOString());
    });

    it('sends the tax date when one is set', () => {
        const req = draftToCreateRequest({ ...buy(), transactedAt: '2026-05-18' });
        expect(req.transactedAt).toBe(new Date('2026-05-18').toISOString());
    });

    it('PATCH always carries a tax date, so an unrelated edit cannot erase it', () => {
        // The regression guard. A null/absent transactedAt here is the bug.
        const opened = { memo: '', tags: [] };
        const req = draftToPatchRequest({ ...buy(), transactedAt: '2026-05-18' }, opened);
        expect(req.transactedAt).toBe(new Date('2026-05-18').toISOString());

        const blank = draftToPatchRequest(buy(), opened);
        expect(blank.transactedAt).toBe(new Date('2026-05-21').toISOString());
        expect(blank.transactedAt).not.toBeNull();
    });
});

describe('memo and tags on a PATCH follow presence', () => {
    const buy = (over: Partial<InvestmentTxnDraft> = {}): InvestmentTxnDraft => ({
        ...emptyDraft(),
        action: 'buy',
        securityId: SECURITY,
        shares: 10,
        price: 100,
        ...over,
    });

    it('omits both when nothing changed, so an untouched save writes neither', () => {
        const req = draftToPatchRequest(
            buy({ memo: 'Quarterly rebalance', tags: ['Retirement'] }),
            { memo: 'Quarterly rebalance', tags: ['Retirement'] },
        );
        expect('memo' in req).toBe(false);
        expect('tags' in req).toBe(false);
        // Everything else is still the wholesale new state.
        expect(req.action).toBe('buy');
    });

    it('omits them for a brokerage left inheriting the other side\'s value', () => {
        const req = draftToPatchRequest(buy(), { memo: '', tags: [] });
        expect('memo' in req).toBe(false);
        expect('tags' in req).toBe(false);
    });

    it('sends a changed memo trimmed, and a cleared one as null', () => {
        expect(draftToPatchRequest(buy({ memo: '  Monthly ' }), { memo: '', tags: [] }).memo)
            .toBe('Monthly');
        const cleared = draftToPatchRequest(buy({ memo: '' }), { memo: 'Monthly', tags: [] });
        expect('memo' in cleared).toBe(true);
        expect(cleared.memo).toBeNull();
    });

    it('sends changed tags, an emptied set as []', () => {
        expect(draftToPatchRequest(buy({ tags: ['a'] }), { memo: '', tags: [] }).tags).toEqual(['a']);
        expect(draftToPatchRequest(buy({ tags: [] }), { memo: '', tags: ['a'] }).tags).toEqual([]);
    });

    it('a create still sends what the draft holds', () => {
        const req = draftToCreateRequest(buy({ memo: ' Monthly ', tags: ['a'] }));
        expect(req.memo).toBe('Monthly');
        expect(req.tags).toEqual(['a']);
    });
});

describe('seedTransactedAt', () => {
    it('blanks a tax date that falls on the posted day', () => {
        // The common case since mig 189 made transacted_at NOT NULL: it equals
        // posted_at whenever there is no distinct tax date. Prefilling it would
        // put a redundant value in the field on virtually every row.
        expect(seedTransactedAt({
            postedAt: '2026-05-15T00:00:00Z',
            transactedAt: '2026-05-15T00:00:00Z',
        })).toBe('');
    });

    it('keeps a genuinely distinct tax date', () => {
        expect(seedTransactedAt({
            postedAt: '2026-05-15T00:00:00Z',
            transactedAt: '2026-04-28T00:00:00Z',
        })).toBe('2026-04-28');
    });

    it('treats a null tax date as blank', () => {
        expect(seedTransactedAt({ postedAt: '2026-05-15T00:00:00Z', transactedAt: null })).toBe('');
    });
});

describe('validate — action gate', () => {
    it('rejects empty action with a single top-level error', () => {
        const errors = validate(emptyDraft());
        expect(errors).toEqual({ action: expect.any(String) });
    });

    it('rejects missing brokerage account', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'buy',
            brokerageAccountId: null,
        };
        const errors = validate(draft);
        expect(errors.brokerageAccount).toBeDefined();
    });
});

describe('validate — buy / sell shape (security + shares + price)', () => {
    it('accepts a complete buy', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'buy',
            securityId: SECURITY,
            shares: 10,
            price: 650,
            amount: 6500,
        };
        expect(validate(draft)).toEqual({});
    });

    it('rejects buy with zero shares', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'buy',
            securityId: SECURITY,
            shares: 0,
            price: 650,
        };
        expect(validate(draft).shares).toMatch(/non-zero/i);
    });

    it('rejects buy with negative shares (sign-pinned by action)', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'buy',
            securityId: SECURITY,
            shares: -10,            // wrong direction for buy
            price: 650,
        };
        expect(validate(draft).shares).toMatch(/positive/i);
    });

    it('requires shares to be negative on sell', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'sell',
            securityId: SECURITY,
            shares: 5,              // wrong direction for sell
            price: 700,
        };
        expect(validate(draft).shares).toMatch(/negative.*sell/i);
    });

    it('rejects buy with non-positive price', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'buy',
            securityId: SECURITY,
            shares: 10,
            price: 0,
        };
        expect(validate(draft).price).toMatch(/positive/i);
    });
});

describe('validate — buyx / sellx shape (adds transfer destination)', () => {
    it('accepts a complete buyx', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'buyx',
            securityId: SECURITY,
            shares: 10,
            price: 650,
            amount: 6500,
            transferAccountId: TRANSFER,
        };
        expect(validate(draft)).toEqual({});
    });

    it('rejects sellx missing the transfer destination', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'sellx',
            securityId: SECURITY,
            shares: -5,
            price: 700,
        };
        expect(validate(draft).transfer).toBeDefined();
    });
});

describe('validate — dividend_cash shape (security + amount + category)', () => {
    it('accepts a complete dividend_cash', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'dividend_cash',
            securityId: SECURITY,
            amount: 25.40,
            categoryAccountId: CATEGORY,
        };
        expect(validate(draft)).toEqual({});
    });

    it('rejects dividend_cash missing amount', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'dividend_cash',
            securityId: SECURITY,
            categoryAccountId: CATEGORY,
        };
        expect(validate(draft).amount).toBeDefined();
    });

    it('rejects dividend_cash missing category', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'dividend_cash',
            securityId: SECURITY,
            amount: 25.40,
        };
        expect(validate(draft).category).toBeDefined();
    });
});

describe('validate — dividend_reinvest shape', () => {
    it('accepts a complete dividend_reinvest', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'dividend_reinvest',
            securityId: SECURITY,
            shares: 0.025,
            price: 480.30,
            amount: 12.01,
            categoryAccountId: CATEGORY,
        };
        expect(validate(draft)).toEqual({});
    });
});

describe('validate — divx shape (income + transfer)', () => {
    it('accepts a complete divx', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'divx',
            securityId: SECURITY,
            amount: 5.98,
            categoryAccountId: CATEGORY,
            transferAccountId: TRANSFER,
        };
        expect(validate(draft)).toEqual({});
    });

    it('rejects divx missing transfer destination', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'divx',
            securityId: SECURITY,
            amount: 5.98,
            categoryAccountId: CATEGORY,
        };
        expect(validate(draft).transfer).toBeDefined();
    });
});

describe('validate — transfer shape (amount + transfer only; no security)', () => {
    it('accepts a complete transfer', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'transfer',
            amount: 1000,
            transferAccountId: TRANSFER,
        };
        expect(validate(draft)).toEqual({});
    });

    it("doesn't require security on transfer", () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'transfer',
            amount: 1000,
            transferAccountId: TRANSFER,
        };
        expect(validate(draft).security).toBeUndefined();
    });
});

describe('validate — misc shape (security optional; amount + category required)', () => {
    it('accepts a misc income (positive amount)', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'misc',
            amount: 50,
            categoryAccountId: CATEGORY,
        };
        expect(validate(draft)).toEqual({});
    });

    it('accepts a misc expense (negative amount)', () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'misc',
            amount: -25,
            categoryAccountId: CATEGORY,
        };
        expect(validate(draft)).toEqual({});
    });

    it("doesn't require security on misc (optional per matrix)", () => {
        const draft: InvestmentTxnDraft = {
            ...emptyDraft(),
            action: 'misc',
            amount: 50,
            categoryAccountId: CATEGORY,
            // securityId left null on purpose
        };
        expect(validate(draft).security).toBeUndefined();
    });
});

describe('validate — fee field paired-presence rule', () => {
    const happyBuy: InvestmentTxnDraft = {
        ...emptyDraft(),
        action: 'buy',
        securityId: SECURITY,
        shares: 10,
        price: 650,
        amount: 6500,
    };

    it('accepts buy with both fee account AND positive fee amount', () => {
        const draft = { ...happyBuy, feeAccountId: FEE_CAT, feeAmount: 0.89 };
        expect(validate(draft)).toEqual({});
    });

    it('rejects fee amount without fee account', () => {
        const draft = { ...happyBuy, feeAmount: 0.89 };
        expect(validate(draft).feeAmount).toMatch(/category|account/i);
    });

    it('rejects fee account without fee amount', () => {
        const draft = { ...happyBuy, feeAccountId: FEE_CAT };
        expect(validate(draft).feeAmount).toMatch(/required/i);
    });

    it('rejects non-positive fee amount', () => {
        const draft = { ...happyBuy, feeAccountId: FEE_CAT, feeAmount: 0 };
        expect(validate(draft).feeAmount).toMatch(/positive/i);
    });
});
