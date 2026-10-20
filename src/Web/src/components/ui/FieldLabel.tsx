import type { LabelHTMLAttributes, ReactNode } from 'react';

import { cn } from '@/lib/cn';

// FieldLabel — the small all-caps field label (ADR-0023 §P). Was the exact
// `text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted`
// string inlined ~52x across editors. One component now.
//
// `leading-[1.5]` is load-bearing, not decoration. The font size is an
// arbitrary value, which sets font-size ALONE, so the line box used to come
// from whatever line-height was inherited. Tailwind v4 expresses a text size's
// line-height as a unitless RATIO, so a parent carrying `text-xs` (ratio 4/3)
// instead of the document's 1.5 made its caption 13.3px where every other
// caption was 15px. AccountCategoryPicker's root does exactly that, and the
// 1.7px it lost lifted its input, its tags caption and its tags control above
// the rest of the row. Pinning the ratio makes a caption's height its own.

export function FieldLabel({
    children,
    className,
    ...props
}: LabelHTMLAttributes<HTMLLabelElement> & { children: ReactNode }) {
    return (
        <label
            className={cn(
                'text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted',
                className,
            )}
            {...props}
        >
            {children}
        </label>
    );
}
