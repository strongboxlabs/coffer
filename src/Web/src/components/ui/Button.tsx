import { forwardRef, type ButtonHTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';

import { cn } from '@/lib/cn';

// Hand-built Button primitive following shadcn-ish conventions. We
// don't pull shadcn/ui via its CLI for two reasons: (1) the CLI
// requires npm registry access during scaffolding, which is
// brittle for offline / restricted environments, and (2) maintaining
// ~10 source files we wrote ourselves is simpler than tracking
// upstream-generated files. The variant API is from
// class-variance-authority — the same library shadcn uses — so
// migrating later is trivial if we want to.
//
// Variants reference the semantic tokens declared in src/index.css
// (@theme block) — see ADR-0021. A palette tweak is a one-line CSS
// change, not a codebase-wide find-and-replace.

const buttonVariants = cva(
    // Base classes: focus ring + disabled state + size-stable
    'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium ' +
        'transition-colors disabled:pointer-events-none disabled:opacity-50 ' +
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-accent',
    {
        variants: {
            variant: {
                primary: 'bg-accent text-text-inverse hover:bg-accent-hover',
                secondary:
                    'bg-surface text-text border border-border hover:bg-surface-hover',
                ghost: 'text-text hover:bg-surface-hover',
                // Destructive affirmative — for delete / revoke /
                // drop confirms (see ConfirmDialog).
                danger: 'bg-state-danger text-text-inverse hover:bg-state-danger/90',
            },
            size: {
                md: 'h-10 px-4 py-2',
                sm: 'h-9 px-3',
                lg: 'h-11 px-8',
            },
        },
        defaultVariants: {
            variant: 'primary',
            size: 'md',
        },
    },
);

export interface ButtonProps
    extends ButtonHTMLAttributes<HTMLButtonElement>,
        VariantProps<typeof buttonVariants> {}

/**
 * Button with semantic variants + size scale.
 *
 * Always render an explicit `type` (`button` or `submit`); the
 * default HTML button type is `submit` which surprises forms. The
 * `type` prop is passed through unchanged — callers are responsible
 * for setting it.
 */
export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
    function Button({ className, variant, size, type, ...props }, ref) {
        return (
            <button
                ref={ref}
                type={type ?? 'button'}
                className={cn(buttonVariants({ variant, size }), className)}
                {...props}
            />
        );
    },
);
