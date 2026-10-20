import { forwardRef, type ButtonHTMLAttributes } from 'react';

import { cn } from '@/lib/cn';

// IconButton — compact square button for icon-only affordances
// (⌘K trigger, settings cog, sidebar collapse). Always require an
// accessible label via `aria-label`.

export interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
    /** Required: screen readers need a name for icon-only buttons. */
    'aria-label': string;
    /**
     * `md` (28px) is the default and the register's control height.
     *
     * `sm` (20px) exists for the sidebar nav rows, which are ~23px tall — a
     * 28px control there would push every row taller and change the rail's
     * density, which is the one thing a row-actions affordance must not do.
     * Do not reach for it to make a button unobtrusive; 20px is already at the
     * floor of a comfortable pointer target.
     */
    size?: 'sm' | 'md';
}

export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(
    function IconButton({ className, type, size = 'md', ...props }, ref) {
        return (
            <button
                ref={ref}
                type={type ?? 'button'}
                className={cn(
                    'inline-flex items-center justify-center rounded text-text-muted',
                    size === 'sm' ? 'size-control-20px' : 'size-control-28px',
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
