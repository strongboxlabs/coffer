import { describe, it, expect } from 'vitest';
import { classifySimpleFinDescription } from './simpleFinDescriptionClassifier';

// These cases must stay in lockstep with
// tests/Api.Tests/Unit/Ingest/SimpleFinDescriptionClassifierTests.cs.
// If you add or modify a case here, mirror it there (and vice versa)
// so the two classifiers can't drift.

describe('classifySimpleFinDescription', () => {
    // ----- buy -----
    it.each([
        ['YOU BOUGHT ACME INDEX FUNDS S&P 500 ETF (ETFA) (Cash) Cash', 'buy', 'ETFA'],
        ['BOUGHT APPLE INC COMMON STOCK', 'buy', null],
        ['BUY 100 SHARES OF MSFT', 'buy', null],
        ['  YOU BOUGHT 50 SHARES', 'buy', null],
    ])('classifies "%s" as buy/%s', (input, expectedAction, expectedTicker) => {
        const { action, tickerHint } = classifySimpleFinDescription(input);
        expect(action).toBe(expectedAction);
        expect(tickerHint).toBe(expectedTicker);
    });

    // ----- sell -----
    it.each([
        ['YOU SOLD 50 SHARES OF AAPL', 'sell'],
        ['SOLD 100 SHARES OF IDXC', 'sell'],
        ['SELL TO OPEN', 'sell'],
    ])('classifies "%s" as %s', (input, expected) => {
        expect(classifySimpleFinDescription(input).action).toBe(expected);
    });

    // ----- dividend_cash -----
    it.each([
        ['DIVIDEND RECEIVED FROM ETFA', 'dividend_cash'],
        ['DIVIDEND APPLE INC', 'dividend_cash'],
        ['DIV PAYMENT', 'dividend_cash'],
    ])('classifies "%s" as %s', (input, expected) => {
        expect(classifySimpleFinDescription(input).action).toBe(expected);
    });

    // ----- dividend_reinvest -----
    it.each([
        ['REINVESTMENT ACME INDEX (ETFA)', 'dividend_reinvest'],
        ['REINVEST DIVIDEND', 'dividend_reinvest'],
        ['DIVIDEND REINVESTMENT FROM ETFA', 'dividend_reinvest'],
    ])('classifies "%s" as %s', (input, expected) => {
        expect(classifySimpleFinDescription(input).action).toBe(expected);
    });

    it('reinvestment takes precedence over plain dividend', () => {
        const result = classifySimpleFinDescription(
            'DIVIDEND REINVESTMENT FROM ACME INDEX FUNDS (ETFA)');
        expect(result.action).toBe('dividend_reinvest');
        expect(result.tickerHint).toBe('ETFA');
    });

    // ----- transfer -----
    it.each([
        ['TRANSFER FROM CHECKING'],
        ['TRANSFER TO SAVINGS'],
        ['TRANSFER 1000.00'],
    ])('classifies "%s" as transfer', (input) => {
        expect(classifySimpleFinDescription(input).action).toBe('transfer');
    });

    // ----- abstain -----
    it.each([
        ['STARBUCKS COFFEE PURCHASE'],
        ['ATM WITHDRAWAL'],
        ['BANK FEE'],
        ['PAYROLL DEPOSIT'],
        ['INTEREST PAYMENT'],
    ])('abstains on "%s"', (input) => {
        expect(classifySimpleFinDescription(input).action).toBeNull();
    });

    it.each([null, undefined, '', '   '])(
        'returns nulls for blank input %s',
        (input) => {
            const { action, tickerHint } = classifySimpleFinDescription(input);
            expect(action).toBeNull();
            expect(tickerHint).toBeNull();
        },
    );

    // ----- case insensitivity on action keywords -----
    it('is case-insensitive on action keywords', () => {
        expect(classifySimpleFinDescription('you bought etfa').action).toBe('buy');
    });

    // ----- ticker extraction -----
    it.each([
        ['YOU BOUGHT ACME INDEX (ETFA) (Cash) Cash', 'ETFA'],
        ['YOU SOLD (AAPL) APPLE STOCK', 'AAPL'],
        ['DIVIDEND FROM (T) AT&T COMMON', 'T'],
        ['REINVESTMENT (GOOGL) ALPHABET INC', 'GOOGL'],
    ])('extracts ticker from "%s"', (input, expected) => {
        expect(classifySimpleFinDescription(input).tickerHint).toBe(expected);
    });

    it.each([
        ['YOU BOUGHT ACME INDEX FUND'],
        ['YOU BOUGHT (Cash) Cash'],
        ['YOU BOUGHT (NASDAQ) MARKET INDEX'],
        ['YOU BOUGHT (BRK.B) BERKSHIRE HATHAWAY'],
    ])('returns null ticker for "%s"', (input) => {
        expect(classifySimpleFinDescription(input).tickerHint).toBeNull();
    });

    it('surfaces action + ticker independently when only one matches', () => {
        const result = classifySimpleFinDescription(
            'STOCK SPLIT FOR (AAPL) APPLE INC');
        expect(result.action).toBeNull();
        expect(result.tickerHint).toBe('AAPL');
    });
});
