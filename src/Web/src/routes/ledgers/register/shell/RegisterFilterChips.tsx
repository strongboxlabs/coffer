import { useMemo } from 'react';

import { buildAccountPathMap } from '@/lib/accountPath';
import { isRegisterFilterActive, type RegisterFilterArgs } from '@/lib/api/register';
import type { AccountSummary, SecuritySummary } from '@/lib/types';

// Active-filter chips row (mig 164 UI). Renders one removable chip per active
// dimension — search, date range, amount range, category, tag, security — plus
// the match count and a Clear-all. Sits on its own full-width row beneath the
// search/filter controls so wrapping chips never squeeze the controls. Returns
// null when no user filter is active (the row disappears entirely).

/** The user-editable dimensions (status/today are owned elsewhere). One chip
 *  key per group — a date/amount range clears both bounds together. */
type ChipDim = 'search' | 'dateFrom' | 'amountMin' | 'categoryId' | 'tag' | 'securityId';

export interface RegisterFilterChipsProps {
    filter: RegisterFilterArgs;
    onChange: (next: RegisterFilterArgs) => void;
    /** EVERY account in the ledger — resolves the counterparty chip's id → path.
     *  Not just the categories: on a CATEGORY register the counterparty is a
     *  money account, and a list of categories could not name it. */
    accounts: readonly AccountSummary[];
    /** What the other side of a posting is here; labels the chip. */
    counterpartyKind?: 'category' | 'account';
    /** Securities — resolves the security chip's id → ticker/name. */
    securities?: readonly SecuritySummary[];
    /** Total matching entries when a filter is active; null hides the count. */
    resultCount: number | null;
}

export function RegisterFilterChips({
    filter,
    onChange,
    accounts,
    securities,
    resultCount,
    counterpartyKind = 'category',
}: RegisterFilterChipsProps) {
    // Hooks before the early return — Rules of Hooks.
    const categoryPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);

    if (!isRegisterFilterActive(filter)) return null;

    // FULL PATH, not the leaf. This chip is the only thing on screen telling you
    // which of two same-named categories the register is filtered to, and a real
    // ledger has several such pairs — two "Food", two "Loan", two "Automobile"
    // under different parents.
    //
    // The fallback is a placeholder rather than the raw id it used to print: an
    // inactive or deleted category is absent from `categories`, and a UUID in a
    // chip is not an answer to "what am I filtered to". The chip is still
    // removable, which is what the user actually needs from it.
    const categoryName = (id?: string) => {
        if (id === undefined) return undefined;
        return categoryPaths.get(id)
            ?? accounts.find((c) => c.id === id)?.name
            ?? `Unknown ${counterpartyKind}`;
    };
    const securityLabel = (id?: string) => {
        const s = securities?.find((x) => x.id === id);
        return s ? (s.ticker ?? s.name) : id;
    };

    const chips: { dim: ChipDim; label: string }[] = [];
    if (filter.search) chips.push({ dim: 'search', label: `"${filter.search}"` });
    if (filter.dateFrom || filter.dateTo)
        chips.push({ dim: 'dateFrom', label: `${filter.dateFrom ?? '…'} – ${filter.dateTo ?? '…'}` });
    if (filter.amountMin !== undefined || filter.amountMax !== undefined)
        chips.push({ dim: 'amountMin', label: `$${filter.amountMin ?? '0'} – $${filter.amountMax ?? '∞'}` });
    if (filter.categoryId) {
        const what = counterpartyKind === 'account' ? 'Account' : 'Category';
        chips.push({ dim: 'categoryId', label: `${what}: ${categoryName(filter.categoryId)}` });
    }
    if (filter.tag) chips.push({ dim: 'tag', label: `Tag: ${filter.tag}` });
    if (filter.securityId) chips.push({ dim: 'securityId', label: `Security: ${securityLabel(filter.securityId)}` });

    const clearChip = (dim: ChipDim) => {
        if (dim === 'dateFrom') onChange({ ...filter, dateFrom: undefined, dateTo: undefined });
        else if (dim === 'amountMin') onChange({ ...filter, amountMin: undefined, amountMax: undefined });
        else onChange({ ...filter, [dim]: undefined });
    };
    // Clear-all resets every user dimension; status/today are added back by the
    // controller, so an empty object is the correct "no user filter" state.
    // It leaves the sub-category SCOPE alone — the page only re-reads that key
    // when it is explicitly present — because "clear the filters" should not
    // silently change which register you are looking at.
    const clearAll = () => onChange({});

    return (
        <div className="flex min-w-0 flex-wrap items-center gap-1.5 text-[0.6875rem]">
            {resultCount !== null ? (
                <span className="text-text-subtle">
                    {resultCount} match{resultCount === 1 ? '' : 'es'}
                </span>
            ) : null}
            {chips.map((c) => (
                <button
                    key={c.dim}
                    type="button"
                    onClick={() => clearChip(c.dim)}
                    className="inline-flex items-center gap-1 rounded border border-accent bg-accent-soft px-1.5 py-0.5 text-accent hover:bg-accent-soft/70"
                    title="Remove filter"
                >
                    <span>{c.label}</span>
                    <span aria-hidden>✕</span>
                </button>
            ))}
            <button
                type="button"
                onClick={clearAll}
                className="rounded px-1.5 py-0.5 text-text-subtle underline hover:text-text"
            >
                Clear all
            </button>
        </div>
    );
}
