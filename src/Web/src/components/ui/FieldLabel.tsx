import type { LabelHTMLAttributes, ReactNode } from 'react';

import { cn } from '@/lib/cn';

// FieldLabel — the small all-caps field label (ADR-0023 §P): the
// `text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted`
// string, in one component rather than inlined across editors.
//
// `leading-[1.5]` is load-bearing, not decoration. The font size is an
// arbitrary value, which sets font-size ALONE, so without it the line box comes
// from whatever line-height is inherited. Tailwind v4 expresses a text size's
// line-height as a unitless RATIO, so a parent carrying `text-xs` (ratio 4/3)
// instead of the document's 1.5 makes its caption 13.3px where every other
// caption is 15px. AccountCategoryPicker's root does exactly that, and the
// 1.7px lost would lift its input, its tags caption and its tags control above
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
