import { useEffect, useId, useRef, useState, type ReactNode } from 'react';

import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { TagCombobox } from '@/components/tags/TagCombobox';
import { SecurityField } from '@/routes/ledgers/investment-edit/fields/SecurityField';
import type { RegisterFilterArgs } from '@/lib/api/register';
import type { AccountSummary, SecuritySummary, TagDto } from '@/lib/types';

/**
 * Register search + structured-filter controls (mig 164 UI). A compact live
 * search box plus a "Filter ▾" popover for the structured dimensions (date /
 * amount range, category, tag, security). All server-side (the register is
 * windowed), so this only edits the {@link RegisterFilterArgs} and hands it up;
 * the controller pushes it into the fetch. The active-filter chips live in the
 * sibling {@link RegisterFilterChips} row so they can span the full width below
 * this control row.
 *
 * Category + security reuse the app's shared pickers (AccountCategoryPicker,
 * SecurityField) — the same id-based controls the transaction editor uses —
 * rather than bare <select>s, so selection looks and behaves identically
 * everywhere (feedback: reuse shared controls).
 *
 * The `status`/`today` dimensions are owned elsewhere (the status dropdown +
 * the controller), so they're intentionally absent here.
 */
export interface RegisterFilterControlsProps {
    filter: RegisterFilterArgs;
    onChange: (next: RegisterFilterArgs) => void;
    /** Categories (account_type='category') for the Category picker. */
    categories: readonly AccountSummary[];
    /** Ledger tags for the Tag filter's autocomplete. */
    tags: readonly TagDto[];
    /** Securities for the Security picker — omit to hide it (bank registers). */
    securities?: readonly SecuritySummary[];
}

export function RegisterFilterControls({
    filter,
    onChange,
    categories,
    tags,
    securities,
}: RegisterFilterControlsProps) {
    const popId = useId();
    const [open, setOpen] = useState(false);
    const rootRef = useRef<HTMLDivElement>(null);

    // Search is debounced locally so each keystroke doesn't re-seed the window;
    // the rest apply immediately (they change less often).
    const [searchText, setSearchText] = useState(filter.search ?? '');
    useEffect(() => setSearchText(filter.search ?? ''), [filter.search]);
    useEffect(() => {
        const trimmed = searchText.trim();
        const current = filter.search ?? '';
        if (trimmed === current) return;
        const t = setTimeout(() => onChange({ ...filter, search: trimmed || undefined }), 300);
        return () => clearTimeout(t);
    }, [searchText, filter, onChange]);

    // Close the popover on outside click.
    useEffect(() => {
        if (!open) return;
        const onDoc = (e: MouseEvent) => {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
        };
        document.addEventListener('mousedown', onDoc);
        return () => document.removeEventListener('mousedown', onDoc);
    }, [open]);

    const set = (patch: Partial<RegisterFilterArgs>) => onChange({ ...filter, ...patch });
    const num = (v: string): number | undefined => {
        const n = Number(v);
        return v.trim() === '' || Number.isNaN(n) ? undefined : n;
    };

    // The "Filter · N" badge counts the POPOVER dimensions only — search has its
    // own box (and its own chip), so it isn't part of this count.
    const popoverCount =
        (filter.dateFrom || filter.dateTo ? 1 : 0)
        + (filter.amountMin !== undefined || filter.amountMax !== undefined ? 1 : 0)
        + (filter.categoryId ? 1 : 0)
        + (filter.tag ? 1 : 0)
        + (filter.securityId ? 1 : 0);
    const anyPopover = popoverCount > 0;

    const fieldClass = 'h-7 rounded border border-border bg-surface px-2 text-xs';

    return (
        <div ref={rootRef} className="flex min-w-0 items-center gap-2">
            <input
                type="search"
                value={searchText}
                onChange={(e) => setSearchText(e.target.value)}
                placeholder="Search…"
                aria-label="Search transactions"
                title="Search payee, memo, check #, category, or tag"
                className={`${fieldClass} w-56`}
            />
            <div className="relative shrink-0">
                <button
                    type="button"
                    onClick={() => setOpen((v) => !v)}
                    aria-expanded={open}
                    aria-controls={popId}
                    className={
                        'inline-flex h-7 items-center gap-1 rounded border px-2 text-xs '
                        + (anyPopover
                            ? 'border-accent bg-accent-soft text-accent'
                            : 'border-border bg-surface text-text hover:border-accent')
                    }
                >
                    Filter{anyPopover ? ` · ${popoverCount}` : ''}
                    <span aria-hidden>▾</span>
                </button>
                {open ? (
                    <div
                        id={popId}
                        className="absolute right-0 z-30 mt-1 w-80 rounded border border-border bg-surface p-3 text-xs shadow-lg"
                    >
                        <div className="flex gap-2">
                            <FilterField label="Date from">
                                <input type="date" value={filter.dateFrom ?? ''}
                                    onChange={(e) => set({ dateFrom: e.target.value || undefined })}
                                    className={`${fieldClass} w-full`} />
                            </FilterField>
                            <FilterField label="Date to">
                                <input type="date" value={filter.dateTo ?? ''}
                                    onChange={(e) => set({ dateTo: e.target.value || undefined })}
                                    className={`${fieldClass} w-full`} />
                            </FilterField>
                        </div>
                        <div className="flex gap-2">
                            <FilterField label="Amount min">
                                <input type="number" inputMode="decimal" value={filter.amountMin ?? ''}
                                    onChange={(e) => set({ amountMin: num(e.target.value) })}
                                    className={`${fieldClass} w-full`} />
                            </FilterField>
                            <FilterField label="Amount max">
                                <input type="number" inputMode="decimal" value={filter.amountMax ?? ''}
                                    onChange={(e) => set({ amountMax: num(e.target.value) })}
                                    className={`${fieldClass} w-full`} />
                            </FilterField>
                        </div>
                        <div className="mb-2">
                            <AccountCategoryPicker
                                accounts={categories}
                                isEligible={(a) => a.accountType === 'category'}
                                valueId={filter.categoryId ?? null}
                                onChangeId={(id) => set({ categoryId: id ?? undefined })}
                                label="Category"
                                placeholder="Any category"
                            />
                        </div>
                        <FilterField label="Tag">
                            {filter.tag ? (
                                <span className="inline-flex items-center gap-1 self-start rounded bg-surface-muted px-1.5 py-0.5 text-[0.6875rem] text-text">
                                    {filter.tag}
                                    <button
                                        type="button"
                                        onClick={() => set({ tag: undefined })}
                                        aria-label={`Clear tag filter ${filter.tag}`}
                                        className="text-text-subtle hover:text-state-danger focus-visible:outline-none"
                                    >
                                        ×
                                    </button>
                                </span>
                            ) : (
                                <TagCombobox
                                    tags={tags}
                                    allowCreate={false}
                                    onCommit={(name) => set({ tag: name })}
                                    placeholder="Any tag"
                                    aria-label="Filter by tag"
                                    inputClassName={`${fieldClass} w-full`}
                                />
                            )}
                        </FilterField>
                        {securities ? (
                            <SecurityField
                                securities={securities}
                                valueId={filter.securityId ?? null}
                                onChangeId={(id) => set({ securityId: id ?? undefined })}
                            />
                        ) : null}
                    </div>
                ) : null}
            </div>
        </div>
    );
}

function FilterField({ label, children }: { label: string; children: ReactNode }) {
    return (
        <label className="mb-2 flex min-w-0 flex-1 flex-col gap-0.5">
            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">
                {label}
            </span>
            {children}
        </label>
    );
}
