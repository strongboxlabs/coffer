import type { ReactNode } from 'react';

import { cn } from '@/lib/cn';
import { RowActionsButton } from '@/components/ui/RowActionsButton';

// One row of one of Structure's three lists (ADR-0111 D4). The columns are the same on every
// list, so the tabs read as one page: the name and its badges, a line of secondary text, a
// count, an amount, and the way into the row's menu. The count and the amount are always
// there, empty where a list has nothing for them, so the count sits at the same distance from
// the menu on every tab and flipping between them moves nothing. The secondary text is the one
// column a list may leave out, and the name, which takes what is left, is wider for it.
//
// The row owns the menu's two doors. A right-click and the kebab open the same menu at
// the point they were made (ADR-0021 Rule 10: a menu reachable only by right-click has no
// visible path, and none at all on touch), and the lists that use the row cannot forget
// either one.

/** Where a row's menu opens: a pointer's position, or the corner of the kebab. */
export interface MenuAnchor { x: number; y: number }

/**
 * A row's way into its menu.
 *
 * - absent: the list has no menu column at all, which is what a reader who cannot edit
 *   gets, so no row carries a control that could only be refused;
 * - `'none'`: the list has the column and this row has nothing in it (a system row), so a
 *   same-size spacer keeps the rows that do have a button aligned with it;
 * - an object: the kebab, named for the row, and the right-click.
 */
export type RowMenu = 'none' | { label: string; onOpen: (anchor: MenuAnchor) => void };

export interface StructureRowProps {
    /** The name, usually a link; a tag's colour dot goes in front of it. */
    name: ReactNode;
    /** Inactive and System, as `StructureBadge`s. */
    badges?: ReactNode;
    /** Secondary text. `undefined`: this list has none. `null`: it does, and this row has no text. */
    secondary?: ReactNode;
    /** A count. The column is always there; an absent or `null` value leaves this row's cell empty. */
    count?: ReactNode;
    /** An amount, on the lists that carry one. The column is always there, empty on the rest. */
    amount?: ReactNode;
    /** How far a tree row is indented, in steps. */
    depth?: number;
    menu?: RowMenu;
    /** Given the kebab, for a list whose menu can take the row away (useRowRemovalFocus). */
    menuControl?: (el: HTMLElement | null) => void;
}

export function StructureRow({ name, badges, secondary, count, amount, depth = 0, menu, menuControl }: StructureRowProps) {
    const opens = typeof menu === 'object' ? menu : null;
    return (
        <li
            onContextMenu={opens
                ? (e) => {
                    e.preventDefault();
                    opens.onOpen({ x: e.clientX, y: e.clientY });
                }
                : undefined}
            className="flex items-center gap-3 px-3 py-2.5 hover:bg-surface-hover"
        >
            <div
                className="flex min-w-0 flex-1 items-center gap-2"
                style={depth > 0 ? { paddingLeft: `${depth * 1.25}rem` } : undefined}
            >
                {name}
                {badges}
            </div>
            {secondary !== undefined ? (
                <div className="hidden w-fixed-160px shrink-0 truncate text-xs text-text-subtle md:block">
                    {secondary}
                </div>
            ) : null}
            <div className="w-fixed-128px shrink-0 text-right text-xs tabular-nums text-text-subtle">
                {count}
            </div>
            {/* Held open on a wide screen even where there is no amount, for the alignment; on a
                narrow one an empty column would only squeeze the name. */}
            <div
                className={cn(
                    'w-fixed-96px shrink-0 text-right text-sm tabular-nums text-text',
                    amount === undefined && 'hidden md:block',
                )}
            >
                {amount}
            </div>
            {opens ? (
                <RowActionsButton ref={menuControl} label={opens.label} onOpen={opens.onOpen} />
            ) : menu === 'none' ? (
                // Aligns the columns of the rows that do have a button.
                <span aria-hidden className="size-control-28px shrink-0" />
            ) : null}
        </li>
    );
}

/** The small marker for a row's state: Inactive, System. One component for every list. */
export function StructureBadge({ children, className }: { children: ReactNode; className?: string }) {
    return (
        <span
            className={cn(
                'shrink-0 rounded bg-surface-muted px-1.5 py-0.5 text-[0.625rem] font-medium uppercase tracking-wide text-text-subtle',
                className,
            )}
        >
            {children}
        </span>
    );
}
