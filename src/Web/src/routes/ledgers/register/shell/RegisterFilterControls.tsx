import { useEffect, useId, useRef, useState, type ReactNode, type Ref } from 'react';

import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { TagCombobox } from '@/components/tags/TagCombobox';
import {
    dismissOnEscape,
    happenedInside,
    useDismissOnPageEscape,
    useReturnFocusOnClose,
} from '@/components/ui/popoverDismissal';
import { MoneyInput } from '@/components/ui/MoneyInput';
import { SecurityField } from '@/routes/ledgers/investment-edit/fields/SecurityField';
import { sameRegisterFilter, type RegisterFilterArgs } from '@/lib/api/register';
import { counterpartyWords, type CounterpartyKind } from '@/lib/counterparty';
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
    /** EVERY account in the ledger. The picker applies its own eligibility and
     *  needs the rest for parent-path building. */
    accounts: readonly AccountSummary[];
    /**
     * What the OTHER side of a posting is on THIS register.
     *
     * The filter's `categoryId` is really "counterparty account id" — it has
     * always matched on the other leg, whatever type it is. On a money
     * account's register the other side is a category, so the picker offers
     * categories. On a CATEGORY's register the other side is a money account,
     * and offering categories there filtered an axis the rows do not have: the
     * counterparty column shows "Checking", and nothing in the list matched it.
     */
    counterpartyKind?: CounterpartyKind;
    /** The amount range's currency: the account's on an account register, so its
     *  inputs show the symbol and round at its places; null on a category register,
     *  whose rows can be in different currencies: no symbol, no rounding (ADR-0114 D14). */
    currency: string | null;
    /** Ledger tags for the Tag filter's autocomplete. */
    tags: readonly TagDto[];
    /** Securities for the Security picker — omit to hide it (bank registers). */
    securities?: readonly SecuritySummary[];
    /** The search box, for a host that moves focus to it. */
    searchRef?: Ref<HTMLInputElement>;
}

export function RegisterFilterControls({
    filter,
    onChange,
    accounts,
    tags,
    securities,
    counterpartyKind = 'category',
    currency,
    searchRef,
}: RegisterFilterControlsProps) {
    const facingAccounts = counterpartyKind === 'account';
    const counterpartyLabel = counterpartyWords(counterpartyKind).noun;
    const popId = useId();
    const [open, setOpen] = useState(false);
    const rootRef = useRef<HTMLDivElement>(null);
    const toggleRef = useRef<HTMLButtonElement>(null);

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

    // Close the popover on a press outside it. A pick in the category picker is
    // a press inside, though it takes the option pressed off the page before
    // this hears it (ADR-0023 §L), so inside is where the press landed.
    useEffect(() => {
        if (!open) return;
        const onDoc = (e: MouseEvent) => {
            if (rootRef.current && !happenedInside(e, rootRef.current)) setOpen(false);
        };
        document.addEventListener('mousedown', onDoc);
        return () => document.removeEventListener('mousedown', onDoc);
    }, [open]);

    // Esc closes it too, from the popover or from the page, and focus goes
    // back to the Filter button however it closes (ADR-0023 §L). A list inside
    // (the picker, the tag suggestions, the security search) closes on the
    // first Esc and stops it, so the popover closes on the next. With the
    // popover closed, Esc in the search box beside it is the box's, which
    // clears it.
    useDismissOnPageEscape(open, () => setOpen(false));
    useReturnFocusOnClose(open, toggleRef);

    // The tag slot holds the tag field, or the tag with its clear button.
    // Clearing the tag and picking one each replace the control that held
    // focus, which would fall to the page with the popover still open, where
    // a register shortcut acts on the rows under it. Focus goes on to the
    // control that takes its place (ADR-0023 §L).
    const tagFieldRef = useRef<HTMLInputElement>(null);
    const tagClearRef = useRef<HTMLButtonElement>(null);
    const tagHandsOff = useRef(false);
    useEffect(() => {
        if (!tagHandsOff.current) return;
        tagHandsOff.current = false;
        (filter.tag ? tagClearRef : tagFieldRef).current?.focus();
    }, [filter.tag]);

    // An edit that leaves the filter as it was hands up nothing. The register
    // re-reads its rows and clears its selection on a new filter object, and
    // a field can report a value the filter already has: the security search
    // reports "no security" on every keystroke until the text names one, and
    // picking the category already set reports that category again.
    const set = (patch: Partial<RegisterFilterArgs>) => {
        const next = { ...filter, ...patch };
        if (!sameRegisterFilter(next, filter)) onChange(next);
    };
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

    const fieldClass = 'h-control-28px rounded border border-border bg-surface px-2 text-xs';

    return (
        <div ref={rootRef} className="flex min-w-0 items-center gap-2">
            <input
                ref={searchRef}
                type="search"
                value={searchText}
                onChange={(e) => setSearchText(e.target.value)}
                placeholder="Search…"
                aria-label="Search transactions"
                // Where focus goes while the row list is not on the page and
                // there is no "+ New transaction" (useRegisterFocusHome).
                data-register-home="search"
                title={`Search payee, memo, check #, ${counterpartyLabel.toLowerCase()}, or tag`}
                className={`${fieldClass} w-fixed-224px`}
            />
            <div
                className="relative shrink-0"
                onKeyDown={(e) => { if (open) dismissOnEscape(e, () => setOpen(false)); }}
            >
                <button
                    ref={toggleRef}
                    type="button"
                    onClick={() => setOpen((v) => !v)}
                    aria-expanded={open}
                    aria-controls={popId}
                    className={
                        'inline-flex h-control-28px items-center gap-1 rounded border px-2 text-xs '
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
                        className="absolute right-0 z-30 mt-1 w-fixed-320px rounded border border-border bg-surface p-3 text-xs shadow-lg"
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
                                <MoneyInput currency={currency} value={filter.amountMin ?? ''}
                                    onValueChange={(text) => set({ amountMin: num(text) })}
                                    className={`${fieldClass} w-full`} />
                            </FilterField>
                            <FilterField label="Amount max">
                                <MoneyInput currency={currency} value={filter.amountMax ?? ''}
                                    onValueChange={(text) => set({ amountMax: num(text) })}
                                    className={`${fieldClass} w-full`} />
                            </FilterField>
                        </div>
                        <div className="mb-2">
                            <AccountCategoryPicker
                                accounts={accounts}
                                isEligible={(a) => (facingAccounts
                                    ? a.accountType !== 'category'
                                    : a.accountType === 'category')}
                                valueId={filter.categoryId ?? null}
                                onChangeId={(id) => set({ categoryId: id ?? undefined })}
                                label={counterpartyLabel}
                                placeholder={`Any ${counterpartyLabel.toLowerCase()}`}
                            />
                        </div>
                        <FilterField label="Tag">
                            {filter.tag ? (
                                <span className="inline-flex items-center gap-1 self-start rounded bg-surface-muted px-1.5 py-0.5 text-[0.6875rem] text-text">
                                    {filter.tag}
                                    <button
                                        ref={tagClearRef}
                                        type="button"
                                        onClick={() => {
                                            tagHandsOff.current = true;
                                            set({ tag: undefined });
                                        }}
                                        aria-label={`Clear tag filter ${filter.tag}`}
                                        className="rounded text-text-subtle hover:text-state-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                    >
                                        ×
                                    </button>
                                </span>
                            ) : (
                                <TagCombobox
                                    inputRef={tagFieldRef}
                                    tags={tags}
                                    allowCreate={false}
                                    onCommit={(name) => {
                                        tagHandsOff.current = true;
                                        set({ tag: name });
                                    }}
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
            <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                {label}
            </span>
            {children}
        </label>
    );
}
