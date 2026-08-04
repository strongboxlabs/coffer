import { forwardRef, type HTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';

import { cn } from '@/lib/cn';

// Chip — small inline pill for categories, tags, statuses, or any
// labelled enum. Single-color treatment per variant; all colors are
// declared as semantic tokens in src/index.css (ADR-0021 Rule 5).
//
// Variants:
//   - default               — neutral slate (e.g. for tags)
//   - warn                  — amber warning (e.g. "needs category")
//   - flagged               — pink/rose (e.g. "large", manually flagged)
//   - groc/din/house/util/sub/tran/sal/xfer/phone/rec — category palette

const chipVariants = cva(
    'inline-flex items-center gap-1 rounded px-1.5 py-[0.0625rem] text-[0.6875rem] font-medium',
    {
        variants: {
            variant: {
                default: 'bg-surface-hover text-text-muted',
                warn: 'bg-state-warning-soft text-state-warning',
                flagged: 'bg-cat-rec-soft text-cat-rec-text',
                groc: 'bg-cat-groc-soft text-cat-groc-text',
                din: 'bg-cat-din-soft text-cat-din-text',
                house: 'bg-cat-house-soft text-cat-house-text',
                util: 'bg-cat-util-soft text-cat-util-text',
                sub: 'bg-cat-sub-soft text-cat-sub-text',
                tran: 'bg-cat-tran-soft text-cat-tran-text',
                sal: 'bg-cat-sal-soft text-cat-sal-text',
                xfer: 'bg-cat-xfer-soft text-cat-xfer-text',
                phone: 'bg-cat-phone-soft text-cat-phone-text',
                rec: 'bg-cat-rec-soft text-cat-rec-text',
            },
        },
        defaultVariants: { variant: 'default' },
    },
);

export interface ChipProps
    extends HTMLAttributes<HTMLSpanElement>,
        VariantProps<typeof chipVariants> {}

export const Chip = forwardRef<HTMLSpanElement, ChipProps>(function Chip(
    { className, variant, ...props },
    ref,
) {
    return (
        <span
            ref={ref}
            className={cn(chipVariants({ variant }), className)}
            {...props}
        />
    );
});
