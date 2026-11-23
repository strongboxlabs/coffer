import { useEffect, useMemo, useRef, type RefObject } from 'react';

import { buildAccountPathMap } from '@/lib/accountPath';
import { isRegisterFilterActive, type RegisterFilterArgs } from '@/lib/api/register';
import { counterpartyWords, type CounterpartyKind } from '@/lib/counterparty';
import { formatCurrency } from '@/lib/money';
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
    counterpartyKind?: CounterpartyKind;
    /** The amount range's currency: the account's on an account register, null on a
     *  category register (ADR-0114 D14). */
    currency: string | null;
    /** Securities — resolves the security chip's id → ticker/name. */
    securities?: readonly SecuritySummary[];
    /** Total matching entries when a filter is active; null hides the count. */
    resultCount: number | null;
    /** Where focus goes when a press in the row removes the row: the host's
     *  search box. Required: Clear all and the last chip take the row away,
     *  and with nowhere to go, focus would be left on the page. */
    fallbackFocusRef: RefObject<HTMLElement | null>;
}

/** The control focus goes on to after a press in the row: a chip, or Clear all. */
type FocusTarget = ChipDim | 'clearAll';

export function RegisterFilterChips({
    filter,
    onChange,
    accounts,
    securities,
    resultCount,
    counterpartyKind = 'category',
    currency,
    fallbackFocusRef,
}: RegisterFilterChipsProps) {
    // Hooks before the early return — Rules of Hooks.
    const categoryPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);

    // Removing a chip removes the button that held focus, and Clear all or the
    // last chip removes the row. Focus goes on to the control that takes its
    // place (ADR-0023 §L): the next chip, else Clear all, and with the row
    // gone, the host's fallback. Left on the page, it would sit outside every
    // control of the register's header.
    const chipRefs = useRef(new Map<ChipDim, HTMLButtonElement>());
    const clearAllRef = useRef<HTMLButtonElement>(null);
    const focusNext = useRef<FocusTarget | null>(null);
    useEffect(() => {
        const target = focusNext.current;
        if (target === null) return;
        focusNext.current = null;
        const el = target === 'clearAll' ? clearAllRef.current : chipRefs.current.get(target);
        (el ?? clearAllRef.current ?? fallbackFocusRef.current)?.focus();
    }, [filter, fallbackFocusRef]);

    if (!isRegisterFilterActive(filter)) return null;

    // FULL PATH, not the leaf. This chip is the only thing on screen telling you
    // which of two same-named categories the register is filtered to, and a
    // ledger can hold the same leaf name under different parents, any number
    // of times.
    //
    // The fallback is a placeholder rather than the raw id: an
    // inactive or deleted category is absent from `categories`, and a UUID in a
    // chip is not an answer to "what am I filtered to". The chip is still
    // removable, which is what the user actually needs from it.
    const categoryName = (id?: string) => {
        if (id === undefined) return undefined;
        return categoryPaths.get(id)
            ?? accounts.find((c) => c.id === id)?.name
            ?? `Unknown ${counterpartyWords(counterpartyKind).lower}`;
    };
    const securityLabel = (id?: string) => {
        const s = securities?.find((x) => x.id === id);
        return s ? (s.ticker ?? s.name) : id;
    };

    const chips: { dim: ChipDim; label: string }[] = [];
    if (filter.search) chips.push({ dim: 'search', label: `"${filter.search}"` });
    if (filter.dateFrom || filter.dateTo)
        chips.push({ dim: 'dateFrom', label: `${filter.dateFrom ?? '…'} – ${filter.dateTo ?? '…'}` });
    // On an account register the bounds are in the account's currency and read as its
    // amounts do; on a category register, whose rows can be in different currencies,
    // they read as typed, with no symbol (ADR-0114 D14). The open end stays ∞.
    const bound = (n: number) => (currency === null ? String(n) : formatCurrency(n, currency));
    if (filter.amountMin !== undefined || filter.amountMax !== undefined)
        chips.push({
            dim: 'amountMin',
            label: `Amount: ${bound(filter.amountMin ?? 0)} – ${filter.amountMax === undefined ? '∞' : bound(filter.amountMax)}`,
        });
    if (filter.categoryId) {
        const what = counterpartyWords(counterpartyKind).noun;
        chips.push({ dim: 'categoryId', label: `${what}: ${categoryName(filter.categoryId)}` });
    }
    if (filter.tag) chips.push({ dim: 'tag', label: `Tag: ${filter.tag}` });
    if (filter.securityId) chips.push({ dim: 'securityId', label: `Security: ${securityLabel(filter.securityId)}` });

    const clearChip = (dim: ChipDim) => {
        focusNext.current = chips[chips.findIndex((c) => c.dim === dim) + 1]?.dim ?? 'clearAll';
        if (dim === 'dateFrom') onChange({ ...filter, dateFrom: undefined, dateTo: undefined });
        else if (dim === 'amountMin') onChange({ ...filter, amountMin: undefined, amountMax: undefined });
        else onChange({ ...filter, [dim]: undefined });
    };
    // Clear-all resets every user dimension; status/today are added back by the
    // controller, so an empty object is the correct "no user filter" state.
    // It leaves the sub-category SCOPE alone — the page only re-reads that key
    // when it is explicitly present — because "clear the filters" should not
    // silently change which register you are looking at.
    const clearAll = () => {
        focusNext.current = 'clearAll';
        onChange({});
    };

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
                    ref={(el) => {
                        if (el) chipRefs.current.set(c.dim, el);
                        else chipRefs.current.delete(c.dim);
                    }}
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
                ref={clearAllRef}
                type="button"
                onClick={clearAll}
                className="rounded px-1.5 py-0.5 text-text-subtle underline hover:text-text"
            >
                Clear all
            </button>
        </div>
    );
}
