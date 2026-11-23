import { mergeTransaction, patchTransaction } from '@/lib/api';
import type { PatchTransactionRequest, RegisterEntry } from '@/lib/types';

/**
 * The bank editor's edit save: a merge when the body arms one, else the PATCH.
 *
 * A merge is a command with its own route. Once a candidate is picked the
 * editor's fields are moot and it builds a merge-only body
 * (`{ mergeFromHeaderId }`), so the field's presence is the whole signal: there
 * is no mixed edit-and-merge save to tell apart, and the PATCH contract does
 * not accept the field. The edited row is the loser; the candidate survives as
 * it was entered, and the response is the survivor's entries.
 *
 * With `accountId`, both answer the saved header's entries on that account,
 * which the register swaps in place.
 */
export function saveBankEdit(
    ledgerId: string,
    headerId: string,
    body: PatchTransactionRequest,
    accountId?: string,
): Promise<RegisterEntry[]> {
    return body.mergeFromHeaderId
        ? mergeTransaction(ledgerId, headerId, body.mergeFromHeaderId, accountId)
        : patchTransaction(ledgerId, headerId, body, accountId);
}
