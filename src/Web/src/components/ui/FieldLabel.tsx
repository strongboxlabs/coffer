import type { LabelHTMLAttributes, ReactNode } from 'react';

import { cn } from '@/lib/cn';

// FieldLabel — the small all-caps field label (ADR-0023 §P). Was the exact
// `text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted`
// string inlined ~52× across editors. One component now.

export function FieldLabel({
    children,
    className,
    ...props
}: LabelHTMLAttributes<HTMLLabelElement> & { children: ReactNode }) {
    return (
        <label
            className={cn(
                'text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted',
                className,
            )}
            {...props}
        >
            {children}
        </label>
    );
}
