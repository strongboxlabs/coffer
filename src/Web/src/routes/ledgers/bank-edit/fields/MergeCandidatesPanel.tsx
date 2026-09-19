/**
 * Extracted from TxnRowEdit.tsx during the Slice 3 decomposition. Presentational
 * only: it holds no draft state and fetches nothing — the shell owns the query
 * and hands results down as readonly props, per the boundary rule in
 * ../README.md.
 */

import type { MergeCandidateDto } from '@/lib/types';

// --------------------------------------------------------------------
// MergeCandidatesPanel (slice 2c.6d)
// --------------------------------------------------------------------
// "Possible matches" chip row beneath the payee field. Each chip
// represents a manual row whose source-account aggregate equals
// this row's, within ±7 days. Clicking pre-fills the editor with
// the candidate's payee / memo / tags / postings AND arms
// `mergeFromHeaderId` for the next save (the saving handler sends
// it in the PATCH body so the server stamps `is_merged_into` on
// the manual row in the same transaction).
//
// Selection is a toggle: re-clicking the active chip clears the
// merge arm (keeping the form state) — provides a "cancel merge,
// keep my edits" path without restoring original state.

export function MergeCandidatesPanel({
    candidates,
    accountPaths,
    selectedHeaderId,
    disabled,
    onSelect,
}: {
    candidates: readonly MergeCandidateDto[];
    accountPaths: Map<string, string>;
    selectedHeaderId: string | null;
    disabled: boolean;
    onSelect: (candidate: MergeCandidateDto) => void;
}) {
    if (candidates.length === 0) return null;
    return (
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-1.5 gap-y-1 pt-0.5 text-[0.625rem]">
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
                        disabled={disabled}
                        onClick={() => onSelect(c)}
                        aria-pressed={isSelected}
                        title={
                            isSelected
                                ? 'Click to cancel the merge (form edits stay).'
                                : `Merge with ${c.payee ?? '(no payee)'} (${dateLabel}). The bank row keeps its identity; the manual row is marked as merged.`
                        }
                        className={
                            'inline-flex items-baseline gap-1 rounded border px-1.5 py-0.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 ' +
                            (isSelected
                                ? 'border-accent bg-accent-soft text-accent'
                                : 'border-border bg-surface text-text hover:border-accent hover:bg-surface-muted')
                        }
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
