import {
    useEffect,
    useMemo,
    useRef,
    useState,
    type KeyboardEvent,
} from 'react';
import type { AccountSummary, FrequentCounterpartiesResponse } from '@/lib/types';
import { buildAccountPathMap } from '@/lib/accountPath';
import { cn } from '@/lib/cn';
import {
    buildCategoryTreeRows,
    categoryPathMatches,
    categoryPathSegments,
} from './categoryPickerRows';

/**
 * Global account/category picker (ADR-0043). A combobox over the
 * ledger's accounts and categories with:
 *
 *  - every match shown (no row cap) inside a scrollable panel;
 *  - filter buttons [All · Accounts · Categories], with keyboard
 *    shortcuts Alt+1 / Alt+2 / Alt+3 (buttons only appear for the
 *    domains the caller's eligibility includes);
 *  - a pinned "Frequent" group (the source account's most-used
 *    counterparties, from the API), then accounts grouped by type,
 *    then categories as a ROOT-FIRST TREE (indented by depth) split
 *    into Income / Expense — the Categories-page mental model;
 *  - path-aware filtering for categories: `Bills/El` navigates to
 *    Bills › Electricity, a trailing slash (`Bills/`) lists a subtree,
 *    and a plain term still fuzzy-matches any path component;
 *  - copy/paste: opening pre-fills the selected item's full slash path
 *    and selects it (so it's copyable + first keystroke replaces), and
 *    typing/pasting a full path (`Bills/Electricity`) + Enter commits it;
 *  - id-based selection — picking commits the item's id, never a name
 *    string, so duplicate names resolve correctly.
 *
 * The caller supplies the FULL ledger account list (for parent-path
 * building) plus an `isEligible` predicate (its existing filter).
 */
export interface AccountCategoryPickerProps {
    /** Every account in the ledger — used for parent-path building.
     *  Eligibility is applied via {@link isEligible}. */
    accounts: readonly AccountSummary[];
    /** The caller's eligibility filter (which entries are pickable). */
    isEligible: (a: AccountSummary) => boolean;
    valueId: string | null;
    onChangeId: (next: string | null) => void;
    /** The source account's most-used counterparties, pinned to the
     *  top. Null/undefined while loading or unavailable. */
    frequent?: FrequentCounterpartiesResponse | null;
    label?: string;
    placeholder?: string;
    error?: string | null;
    disabled?: boolean;
    ariaLabel?: string;
}

type Domain = 'all' | 'accounts' | 'categories';

// Display order + label for account-type groups.
const ACCOUNT_TYPE_ORDER: ReadonlyArray<[type: string, label: string]> = [
    ['bank', 'Bank'],
    ['cash', 'Cash'],
    ['credit_card', 'Credit card'],
    ['investment', 'Investment'],
    ['asset', 'Asset'],
    ['liability', 'Liability'],
    ['loan', 'Loan'],
];

function accountTypeLabel(type: string): string {
    return ACCOUNT_TYPE_ORDER.find(([t]) => t === type)?.[1]
        ?? type.replace('_', ' ');
}

type Row =
    | { kind: 'header'; key: string; label: string }
    | {
          kind: 'item';
          key: string;
          account: AccountSummary;
          // A short right-aligned tag (account type, or kind · parent) for
          // FLAT rows — Frequent + accounts. Null on category tree rows, where
          // the indentation + Income/Expense header already convey context.
          qualifier: string | null;
          // Indent level for category tree rows (0 = root); 0 for flat rows.
          depth: number;
      };

export function AccountCategoryPicker({
    accounts,
    isEligible,
    valueId,
    onChangeId,
    frequent,
    label,
    placeholder = 'Account or category…',
    error,
    disabled,
    ariaLabel,
}: AccountCategoryPickerProps) {
    const byId = useMemo(() => {
        const m = new Map<string, AccountSummary>();
        for (const a of accounts) m.set(a.id, a);
        return m;
    }, [accounts]);

    // Full slash path per account (e.g. Food/Groceries) so the selected-value
    // display matches the register chips (ADR-0069). Categories show their
    // chain; real accounts have no parent, so this is just the name.
    const pathMap = useMemo(() => buildAccountPathMap(accounts), [accounts]);

    // Immediate parent name per account (for the flat-row qualifier /
    // duplicate-name disambiguation).
    const parentName = (a: AccountSummary): string | null =>
        a.parentId !== null ? (byId.get(a.parentId)?.name ?? null) : null;

    const eligible = useMemo(
        () => accounts.filter(isEligible),
        [accounts, isEligible],
    );

    // Full lowercased category path -> id, for "type/paste a path + Enter
    // commits it" (the copy/paste round-trip). Categories only; real accounts
    // resolve by name via the highlighted row.
    const pathToId = useMemo(() => {
        const m = new Map<string, string>();
        for (const a of eligible) {
            if (a.accountType === 'category') {
                m.set(categoryPathSegments(a, byId).join('/'), a.id);
            }
        }
        return m;
    }, [eligible, byId]);

    const hasAccounts = useMemo(
        () => eligible.some((a) => a.accountType !== 'category'),
        [eligible],
    );
    const hasCategories = useMemo(
        () => eligible.some((a) => a.accountType === 'category'),
        [eligible],
    );
    // Filter buttons only matter when BOTH domains are present.
    const showFilters = hasAccounts && hasCategories;

    const [open, setOpen] = useState(false);
    const [query, setQuery] = useState('');
    const [domain, setDomain] = useState<Domain>('all');
    const [highlight, setHighlight] = useState(0);
    const inputRef = useRef<HTMLInputElement | null>(null);
    const listRef = useRef<HTMLUListElement | null>(null);
    const rootRef = useRef<HTMLLabelElement | null>(null);

    // The text shown in the input: while closed, the selected item's full
    // path (Food/Groceries) so it matches the register chips; while open,
    // the live query (pre-filled to that path on open — see openPanel).
    const selected = valueId !== null ? byId.get(valueId) ?? null : null;
    const selectedPath = selected !== null
        ? (pathMap.get(selected.id) ?? selected.name)
        : '';
    const inputText = open ? query : selectedPath;

    const qualifier = (a: AccountSummary): string => {
        if (a.accountType === 'category') {
            const kind = a.categoryKind === 'income' ? 'Income'
                : a.categoryKind === 'expense' ? 'Expense' : 'Category';
            const parent = parentName(a);
            return parent !== null ? `${kind} · ${parent}` : kind;
        }
        return accountTypeLabel(a.accountType);
    };

    // Path-aware for categories (Bills/El, trailing slash, fuzzy component);
    // plain name substring for real accounts (no tree).
    const matches = (a: AccountSummary): boolean => {
        if (a.accountType === 'category') {
            return categoryPathMatches(categoryPathSegments(a, byId), query);
        }
        const q = query.trim().toLowerCase();
        if (q.length === 0) return true;
        return a.name.toLowerCase().includes(q);
    };

    // Build the row model (headers + items): Frequent (pinned, flat) →
    // account-type groups (flat) → category kind groups (root-first tree).
    const rows = useMemo<Row[]>(() => {
        const out: Row[] = [];
        const seenAccounts = new Set<string>();

        const wantAccounts = domain !== 'categories';
        const wantCategories = domain !== 'accounts';

        const eligibleById = new Map(eligible.map((a) => [a.id, a]));

        // --- Frequent (pinned, flat) ---
        const freqIds: string[] = [];
        if (frequent) {
            if (wantAccounts) freqIds.push(...frequent.accounts.map((f) => f.id));
            if (wantCategories) freqIds.push(...frequent.categories.map((f) => f.id));
        }
        const freqSeen = new Set<string>();
        const freqItems = freqIds
            .map((id) => eligibleById.get(id))
            .filter((a): a is AccountSummary => a !== undefined && matches(a));
        if (freqItems.length > 0) {
            out.push({ kind: 'header', key: 'h:frequent', label: 'Frequent' });
            for (const a of freqItems) {
                if (freqSeen.has(a.id)) continue;
                freqSeen.add(a.id);
                // Frequent ACCOUNTS are also skipped in their type group below
                // (a pinned account shouldn't repeat); categories still appear
                // in the tree so the hierarchy stays intact.
                if (a.accountType !== 'category') seenAccounts.add(a.id);
                out.push({ kind: 'item', key: `freq:${a.id}`, account: a, qualifier: qualifier(a), depth: 0 });
            }
        }

        // --- Accounts, grouped by type (flat) ---
        if (wantAccounts) {
            for (const [type, typeLabel] of ACCOUNT_TYPE_ORDER) {
                const group = eligible
                    .filter((a) => a.accountType === type && !seenAccounts.has(a.id) && matches(a))
                    .sort((x, y) => x.name.localeCompare(y.name));
                if (group.length === 0) continue;
                out.push({ kind: 'header', key: `h:acct:${type}`, label: typeLabel });
                for (const a of group) {
                    out.push({ kind: 'item', key: `acct:${a.id}`, account: a, qualifier: qualifier(a), depth: 0 });
                }
            }
        }

        // --- Categories, as a root-first tree per kind ---
        if (wantCategories) {
            const pushTree = (kindCats: AccountSummary[], key: string, label: string) => {
                const treeRows = buildCategoryTreeRows(kindCats, byId, query);
                if (treeRows.length === 0) return;
                out.push({ kind: 'header', key, label });
                for (const tr of treeRows) {
                    out.push({ kind: 'item', key: `cat:${tr.account.id}`, account: tr.account, qualifier: null, depth: tr.depth });
                }
            };
            const cats = eligible.filter((a) => a.accountType === 'category');
            pushTree(cats.filter((a) => a.categoryKind === 'income'), 'h:cat:income', 'Income');
            pushTree(cats.filter((a) => a.categoryKind === 'expense'), 'h:cat:expense', 'Expense');
            // Defensive: categories with no kind.
            pushTree(
                cats.filter((a) => a.categoryKind !== 'income' && a.categoryKind !== 'expense'),
                'h:cat:other', 'Categories',
            );
        }

        return out;
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [eligible, frequent, domain, query, byId]);

    const itemIndices = useMemo(
        () => rows.reduce<number[]>((acc, r, i) => {
            if (r.kind === 'item') acc.push(i);
            return acc;
        }, []),
        [rows],
    );

    // Keep highlight in range when the row set changes.
    useEffect(() => {
        setHighlight((h) => Math.min(h, Math.max(0, itemIndices.length - 1)));
    }, [itemIndices.length]);

    // Close on outside click.
    useEffect(() => {
        if (!open) return;
        const onDown = (e: MouseEvent) => {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
                setOpen(false);
            }
        };
        document.addEventListener('mousedown', onDown);
        return () => document.removeEventListener('mousedown', onDown);
    }, [open]);

    // On open, select the pre-filled path so it's copyable and the first
    // keystroke replaces it (edit-existing-value UX).
    useEffect(() => {
        if (open) inputRef.current?.select();
    }, [open]);

    function commit(a: AccountSummary) {
        onChangeId(a.id);
        setQuery('');
        setOpen(false);
    }

    function openPanel() {
        if (disabled) return;
        // Pre-fill the current selection's full path (copyable; path-editable).
        setQuery(selectedPath);
        setDomain('all');
        setHighlight(0);
        setOpen(true);
    }

    function setDomainSafe(next: Domain) {
        // Don't switch to a domain that has no entries.
        if (next === 'accounts' && !hasAccounts) return;
        if (next === 'categories' && !hasCategories) return;
        setDomain(next);
        setHighlight(0);
    }

    function commitHighlight() {
        const rowIdx = itemIndices[highlight];
        if (rowIdx === undefined) return;
        const row = rows[rowIdx];
        if (row?.kind === 'item') commit(row.account);
    }

    function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
        if (e.altKey && showFilters) {
            if (e.key === '1') { e.preventDefault(); setDomainSafe('all'); return; }
            if (e.key === '2') { e.preventDefault(); setDomainSafe('accounts'); return; }
            if (e.key === '3') { e.preventDefault(); setDomainSafe('categories'); return; }
        }
        if (e.key === 'ArrowDown') {
            e.preventDefault();
            if (!open) { openPanel(); return; }
            setHighlight((h) => Math.min(h + 1, itemIndices.length - 1));
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            setHighlight((h) => Math.max(h - 1, 0));
        } else if (e.key === 'Enter') {
            if (!open) return;
            e.preventDefault();
            // A typed/pasted EXACT full category path commits directly, even if
            // the highlight is elsewhere (the copy/paste round-trip).
            const exactId = pathToId.get(query.trim().toLowerCase());
            const exact = exactId !== undefined ? byId.get(exactId) : undefined;
            if (exact !== undefined) { commit(exact); return; }
            commitHighlight();
        } else if (e.key === 'Escape') {
            if (open) { e.preventDefault(); setOpen(false); }
        }
    }

    // Scroll the highlighted row into view.
    useEffect(() => {
        if (!open) return;
        const rowIdx = itemIndices[highlight];
        if (rowIdx === undefined) return;
        const el = listRef.current?.querySelector<HTMLElement>(`[data-row="${rowIdx}"]`);
        el?.scrollIntoView({ block: 'nearest' });
    }, [highlight, open, itemIndices]);

    return (
        <label
            ref={rootRef}
            className="relative flex min-w-0 flex-col gap-1 text-xs"
        >
            {label !== undefined ? (
                <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">
                    {label}
                </span>
            ) : null}
            <input
                ref={inputRef}
                type="text"
                role="combobox"
                aria-expanded={open}
                aria-label={ariaLabel ?? label ?? 'Account or category'}
                value={inputText}
                placeholder={placeholder}
                disabled={disabled}
                onFocus={openPanel}
                onChange={(e) => { if (!open) setOpen(true); setQuery(e.target.value); setHighlight(0); }}
                onKeyDown={onKeyDown}
                className={cn(
                    'w-full rounded border bg-surface px-2 py-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50',
                    error ? 'border-state-danger' : 'border-border',
                )}
            />
            {error ? (
                <span className="text-[0.6875rem] leading-tight text-state-danger">
                    {error}
                </span>
            ) : null}

            {open ? (
                <div className="absolute top-full left-0 z-50 mt-1 w-full min-w-[16rem] overflow-hidden rounded border border-border bg-surface shadow-lg">
                    {showFilters ? (
                        <div className="flex items-center gap-1 border-b border-border bg-surface-muted/40 px-2 py-1">
                            {([
                                ['all', 'All', '⌥1'],
                                ['accounts', 'Accounts', '⌥2'],
                                ['categories', 'Categories', '⌥3'],
                            ] as const).map(([d, lbl, hint]) => (
                                <button
                                    key={d}
                                    type="button"
                                    // Keep focus in the input so typing + keyboard
                                    // shortcuts keep working after a click.
                                    onMouseDown={(e) => { e.preventDefault(); setDomainSafe(d); }}
                                    className={cn(
                                        'rounded px-2 py-0.5 text-[0.6875rem] font-medium',
                                        domain === d
                                            ? 'bg-accent text-on-accent'
                                            : 'text-text-muted hover:bg-surface-hover',
                                    )}
                                >
                                    {lbl} <span className="opacity-60">{hint}</span>
                                </button>
                            ))}
                        </div>
                    ) : null}
                    <ul
                        ref={listRef}
                        role="listbox"
                        className="max-h-64 overflow-y-auto py-1"
                    >
                        {rows.length === 0 ? (
                            <li className="px-3 py-2 text-text-subtle">No matches</li>
                        ) : (
                            rows.map((row, i) =>
                                row.kind === 'header' ? (
                                    <li
                                        key={row.key}
                                        className="px-3 pt-2 pb-0.5 text-[0.5625rem] font-semibold uppercase tracking-wider text-text-subtle"
                                    >
                                        {row.label}
                                    </li>
                                ) : (
                                    <li
                                        key={row.key}
                                        data-row={i}
                                        role="option"
                                        aria-selected={itemIndices[highlight] === i}
                                        onMouseDown={(e) => { e.preventDefault(); commit(row.account); }}
                                        onMouseEnter={() => setHighlight(itemIndices.indexOf(i))}
                                        // Indent category tree rows by depth; flat rows (depth 0)
                                        // keep the base inset. Right padding stays via pr-3.
                                        style={{ paddingLeft: `${0.75 + row.depth * 0.85}rem` }}
                                        className={cn(
                                            'flex cursor-pointer items-center justify-between gap-3 py-1 pr-3',
                                            itemIndices[highlight] === i ? 'bg-accent-soft/40' : 'hover:bg-surface-hover',
                                        )}
                                    >
                                        <span className="min-w-0 truncate">{row.account.name}</span>
                                        {row.qualifier !== null ? (
                                            <span className="shrink-0 text-[0.625rem] text-text-muted">
                                                {row.qualifier}
                                            </span>
                                        ) : null}
                                    </li>
                                ),
                            )
                        )}
                    </ul>
                </div>
            ) : null}
        </label>
    );
}
