import { forwardRef, type HTMLAttributes } from 'react';

import { cn } from '@/lib/cn';

// Panel — the white-with-hairline-border container. ADR-0021 Rule 2:
// 1px border, no rounded corners on the panel itself, and the single
// shadow recipe `0 1px 2px rgba(15,23,42,.025)`. No elevation
// hierarchy — every panel sits at the same depth.

export type PanelProps = HTMLAttributes<HTMLDivElement>;

export const Panel = forwardRef<HTMLDivElement, PanelProps>(function Panel(
    { className, ...props },
    ref,
) {
    return (
        <div
            ref={ref}
            className={cn(
                'bg-surface border border-border shadow-[0_1px_2px_rgba(15,23,42,0.025)]',
                className,
            )}
            {...props}
        />
    );
});

export type PanelHeadProps = HTMLAttributes<HTMLDivElement>;

/**
 * Heading strip at the top of a Panel. Houses an `<h2>` (or label)
 * + an optional action slot on the right via flex children.
 */
export const PanelHead = forwardRef<HTMLDivElement, PanelHeadProps>(
    function PanelHead({ className, ...props }, ref) {
        return (
            <div
                ref={ref}
                className={cn(
                    'flex items-center justify-between border-b border-border px-4 py-2.5',
                    className,
                )}
                {...props}
            />
        );
    },
);

export type PanelBodyProps = HTMLAttributes<HTMLDivElement>;

/** Optional helper for padded content inside a Panel. */
export const PanelBody = forwardRef<HTMLDivElement, PanelBodyProps>(
    function PanelBody({ className, ...props }, ref) {
        return <div ref={ref} className={cn('p-4', className)} {...props} />;
    },
);
