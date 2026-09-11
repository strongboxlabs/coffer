import type { CsvDelimiterName, CsvMappingShape } from '@/lib/types';

/**
 * Reading a sample of a delimited file well enough to POINT AT ITS COLUMNS.
 *
 * <b>This is not the parser of record.</b> The server reads the file with CsvHelper and
 * is the only thing whose interpretation reaches the ledger; everything here exists so
 * the wizard can show a grid of the user's own data and let them pick "the date is that
 * one" instead of counting fields in a text editor. Where the two could disagree — an
 * exotic quoting case — the wizard's live preview comes from the SERVER's preview
 * endpoint, so the authoritative answer is always on screen next to the guess.
 */

/** How many rows the grid shows. Enough to recognise a column, few enough to scan. */
export const SAMPLE_ROWS = 8;

const DELIMITERS: Record<CsvDelimiterName, string> = {
    comma: ',',
    tab: '\t',
    semicolon: ';',
    pipe: '|',
};

/**
 * Guess the delimiter by which candidate yields the most CONSISTENT column count across
 * the sample.
 *
 * Frequency alone is the obvious approach and it is wrong: a file of merchant names is
 * full of spaces and the odd comma, and a tab-separated file with one comma in a payee
 * would still be "mostly commas" by count. What actually distinguishes the real
 * delimiter is that it splits every line into the SAME number of fields.
 */
export function sniffDelimiter(sample: string): CsvDelimiterName {
    const lines = sample.split(/\r?\n/).filter((l) => l.trim() !== '').slice(0, SAMPLE_ROWS);
    if (lines.length === 0) return 'comma';

    let best: CsvDelimiterName = 'comma';
    let bestScore = -1;
    for (const name of Object.keys(DELIMITERS) as CsvDelimiterName[]) {
        const counts = lines.map((l) => splitLine(l, DELIMITERS[name]).length);
        const columns = counts[0]!;
        // One field means it never split; that is not a delimiter, whatever the count.
        if (columns < 2) continue;
        const consistent = counts.every((c) => c === columns);
        // Prefer consistency, then more columns — a file that splits evenly into four is
        // better described than one that splits evenly into two by accident.
        const score = (consistent ? 1000 : 0) + columns;
        if (score > bestScore) {
            bestScore = score;
            best = name;
        }
    }
    return best;
}

/**
 * Split one line, honouring double quotes.
 *
 * Deliberately minimal — it handles the quoting that would otherwise mis-align the
 * DISPLAY grid, and nothing more. Embedded newlines inside quotes are not handled here
 * because a sample is line-oriented; the server handles them for the real import.
 */
export function splitLine(line: string, delimiter: string): string[] {
    const out: string[] = [];
    let field = '';
    let quoted = false;
    for (let i = 0; i < line.length; i++) {
        const c = line[i]!;
        if (quoted) {
            if (c === '"') {
                if (line[i + 1] === '"') { field += '"'; i++; } else { quoted = false; }
            } else {
                field += c;
            }
        } else if (c === '"') {
            quoted = true;
        } else if (c === delimiter) {
            out.push(field);
            field = '';
        } else {
            field += c;
        }
    }
    out.push(field);
    return out;
}

/** The sample as a grid: rows of fields, already split. */
export function sampleGrid(text: string, delimiter: CsvDelimiterName): string[][] {
    return text
        .split(/\r?\n/)
        .filter((l) => l.trim() !== '')
        .slice(0, SAMPLE_ROWS)
        .map((l) => splitLine(l, DELIMITERS[delimiter]));
}

/**
 * Date formats worth offering, commonest first.
 *
 * Offered as a CHOICE rather than a text box because `MM/dd/yyyy` is a .NET format
 * string, and the trap in it — `M` is month, lowercase `m` is minutes — is not something
 * anyone should have to know to import a bank statement.
 */
export const DATE_FORMATS = [
    'MM/dd/yyyy',
    'M/d/yyyy',
    'yyyy-MM-dd',
    'dd/MM/yyyy',
    'd/M/yyyy',
    'dd.MM.yyyy',
    'MM-dd-yyyy',
    'yyyy/MM/dd',
    'dd-MMM-yyyy',
    'MMM d, yyyy',
] as const;

/**
 * Which offered formats actually read the sample values.
 *
 * Approximate on purpose: this is a hint for ordering the dropdown, and the server's
 * preview is what proves a format works. It refuses to guess between formats that are
 * genuinely ambiguous — 03/04/2026 is both March 4th and 4th March, and no amount of
 * cleverness here can tell which without more rows.
 */
export function plausibleDateFormats(values: string[]): string[] {
    const nonEmpty = values.map((v) => v.trim()).filter((v) => v !== '');
    if (nonEmpty.length === 0) return [...DATE_FORMATS];
    return DATE_FORMATS.filter((f) => nonEmpty.every((v) => looksLike(v, f)));
}

/** A shape test, not a parse: does the value have this format's separators and parts? */
function looksLike(value: string, format: string): boolean {
    const sep = format.includes('/') ? '/' : format.includes('-') ? '-' : format.includes('.') ? '.' : ' ';
    const formatParts = format.split(sep).length;
    const valueParts = value.split(sep).length;
    if (formatParts !== valueParts) return false;

    // Numeric formats need numeric parts; a month NAME never satisfies MM.
    const wantsMonthName = format.includes('MMM');
    const hasLetters = /[A-Za-z]/.test(value);
    return wantsMonthName === hasLetters;
}

/**
 * How much a value looks like money.
 *
 * Weighted rather than boolean, because the trap is a column of plain integers — a
 * reference or cheque number reads as a perfectly good number and would otherwise win
 * the amount column outright. Cents and a currency symbol are what actually distinguish
 * money from a number that happens to be there.
 */
function moneyScore(value: string): number {
    const text = value.trim();
    if (text === '') return 0;
    const decorated = /[$\u20ac\u00a3\u00a5]/.test(text) || /^\(.*\)$/.test(text);
    const bare = text.replace(/[()\s$\u20ac\u00a3\u00a5,]/g, '');
    if (!/^[-+]?\d+(\.\d{1,2})?$/.test(bare)) return 0;
    return 1 + (decorated ? 2 : 0) + (/\.\d{1,2}$/.test(bare) ? 2 : 0);
}

/** The size of a money cell, ignoring sign — null when it is not money at all. */
function moneyMagnitude(value: string): number | null {
    const bare = value.trim().replace(/[()\s$\u20ac\u00a3\u00a5,]/g, '');
    if (!/^[-+]?\d+(\.\d{1,2})?$/.test(bare)) return null;
    return Math.abs(Number(bare));
}

/** Typical size of the money in a column, for telling an amount from a balance. */
function typicalSize(values: string[]): number {
    const sizes = values.map(moneyMagnitude).filter((n): n is number => n !== null);
    if (sizes.length === 0) return Number.POSITIVE_INFINITY;
    return sizes.reduce((a, b) => a + b, 0) / sizes.length;
}

/** Does any offered format read this value? Used to tell a header row from data. */
function looksLikeAnyDate(value: string): boolean {
    return DATE_FORMATS.some((f) => looksLike(value, f));
}

/**
 * Everything the wizard can work out from the file itself.
 *
 * <b>Guessing only the delimiter was the defect.</b> The date format sat hard-coded at
 * `MM/dd/yyyy` while `plausibleDateFormats` was computed and spent purely on ordering
 * the dropdown — so an ISO file opened with the one setting that cannot read it, and the
 * preview said "0 transactions" with a warning per row. Sniffing the separator and then
 * hard-coding the two fields most likely to be wrong is worse than not guessing: it
 * looks answered.
 *
 * Every guess here is a STARTING POINT with the file's own rows shown beside it and a
 * control on top of it. That is the licence to guess at all — nothing is hidden, and
 * being wrong costs one dropdown rather than a bad import.
 */
export function guessChoices(text: string): WizardChoices {
    const delimiter = sniffDelimiter(text);
    const grid = sampleGrid(text, delimiter);
    const columns = grid.reduce((n, r) => Math.max(n, r.length), 0) || 1;
    const at = (row: string[], i: number) => (row[i] ?? '').trim();

    // A header row is one whose cells read as labels where the rows below read as data.
    // Detected off DATES rather than "the first row looks texty": a payee column is text
    // on every row, so text alone says nothing.
    const headerRows = grid.length >= 2
        && !grid[0]!.some(looksLikeAnyDate)
        && grid.slice(1).some((r) => r.some(looksLikeAnyDate))
        ? 1 : 0;
    const body = grid.slice(headerRows);

    // The date column and its format are chosen TOGETHER — a column is only the date
    // column by virtue of some format reading it, and picking the column first would
    // then have to hard-code a format for it, which is the bug being fixed.
    let dateColumn = 1;
    let dateFormat: string = DATE_FORMATS[0];
    let bestDate = 0;
    for (let i = 0; i < columns; i++) {
        const values = body.map((r) => at(r, i)).filter((v) => v !== '');
        if (values.length === 0) continue;
        for (const format of DATE_FORMATS) {
            const hits = values.filter((v) => looksLike(v, format)).length;
            // Strictly greater, so the earliest column and the commonest format win a
            // tie. Ambiguity between MM/dd and dd/MM is real and unresolvable from a
            // sample; the dropdown offers both and the grid is on screen.
            if (hits > bestDate) {
                bestDate = hits;
                dateColumn = i + 1;
                dateFormat = format;
            }
        }
    }

    // No date-column exclusion here, deliberately. It was unreachable — every offered
    // date format carries a separator that fails the money test — and in the one case
    // it DID reach, a file with money in column 1 and no dates at all, it fired on the
    // fallback dateColumn of 1 and pushed the amount off the only column that had any.
    // A guard that cannot fire except to do harm is worse than no guard.
    let amountColumn = Math.min(2, columns);
    let bestMoney = 0;
    let bestSize = Number.POSITIVE_INFINITY;
    for (let i = 0; i < columns; i++) {
        const values = body.map((r) => at(r, i));
        const score = values.reduce((sum, v) => sum + moneyScore(v), 0);
        if (score === 0) continue;
        // A RUNNING BALANCE is the same shape as an amount and scores identically —
        // same cents, same currency symbol — so position cannot separate them. It is
        // reliably the LARGER of the two, being the sum of everything so far, and that
        // is what breaks the tie. Picking by position instead put the balance in the
        // amount column for the commonest checking-account export there is:
        // Date, Description, Amount, Balance.
        const size = typicalSize(values);
        if (score > bestMoney || (score === bestMoney && size < bestSize)) {
            bestMoney = score;
            bestSize = size;
            amountColumn = i + 1;
        }
    }

    // Whatever is left that carries the most text. A merchant name is the longest thing
    // on the row in every export I have seen, and being wrong here is visible instantly
    // in the picker, which shows a sample value beside each column.
    let payeeColumn = Math.min(3, columns);
    let bestText = -1;
    for (let i = 0; i < columns; i++) {
        if (i + 1 === dateColumn || i + 1 === amountColumn) continue;
        const total = body.reduce((sum, r) => sum + at(r, i).length, 0);
        if (total > bestText) {
            bestText = total;
            payeeColumn = i + 1;
        }
    }

    return {
        delimiter,
        headerRows,
        footerRows: 0,
        dateColumn,
        dateFormat,
        payeeColumn,
        memoColumn: null,
        amountShape: 'signed',
        amountColumn,
        // Not guessable from the file: whether a positive number means money in or money
        // out is a fact about the ISSUER, not the text. The form asks it as a question
        // about the statement instead.
        amountInvert: false,
        debitColumn: Math.min(3, columns),
        creditColumn: Math.min(4, columns),
    };
}

/** Everything the guided form decides. */
export interface WizardChoices {
    delimiter: CsvDelimiterName;
    headerRows: number;
    footerRows: number;
    dateColumn: number;
    dateFormat: string;
    payeeColumn: number;
    memoColumn: number | null;
    amountShape: 'signed' | 'debit_credit';
    amountColumn: number;
    amountInvert: boolean;
    debitColumn: number;
    creditColumn: number;
}

/**
 * Render the choices as the document the API takes, with the options written beside the
 * keys they belong to.
 *
 * The wizard does not have its own wire format: it WRITES the same YAML a person would,
 * which is what makes the advanced editor a genuine bypass rather than a parallel path.
 * Switching to the editor shows exactly what the form built, and switching costs nothing
 * because there is only ever one document.
 *
 * <b>The comments are the editor's documentation, delivered where it is read.</b> A bare
 * document is a poor thing to hand someone and call it an escape hatch: an optional key
 * is INVISIBLE when unused — nothing in a working mapping hints that `footer_rows` or
 * `memo` exist — and the amount block has two mutually exclusive shapes, which cannot be
 * guessed from seeing one of them. So the unused options are emitted commented out and
 * ready to uncomment, and the enumerable values are listed next to their key. Comments
 * are dropped by the YAML parser, so none of it reaches the validator.
 */
export function toYaml(c: WizardChoices): string {
    const lines = [
        '# Coffer CSV mapping. Anything after a # is a comment.',
        '# Columns are numbered from 1, left to right, as in the grid above.',
        'version: 1',
        '',
        // Derived, not typed out: a hand-written list of the accepted values is a
        // second copy that goes stale the moment a delimiter is added.
        `# ${(Object.keys(DELIMITERS) as CsvDelimiterName[]).join(' | ')}`,
        `delimiter: ${c.delimiter}`,
        '',
        `header_rows: ${c.headerRows}  # rows to skip before the data starts`,
        c.footerRows > 0
            ? `footer_rows: ${c.footerRows}  # rows to skip at the end`
            : '# footer_rows: 1  # skip totals or a disclaimer at the end of the file',
        '',
        'date:',
        `  column: ${c.dateColumn}`,
        '  # Any .NET format that spells out a year, a month and a day.',
        '  # M is month; lowercase m is MINUTES.',
        '  # MM/dd/yyyy | yyyy-MM-dd | dd/MM/yyyy | dd.MM.yyyy | dd-MMM-yyyy',
        `  format: ${c.dateFormat}`,
        '',
        'payee:',
        `  column: ${c.payeeColumn}`,
        '',
    ];
    lines.push(...(c.memoColumn !== null
        ? ['memo:', `  column: ${c.memoColumn}`]
        : ['# memo:  # a second description column, if the file has one', '#   column: 4']));
    lines.push(
        '',
        'amount:',
        '  # signed: one column carrying a +/- sign.',
        '  # debit_credit: two columns, one of them empty on every row.',
        `  shape: ${c.amountShape}`,
    );
    if (c.amountShape === 'signed') {
        lines.push(
            `  column: ${c.amountColumn}`,
            '  # true when a purchase is written POSITIVE, as card exports write it.',
            '  # Coffer records a purchase as money out, so those get flipped.',
            `  invert: ${c.amountInvert}`,
            '  # Two columns instead? Swap shape/column/invert for:',
            '  #   shape: debit_credit',
            `  #   debit_column: ${c.debitColumn}`,
            `  #   credit_column: ${c.creditColumn}`,
        );
    } else {
        lines.push(
            `  debit_column: ${c.debitColumn}`,
            `  credit_column: ${c.creditColumn}`,
            '  # One signed column instead? Swap shape/debit_column/credit_column for:',
            '  #   shape: signed',
            `  #   column: ${c.amountColumn}`,
            `  #   invert: ${c.amountInvert}`,
        );
    }
    return lines.join('\n') + '\n';
}

/**
 * Fill the guided form from a document the server has already read.
 *
 * <b>The form is a view of a document, not a second source of truth.</b> This is what
 * lets a saved layout open in the wizard rather than only in the text box — and it goes
 * through the SERVER's reading of that document, never a parser of our own, so the
 * controls can never describe something different from what the importer will do.
 *
 * The shape carries only the keys its amount branch owns; the other branch's controls
 * still need a value to render. Those fall back to the same first guesses a new document
 * gets, so switching shape lands somewhere sensible rather than on column 0 — and
 * nothing is written back to the document until a control is actually touched.
 */
export function choicesFromShape(shape: CsvMappingShape): WizardChoices {
    return {
        delimiter: shape.delimiter,
        headerRows: shape.headerRows,
        footerRows: shape.footerRows,
        dateColumn: shape.dateColumn,
        dateFormat: shape.dateFormat,
        payeeColumn: shape.payeeColumn,
        memoColumn: shape.memoColumn,
        amountShape: shape.amountShape,
        amountColumn: shape.amountColumn ?? 2,
        amountInvert: shape.amountInvert,
        debitColumn: shape.debitColumn ?? 3,
        creditColumn: shape.creditColumn ?? 4,
    };
}

/**
 * The document with its comments and blank lines removed — what the parser actually
 * sees.
 *
 * Exported because "this key is not in the document" is a property worth holding onto,
 * and it stopped being a text search the moment the options arrived as commented-out
 * examples: `invert` now appears in a `debit_credit` document, in a comment, where the
 * validator would reject it as a key. Tests assert against this rather than the raw
 * string so they still fail if the key is ever emitted for real.
 *
 * Handles the comment rule for documents THIS MODULE writes: a `#` opens a comment at
 * the start of a line or after whitespace. It does not know about quoted scalars, which
 * the emitter never produces.
 */
export function withoutComments(yaml: string): string {
    return yaml
        .split('\n')
        .map((line) => {
            const at = line.search(/(^|\s)#/);
            return (at === -1 ? line : line.slice(0, at)).replace(/\s+$/, '');
        })
        .filter((line) => line !== '')
        .join('\n');
}
