import { forwardRef, type HTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';

import { cn } from '@/lib/cn';

// Status badge — small round pill with a single character.
//   - cleared       ✓ on light green     (matched against a statement)
//   - reconciling   · on accent-soft     (user is mid-reconciliation; functionally still uncleared but flagged)
//   - pending       P on amber          (SimpleFIN-style pending bank charge)
//   - scheduled     S on accent-soft    (future-dated, not yet posted)
//   - uncleared     (hollow ring)        (default; not yet matched)
//   - danger        ! on rose            (sync failures, validation errors)
//
// Vocabulary matches the DB's normalized recon status enum
// (uncleared / reconciling / cleared) — stored per-leg in
// `txn_leg_recon` (migration 171, ADR-0082; formerly
// `txn_headers.status`, migration 030). The status
// badge is also the click target for cycling that field — see
// RegisterPage.tsx for the cycle ordering.
//
// Width/height fixed at 1rem so a column of these aligns regardless
// of label content. Per ADR-0021 Rule 5.

const statusBadgeVariants = cva(
    'inline-flex h-4 w-4 items-center justify-center rounded-full text-[0.625rem] font-bold leading-none',
    {
        variants: {
            status: {
                cleared: 'bg-state-success-soft text-state-success',
                // Reconciling: small accent-coloured dot. Distinct from
                // both the filled-green cleared and the hollow-ring
                // uncleared, so the eye can scan a reconciliation
                // session's "marked but not finished" rows at a glance.
                reconciling: 'bg-accent-soft text-accent border border-accent',
                pending: 'bg-state-warning-soft text-state-warning',
                scheduled: 'bg-accent-soft text-accent-soft-text',
                // Hollow ring — visible but explicitly "not done."
                // Border picks up the text-subtle token so it sits
                // calmly next to the filled states.
                uncleared: 'border border-text-subtle bg-transparent text-transparent',
                danger: 'bg-state-danger-soft text-state-danger',
            },
        },
        defaultVariants: { status: 'cleared' },
    },
);

const LABELS: Record<NonNullable<StatusBadgeProps['status']>, string> = {
    cleared: '✓',
    reconciling: '·',
    pending: 'P',
    scheduled: 'S',
    uncleared: '', // hollow ring — no glyph
    danger: '!',
};

const ARIA: Record<NonNullable<StatusBadgeProps['status']>, string> = {
    cleared: 'Cleared',
    reconciling: 'Reconciling',
    pending: 'Pending',
    scheduled: 'Scheduled',
    uncleared: 'Uncleared',
    danger: 'Failed',
};

export interface StatusBadgeProps
    extends HTMLAttributes<HTMLSpanElement>,
        VariantProps<typeof statusBadgeVariants> {}

export const StatusBadge = forwardRef<HTMLSpanElement, StatusBadgeProps>(
    function StatusBadge({ className, status, ...props }, ref) {
        const resolved = status ?? 'cleared';
        return (
            <span
                ref={ref}
                aria-label={ARIA[resolved]}
                role="img"
                className={cn(statusBadgeVariants({ status: resolved }), className)}
                {...props}
            >
                {LABELS[resolved]}
            </span>
        );
    },
);
