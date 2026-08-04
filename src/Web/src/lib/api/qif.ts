// QIF file-upload endpoints (ADR-0042). Multipart, mirroring
// `ofx.ts`. When a third multipart surface lands, the shared
// upload helper noted in ofx.ts should move to `_request.ts`.

import { ApiError } from './_request';
import type {
    QifImportResponse,
    QifPreviewResponse,
} from '../types/qif';

/** Upload a QIF file for preview. No DB writes — returns the single
 *  discovered account block (QIF is single-account-implicit) so the
 *  wizard can show the transaction count before import. */
export async function previewQif(
    ledgerId: string,
    file: Blob,
): Promise<QifPreviewResponse> {
    return uploadMultipart<QifPreviewResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/qif/preview`,
        buildFormData({ file }),
    );
}

/** Re-upload the file plus the target Coffer `accountId` and run the
 *  import. `providerAccountId` is the sentinel from `previewQif`
 *  ('qif'); passed for shape-parity with the OFX import. */
export async function importQif(
    ledgerId: string,
    file: Blob,
    accountId: string,
    providerAccountId: string,
): Promise<QifImportResponse> {
    return uploadMultipart<QifImportResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/qif/import`,
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
    // though the parser doesn't read it.
    form.append('file', fields.file, 'upload.qif');
    if (fields.accountId !== undefined) {
        form.append('accountId', fields.accountId);
    }
    if (fields.providerAccountId !== undefined) {
        form.append('providerAccountId', fields.providerAccountId);
    }
    return form;
}

async function uploadMultipart<T>(path: string, body: FormData): Promise<T> {
    // Content-Type intentionally unset — the browser adds the
    // multipart boundary; setting it manually strips the boundary.
    const response = await fetch(path, {
        method: 'POST',
        credentials: 'include',
        headers: { Accept: 'application/json' },
        body,
    });
    if (!response.ok) {
        throw await buildApiError(response);
    }
    return (await response.json()) as T;
}

async function buildApiError(response: Response): Promise<ApiError> {
    let code: string | undefined;
    let detail = response.statusText || `HTTP ${response.status}`;
    try {
        const body = (await response.json()) as {
            detail?: string;
            title?: string;
            code?: string;
        };
        if (typeof body.detail === 'string' && body.detail.length > 0) {
            detail = body.detail;
        } else if (typeof body.title === 'string' && body.title.length > 0) {
            detail = body.title;
        }
        if (typeof body.code === 'string') {
            code = body.code;
        }
    } catch {
        // Body wasn't JSON; status-text fallback stays.
    }
    return new ApiError(response.status, detail, code);
}
