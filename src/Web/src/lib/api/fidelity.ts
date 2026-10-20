import type { OfxImportResponse, OfxPreviewResponse } from '@/lib/types';

import { requestMultipart } from './_request';
import { buildFormData } from './upload';

/**
 * Fidelity brokerage CSV ingest (ADR-0031 Phase 6).
 *
 * Its own routes rather than the generic CSV ones, because the generic path takes a
 * mapping document per request and this path needs none — the format knowledge lives in
 * the server-side shim. Responses are shape-compatible with OFX's, as QIF's are.
 */
export async function previewFidelity(
    ledgerId: string,
    file: Blob,
): Promise<OfxPreviewResponse> {
    return requestMultipart<OfxPreviewResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/fidelity/preview`,
        buildFormData({ file }, 'upload.csv'),
    );
}

export async function importFidelity(
    ledgerId: string,
    file: Blob,
    accountId: string,
    providerAccountId: string,
): Promise<OfxImportResponse> {
    return requestMultipart<OfxImportResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/fidelity/import`,
        buildFormData({ file, accountId, providerAccountId }, 'upload.csv'),
    );
}
