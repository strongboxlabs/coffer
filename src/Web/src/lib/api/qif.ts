// QIF file-upload endpoints (ADR-0042). Multipart, mirroring
// `ofx.ts`; the form builder is shared in `upload.ts` and sending is
// `_request.ts`'s `requestMultipart`.

import { requestMultipart } from './_request';
import { buildFormData } from './upload';
import type {
    QifImportResponse,
    QifPreviewResponse,
} from '../types/qif';

/** Upload a QIF file for preview. No DB writes — returns the single
 *  discovered account block (QIF is single-account-implicit) so the
 *  wizard can show the transaction count before import. `accountId` is
 *  the account the file will go into: the server reads its currency, so
 *  an amount the import would refuse for that currency's places is
 *  reported in the preview too (ADR-0114 D15). */
export async function previewQif(
    ledgerId: string,
    file: Blob,
    accountId?: string,
): Promise<QifPreviewResponse> {
    return requestMultipart<QifPreviewResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/qif/preview`,
        buildFormData({ file, accountId }, 'upload.qif'),
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
    return requestMultipart<QifImportResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/qif/import`,
        buildFormData({ file, accountId, providerAccountId }, 'upload.qif'),
    );
}
