import { forwardRef, type InputHTMLAttributes } from 'react';

import { cn } from '@/lib/cn';

import { pendingTextProps } from './pending';

export type InputProps = InputHTMLAttributes<HTMLInputElement> & {
    /**
     * The request this field belongs to is in flight: the field keeps focus
     * and is read-only until it is not (pending.ts, ADR-0023 §L). `disabled`
     * is for a field that is unavailable for another reason.
     */
    pending?: boolean;
};

/**
 * Text input primitive. The caller supplies every important attribute
 * (id, name, type, autoComplete, aria-*); this component only owns
 * the visual treatment so accessibility isn't accidentally hidden
 * behind a wrapper.
 */
export const Input = forwardRef<HTMLInputElement, InputProps>(
    function Input({ className, type, pending, ...props }, ref) {
        return (
            <input
                ref={ref}
                type={type ?? 'text'}
                className={cn(
                    'flex h-control-40px w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text',
                    'placeholder:text-text-subtle',
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-accent',
                    // A pending field (aria-disabled, pending.ts) looks as a disabled one does.
                    'disabled:cursor-not-allowed disabled:opacity-50',
                    'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                    // aria-invalid must be VISIBLE, not just announced. Callers
                    // already set it; without a paired visual treatment a sighted
                    // user sees a pristine field next to a disabled submit button
                    // and has no idea which input is at fault.
                    'aria-invalid:border-state-danger',
                    'aria-invalid:focus-visible:ring-state-danger',
                    className,
                )}
                {...props}
                {...pendingTextProps(pending)}
            />
        );
    },
);
