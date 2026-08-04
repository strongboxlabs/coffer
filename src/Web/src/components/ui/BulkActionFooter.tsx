import type { ReactNode } from 'react';

import { cn } from '@/lib/cn';

// BulkActionFooter — bottom strip that appears when ≥1 register row is
// selected. Shows "N selected" + the caller's bulk actions. ADR-0021 Rule 6.
// It deliberately does NOT surface how many rows are loaded into the virtual
// window — that's an internal windowing detail the user doesn't care about.

export interface BulkActionFooterProps {
    /** Number of selected rows. Footer is hidden when 0 (unless `alwaysVisible`
     *  or a `trailing` node is present). */
    selectedCount: number;
    /** Clear the selection — rendered as an ✕ next to the "N selected" count
     *  (the clear affordance, in place of a separate button). Omit to show the
     *  count with no clear control. */
    onClear?: () => void;
    /** Action slot — usually <Button variant="ghost">…</Button> children. */
    actions?: ReactNode;
    /** Right-side slot (e.g. a "Loading…" hint). */
    trailing?: ReactNode;
    /** Render even when nothing is selected. */
    alwaysVisible?: boolean;
    className?: string;
}

export function BulkActionFooter({
    selectedCount,
    onClear,
    actions,
    trailing,
    alwaysVisible = true,
    className,
}: BulkActionFooterProps) {
    // When not pinned, the strip only earns its space if there's something to
    // show: an active selection (the bulk-action bar) or a `trailing` node (the
    // "Loading…" hint). Otherwise it's just noise, so we hide it.
    if (!alwaysVisible && selectedCount === 0 && !trailing) return null;
    return (
        <div
            role="region"
            aria-label="Bulk actions"
            className={cn(
                'flex items-center justify-between gap-3 border-t border-border bg-surface-muted px-3 py-2 text-xs',
                className,
            )}
        >
            <div className="flex items-center gap-1.5 font-mono tabular-nums text-text-muted">
                {selectedCount > 0 ? (
                    <>
                        <span>{selectedCount} selected</span>
                        {onClear ? (
                            <button
                                type="button"
                                onClick={onClear}
                                aria-label="Clear selection"
                                title="Clear selection (Esc)"
                                className="rounded px-1 leading-none text-text-subtle transition-colors hover:bg-surface-hover hover:text-text"
                            >
                                ✕
                            </button>
                        ) : null}
                    </>
                ) : null}
            </div>
            <div className="flex items-center gap-2">
                {selectedCount > 0 ? actions : null}
                {selectedCount > 0 && trailing ? (
                    <span className="text-text-subtle">|</span>
                ) : null}
                {trailing}
            </div>
        </div>
    );
}
