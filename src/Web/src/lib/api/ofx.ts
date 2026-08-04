// OFX/QFX file-upload endpoints (ADR-0031 Phase 4 slice 3).

import { requestMultipart } from './_request';
import type {
    OfxImportResponse,
    OfxPreviewResponse,
} from '../types/ofx';

/** Upload an OFX/QFX file for preview. No DB writes — returns the
 *  set of discovered account blocks so the wizard can ask the user
 *  which one maps to the current Coffer account. */
export async function previewOfx(
    ledgerId: string,
    file: Blob,
): Promise<OfxPreviewResponse> {
    return requestMultipart<OfxPreviewResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/ofx/preview`,
        buildFormData({ file }),
    );
}

/** Re-upload the file plus a confirmed mapping and run the import.
 *  The user's chosen `providerAccountId` (from `previewOfx`)
 *  filters the file's transactions down to one account; the
 *  `accountId` is the Coffer-side target. */
export async function importOfx(
    ledgerId: string,
    file: Blob,
    accountId: string,
    providerAccountId: string,
): Promise<OfxImportResponse> {
    return requestMultipart<OfxImportResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/ofx/import`,
        buildFormData({ file, accountId, providerAccountId }),
    );
}

function buildFormData(fields: {
    file: Blob;
    accountId?: string;
    providerAccountId?: string;
}): FormData {
    const form = new FormData();
    // Filename is required by the API's IFormFile binding even
    // though the parser doesn't read it; "upload.ofx" keeps the
    // wire shape generic.
    form.append('file', fields.file, 'upload.ofx');
    if (fields.accountId !== undefined) {
        form.append('accountId', fields.accountId);
    }
    if (fields.providerAccountId !== undefined) {
        form.append('providerAccountId', fields.providerAccountId);
    }
    return form;
}
