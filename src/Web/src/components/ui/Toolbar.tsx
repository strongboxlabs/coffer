import {
    forwardRef,
    type ButtonHTMLAttributes,
    type HTMLAttributes,
} from 'react';

import { cn } from '@/lib/cn';

// Toolbar — the strip above the register that holds filter chips,
// search, and action buttons. ADR-0021 Rule 6.

export type ToolbarProps = HTMLAttributes<HTMLDivElement>;

export const Toolbar = forwardRef<HTMLDivElement, ToolbarProps>(
    function Toolbar({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn(
                    'flex items-center justify-between gap-3 border-b border-border bg-surface px-4 py-2',
                    className,
                )}
                {...props}
            />
        );
    },
);

export type ToolbarGroupProps = HTMLAttributes<HTMLDivElement>;

/** Horizontal flex group within a Toolbar. Groups separated by `|`. */
export const ToolbarGroup = forwardRef<HTMLDivElement, ToolbarGroupProps>(
    function ToolbarGroup({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn('flex items-center gap-1.5', className)}
                {...props}
            />
        );
    },
);

/** Vertical hairline used between toolbar groups. */
export function ToolbarDivider() {
    return <span className="text-text-subtle">|</span>;
}

export interface ToolbarFilterButtonProps
    extends ButtonHTMLAttributes<HTMLButtonElement> {
    active?: boolean;
}

/**
 * Filter pill in the toolbar. Active variant uses the accent-soft
 * background to signal "this filter is on" without competing with
 * the primary button accent.
 */
export const ToolbarFilterButton = forwardRef<
    HTMLButtonElement,
    ToolbarFilterButtonProps
>(function ToolbarFilterButton(
    { className, active, type, ...props },
    ref,
) {
    return (
        <button
            ref={ref}
            type={type ?? 'button'}
            aria-pressed={active}
            className={cn(
                'rounded px-2 py-1 text-xs text-text-muted hover:bg-surface-hover',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1',
                active && 'bg-accent-soft font-semibold text-accent-soft-text',
                className,
            )}
            {...props}
        />
    );
});
