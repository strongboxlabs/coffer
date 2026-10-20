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
}

export type ImportJobState = 'running' | 'succeeded' | 'failed';

/** A background import's status, polled after starting. */
export interface ImportJob {
    jobId: string;
    state: ImportJobState;
    completed: number;
    total: number;
    step: string | null;
    ledgerId: string | null;
    error: string | null;
}

function fileForm(file: File, extra?: Record<string, string>): FormData {
    const form = new FormData();
    form.append('file', file, file.name || 'export.json');
    for (const [k, v] of Object.entries(extra ?? {})) form.append(k, v);
    return form;
}

/** Parse the export and return per-type counts. No ledger is created. */
export function previewMoneydanceImport(file: File): Promise<ImportPreview> {
    return requestMultipart<ImportPreview>('/api/imports/moneydance/preview', fileForm(file));
}

/** Create the named new ledger and start the background import. */
export function startMoneydanceImport(file: File, ledgerName: string): Promise<ImportJob> {
    return requestMultipart<ImportJob>('/api/imports/moneydance', fileForm(file, { ledgerName }));
}

/** Poll a running import's status. */
export function fetchImportJob(jobId: string): Promise<ImportJob> {
    return request<ImportJob>(`/api/imports/moneydance/${encodeURIComponent(jobId)}`);
}
