import { cn } from '@/lib/cn';

import { IconButton } from './IconButton';

// A visible way into a row's context menu.
//
// WHY THIS EXISTS. The Tags and Categories settings panels had NO visible
// controls at all — `grep -c '<Button|<IconButton'` returned 0 for both — and
// eight actions (rename, recolour, merge, delete, add sub-category, move) were
// reachable only by right-clicking a row. A `title="Right-click for actions"`
// tooltip was the entire discovery mechanism, and tooltips do not exist on
// touch and are not announced as affordances.
//
// Right-click stays: on a list you use often it is faster, and removing it
// would punish the people who already know. This is the discoverable path, not
// a replacement.
//
// NOT applied to the register, but that is a judgement about density, not a
// clean bill of health. Selecting rows reveals a bulk bar covering Categorize /
// Move / Delete, so those have a visible path; Accept, Duplicate, Create
// reminder and Show other side remain right-click-only. A control on every row
// of a dense grid would cost more than it buys, so the gap is recorded in
// ADR-0021 Rule 10 rather than closed here.

export function RowActionsButton({
    label,
    onOpen,
    disabled,
    size = 'md',
    className,
}: {
    /** Announced to screen readers — name the row, e.g. `Actions for Groceries`. */
    label: string;
    /** Given the button's own rectangle so the menu can anchor to it rather
     *  than to a pointer position the keyboard never produces. */
    onOpen: (anchor: { x: number; y: number }) => void;
    disabled?: boolean;
    /** `sm` (20px) fits the sidebar's ~23px nav rows; see IconButton. */
    size?: 'sm' | 'md';
    /** For positioning only — the sidebar overlays it on a reserved slot. */
    className?: string;
}) {
    return (
        <IconButton
            aria-label={label}
            aria-haspopup="menu"
            disabled={disabled}
            size={size}
            className={cn('shrink-0', className)}
            onClick={(e) => {
                e.stopPropagation();          // a row may have its own click
                const r = e.currentTarget.getBoundingClientRect();
                onOpen({ x: r.right, y: r.bottom });
            }}
        >
            {/* Kebab. Inline SVG per ADR-0021 Rule 7 (lucide is not in yet). */}
            <svg
                width={size === 'sm' ? 10 : 14}
                height={size === 'sm' ? 10 : 14}
                viewBox="0 0 16 16"
                fill="currentColor"
                aria-hidden="true"
            >
                <circle cx="8" cy="3" r="1.4" />
                <circle cx="8" cy="8" r="1.4" />
                <circle cx="8" cy="13" r="1.4" />
            </svg>
        </IconButton>
    );
}
