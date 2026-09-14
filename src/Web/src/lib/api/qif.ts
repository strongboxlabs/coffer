// QIF file-upload endpoints (ADR-0042). Multipart, mirroring
// `ofx.ts`. The third surface landed (Fidelity, ADR-0031 Phase 6),
// so the form builder moved to `upload.ts` as this note asked —
// and the local `uploadMultipart` went with it, having been a
// reinvention of `_request.ts`'s `requestMultipart` all along.

import { requestMultipart } from './_request';
import { buildFormData } from './upload';
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
    return requestMultipart<QifPreviewResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/ingest/qif/preview`,
        buildFormData({ file }, 'upload.qif'),
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
