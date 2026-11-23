import type { ReactNode } from 'react';

import type { RegisterRowNotes } from '@/lib/types';
import { displayAccountPath } from '@/lib/accountPath';
import { InheritedText } from '@/components/register/InheritedMarker';
import { TagLine } from '@/components/tags/TagLine';

/**
 * A register row's memo and tags as the server presented them for this
 * register: plain when they are this side's own, with the inherited marker
 * when they are the other side's. Shared by both registers, so a bank row and
 * an investment row cannot draw the same fields differently.
 *
 * Nothing here decides which value a row shows; `memo`, `tags` and the two
 * `…Inherited` flags already say it.
 */

function fromName(
    row: Pick<RegisterRowNotes, 'inheritFromAccountId'>,
    accountPaths: ReadonlyMap<string, string> | undefined,
): string | null {
    return displayAccountPath(accountPaths, row.inheritFromAccountId, null) ?? null;
}

/** The memo subtitle under the payee; nothing when the row shows no memo. */
export function renderRowMemo(
    row: Pick<RegisterRowNotes, 'memo' | 'memoInherited' | 'inheritFromAccountId'>,
    accountPaths?: ReadonlyMap<string, string>,
): ReactNode {
    if (!row.memo) return null;
    if (!row.memoInherited) {
        return (
            <span className="block truncate text-[0.6875rem] text-text-muted">
                {row.memo}
            </span>
        );
    }
    return (
        <InheritedText inherited from={fromName(row, accountPaths)} className="text-[0.6875rem]">
            {row.memo}
        </InheritedText>
    );
}

/** The tag line under the category; nothing when the row shows no tags. */
export function renderRowTags(
    row: Pick<RegisterRowNotes, 'tags' | 'tagsInherited' | 'inheritFromAccountId'>,
    accountPaths?: ReadonlyMap<string, string>,
): ReactNode {
    return (
        <TagLine
            tags={row.tags}
            inset
            inherited={row.tagsInherited ? { from: fromName(row, accountPaths) } : false}
        />
    );
}
