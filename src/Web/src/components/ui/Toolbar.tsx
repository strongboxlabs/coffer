import {
    forwardRef,
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
