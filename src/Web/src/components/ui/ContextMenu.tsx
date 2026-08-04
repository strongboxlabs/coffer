import {
    useCallback,
    useEffect,
    useLayoutEffect,
    useMemo,
    useRef,
    useState,
    type CSSProperties,
    type KeyboardEvent,
    type ReactNode,
} from 'react';

import { cn } from '@/lib/cn';

// ContextMenu — anchored popover triggered by right-click (or
// programmatically), used today for per-row register actions
// (Delete / Duplicate / Show Other Side).
//
// Contract (this file's reason-for-being):
//
//   * Opens at the supplied screen-coordinates; flips to fit when the
//     menu would otherwise spill off the right or bottom edge.
//   * Items are a flat array; rendering / icons / shortcut hints all
//     come from the per-item shape (`label`, `onSelect`, `danger`,
//     `disabled`, `shortcutHint`).
//   * Keyboard:
//       ArrowDown / ArrowUp — move highlight (wraps).
//       Enter / Space      — activate highlighted item.
//       Esc                — close (does not bubble — parent form's
//                            cancel handler must not also fire).
//       Tab                — close + move focus naturally to the next
//                            element (modern web convention: Tab
//                            commits the menu's selection out is fine).
//   * Outside click / blur / window blur — close.
//   * Items with `disabled: true` are skipped by ArrowUp/ArrowDown
//     and are non-clickable.
//
// Why not a third-party library: we already hand-rolled Typeahead in
// the same idiom and we want zero new runtime deps in PR-4.7. Radix-
// DropdownMenu would be the obvious upgrade if/when we add more menu
// surfaces — file an ADR if you reach for it.

export interface ContextMenuItem {
    /** Stable identity for keyboard nav + React reconciliation. */
    id: string;
    /** Visible row label. */
    label: string;
    /** Activation callback. The component closes the menu before
     * calling this — `onSelect` is free to navigate / open dialogs
     * without racing against the menu's close. */
    onSelect: () => void;
    /** Render in a destructive style (text-state-danger). The menu
     * doesn't gate the action — confirm dialogs are the caller's
     * responsibility. */
    danger?: boolean;
    /** Skip in arrow-key navigation; render at reduced opacity. */
    disabled?: boolean;
    /** Optional right-aligned shortcut hint ("⌘D", "Del", …). Pure
     * presentation — the menu does not bind these keys itself. */
    shortcutHint?: string;
}

/** Coordinates where the menu's top-left corner should anchor.
 *  Screen-relative (i.e. `clientX` / `clientY` from a mouse event). */
export interface ContextMenuAnchor {
    x: number;
    y: number;
}

export interface ContextMenuProps {
    /** Menu anchor. The component reads this once on mount; remount
     * (via React `key`) to reposition. */
    anchor: ContextMenuAnchor;
    /** Item list. Empty arrays still render an empty rounded surface
     * — render-guard at the caller if "no items" should hide it. */
    items: readonly ContextMenuItem[];
    /** Fired on Esc / outside-click / Tab / successful item activation.
     * The component itself closes (re-renders nothing) only via
     * unmount, so the caller's state drives visibility. */
    onClose: () => void;
}

export function ContextMenu({ anchor, items, onClose }: ContextMenuProps) {
    const rootRef = useRef<HTMLDivElement | null>(null);

    const activeIndices = useMemo(
        () => items.map((item, idx) => (item.disabled ? -1 : idx)).filter(i => i >= 0),
        [items],
    );

    const initialIndex = activeIndices.length > 0 ? activeIndices[0] : -1;
    const [highlightedIndex, setHighlightedIndex] = useState<number>(initialIndex);

    // After mount, position + focus.
    const [position, setPosition] = useState<CSSProperties>({
        left: anchor.x,
        top: anchor.y,
        visibility: 'hidden',
    });
    useLayoutEffect(() => {
        const node = rootRef.current;
        if (!node) return;
        const rect = node.getBoundingClientRect();
        const vw = window.innerWidth;
        const vh = window.innerHeight;
        const margin = 4;
        // Flip horizontally if menu would spill off the right.
        const left = anchor.x + rect.width + margin > vw
            ? Math.max(margin, anchor.x - rect.width)
            : anchor.x;
        // Flip vertically if menu would spill off the bottom.
        const top = anchor.y + rect.height + margin > vh
            ? Math.max(margin, anchor.y - rect.height)
            : anchor.y;
        setPosition({ left, top });
        node.focus();
    }, [anchor.x, anchor.y]);

    // Outside-click + window-blur dismissal.
    useEffect(() => {
        function handlePointerDown(e: MouseEvent) {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
                onClose();
            }
        }
        function handleWindowBlur() {
            onClose();
        }
        // Listen on pointerdown (not click) so the menu closes before
        // any other handler runs on the click that opens another menu.
        window.addEventListener('pointerdown', handlePointerDown);
        window.addEventListener('blur', handleWindowBlur);
        return () => {
            window.removeEventListener('pointerdown', handlePointerDown);
            window.removeEventListener('blur', handleWindowBlur);
        };
    }, [onClose]);

    const moveHighlight = useCallback(
        (delta: 1 | -1) => {
            if (activeIndices.length === 0) return;
            const currentPos = activeIndices.indexOf(highlightedIndex);
            const nextPos = currentPos === -1
                ? (delta === 1 ? 0 : activeIndices.length - 1)
                : (currentPos + delta + activeIndices.length) % activeIndices.length;
            setHighlightedIndex(activeIndices[nextPos]);
        },
        [activeIndices, highlightedIndex],
    );

    const activate = useCallback(
        (index: number) => {
            const item = items[index];
            if (!item || item.disabled) return;
            onClose();
            item.onSelect();
        },
        [items, onClose],
    );

    const handleKeyDown = useCallback(
        (e: KeyboardEvent<HTMLDivElement>) => {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                moveHighlight(1);
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                moveHighlight(-1);
            } else if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                if (highlightedIndex >= 0) activate(highlightedIndex);
            } else if (e.key === 'Escape') {
                // Stop bubble — parent form / register row mustn't also
                // cancel anything else as a side effect of closing me.
                e.preventDefault();
                e.stopPropagation();
                onClose();
            } else if (e.key === 'Tab') {
                // Tab closes the menu and lets focus advance naturally.
                onClose();
            }
        },
        [highlightedIndex, moveHighlight, activate, onClose],
    );

    return (
        <div
            ref={rootRef}
            role="menu"
            tabIndex={-1}
            onKeyDown={handleKeyDown}
            className={cn(
                'fixed z-50 min-w-[12rem] rounded-md border border-border bg-surface',
                'py-1 shadow-md outline-none',
            )}
            style={position}
        >
            {items.map((item, idx) => (
                <ContextMenuRow
                    key={item.id}
                    item={item}
                    highlighted={idx === highlightedIndex}
                    onHover={() => !item.disabled && setHighlightedIndex(idx)}
                    onClick={() => activate(idx)}
                />
            ))}
        </div>
    );
}

interface ContextMenuRowProps {
    item: ContextMenuItem;
    highlighted: boolean;
    onHover: () => void;
    onClick: () => void;
}

function ContextMenuRow({ item, highlighted, onHover, onClick }: ContextMenuRowProps): ReactNode {
    return (
        <button
            type="button"
            role="menuitem"
            disabled={item.disabled}
            onMouseEnter={onHover}
            onClick={onClick}
            className={cn(
                'flex w-full items-center justify-between px-3 py-1.5 text-left text-sm',
                'transition-colors',
                item.disabled
                    ? 'cursor-not-allowed opacity-50'
                    : 'cursor-pointer',
                !item.disabled && highlighted && 'bg-accent-soft/40',
                item.danger ? 'text-state-danger' : 'text-text',
            )}
        >
            <span>{item.label}</span>
            {item.shortcutHint ? (
                <span className="ml-4 text-[0.6875rem] text-text-muted">
                    {item.shortcutHint}
                </span>
            ) : null}
        </button>
    );
}
