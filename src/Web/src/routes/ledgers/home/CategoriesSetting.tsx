import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, type RefObject } from 'react';
import { X } from 'lucide-react';

import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { IconButton } from '@/components/ui/IconButton';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import { buildAccountPathMap } from '@/lib/accountPath';
import { categoryDotClass, systemCategoryIds } from '@/lib/categoryChip';
import { cn } from '@/lib/cn';
import type { AccountSummary } from '@/lib/types';

import { useHome } from './homeContext';
import { useRailAccounts } from './queries';

/**
 * A widget setting that is a list of the ledger's own categories (the Watchlist's):
 * the chosen ones in order, each with a remove button, and the register's picker
 * to add one more, up to `max`. The picker offers active expense categories not
 * already chosen; the list is what the widget watches, so an id that names no
 * category any more is dropped from it here, the one place it is edited.
 */
export function CategoriesSetting({ label, max, value, onChange, panel }: {
    label: string;
    max: number;
    value: unknown;
    onChange: (ids: string[]) => void;
    /** The settings panel the list is in, which takes focus when the last
     *  chosen category is removed. */
    panel: RefObject<HTMLElement | null>;
}) {
    const home = useHome();
    const accounts = useRailAccounts(home.ledgerId);
    const all = useMemo(() => accounts.data ?? [], [accounts.data]);
    const paths = useMemo(() => buildAccountPathMap(all), [all]);
    const byId = useMemo(() => new Map(all.map((a) => [a.id, a])), [all]);
    // The ledger's Uncategorized, whose dot is always neutral.
    const systemIds = useMemo(() => systemCategoryIds(all), [all]);

    const stored = useMemo(() => (Array.isArray(value) ? (value as string[]) : []), [value]);
    const chosen = useMemo(
        () => (accounts.data ? stored.filter((id) => byId.get(id)?.accountType === 'category') : stored),
        [accounts.data, stored, byId],
    );
    // Once the accounts are known, an id naming no category leaves the list, so
    // the count, the cap and Apply all speak of what the widget can show.
    useEffect(() => {
        if (chosen.length !== stored.length) onChange(chosen);
    }, [chosen, stored, onChange]);

    const isEligible = useCallback(
        (a: AccountSummary) => a.accountType === 'category' && a.categoryKind === 'expense' && a.isActive && !chosen.includes(a.id),
        [chosen],
    );
    const full = chosen.length >= max;

    // A Remove takes its row away: focus goes to the next row's Remove, the
    // previous row's, or, with none left, the settings panel. Not the picker,
    // the list's add control, whose list opens on focus; the panel is where a
    // closing panel's focus goes for the same reason (ADR-0023 §L).
    const removeControl = useRowRemovalFocus(chosen, panel);
    // The add that fills the list disables the picker, which holds focus, and a
    // browser drops focus from a disabled control: focus goes to the new row's
    // Remove instead, as a create that brings a list to its cap gives it to the
    // new row (ADR-0023 §L).
    const listRef = useRef<HTMLUListElement | null>(null);
    const fillingAdd = useRef<string | null>(null);
    useLayoutEffect(() => {
        const id = fillingAdd.current;
        if (id === null || !chosen.includes(id)) return;
        fillingAdd.current = null;
        // Focus lost: on the page, or left on the control that was disabled.
        const active = document.activeElement as HTMLInputElement | null;
        if (active !== null && active !== document.body && !active.disabled) return;
        listRef.current?.querySelector<HTMLElement>(`[data-remove-id="${id}"]`)?.focus();
    });

    return (
        <fieldset className="flex min-w-0 flex-col gap-1.5">
            <legend className="mb-1 text-xs font-medium text-text-muted">{label}</legend>
            {accounts.data && chosen.length > 0 ? (
                <ul ref={listRef} aria-label="Chosen categories" className="divide-y divide-border/60 rounded border border-border bg-surface">
                    {chosen.map((id) => {
                        const category = byId.get(id)!;
                        const path = paths.get(id) ?? category.name;
                        return (
                            <li key={id} className="flex items-center justify-between gap-2 py-px pl-2.5 pr-px text-sm">
                                <span className="flex min-w-0 items-center gap-1.5" title={path}>
                                    <span aria-hidden className={cn('size-dot-sm shrink-0 rounded-full', categoryDotClass(category.name, id, systemIds.has(id)))} />
                                    <span className="truncate">{path}</span>
                                </span>
                                <IconButton
                                    ref={removeControl(id)}
                                    data-remove-id={id}
                                    aria-label={`Remove ${path}`}
                                    onClick={() => onChange(chosen.filter((c) => c !== id))}
                                >
                                    <X className="size-icon-sm" aria-hidden />
                                </IconButton>
                            </li>
                        );
                    })}
                </ul>
            ) : null}
            <span className="flex items-center gap-2">
                <span className="min-w-0 flex-1">
                    <AccountCategoryPicker
                        accounts={all}
                        isEligible={isEligible}
                        valueId={null}
                        onChangeId={(id) => {
                            if (id === null || full) return;
                            if (chosen.length + 1 >= max) fillingAdd.current = id;
                            onChange([...chosen, id]);
                        }}
                        placeholder="Add a category…"
                        ariaLabel="Add a category"
                        disabled={!accounts.data || full}
                        compact
                    />
                </span>
                <span className="shrink-0 text-xs text-text-muted">{chosen.length} of {max}</span>
            </span>
        </fieldset>
    );
}
