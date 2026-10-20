// Mapping documents and delimited-file ingest (ADR-0031 Phase 5).

import { request, requestMultipart } from './_request';
import type {
    CsvMapping,
    CsvMappingVerdict,
} from '../types/csvMapping';
import type { OfxImportResponse, OfxPreviewResponse } from '../types/ofx';

export async function fetchCsvMappings(ledgerId: string): Promise<CsvMapping[]> {
    return request<CsvMapping[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/csv-mappings`);
}

/**
 * Check a document without saving it.
 *
 * Answers 200 even when the verdict is "no": an invalid draft is a normal step in
 * writing one, not a failed request.
 */
export async function validateCsvMapping(
    ledgerId: string,
    definitionYaml: string,
): Promise<CsvMappingVerdict> {
    return request<CsvMappingVerdict>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/csv-mappings/validate`,
        { method: 'POST', body: { name: 'draft', definitionYaml } },
    );
}

export async function createCsvMapping(
    ledgerId: string,
    name: string,
    definitionYaml: string,
): Promise<CsvMapping> {
    return request<CsvMapping>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/csv-mappings`,
        { method: 'POST', body: { name, definitionYaml } },
    );
}

export async function updateCsvMapping(
    ledgerId: string,
    id: string,
    name: string,
    definitionYaml: string,
): Promise<CsvMapping> {
    return request<CsvMapping>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/csv-mappings/${encodeURIComponent(id)}`,
        { method: 'PUT', body: { name, definitionYaml } },
    );
}

export async function deleteCsvMapping(ledgerId: string, id: string): Promise<void> {
    await request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/csv-mappings/${encodeURIComponent(id)}`,
        { method: 'DELETE' },
    );
}

/**
 * Preview a delimited file. Writes nothing.
 *
 * Takes a saved mapping OR a draft, never both — the draft path is what lets the wizard
 * try a delimiter without persisting a half-built mapping first.
 */
export async function previewCsv(
    ledgerId: string,
    file: Blob,
    mapping: { mappingId?: string; mappingYaml?: string },
): Promise<OfxPreviewResponse> {
    return requestMultipart<OfxPreviewResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/csv/preview`,
        buildForm({ file, ...mapping }),
    );
}

export async function importCsv(
    ledgerId: string,
    file: Blob,
    accountId: string,
    providerAccountId: string,
    mapping: { mappingId?: string; mappingYaml?: string },
): Promise<OfxImportResponse> {
    return requestMultipart<OfxImportResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/csv/import`,
        buildForm({ file, accountId, providerAccountId, ...mapping }),
    );
}

function buildForm(fields: {
    file: Blob;
    accountId?: string;
    providerAccountId?: string;
    mappingId?: string;
    mappingYaml?: string;
}): FormData {
    const form = new FormData();
    form.append('file', fields.file, 'statement.csv');
    if (fields.accountId) form.append('accountId', fields.accountId);
    if (fields.providerAccountId) form.append('providerAccountId', fields.providerAccountId);
    if (fields.mappingId) form.append('mappingId', fields.mappingId);
    if (fields.mappingYaml) form.append('mappingYaml', fields.mappingYaml);
    return form;
}
