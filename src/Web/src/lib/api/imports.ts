// Undoing a file import (mig 221).
//
// This is what file imports have instead of dedup. CSV rows carry no
// issuer-assigned id, so matching a re-uploaded statement would mean inferring
// identity from content — which cannot separate two genuinely identical
// transactions and gets it silently wrong in both directions. Removing exactly
// the rows one import wrote needs no inference at all.

import { request } from './_request';
import type { UndoImportResult } from '../types/imports';

/**
 * Undo one import, or count what it would remove.
 *
 * `dryRun` returns the counts and changes nothing, so a confirm can state how
 * many transactions will go and how many of them have been edited since.
 * Deleting money on an unconfirmed click is not something to make easy.
 */
export async function undoImport(
    ledgerId: string,
    operationId: string,
    options: { dryRun?: boolean } = {},
): Promise<UndoImportResult> {
    const query = options.dryRun ? '?dryRun=true' : '';
    return request<UndoImportResult>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}`
        + `/ledger-operations/${encodeURIComponent(operationId)}/undo-import${query}`,
        { method: 'POST' },
    );
}
