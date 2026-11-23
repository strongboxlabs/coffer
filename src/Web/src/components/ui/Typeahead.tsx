import {
    forwardRef,
    Fragment,
    useCallback,
    useEffect,
    useId,
    useMemo,
    useRef,
    useState,
    type FocusEvent,
    type KeyboardEvent,
} from 'react';

import { cn } from '@/lib/cn';
import { useFieldSizeClass } from './fieldSize';
import { pendingTextProps } from './pending';
import { happenedInside } from './popoverDismissal';

// Typeahead — controlled input with a filtered-suggestion popover.
//
// Contract (this is the file's reason-for-being):
//
//   * Selection is a pure `onChange(label)` — exactly the same as
//     typing the label by hand. The parent owns the value; the
//     popover is just a faster way to put text in.
//   * There is no separate `onCommit`/`onCancel` callback pair. The
//     enclosing form catches Enter/Escape/Tab at its own level and
//     reads from its state to save/cancel. This eliminates the
//     React-setState-batching race where the parent's commit handler
//     would read a stale `value` because `onChange(newValue)` and
//     `onCommit()` were called in the same callback.
//   * Keyboard:
//       ArrowDown / ArrowUp  — open the popover, move the highlight
//       Enter (with highlight) — apply highlight via onChange; close popover; prevent default so the parent form catches it
//       Enter (no highlight)   — pass through to the parent form (no onChange)
//       Tab (with highlight)   — apply highlight via onChange; close popover; DO NOT preventDefault so focus advances
//       Escape (list showing)  — close the list and stop there: preventDefault + stopPropagation, so neither the
//                                parent form's cancel nor a dialog around it acts. Focus and the typed text stay.
//       Escape (no list)       — pass through to the parent form, whose Escape cancels the edit
//     Every list in the transaction editors follows that Escape rule
//     (ADR-0023 §F): the first Escape closes the list, the next one
//     cancels.
//   * Click on an item        — onChange + close popover (same as Enter with highlight)
//   * Click outside           — close popover, no onChange (the parent keeps whatever's typed)
//
// The filter is a case-insensitive substring against
// `getSearchableText` (defaults to `getLabel`). For compound paths
// like accounts the parent passes the slash-joined path so
// "Food/Groc" matches both parent chain and leaf.

export interface TypeaheadProps<T> {
    /** Suggestion pool. Pre-fetch + cache at the parent; the
     * primitive filters client-side. */
    items: readonly T[];
    /** Current input text. Fully controlled — every keystroke and
     * every suggestion-pick lands here through {@link onChange}. */
    value: string;
    /** Fired with the new value on every keystroke AND on suggestion
     * pick. The parent's render uses this directly. */
    onChange: (value: string) => void;
    /** Stable identity for React reconciliation. */
    getKey: (item: T) => string;
    /** Display text in the dropdown row + the value committed to
     * `onChange` when the user picks this item. */
    getLabel: (item: T) => string;
    /** Optional substring-search text. Defaults to `getLabel`. Use
     * this when the visible label differs from the searchable text
     * (e.g., the account picker matches the full slash-joined path,
     * not just the leaf name). */
    getSearchableText?: (item: T) => string;
    /** Max rows in the suggestion dropdown. Defaults to 8. */
    maxRows?: number;
    /** Render a custom row body — defaults to the label. The wrapping
     * `<li>` (role + keyboard handlers + highlight state) is owned by
     * the primitive. */
    renderItem?: (item: T, query: string) => React.ReactNode;
    placeholder?: string;
    disabled?: boolean;
    /** The request this field belongs to is in flight: it keeps focus, is
     *  read-only, and shows no suggestions to pick (pending.ts). */
    pending?: boolean;
    autoFocus?: boolean;
    className?: string;
    /** Forwarded to the underlying input. */
    'aria-label'?: string;
    /** Forwarded to the input. Use for form-level submit/cancel
     * keyboard handling; the Typeahead's own keyboard logic runs
     * first, and a key it used (Enter on a highlight, Escape that
     * closed the list) never reaches this. */
    onKeyDown?: (event: KeyboardEvent<HTMLInputElement>) => void;
    /** Forwarded to the input. The popover closes on blur
     * regardless. */
    onBlur?: (event: FocusEvent<HTMLInputElement>) => void;
    /** Optional: items where `prioritize(item)` returns true are
     * sorted to the top of the dropdown within the filtered set,
     * with a divider after. Original list order is preserved
     * within each group. Use when a small, contextually relevant
     * subset should be prominent (e.g. the security picker
     * surfaces the account's current holdings before the rest of
     * the ledger). */
    prioritize?: (item: T) => boolean;
    /** Optional: render a creation row at the bottom of the
     * dropdown. When set + the user's query is non-empty, an
     * extra row appears reading `creationOption.label(query)`.
     * Picking it (click or keyboard Enter when highlighted) fires
     * `creationOption.onSelect(query)`. It does NOT call
     * `onChange` — the parent owns whatever happens next (typically
     * opening a create dialog and selecting the new item itself). */
    creationOption?: {
        label: (query: string) => string;
        onSelect: (query: string) => void;
        /** Optional gate. Defaults to "show whenever the trimmed
         * query is non-empty." Override to hide the row when an
         * exact match exists. */
        show?: (query: string, hasMatches: boolean) => boolean;
    };
}

const DEFAULT_MAX_ROWS = 8;

function filterItems<T>(
    items: readonly T[],
    query: string,
    getSearchableText: (item: T) => string,
    limit: number,
): T[] {
    const trimmed = query.trim();
    if (trimmed.length === 0) return items.slice(0, limit);
    const needle = trimmed.toLowerCase();
    const matches: T[] = [];
    for (const item of items) {
        if (getSearchableText(item).toLowerCase().includes(needle)) {
            matches.push(item);
            if (matches.length >= limit) break;
        }
    }
    return matches;
}

/** Substring match without a row limit — caller is responsible for
 *  slicing. Used by the prioritized path so the partition can run
 *  over the whole match set before the limit is applied. */
function filterItemsAll<T>(
    items: readonly T[],
    query: string,
    getSearchableText: (item: T) => string,
): readonly T[] {
    const trimmed = query.trim();
    if (trimmed.length === 0) return items;
    const needle = trimmed.toLowerCase();
    const matches: T[] = [];
    for (const item of items) {
        if (getSearchableText(item).toLowerCase().includes(needle)) {
            matches.push(item);
        }
    }
    return matches;
}

function TypeaheadInner<T>(
    {
        items,
        value,
        onChange,
        getKey,
        getLabel,
        getSearchableText,
        maxRows = DEFAULT_MAX_ROWS,
        renderItem,
        placeholder,
        disabled,
        pending,
        autoFocus,
        className,
        'aria-label': ariaLabel,
        onKeyDown,
        onBlur,
        prioritize,
        creationOption,
    }: TypeaheadProps<T>,
    ref: React.ForwardedRef<HTMLInputElement>,
) {
    const inputId = useId();
    const listboxId = `${inputId}-listbox`;
    const sizeClass = useFieldSizeClass();

    const containerRef = useRef<HTMLDivElement | null>(null);
    const inputRef = useRef<HTMLInputElement | null>(null);

    const [isOpen, setIsOpen] = useState(false);
    const [highlightIndex, setHighlightIndex] = useState(-1);

    const searchableText = getSearchableText ?? getLabel;
    const filtered = useMemo(() => {
        // Without prioritize, the old filter+limit path is fine.
        if (!prioritize) {
            return filterItems(items, value, searchableText, maxRows);
        }
        // With prioritize, the limit must apply AFTER the partition
        // — otherwise the limit slices off the first N input items
        // (typically alphabetical) before priority items get a
        // chance to bubble up, and the holdings-first feature
        // silently degrades to alphabetical-first whenever the user
        // doesn't type. Match over the full list, then partition,
        // then slice.
        const matchedAll = filterItemsAll(items, value, searchableText);
        const priority: T[] = [];
        const rest: T[] = [];
        for (const item of matchedAll) {
            (prioritize(item) ? priority : rest).push(item);
        }
        return [...priority, ...rest].slice(0, maxRows);
    }, [items, value, searchableText, maxRows, prioritize]);

    // Index of the first non-priority item — used to render a
    // divider above it. -1 when no divider is needed (all items
    // are priority, or none are).
    const dividerIndex = useMemo(() => {
        if (!prioritize) return -1;
        const firstNonPriority = filtered.findIndex((item) => !prioritize(item));
        // No priority items, or all items are priority → no divider.
        if (firstNonPriority === -1 || firstNonPriority === 0) return -1;
        return firstNonPriority;
    }, [filtered, prioritize]);

    const trimmedQuery = value.trim();
    const showCreationRow =
        creationOption !== undefined
        && trimmedQuery.length > 0
        && (creationOption.show?.(trimmedQuery, filtered.length > 0) ?? true);
    // Index of the creation row in the keyboard-nav sequence
    // (positions after all filtered items). -1 when not shown.
    const creationRowIndex = showCreationRow ? filtered.length : -1;
    const totalSelectable = filtered.length + (showCreationRow ? 1 : 0);
    // What the user sees. `isOpen` alone is not it: the field is "open" from
    // the moment it takes focus, and with nothing matching there is no list.
    // A pending field offers nothing to pick.
    const listShown = isOpen && totalSelectable > 0 && pending !== true;

    // Reset highlight when the filtered set shifts under us.
    // First filtered item wins; if there are none but a creation
    // row is visible, highlight that so Enter still does something.
    useEffect(() => {
        if (filtered.length > 0) {
            setHighlightIndex(0);
        } else if (showCreationRow) {
            setHighlightIndex(0);
        } else {
            setHighlightIndex(-1);
        }
    }, [filtered, showCreationRow]);

    const setInputRef = useCallback(
        (node: HTMLInputElement | null) => {
            inputRef.current = node;
            if (typeof ref === 'function') ref(node);
            else if (ref) ref.current = node;
        },
        [ref],
    );

    // Click-outside closes the popover but does NOT trigger an
    // onChange — whatever is typed stays in the parent's state for
    // form-level Save/Cancel to act on.
    useEffect(() => {
        if (!isOpen) return;
        const onPointerDown = (event: PointerEvent) => {
            if (happenedInside(event, containerRef.current)) return;
            setIsOpen(false);
        };
        document.addEventListener('pointerdown', onPointerDown, true);
        return () => {
            document.removeEventListener('pointerdown', onPointerDown, true);
        };
    }, [isOpen]);

    function pickItem(item: T) {
        onChange(getLabel(item));
        setIsOpen(false);
    }

    function pickCreation() {
        creationOption?.onSelect(trimmedQuery);
        setIsOpen(false);
    }

    function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
        // Pending: no key opens the list or picks from it; the form's own keys
        // still reach it.
        if (pending) {
            onKeyDown?.(event);
            return;
        }
        if (event.key === 'ArrowDown') {
            event.preventDefault();
            if (!isOpen) {
                setIsOpen(true);
                return;
            }
            setHighlightIndex((prev) =>
                totalSelectable === 0 ? -1 : (prev + 1) % totalSelectable,
            );
            return;
        }
        if (event.key === 'ArrowUp') {
            event.preventDefault();
            if (!isOpen) {
                setIsOpen(true);
                return;
            }
            setHighlightIndex((prev) =>
                totalSelectable === 0
                    ? -1
                    : (prev - 1 + totalSelectable) % totalSelectable,
            );
            return;
        }
        if (event.key === 'Enter') {
            if (isOpen && highlightIndex === creationRowIndex && showCreationRow) {
                event.preventDefault();
                pickCreation();
                return;
            }
            if (isOpen && highlightIndex >= 0 && filtered[highlightIndex] !== undefined) {
                // Take the highlight — prevent default so the form
                // doesn't also try to submit on this Enter.
                event.preventDefault();
                pickItem(filtered[highlightIndex]!);
                return;
            }
            // No highlight to pick → let the parent form catch Enter
            // (e.g., to submit). Fall through to parent's onKeyDown.
        }
        if (event.key === 'Tab') {
            if (isOpen && highlightIndex === creationRowIndex && showCreationRow) {
                pickCreation();
            } else if (isOpen && highlightIndex >= 0 && filtered[highlightIndex] !== undefined) {
                // Apply highlight then let focus advance naturally —
                // no preventDefault.
                pickItem(filtered[highlightIndex]!);
            }
            // Tab always bubbles; parent gets it too.
        }
        if (event.key === 'Escape' && listShown) {
            // This Escape dismissed the suggestions, nothing more. Marked
            // handled with preventDefault, the mark every list in the editors
            // leaves and the editors and Modal read; stopped as well, so
            // nothing further up acts on it. The next Escape, with no list
            // showing, falls through below and cancels the edit.
            event.preventDefault();
            event.stopPropagation();
            setIsOpen(false);
            return;
        }
        onKeyDown?.(event);
    }

    function handleBlur(event: FocusEvent<HTMLInputElement>) {
        setIsOpen(false);
        onBlur?.(event);
    }

    return (
        <div
            ref={containerRef}
            className={cn('relative', className)}
        >
            <input
                ref={setInputRef}
                id={inputId}
                type="text"
                role="combobox"
                aria-label={ariaLabel}
                aria-autocomplete="list"
                // The list on screen, not the focus state: a focused field with
                // nothing matching shows no list and must not announce one.
                aria-expanded={listShown}
                aria-controls={listboxId}
                aria-activedescendant={
                    listShown && highlightIndex >= 0 && filtered[highlightIndex] !== undefined
                        ? `${listboxId}-${getKey(filtered[highlightIndex]!)}`
                        : undefined
                }
                autoComplete="off"
                autoCapitalize="off"
                autoCorrect="off"
                spellCheck={false}
                placeholder={placeholder}
                disabled={disabled}
                autoFocus={autoFocus}
                value={value}
                onChange={(event) => {
                    onChange(event.target.value);
                    setIsOpen(true);
                }}
                onFocus={() => setIsOpen(true)}
                onKeyDown={handleKeyDown}
                onBlur={handleBlur}
                className={cn(
                    // 28px / text-xs: every caller is a transaction editor, and all
                    // their other fields are that. 32px and text-sm were this
                    // component's own defaults from before it had callers, and they
                    // made the payee sit 4px taller than the date beside it.
                    'flex h-control-28px w-full rounded border border-border bg-surface px-2 text-xs text-text',
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-1 focus-visible:ring-accent',
                    'disabled:cursor-not-allowed disabled:opacity-50',
                    'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                    sizeClass,
                )}
                {...pendingTextProps<HTMLInputElement>(pending)}
            />
            {listShown ? (
                <ul
                    id={listboxId}
                    role="listbox"
                    className="absolute left-0 right-0 z-20 mt-1 max-h-fixed-256px overflow-auto rounded border border-border bg-surface shadow-[0_4px_12px_rgba(15,23,42,0.08)]"
                >
                    {filtered.map((item, index) => {
                        const key = getKey(item);
                        const isHighlight = index === highlightIndex;
                        const showDividerBefore = index === dividerIndex;
                        return (
                            <Fragment key={key}>
                                {showDividerBefore ? (
                                    <li
                                        role="separator"
                                        aria-hidden="true"
                                        className="border-t border-border/60 my-1"
                                    />
                                ) : null}
                                <li
                                    id={`${listboxId}-${key}`}
                                    role="option"
                                    aria-selected={isHighlight}
                                    onMouseEnter={() => setHighlightIndex(index)}
                                    // pointerdown beats blur — the input
                                    // would otherwise blur first and the
                                    // click never resolves to a pick.
                                    onPointerDown={(event) => {
                                        event.preventDefault();
                                        pickItem(item);
                                    }}
                                    className={cn(
                                        'cursor-pointer px-2 py-1.5 text-sm',
                                        isHighlight
                                            ? 'bg-surface-hover text-text'
                                            : 'text-text',
                                    )}
                                >
                                    {renderItem ? renderItem(item, value) : getLabel(item)}
                                </li>
                            </Fragment>
                        );
                    })}
                    {showCreationRow && creationOption ? (
                        <>
                            {filtered.length > 0 ? (
                                <li
                                    role="separator"
                                    aria-hidden="true"
                                    className="border-t border-border/60 my-1"
                                />
                            ) : null}
                            <li
                                id={`${listboxId}-create`}
                                role="option"
                                aria-selected={highlightIndex === creationRowIndex}
                                onMouseEnter={() => setHighlightIndex(creationRowIndex)}
                                onPointerDown={(event) => {
                                    event.preventDefault();
                                    pickCreation();
                                }}
                                className={cn(
                                    'cursor-pointer px-2 py-1.5 text-sm font-medium text-accent',
                                    highlightIndex === creationRowIndex
                                        ? 'bg-surface-hover'
                                        : '',
                                )}
                            >
                                {creationOption.label(trimmedQuery)}
                            </li>
                        </>
                    ) : null}
                </ul>
            ) : null}
        </div>
    );
}

/**
 * Generic typeahead with keyboard navigation, click-outside close,
 * and ARIA combobox semantics. Selection is a pure {@link onChange};
 * the parent owns commit/cancel logic at form level.
 */
export const Typeahead = forwardRef(TypeaheadInner) as <T>(
    props: TypeaheadProps<T> & { ref?: React.ForwardedRef<HTMLInputElement> },
) => React.ReactElement;
