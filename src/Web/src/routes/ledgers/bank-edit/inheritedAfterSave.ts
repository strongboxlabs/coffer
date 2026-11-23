import type { InheritedNotes } from '@/lib/annotations/notes';

import type { PostingDraft } from './postingDraft';

/**
 * What a one-posting row's side will inherit once the bank editor's draft is
 * saved: the value behind the Memo and Tags captions' "from X", the marker in
 * each field and the fields' placeholders.
 *
 * `stored` is what the side inherits as the row is stored (the edit seed's
 * `inherited`) and `storedLegId` the source-side leg of its one posting. The
 * draft's one posting decides the rest, by what the PATCH does with it:
 *
 *   - The stored posting, kept (same leg id). The save keeps both of its legs
 *     and their ids; a changed counterparty only moves the other leg to that
 *     account, and its memo and tags go with it (values follow their legs).
 *     So the value is the stored one, and it comes from the counterparty the
 *     draft has now.
 *   - A posting added after the stored one was removed. The save deletes the
 *     stored legs, with their values, and inserts legs that carry none. So
 *     nothing is inherited.
 *
 * The side is named by the draft's counterparty, never by the stored one,
 * because the draft is what the save writes: on a row awaiting review that is
 * stored against Uncategorized, the category picked in the field. It is null
 * while the field names none, and the caption then says "the other side".
 *
 * Null when nothing is stored to inherit (a split, a create).
 */
export function inheritedAfterSave(
    stored: InheritedNotes | null,
    storedLegId: string | null,
    posting: Pick<PostingDraft, 'legId' | 'counterpartyId'>,
): InheritedNotes | null {
    if (stored === null) return null;
    const kept = posting.legId !== null && posting.legId === storedLegId;
    return {
        memo: kept ? stored.memo : null,
        tags: kept ? stored.tags : [],
        fromAccountId: posting.counterpartyId,
    };
}
