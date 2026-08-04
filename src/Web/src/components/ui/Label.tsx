import { forwardRef, type LabelHTMLAttributes } from 'react';

import { cn } from '@/lib/cn';

export type LabelProps = LabelHTMLAttributes<HTMLLabelElement>;

/**
 * Form label primitive. Always pair with an Input that has a
 * matching `id` (and `htmlFor` here) so screen readers can announce
 * the label when the input gets focus.
 */
export const Label = forwardRef<HTMLLabelElement, LabelProps>(
    function Label({ className, ...props }, ref) {
        return (
            <label
                ref={ref}
                className={cn('text-sm font-medium text-text', className)}
                {...props}
            />
        );
    },
);
