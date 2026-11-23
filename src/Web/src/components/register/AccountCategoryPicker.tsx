import {
    useEffect,
    useMemo,
    useRef,
    useState,
    type FocusEvent,
    type KeyboardEvent,
    type MouseEvent as ReactMouseEvent,
} from 'react';
import type { AccountSummary, FrequentCounterpartiesResponse } from '@/lib/types';
import { buildAccountPathMap } from '@/lib/accountPath';
import { cn } from '@/lib/cn';
import { SegmentedControl, type SegmentedOption } from '@/components/ui/SegmentedControl';
import { useFieldSizeClass } from '@/components/ui/fieldSize';
import { pendingTextProps, refuseKey } from '@/components/ui/pending';
import { happenedInside } from '@/components/ui/popoverDismissal';
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
 *    into Income / Expense — the Categories tab's mental model;
 *  - path-aware filtering for categories: `Bills/El` navigates to
 *    Bills › Electricity, a trailing slash (`Bills/`) lists a subtree,
 *    and a plain term still fuzzy-matches any path component;
 *  - copy/paste: opening pre-fills the selected item's full slash path
 *    and selects it (so it's copyable + first keystroke replaces), and
 *    typing/pasting a full path (`Bills/Electricity`) + Enter commits it;
 *    a current value the picker does not offer (one `isEligible` refuses:
 *    the system Uncategorized category, an inactive one) is pre-filled the
 *    same way but does not narrow the list until the user types;
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
    /** Mark the control invalid WITHOUT the message `error` renders below it.
     *  The splits grid needs that: the defect is already reported on the row
     *  and in the summary strip, and a message under the field would push the
     *  row taller than the grid's fixed height budget. */
    invalid?: boolean;
    disabled?: boolean;
    /** The request this field belongs to is in flight: it keeps focus, is
     *  read-only, and neither opens its list nor commits a pick (pending.ts). */
    pending?: boolean;
    ariaLabel?: string;
    /** DOM id for the combobox input, so a caller can send focus to it. */
    id?: string;
    /**
     * Cap the results list shorter, for a container where a tall panel reads as
     * overwhelming rather than helpful.
     *
     * Only a preference, not a fit. A position:absolute panel would be clipped
     * by an ancestor with `overflow-y: auto` and need `compact` to FIT the
     * splits grid's 344px leg viewport — and an absolutely positioned child
     * counts toward its scroller's content, so opening the panel would grow the
     * grid's scroll range, leave dead space under "Add split", and need the row
     * yanked to the top of the viewport first to make room at all.
     *
     * So the panel is fixed to the viewport and closes on scroll, which keeps
     * the guarantee (it cannot outlive the view it was opened from) without
     * those side effects. The list is sized to the room actually below the
     * control, so `compact` is only a preference for a shorter list.
     */
    compact?: boolean;
}

type Domain = 'all' | 'accounts' | 'categories';

const DOMAIN_OPTIONS: readonly SegmentedOption<Domain>[] = [
    { value: 'all', label: 'All', hint: '⌥1' },
    { value: 'accounts', label: 'Accounts', hint: '⌥2' },
    { value: 'categories', label: 'Categories', hint: '⌥3' },
];

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

/** Gap between the control and its panel. */
const PANEL_GAP = 4;

/** Keep the panel off the window's edges. */
const PANEL_EDGE = 8;

/** The panel is at least this wide, however narrow the control is. */
const PANEL_MIN_WIDTH = 256;

/** Filter bar, borders and padding — everything that is not the list. */
const PANEL_CHROME = 48;

const LIST_FULL = 256;
const LIST_COMPACT = 160;

/** The floor: below this a list is not worth rendering, so the panel may
 *  overhang rather than shrink further. */
const LIST_MIN = 84;

export function AccountCategoryPicker({
    accounts,
    isEligible,
    valueId,
    onChangeId,
    frequent,
    label,
    placeholder = 'Account or category…',
    error,
    invalid,
    id,
    compact,
    disabled,
    pending,
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
    const sizeClass = useFieldSizeClass();

    const [open, setOpen] = useState(false);

    const [query, setQuery] = useState('');
    // Whether the text in the input narrows the list. False from an open on a
    // value this picker does not offer until the user types (see openPanel).
    const [queryNarrows, setQueryNarrows] = useState(true);
    const [domain, setDomain] = useState<Domain>('all');
    const [highlight, setHighlight] = useState(0);
    const inputRef = useRef<HTMLInputElement | null>(null);
    const listRef = useRef<HTMLUListElement | null>(null);
    const rootRef = useRef<HTMLLabelElement | null>(null);
    // Set while this control hands focus back to its own input, so that focus
    // does not reopen the list an Escape has just closed (see onEscape).
    const returningFocus = useRef(false);
    const [place, setPlace] = useState<
        { top: number; left: number; width: number; list: number } | null
    >(null);

    // The text shown in the input: while closed, the selected item's full
    // path (Food/Groceries) so it matches the register chips; while open,
    // the live query (pre-filled to that path on open — see openPanel).
    const selected = valueId !== null ? byId.get(valueId) ?? null : null;
    const selectedPath = selected !== null
        ? (pathMap.get(selected.id) ?? selected.name)
        : '';
    const inputText = open ? query : selectedPath;
    // A current value can be one the picker never offers for a fresh pick:
    // the system Uncategorized category the bank feed stamps on new rows, an
    // inactive category or account. It is shown, from the full accounts map,
    // but listed nowhere, so its path narrows the list to nothing.
    const selectedOffered = selected === null || isEligible(selected);
    // The text the list is narrowed by: the input's, once it narrows.
    const filterText = queryNarrows ? query : '';

    // KIND ONLY for categories. `Expense · ImmediateParent` would be the one-hop
    // half of an answer: it disambiguates two "Food" under different parents
    // only when the tree is two deep, and says nothing at three. The full chain
    // lives in the label (see the row render), so
    // repeating the parent here would print it twice and spend the width that
    // the path needs.
    //
    // The kind stays, and is the point of the qualifier now: ADR-0017 makes kind
    // DISCLOSED rather than enforced — pickers no longer filter by it — so it has
    // to be legible at the moment of choosing.
    const qualifier = (a: AccountSummary): string => {
        if (a.accountType === 'category') {
            return a.categoryKind === 'income' ? 'Income'
                : a.categoryKind === 'expense' ? 'Expense'
                : a.categoryKind === 'adjustment' ? 'Adjustment'
                : 'Category';
        }
        return accountTypeLabel(a.accountType);
    };

    // Path-aware for categories (Bills/El, trailing slash, fuzzy component);
    // plain name substring for real accounts (no tree).
    const matches = (a: AccountSummary): boolean => {
        if (a.accountType === 'category') {
            return categoryPathMatches(categoryPathSegments(a, byId), filterText);
        }
        const q = filterText.trim().toLowerCase();
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
                const treeRows = buildCategoryTreeRows(kindCats, byId, filterText);
                if (treeRows.length === 0) return;
                out.push({ kind: 'header', key, label });
                for (const tr of treeRows) {
                    out.push({ kind: 'item', key: `cat:${tr.account.id}`, account: tr.account, qualifier: null, depth: tr.depth });
                }
            };
            const cats = eligible.filter((a) => a.accountType === 'category');
            pushTree(cats.filter((a) => a.categoryKind === 'income'), 'h:cat:income', 'Income');
            pushTree(cats.filter((a) => a.categoryKind === 'expense'), 'h:cat:expense', 'Expense');
            // Migration 224's third kind, named, rather than left to the
            // catch-all below: there every adjustment category would sit under
            // a heading that names the whole picker rather than the group,
            // inside a list already made entirely of categories.
            pushTree(
                cats.filter((a) => a.categoryKind === 'adjustment'),
                'h:cat:adjustment', 'Adjustment',
            );
            // Defensive, and unreachable for valid data:
            // `accounts_category_kind_consistent` makes the kind non-null for
            // every category. Kept for a FOURTH kind arriving before this line
            // does — labelled "Other", because whatever lands here is by
            // definition something this build has no name for.
            pushTree(
                cats.filter((a) => a.categoryKind !== 'income'
                    && a.categoryKind !== 'expense'
                    && a.categoryKind !== 'adjustment'),
                'h:cat:other', 'Other',
            );
        }

        return out;
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [eligible, frequent, domain, filterText, byId]);

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

    // Fixed to the viewport, placed once on open, closed on any scroll — the
    // same placement TagSelect uses, so the two controls in a leg row behave
    // alike. See the `compact` note for why this stopped being absolute.
    useEffect(() => {
        if (!open) { setPlace(null); return; }
        const el = rootRef.current;
        if (el === null) return;

        const rect = el.getBoundingClientRect();
        // ALWAYS DOWN. Choosing a side by available room makes two adjacent
        // rows in one grid open opposite ways, which reads as arbitrary.
        const room = window.innerHeight - rect.bottom - PANEL_GAP - PANEL_EDGE;
        const cap = compact ? LIST_COMPACT : LIST_FULL;

        setPlace({
            top: rect.bottom + PANEL_GAP,
            left: Math.max(PANEL_EDGE, Math.min(rect.left, window.innerWidth - PANEL_MIN_WIDTH - PANEL_EDGE)),
            width: Math.max(rect.width, PANEL_MIN_WIDTH),
            list: Math.floor(Math.max(LIST_MIN, Math.min(cap, room - PANEL_CHROME))),
        });

        // A frame late: focusing the input can itself scroll an ancestor, and
        // listening immediately would close the panel on the scroll that
        // opening it caused.
        let dispose = () => {};
        const frame = requestAnimationFrame(() => {
            const close = () => setOpen(false);
            // A scroll INSIDE the panel is the user reading the list, not the
            // view moving out from under it. Dragging the list's own scrollbar
            // fires a scroll event that reaches this capture listener, and
            // closing on it made the panel vanish mid-drag. Only a scroll that
            // did NOT come from this control counts as the view moving.
            const closeOnScroll = (e: Event) => {
                if (happenedInside(e, rootRef.current)) return;
                setOpen(false);
            };
            // Capture — the scroll that matters is on an ancestor, and scroll
            // events do not bubble.
            window.addEventListener('scroll', closeOnScroll, true);
            window.addEventListener('resize', close);
            dispose = () => {
                window.removeEventListener('scroll', closeOnScroll, true);
                window.removeEventListener('resize', close);
            };
        });
        return () => { cancelAnimationFrame(frame); dispose(); };
    }, [open, compact]);

    // Close on a press outside. A pick commits as the mouse goes down and takes
    // the option pressed off the page, and that press is still inside.
    useEffect(() => {
        if (!open) return;
        const onDown = (e: MouseEvent) => {
            if (rootRef.current && !happenedInside(e, rootRef.current)) {
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
        if (pending) return;
        onChangeId(a.id);
        setQuery('');
        setOpen(false);
    }

    function openPanel() {
        if (disabled || pending) return;
        // Pre-fill the current selection's full path (copyable; path-editable).
        setQuery(selectedPath);
        // A value the picker offers narrows the list to its own row and its
        // ancestors. One it does not offer would narrow it to nothing, so the
        // full list shows until the user types, with no row highlighted: the
        // value is in none of them, and Enter must not swap it for the first.
        setQueryNarrows(selectedOffered);
        setDomain('all');
        setHighlight(selectedOffered ? 0 : -1);
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
        // Pending: no key opens the list, moves its highlight or commits.
        if (pending) { refuseKey(e); return; }
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
        }
    }

    // Focus arriving with the list already open is coming back from inside the
    // control (a filter button) or from nowhere (the window), not a fresh visit
    // to the field, so the search and the filter chosen so far stay as they are.
    function onFocusInput() {
        if (returningFocus.current || open) return;
        openPanel();
    }

    // Escape with the list open closes the list and nothing else, the rule
    // every list in the editors follows (ADR-0023 §F): marked handled with
    // preventDefault and stopped, so the edit or the dialog stays open and
    // nothing further out acts on it; anything there that acts on Escape
    // ignores a marked one besides. On the root rather than the input, so it
    // holds wherever in the control focus is: Tab reaches the filter buttons
    // before the next field, and an Escape there would otherwise reach the
    // editor with the list still on screen. Those buttons go away with the
    // list, so focus comes back to the input, or it would fall to the page and
    // the next Escape would land outside the editor instead of cancelling it.
    const onEscape = (e: KeyboardEvent<HTMLLabelElement>) => {
        if (e.key !== 'Escape' || !open) return;
        e.preventDefault();
        e.stopPropagation();
        setOpen(false);
        returningFocus.current = true;
        try {
            inputRef.current?.focus();
        } finally {
            returningFocus.current = false;
        }
    };

    // Scroll the highlighted row into view.
    useEffect(() => {
        if (!open) return;
        const rowIdx = itemIndices[highlight];
        if (rowIdx === undefined) return;
        const el = listRef.current?.querySelector<HTMLElement>(`[data-row="${rowIdx}"]`);
        el?.scrollIntoView({ block: 'nearest' });
    }, [highlight, open, itemIndices]);

    // Focus moving to another field closes the list, as it does the tags list
    // (TagSelect). The list opens on focus; left open after a Tab into the next
    // field, it would still be on screen, and the Escape meant to close it would
    // land in that field, reach the editor unmarked and cancel the edit, or
    // close the dialog.
    // Only a move to a NAMED element counts. A press inside the control never
    // moves focus (keepFocus), and focus going nowhere, such as the window
    // losing it, is not focus leaving for another field. A click anywhere
    // outside is the mousedown listener's.
    const onFocusLeave = (e: FocusEvent<HTMLLabelElement>) => {
        const next = e.relatedTarget;
        if (!open || next === null) return;
        if (rootRef.current?.contains(next) === true) return;
        setOpen(false);
    };

    // A press anywhere in the control but its input keeps focus where it is, as
    // a press on an option or a filter button always did. A press on a part
    // that cannot take focus (a group heading, the padding between rows, the
    // filter bar, the caption) would otherwise send focus to the nearest
    // ancestor that can. In a dialog that is the dialog's panel, outside the
    // control, so the list would close on the press and the next Escape would
    // close the dialog. The click still lands, and the label it is inside
    // focuses the input as before.
    const keepFocus = (e: ReactMouseEvent<HTMLLabelElement>) => {
        if (e.target !== inputRef.current) e.preventDefault();
    };

    return (
        <label
            ref={rootRef}
            onKeyDown={onEscape}
            onBlur={onFocusLeave}
            onMouseDown={keepFocus}
            className="relative flex min-w-0 flex-col gap-1 text-xs"
        >
            {label !== undefined ? (
                <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                    {label}
                </span>
            ) : null}
            <input
                id={id}
                ref={inputRef}
                type="text"
                role="combobox"
                aria-expanded={open}
                aria-invalid={error != null || invalid === true ? true : undefined}
                aria-label={ariaLabel ?? label ?? 'Account or category'}
                value={inputText}
                placeholder={placeholder}
                disabled={disabled}
                onFocus={onFocusInput}
                onChange={(e) => {
                    if (!open) setOpen(true);
                    setQuery(e.target.value);
                    setQueryNarrows(true);
                    setHighlight(0);
                }}
                onKeyDown={onKeyDown}
                className={cn(
                    // An explicit height, not py-1: padding made this ~26px, which
                    // is near enough the editor's 28px to look like a mistake rather
                    // than a choice.
                    'h-control-28px w-full rounded border bg-surface px-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50',
                    'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                    error || invalid ? 'border-state-danger' : 'border-border',
                    sizeClass,
                )}
                {...pendingTextProps<HTMLInputElement>(pending)}
            />
            {error ? (
                <span className="text-[0.6875rem] leading-tight text-state-danger">
                    {error}
                </span>
            ) : null}

            {open && place !== null ? (
                <div
                    style={{ top: place.top, left: place.left, width: place.width }}
                    className="fixed z-50 overflow-hidden rounded border border-border bg-surface shadow-lg"
                >
                    {showFilters ? (
                        <div className="flex items-center border-b border-border bg-surface-muted/40 px-2 py-1">
                            {/* keepFocus: typing and the ⌥ shortcuts keep working
                                after a click, because the input never loses focus. */}
                            <SegmentedControl
                                label="Show"
                                options={DOMAIN_OPTIONS}
                                value={domain}
                                onChange={setDomainSafe}
                                keepFocus
                            />
                        </div>
                    ) : null}
                    <ul
                        ref={listRef}
                        role="listbox"
                        // Inline because it is measured, not a token: how much
                        // room there is depends on where this control sits.
                        style={{ maxHeight: place.list }}
                        className="overflow-y-auto py-1"
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
                                        <PickerLabel
                                            account={row.account}
                                            depth={row.depth}
                                            path={pathMap.get(row.account.id) ?? row.account.name}
                                        />
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

/**
 * A picker row's name, showing its ancestry only when the row's own position
 * does not already convey it.
 *
 * WHEN THE PATH APPEARS. Exactly when the rendered depth HIDES the real
 * ancestry: `depth === 0 && parentId !== null`. That is one condition covering
 * two separate ways a nested category ends up looking like a root — the pinned
 * "Frequent" group, which is deliberately flat, and a category whose parent fell
 * out of the caller's eligible set (an ACTIVE child under an INACTIVE parent;
 * deactivation does not cascade) and was re-parented to the forest root by
 * `categoryPickerRows`. Both render two same-named children of different parents
 * as two identical unindented rows, which is the exact ambiguity the rule exists
 * to prevent.
 *
 * Genuine tree rows keep the bare leaf. Indentation is already the answer there,
 * and a self-path on every row would be noise.
 *
 * WHICH END TRUNCATES. The ancestors, never the leaf. The row is narrow — inside
 * the splits grid it is one column — and truncating from the right would hide
 * the very word identifying the category, which is worse than showing no path at
 * all. Ancestors take the flexible track and clip; the leaf is `shrink-0`.
 */
function PickerLabel({
    account,
    depth,
    path,
}: {
    account: AccountSummary;
    depth: number;
    path: string;
}) {
    const ancestryHidden =
        account.accountType === 'category' && depth === 0 && account.parentId !== null;

    if (!ancestryHidden) {
        return <span className="min-w-0 truncate" title={path}>{account.name}</span>;
    }

    const cut = path.lastIndexOf('/');
    const ancestors = cut >= 0 ? path.slice(0, cut + 1) : '';
    const leaf = cut >= 0 ? path.slice(cut + 1) : path;

    return (
        <span className="flex min-w-0 items-baseline" title={path}>
            <span className="min-w-0 truncate text-text-muted">{ancestors}</span>
            <span className="shrink-0">{leaf}</span>
        </span>
    );
}
