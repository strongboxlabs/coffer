import { forwardRef, type ButtonHTMLAttributes } from 'react';

import { cn } from '@/lib/cn';

// IconButton — compact square button for icon-only affordances
// (⌘K trigger, settings cog, sidebar collapse). Always require an
// accessible label via `aria-label`.

export interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
    /** Required: screen readers need a name for icon-only buttons. */
    'aria-label': string;
}

export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(
    function IconButton({ className, type, ...props }, ref) {
        return (
            <button
                ref={ref}
                type={type ?? 'button'}
                className={cn(
                    'inline-flex h-7 w-7 items-center justify-center rounded text-text-muted',
                    'hover:bg-surface-hover hover:text-text',
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1',
                    'disabled:pointer-events-none disabled:opacity-50',
                    className,
                )}
                {...props}
            />
        );
    },
);
