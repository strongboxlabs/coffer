import { describe, expect, it } from 'vitest';

import type { CsvMappingShape } from '@/lib/types';

import {
    choicesFromShape,
    guessChoices,
    guessDecimalSeparator,
    plausibleDateFormats,
    sampleGrid,
    sniffDelimiter,
    splitLine,
    toYaml,
    withoutComments,
    type WizardChoices,
} from './csvSniff';

/** A card-issuer export shape: tab-separated, despite arriving as a .csv. */
const CARD_ISSUER = [
    '09/02/2026\t$40.25\tSAMPLE.COM              ANYTOWN      ST\tpurchase',
    '09/03/2026\t$6.50\tSAMPLE STORE            ANYTOWN      ST\tpurchase',
    '09/04/2026\t-$23.50\tSAMPLE STORE            ANYTOWN      ST\tpurchase',
].join('\n');

describe('sniffDelimiter', () => {
    it('finds the tab in a file that merely calls itself a csv', () => {
        // The extension says comma, the file says tab, and a person should not have
        // to notice that themselves.
        expect(sniffDelimiter(CARD_ISSUER)).toBe('tab');
    });

    it('is not fooled by a comma inside a payee', () => {
        // Frequency alone picks comma here — three of them against three tabs, and the
        // comma count grows with every merchant that has one. What settles it is that
        // the tab splits EVERY line into the same number of fields and the comma does
        // not.
        const mixed = [
            '09/02/2026\t$40.25\tSAMPLE, INC\tpurchase',
            '09/03/2026\t$6.50\tSAMPLE STORE\tpurchase',
            '09/04/2026\t$1.00\tA, B, C LTD\tpurchase',
        ].join('\n');

        expect(sniffDelimiter(mixed)).toBe('tab');
    });

    it('prefers the delimiter that splits EVERY line the same way, not the most fields', () => {
        // The case the test above does not actually reach. There, tab wins on field
        // count alone, so it passes even with the consistency rule removed. Here a
        // description full of commas gives comma MORE
        // fields than tab on the first line, and only consistency picks correctly:
        //   comma -> 5, then 1  (inconsistent)
        //   tab   -> 4, then 4  (consistent)
        const commaHeavy = [
            '09/02/2026\t$40.25\tA, B, C, D, E\tpurchase',
            '09/03/2026\t$6.50\tSHOP\tpurchase',
        ].join('\n');

        expect(sniffDelimiter(commaHeavy)).toBe('tab');
    });

    it('finds the comma in an ordinary comma file', () => {
        expect(sniffDelimiter('a,b,c\n1,2,3\n4,5,6')).toBe('comma');
    });

    it('does not claim a delimiter for a single-column file', () => {
        // Nothing splits it, so any answer is a guess; comma is the harmless default and
        // the grid will show one column, which is itself the tell.
        expect(sniffDelimiter('one\ntwo\nthree')).toBe('comma');
    });
});

describe('splitLine', () => {
    it('keeps a quoted delimiter inside its field', () => {
        expect(splitLine('a,"b,c",d', ',')).toEqual(['a', 'b,c', 'd']);
    });

    it('unescapes a doubled quote', () => {
        expect(splitLine('a,"say ""hi""",c', ',')).toEqual(['a', 'say "hi"', 'c']);
    });

    it('keeps empty fields, because their position is the information', () => {
        // A debit/credit file says which side a row is on by which column is BLANK.
        expect(splitLine('2026-09-02,SHOP,,23.50', ',')).toEqual(['2026-09-02', 'SHOP', '', '23.50']);
    });
});

describe('sampleGrid', () => {
    it('splits the sample into rows of fields', () => {
        const grid = sampleGrid(CARD_ISSUER, 'tab');
        expect(grid).toHaveLength(3);
        expect(grid[0]).toHaveLength(4);
        expect(grid[0]![1]).toBe('$40.25');
    });

    it('drops blank lines, which every export ends with', () => {
        expect(sampleGrid('a,b\n\nc,d\n', 'comma')).toHaveLength(2);
    });
});

describe('plausibleDateFormats', () => {
    it('narrows to formats whose shape matches the sample', () => {
        const formats = plausibleDateFormats(['09/02/2026', '09/03/2026']);
        expect(formats).toContain('MM/dd/yyyy');
        // Different separator, so it cannot be reading these values.
        expect(formats).not.toContain('yyyy-MM-dd');
    });

    it('refuses to choose between formats a sample cannot separate', () => {
        // 09/02/2026 is both September 2nd and 2nd September. Offering only one would be
        // a guess about someone's money, so both stay.
        const formats = plausibleDateFormats(['09/02/2026']);
        expect(formats).toContain('MM/dd/yyyy');
        expect(formats).toContain('dd/MM/yyyy');
    });

    it('does not offer a numeric format for a month name', () => {
        const formats = plausibleDateFormats(['02-Sep-2026']);
        expect(formats).toContain('dd-MMM-yyyy');
        expect(formats).not.toContain('MM-dd-yyyy');
    });
});

describe('guessChoices', () => {
    it('reads an ISO file as ISO, which is the whole reason it exists', () => {
        // Sniffing the separator while hard-coding the date format to MM/dd/yyyy
        // would open an ordinary ISO export with the one setting that cannot read it,
        // previewing "0 transactions" with a warning per row. Guessing one field and
        // hard-coding the two most likely to be wrong is worse than not guessing,
        // because it looks answered.
        const guess = guessChoices([
            'Date,Description,Amount',
            '2026-09-02,SHOP,-23.50',
            '2026-09-03,CAFE,-4.20',
        ].join('\n'));

        expect(guess.dateFormat).toBe('yyyy-MM-dd');
        expect(guess.dateFormat).not.toBe('MM/dd/yyyy');
    });

    it('strikes out a header row, having noticed it carries no date', () => {
        // Detected off DATES, not "the first row looks like words": a payee column is
        // text on every row, so text alone says nothing about which row is a label.
        const withHeader = guessChoices([
            'Date,Description,Amount',
            '2026-09-02,SHOP,-23.50',
        ].join('\n'));
        expect(withHeader.headerRows).toBe(1);
        // ...and the payee is the description, not the date. An ISO date is 10
        // characters a row and outweighs a short merchant name, so "most text wins"
        // only lands correctly because date and amount are taken out of the running.
        expect(withHeader.payeeColumn).toBe(2);
        expect(withHeader.dateColumn).toBe(1);
        expect(withHeader.amountColumn).toBe(3);

        const without = guessChoices([
            '2026-09-02,SHOP,-23.50',
            '2026-09-03,CAFE,-4.20',
        ].join('\n'));
        expect(without.headerRows).toBe(0);
    });

    it('finds date, amount and payee in a card-issuer export', () => {
        // The card-issuer shape: tab-separated despite a .csv name, no header, a currency
        // symbol on the amount, description in the widest column.
        const guess = guessChoices(CARD_ISSUER);

        expect(guess.delimiter).toBe('tab');
        expect(guess.headerRows).toBe(0);
        expect(guess.dateColumn).toBe(1);
        expect(guess.dateFormat).toBe('MM/dd/yyyy');
        expect(guess.amountColumn).toBe(2);
        expect(guess.payeeColumn).toBe(3);
    });

    it('is not fooled into reading a reference number as the amount', () => {
        // A cheque or reference number is a perfectly good integer, and a boolean
        // "is this a number" test hands it the amount column. Cents and a currency
        // symbol are what actually separate money from a number that happens to be
        // sitting there.
        //
        // The reference sits to the RIGHT of the amount on purpose. With it on the
        // left, the rightmost-wins tie-break would pass this test on its own and the
        // weighting would go untested: flattening moneyScore to a constant would stay
        // green.
        const guess = guessChoices([
            '09/02/2026,SHOP,-23.50,100234',
            '09/03/2026,CAFE,-4.20,100235',
        ].join('\n'));

        expect(guess.amountColumn).toBe(3);
    });

    it('takes the amount, not the running balance, when both are money', () => {
        // The commonest checking export there is. Both columns are money and score
        // identically — same cents, no currency symbol — so nothing about their shape
        // separates them and position gets it wrong: the balance is on the right. What
        // separates them is size, because a balance is the sum of everything so far.
        const guess = guessChoices([
            'Date,Description,Amount,Balance',
            '2026-09-02,SHOP,-23.50,976.50',
            '2026-09-03,CAFE,-4.20,972.30',
        ].join('\n'));

        expect(guess.amountColumn).toBe(3);
    });

    it('is not fooled by a small integer column either', () => {
        // The size tie-break rescues a LARGE stray integer (a reference number) on its
        // own, so that case cannot show whether the cents weighting does anything. A line
        // number is the case that needs it: tiny, so "smaller wins" picks it, and only
        // the weighting knows that the line numbers are not money while a signed
        // figure with cents is.
        const guess = guessChoices([
            '09/02/2026,1,SHOP,-1234.56',
            '09/03/2026,2,CAFE,-2345.67',
        ].join('\n'));

        expect(guess.amountColumn).toBe(4);
        expect(guess.payeeColumn).toBe(3);
    });

    it('reads a whole-dollar amount as money on the strength of its symbol', () => {
        // Amounts do not always have cents. With no decimal to go on, the currency
        // symbol is the only thing separating a whole-dollar amount from an ordinal in the next column —
        // and the ordinal is SMALLER, so the size tie-break picks it if the symbol
        // counts for nothing.
        const guess = guessChoices([
            '09/02/2026,7,SHOP,$41',
            '09/03/2026,8,CAFE,$8',
        ].join('\n'));

        expect(guess.amountColumn).toBe(4);
        expect(guess.payeeColumn).toBe(3);
    });

    it('takes the money column when the file has no dates at all', () => {
        // With no date anywhere, dateColumn falls back to 1. Skipping that column when
        // hunting for money would push the amount of a file whose only numbers are in
        // column 1 onto a column with nothing in it.
        const guess = guessChoices('-23.50,SHOP\n-4.20,CAFE');

        expect(guess.amountColumn).toBe(1);
    });

    it('leaves inversion alone, because the file cannot answer it', () => {
        // Whether a positive number means money in or money out is a fact about the
        // ISSUER, not about the text. Guessing it would be guessing about someone's
        // money; the form asks it as a question about the statement instead.
        expect(guessChoices(CARD_ISSUER).amountInvert).toBe(false);
    });

    it('still produces a usable answer for a file it cannot read at all', () => {
        // One column, no dates, no money. Every field still has to be in range, because
        // the form renders dropdowns off these numbers.
        const guess = guessChoices('alpha\nbeta\ngamma');

        expect(guess.dateColumn).toBeGreaterThanOrEqual(1);
        expect(guess.amountColumn).toBeGreaterThanOrEqual(1);
        expect(guess.payeeColumn).toBeGreaterThanOrEqual(1);
        expect(guess.headerRows).toBe(0);
    });
});

/**
 * The decimal separator, guessed from the sample when a layout is first made
 * (ADR-0114 D15). Cell shapes from the approved boards CS-1 and CS-2.
 */
describe('guessDecimalSeparator', () => {
    it('reads a comma when the cells group thousands with a point and write a decimal comma', () => {
        // A cell that groups thousands with a point and ends in a decimal comma can only
        // be read with a comma as the decimal separator, and a bare decimal comma says
        // the same.
        expect(guessDecimalSeparator(['-47,05', '-8,90', '2.500,00', '-12,34', '-1.234,56', '-3,20']))
            .toBe('comma');
        // A bare decimal comma decides on its own: a point reading would take its comma
        // for a grouping mark and read a whole number a hundred times too large.
        expect(guessDecimalSeparator(['-12,34'])).toBe('comma');
    });

    it('reads a point when the cells group thousands with a comma and write a decimal point', () => {
        expect(guessDecimalSeparator(['-47.05', '-8.90', '2,500.00', '-12.34', '-1,234.56', '-3.20']))
            .toBe('point');
    });

    it('guesses a point when every cell is a comma followed by three digits, which both readings allow', () => {
        // A thousands grouping under a point, three decimals under a comma. Nothing in
        // the sample decides, and a tie is a point, the only reading a file ever had.
        expect(guessDecimalSeparator(['1,234', '-2,500', '12,345'])).toBe('point');
        expect(guessDecimalSeparator([])).toBe('point');
    });

    it('lets no date or payee vote', () => {
        // A dotted date is a number under neither reading, so a comma-decimal file whose
        // dates are written with points is not outvoted by its own dates.
        expect(guessDecimalSeparator(['03.11.2026', 'Sample Grocer', '04.11.2026', '-8,90']))
            .toBe('comma');
    });
});

describe('guessChoices and the decimal separator', () => {
    it('finds the amount column of a comma-decimal file, past a column of small integers', () => {
        // Read always with a point, a decimal-comma cell lost its comma as a grouping mark and
        // scored as a plain integer, tying with the line numbers beside it, and the
        // smaller-size tie-break then handed the line numbers the amount column.
        const guess = guessChoices([
            'Date;No;Payee;Amount',
            '03.11.2026;1;Sample Grocer;-47,05',
            '04.11.2026;2;Sample Bakery;-8,90',
            '05.11.2026;3;Sample Payroll;2.500,00',
        ].join('\n'));

        expect(guess.delimiter).toBe('semicolon');
        expect(guess.decimalSeparator).toBe('comma');
        expect(guess.amountColumn).toBe(4);
        expect(guess.payeeColumn).toBe(3);
    });

    it('reads a three-place amount as money with cents', () => {
        // A currency with three places writes three decimals (ADR-0114 D15). Refused as
        // money, the amount ties with the line numbers and loses to them on size.
        const guess = guessChoices([
            '2026-11-03,1,Sample Grocer,-47.050',
            '2026-11-04,2,Sample Bakery,-8.905',
        ].join('\n'));

        expect(guess.amountColumn).toBe(4);
    });

    it('guesses a point for a point-decimal file', () => {
        expect(guessChoices(CARD_ISSUER).decimalSeparator).toBe('point');
    });
});

describe('choicesFromShape and the decimal separator', () => {
    const shape = {
        version: 2,
        delimiter: 'semicolon',
        headerRows: 1,
        footerRows: 0,
        dateColumn: 1,
        dateFormat: 'dd.MM.yyyy',
        payeeColumn: 2,
        memoColumn: null,
        amountShape: 'signed',
        amountColumn: 4,
        amountInvert: false,
        debitColumn: null,
        creditColumn: null,
        decimalSeparator: 'comma',
    } as const satisfies CsvMappingShape;

    it('keeps the separator the layout was saved with', () => {
        expect(choicesFromShape(shape).decimalSeparator).toBe('comma');
    });

    it('reads a version-1 layout, which names no separator, with a point', () => {
        // The negative case: a version-1 document is not guessed. It reads as it
        // always did (ADR-0114 D15).
        const v1: Partial<CsvMappingShape> = { ...shape, version: 1 };
        delete v1.decimalSeparator;

        expect(choicesFromShape(v1 as CsvMappingShape).decimalSeparator).toBe('point');
    });
});

describe('toYaml', () => {
    const base: WizardChoices = {
        delimiter: 'tab',
        headerRows: 0,
        footerRows: 0,
        dateColumn: 1,
        dateFormat: 'MM/dd/yyyy',
        payeeColumn: 3,
        memoColumn: null,
        amountShape: 'signed',
        amountColumn: 2,
        amountInvert: true,
        debitColumn: 3,
        creditColumn: 4,
        decimalSeparator: 'point',
    };

    it('writes the document a person would have written by hand', () => {
        // The wizard has no private wire format — this is what makes the advanced
        // editor a real bypass rather than a parallel path. Asserted on the document
        // with its comments stripped, so the guidance can be reworded without anyone
        // having to re-approve the mapping it describes.
        expect(withoutComments(toYaml(base))).toBe(
            'version: 2\n'
            + 'delimiter: tab\n'
            + 'header_rows: 0\n'
            + 'date:\n'
            + '  column: 1\n'
            + '  format: MM/dd/yyyy\n'
            + 'payee:\n'
            + '  column: 3\n'
            + 'amount:\n'
            + '  decimal_separator: point\n'
            + '  shape: signed\n'
            + '  column: 2\n'
            + '  invert: true');
    });

    it('names the options next to the key they belong to', () => {
        // The point of the editor is that someone can go past the form, and they
        // cannot do that against a document that never mentions what it accepts.
        const yaml = toYaml(base);
        // Every delimiter the module accepts, and no invented one: the list is
        // derived from DELIMITERS rather than typed out, and this is what says so.
        expect(yaml).toContain('# comma | tab | semicolon | pipe');
        expect(yaml).toContain('# signed: one column');
        expect(yaml).toContain('# debit_credit: two columns');
        // The .NET trap the form hides behind a dropdown, spelled out where the
        // dropdown is not.
        expect(yaml).toContain('lowercase m is MINUTES');
    });

    it('offers the optional keys it did not use, commented out', () => {
        // An unused optional key is INVISIBLE: nothing in a working mapping suggests
        // footer_rows or memo exist at all. So they are written out ready to uncomment.
        const yaml = toYaml(base);
        expect(yaml).toContain('# footer_rows: 1');
        expect(yaml).toContain('# memo:');
        expect(yaml).toContain('#   column: 4');

        // ...and once they carry a value they stop being suggestions.
        const used = toYaml({ ...base, memoColumn: 4, footerRows: 1 });
        expect(used).toContain('footer_rows: 1  #');
        expect(withoutComments(used)).toContain('memo:\n  column: 4');
        expect(withoutComments(used)).toContain('footer_rows: 1');
    });

    it('shows the amount shape it is NOT using, since one cannot be guessed from the other', () => {
        // Both halves matter. Three commented-out keys with nothing above them are a
        // riddle: the line that says WHICH keys to swap out is what makes them an
        // instruction rather than debris.
        const signed = toYaml(base);
        expect(signed).toContain('Swap shape/column/invert for:');
        expect(signed).toContain('#   shape: debit_credit');
        expect(signed).toContain('#   debit_column: 3');
        expect(signed).toContain('#   credit_column: 4');

        const pair = toYaml({ ...base, amountShape: 'debit_credit' });
        expect(pair).toContain('Swap shape/debit_column/credit_column for:');
        expect(pair).toContain('#   shape: signed');
        expect(pair).toContain('#   column: 2');
        expect(pair).toContain('#   invert: true');
    });

    it('writes version 2 with the decimal separator, which the parser keeps', () => {
        // Version 2 requires the key (ADR-0114 D15), so a document the wizard writes
        // without it would be refused. Past the comments, so the key is real.
        const yaml = toYaml({ ...base, decimalSeparator: 'comma' });
        const bare = withoutComments(yaml);

        expect(bare).toMatch(/^version: 2$/m);
        expect(bare).toContain('amount:\n  decimal_separator: comma');
        // The accepted values next to the key, derived from the vocabulary.
        expect(yaml).toContain('  # point | comma\n  decimal_separator: comma');
        expect(withoutComments(toYaml({ ...base, amountShape: 'debit_credit' })))
            .toContain('amount:\n  decimal_separator: point');
    });

    it('leaves out memo and footer_rows when they say nothing', () => {
        const bare = withoutComments(toYaml(base));
        expect(bare).not.toContain('memo');
        expect(bare).not.toContain('footer_rows');
    });

    it('omits the keys the other amount shape owns', () => {
        // The validator rejects a key belonging to the other shape, so emitting both
        // would produce a document the API refuses. Checked past the comments, which
        // now legitimately mention the other shape's keys.
        const pair = withoutComments(toYaml({ ...base, amountShape: 'debit_credit' }));
        // Scoped to the amount block: `date:` and `payee:` legitimately have their own
        // two-space `column:` lines, so a document-wide match would fail on those.
        const amountBlock = pair.slice(pair.indexOf('amount:'));
        expect(amountBlock).toContain('debit_column: 3');
        expect(amountBlock).toContain('credit_column: 4');
        expect(amountBlock).not.toContain('invert');
        expect(amountBlock).not.toMatch(/^ {2}column:/m);
    });
});

describe('withoutComments', () => {
    it('strips a whole-line comment and a trailing one', () => {
        expect(withoutComments('# hi\nversion: 1  # and this\n')).toBe('version: 1');
    });

    it('keeps a # that is part of a value', () => {
        // Not a comment by YAML's rule, which wants a line start or whitespace before
        // it. Stripping it would silently rewrite someone's payee.
        expect(withoutComments('payee: ACME#7')).toBe('payee: ACME#7');
    });
});
