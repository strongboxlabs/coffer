// Mirrors the API's CSV mapping contracts (ADR-0031 Phase 5, mig 222).

/** The delimiters a mapping may name. Matches the API's closed vocabulary. */
export type CsvDelimiterName = 'comma' | 'tab' | 'semicolon' | 'pipe';

/** The amount shapes a mapping may name. Matches the API's closed vocabulary. */
export type CsvAmountShapeName = 'signed' | 'debit_credit';

/** The decimal separators an amount block may name, as `amount.decimal_separator`
 *  (ADR-0114 D15), required from schema version 2. Matches the API's closed vocabulary. */
export type CsvDecimalSeparatorName = 'point' | 'comma';

/**
 * What a document SAYS, as the server read it.
 *
 * The browser does not parse YAML and should not start: a second reader could disagree
 * with the one that decides what actually gets imported. So loading a saved layout back
 * into the guided form goes through `validate`, which already builds this to decide
 * whether the document is usable and now hands it back.
 */
export interface CsvMappingShape {
    version: number;
    delimiter: CsvDelimiterName;
    headerRows: number;
    footerRows: number;
    dateColumn: number;
    dateFormat: string;
    payeeColumn: number;
    memoColumn: number | null;
    amountShape: CsvAmountShapeName;
    amountColumn: number | null;
    amountInvert: boolean;
    debitColumn: number | null;
    creditColumn: number | null;
    /** The amount block's decimal separator. A version-1 document has no such key and
     *  reads as `point`, so a saved layout loads back with its separator and is never
     *  re-guessed. */
    decimalSeparator: CsvDecimalSeparatorName;
}

/** One stored mapping document. */
export interface CsvMapping {
    id: string;
    name: string;
    /** YAML exactly as its author wrote it — comments and formatting included. Shown
     *  verbatim in the editor, because a document meant to be hand-edited has to open
     *  as it was left. */
    definitionYaml: string;
    schemaVersion: number;
    createdAt: string;
    updatedAt: string;
}

/** One problem with a document: which key, which line, and what is wrong. */
export interface CsvMappingProblem {
    path: string;
    line: number | null;
    message: string;
}

/**
 * The verdict on a document.
 *
 * Carries EVERY problem rather than the first, so the editor can mark them all in one
 * pass instead of making its author fix one per round trip.
 */
export interface CsvMappingVerdict {
    valid: boolean;
    errors: CsvMappingProblem[];
    /** What the document says — null exactly when it says nothing usable. */
    mapping: CsvMappingShape | null;
}
