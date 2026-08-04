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
    /** Categories — resolves the category chip's id → name. */
    categories: readonly AccountSummary[];
    /** Securities — resolves the security chip's id → ticker/name. */
    securities?: readonly SecuritySummary[];
    /** Total matching entries when a filter is active; null hides the count. */
    resultCount: number | null;
}

export function RegisterFilterChips({
    filter,
    onChange,
    categories,
    securities,
    resultCount,
}: RegisterFilterChipsProps) {
    if (!isRegisterFilterActive(filter)) return null;

    const categoryName = (id?: string) => categories.find((c) => c.id === id)?.name ?? id;
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
    if (filter.categoryId) chips.push({ dim: 'categoryId', label: `Category: ${categoryName(filter.categoryId)}` });
    if (filter.tag) chips.push({ dim: 'tag', label: `Tag: ${filter.tag}` });
    if (filter.securityId) chips.push({ dim: 'securityId', label: `Security: ${securityLabel(filter.securityId)}` });

    const clearChip = (dim: ChipDim) => {
        if (dim === 'dateFrom') onChange({ ...filter, dateFrom: undefined, dateTo: undefined });
        else if (dim === 'amountMin') onChange({ ...filter, amountMin: undefined, amountMax: undefined });
        else onChange({ ...filter, [dim]: undefined });
    };
    // Clear-all resets every user dimension; status/today are added back by the
    // controller, so an empty object is the correct "no user filter" state.
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
