import { describe, expect, it } from 'vitest';

import investmentActionWords from '@/lib/investmentActionWords.json';
import type { LedgerInvestmentAction } from '@/lib/types';

import { formatInvestmentAction } from '../register/strategies/investmentStrategy';
import { ACTION_PICKER_ENTRIES } from './actionLayout';

/**
 * One word per investment action, on every surface. The register's chip (and
 * through it the home lists), a merge candidate and the editor's action picker
 * all name an action; the picker's label must be the word the chip shows, and
 * the words are ADR-0027's display words. The stored value is not a word.
 */
const WORDS: Readonly<Record<LedgerInvestmentAction, string>> = {
    buy: 'Buy',
    buyx: 'BuyXfr',
    sell: 'Sell',
    sellx: 'SellXfr',
    dividend_cash: 'Dividend',
    dividend_reinvest: 'Reinvest',
    divx: 'DividendXfr',
    transfer: 'Xfr',
    misc: 'Misc',
    transfer_shares: 'Transfer shares',
};

describe('the investment action words', () => {
    it('the picker offers every action once, in catalog order', () => {
        expect(ACTION_PICKER_ENTRIES.map((entry) => entry.action)).toEqual(Object.keys(WORDS));
    });

    it.each(ACTION_PICKER_ENTRIES)('the picker names $action with the word the register chip shows', ({ action, label }) => {
        expect(label).toBe(formatInvestmentAction(action));
    });

    it.each(Object.entries(WORDS))('%s reads "%s"', (action, word) => {
        expect(formatInvestmentAction(action)).toBe(word);
    });

    it('a derived action the catalog does not hold reads as itself', () => {
        expect(formatInvestmentAction('Xfr')).toBe('Xfr');
    });

    // The table is one JSON file that the API embeds for the register's Action sort, so a
    // key it holds that is no action would be a word the server sorts by and no chip shows.
    it('the word table the API sorts by holds every action and nothing else', () => {
        expect(Object.keys(investmentActionWords).sort()).toEqual(Object.keys(WORDS).sort());
        expect(investmentActionWords).toEqual(WORDS);
    });
});
