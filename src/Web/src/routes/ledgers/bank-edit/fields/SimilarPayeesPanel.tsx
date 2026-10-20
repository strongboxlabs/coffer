/**
 * Extracted from TxnRowEdit.tsx during the Slice 3 decomposition. Presentational
 * only: it holds no draft state and fetches nothing — the shell owns the query
 * and hands results down as readonly props, per the boundary rule in
 * ../README.md.
 */

import type { SimilarPayeeDto } from '@/lib/types';

// --------------------------------------------------------------------
// SimilarPayeesPanel (slice 2c.6c)
// --------------------------------------------------------------------
// Inline chip row beneath the payee input in the single-posting edit
// template. Static at row-open (per design — no typeahead refetch).
// Renders nothing when there are no suggestions (server returns []
// for non-bank-feed rows, missing payees, or no matches). Clicking a
// chip applies its (payee, counterparty) pair to the form draft —
// counterparty path resolution goes through the editor's existing
// accountPaths lookup so the Typeahead's display matches the format
// the rest of the form expects. The counterparty is a category on an
// ordinary expense and a real account when the prior rows were
// settled as transfers; accountPaths covers every account in the
// ledger, so both render the same way.

export function SimilarPayeesPanel({
    suggestions,
    accountPaths,
    disabled,
    onApply,
    showsCounterparty = () => true,
}: {
    suggestions: readonly SimilarPayeeDto[];
    accountPaths: Map<string, string>;
    disabled: boolean;
    onApply: (suggestion: SimilarPayeeDto) => void;
    /** Whether this chip's counterparty half will actually be APPLIED when the
     *  chip is clicked. A chip that shows "→ X" and then does not set X reads
     *  as half-broken, so the arrow and the account name are rendered only
     *  when the caller can take them.
     *
     *  Defaults to true, which is the bank editor: every suggestion it gets
     *  names a category or a transfer account, both of which its posting
     *  picker accepts. The investment editor cannot always take one — a sell
     *  has no category slot, and on a brokerage a prior buy's counterparty is
     *  the structural Holdings sub-account (ADR-0019) that no picker offers —
     *  but the PAYEE is still worth recalling, so the chip stays and loses its
     *  second half. */
    showsCounterparty?: (suggestion: SimilarPayeeDto) => boolean;
}) {
    if (suggestions.length === 0) return null;
    return (
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-1.5 gap-y-1 pt-0.5 text-[0.625rem]">
            <span className="text-text-subtle">Similar:</span>
            {suggestions.map((s) => {
                const counterpartyLabel =
                    accountPaths.get(s.counterpartyAccountId) ?? s.counterpartyAccountName;
                const withCounterparty = showsCounterparty(s);
                return (
                    <button
                        key={`${s.payee}::${s.counterpartyAccountId}`}
                        type="button"
                        disabled={disabled}
                        onClick={() => onApply(s)}
                        title={
                            withCounterparty
                                ? `Apply payee "${s.payee}" → "${counterpartyLabel}" (used ${s.useCount}×)`
                                : `Apply payee "${s.payee}" (used ${s.useCount}×)`
                        }
                        className="inline-flex items-baseline gap-1 rounded border border-border bg-surface px-1.5 py-0.5 text-text hover:border-accent hover:bg-surface-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50"
                    >
                        <span className="font-medium">{s.payee}</span>
                        {withCounterparty ? (
                            <>
                                <span className="text-text-subtle">→</span>
                                <span>{counterpartyLabel}</span>
                            </>
                        ) : null}
                        {s.useCount > 1 ? (
                            <span className="text-text-subtle">×{s.useCount}</span>
                        ) : null}
                    </button>
                );
            })}
        </div>
    );
}
