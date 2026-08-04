import type { InputHTMLAttributes, ReactNode } from 'react';

import { cn } from '@/lib/cn';

// Checkbox — the standard styled checkbox + label (was a hand-rolled
// `<input type="checkbox" className="h-4 w-4 …">` in ~13 files, some bare/
// unstyled). One affordance, one look. Pass `label` for the common
// checkbox-with-text case, or use bare (no label) inline.

export interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
    label?: ReactNode;
    /** Extra classes on the wrapping label (when `label` is set). */
    wrapperClassName?: string;
}

export function Checkbox({ label, className, wrapperClassName, ...props }: CheckboxProps) {
    const box = (
        <input
            type="checkbox"
            className={cn(
                'h-4 w-4 rounded border-border text-accent',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                'disabled:cursor-not-allowed disabled:opacity-50',
                className,
            )}
            {...props}
        />
    );
    if (label === undefined) return box;
    return (
        <label className={cn('flex items-center gap-2 text-sm', wrapperClassName)}>
            {box}
            <span>{label}</span>
        </label>
    );
}
