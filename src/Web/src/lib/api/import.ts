// In-app Moneydance import (ADR-0071 D2). Upload an MD export to create a
// brand-new ledger; the import runs as a background job the client polls.

import { request, requestMultipart } from './_request';

export interface ImportPreviewCount {
    objType: string;
    count: number;
}

/** Parse-time summary returned by the preview endpoint — no DB writes. */
export interface ImportPreview {
    exporter: string;
    build: number;
    exportDate: number;
    totalItems: number;
    counts: ImportPreviewCount[];
    /** The file's base currency: the default reporting currency for the new ledger
     *  (ADR-0114 D7). */
    baseCurrencyCode: string;
}

export type ImportJobState = 'running' | 'succeeded' | 'failed';

/** One validation check a finished import failed, with the server's message. */
export interface ImportWarning {
    /** The check's name, e.g. `no-dropped-transactions`. */
    check: string;
    /** A plain-language sentence for the person who ran the import, shown as is. */
    message: string;
}

/** One Moneydance transaction the import did not bring in. */
export interface ImportSkippedTransaction {
    /** `YYYY-MM-DD`; null when the export's date is not a calendar date. */
    date: string | null;
    /** The importer's reason, e.g. `UnknownSecurity`. */
    reason: string;
    security: string | null;
    ticker: string | null;
    shares: number | null;
}

/** A background import's status, polled after starting. */
export interface ImportJob {
    jobId: string;
    state: ImportJobState;
    completed: number;
    total: number;
    step: string | null;
    ledgerId: string | null;
    error: string | null;
    /** Each failed check; empty until the job succeeds. */
    warnings: ImportWarning[];
    /** The transactions not imported, up to the server's row limit. */
    skippedTransactions: ImportSkippedTransaction[];
    /** How many more transactions were not imported than are listed. */
    skippedTransactionsNotListed: number;
}

function fileForm(file: File, extra?: Record<string, string>): FormData {
    const form = new FormData();
    form.append('file', file, file.name || 'export.json');
    for (const [k, v] of Object.entries(extra ?? {})) form.append(k, v);
    return form;
}

/**
 * Parse the export and return per-type counts and its base currency. No ledger is
 * created.
 *
 * 422 codes (ADR-0114 D5): `import-currency-unresolved` when the export has no single
 * base currency, or an account whose currency is missing, unknown, a security or not
 * a 3-letter code (the detail names up to ten accounts, then counts the rest), and
 * `ledger-currency-invalid` for a `currencyCode` that is not a code.
 */
export function previewMoneydanceImport(file: File, currencyCode?: string): Promise<ImportPreview> {
    return requestMultipart<ImportPreview>(
        '/api/imports/moneydance/preview',
        fileForm(file, currencyCode ? { currencyCode } : undefined),
    );
}

/**
 * Create the named new ledger and start the background import. `currencyCode` is the
 * new ledger's reporting currency; omitted or blank, the file's base currency. The
 * same 422 codes as the preview.
 */
export function startMoneydanceImport(
    file: File,
    ledgerName: string,
    currencyCode?: string,
): Promise<ImportJob> {
    return requestMultipart<ImportJob>(
        '/api/imports/moneydance',
        fileForm(file, currencyCode ? { ledgerName, currencyCode } : { ledgerName }),
    );
}

/** Poll a running import's status. */
export function fetchImportJob(jobId: string): Promise<ImportJob> {
    return request<ImportJob>(`/api/imports/moneydance/${encodeURIComponent(jobId)}`);
}
