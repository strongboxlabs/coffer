/**
 * Extracted from TxnRowEdit.tsx during the Slice 3 decomposition. Presentational
 * only: it holds no draft state and fetches nothing — the shell owns the query
 * and hands results down as readonly props, per the boundary rule in
 * ../README.md.
 */

import { usePendingButtonProps } from '@/components/ui/pending';
import { cn } from '@/lib/cn';
import type { MergeCandidateDto } from '@/lib/types';

import { DIALOG_NARROW_HINT_CHIP, DIALOG_NARROW_HINT_STRIP } from './hintChips';
import { mergeChipTitle } from './mergeChipTitle';

// --------------------------------------------------------------------
// MergeCandidatesPanel (slice 2c.6d)
// --------------------------------------------------------------------
// "Merge candidates" chip row beneath the payee field. Each chip
// represents a settled row (manual, or a feed or imported row already
// accepted) whose source-account aggregate equals this row's, within
// ±7 days. Clicking one arms `mergeFromHeaderId`
// for the next save, which then folds the row being edited into the
// candidate (bank-edit/save.ts): the candidate survives as it was
// entered and the edited row is marked merged. Nothing is copied into
// the form; its fields are moot once a candidate is armed.
//
// Selection is a toggle: re-clicking the active chip clears the
// merge arm, and the form is as the user left it.

export function MergeCandidatesPanel({
    candidates,
    accountPaths,
    selectedHeaderId,
    pending,
    dialog,
    onSelect,
}: {
    candidates: readonly MergeCandidateDto[];
    accountPaths: Map<string, string>;
    selectedHeaderId: string | null;
    /** The editor's save is in flight: the chips keep focus and refuse a pick (pending.ts). */
    pending: boolean;
    /** The editor is in its dialog layout: on a narrow dialog the chips
     *  take the touch size (hintChips.ts). False in the register. */
    dialog: boolean;
    onSelect: (candidate: MergeCandidateDto) => void;
}) {
    const pendingProps = usePendingButtonProps();
    if (candidates.length === 0) return null;
    return (
        <div className={cn(
            'flex min-w-0 flex-wrap items-baseline gap-x-1.5 gap-y-1 pt-0.5 text-[0.625rem]',
            dialog && DIALOG_NARROW_HINT_STRIP,
        )}>
            <span className="text-text-subtle">Merge candidates:</span>
            {candidates.map((c) => {
                const isSelected = selectedHeaderId === c.headerId;
                // postedAt arrives UTC-anchored (server treats it as
                // a calendar date); slice the date portion directly
                // — round-tripping through new Date(...).toISOString()
                // is equivalent but slower.
                const dateLabel = c.postedAt.slice(0, 10);
                const summary =
                    c.postings.length === 1
                        ? accountPaths.get(c.postings[0]!.counterpartyAccountId)
                          ?? c.postings[0]!.counterpartyAccountName
                        : `${c.postings.length} splits`;
                return (
                    <button
                        key={c.headerId}
                        type="button"
                        aria-pressed={isSelected}
                        title={mergeChipTitle(dateLabel, c.origin)}
                        {...pendingProps(pending, () => onSelect(c))}
                        className={cn(
                            'inline-flex items-baseline gap-1 rounded border px-1.5 py-0.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                            isSelected
                                ? 'border-accent bg-accent-soft text-accent'
                                : 'border-border bg-surface text-text hover:border-accent hover:bg-surface-muted',
                            dialog && DIALOG_NARROW_HINT_CHIP,
                        )}
                    >
                        <span className="text-text-subtle">{dateLabel}</span>
                        <span className="font-medium">
                            {c.payee ?? '(no payee)'}
                        </span>
                        <span className="text-text-subtle">→</span>
                        <span>{summary}</span>
                        {isSelected ? (
                            <span className="text-text-subtle" aria-hidden>✓</span>
                        ) : null}
                    </button>
                );
            })}
        </div>
    );
}
