import { forwardRef, type HTMLAttributes, type ReactNode } from 'react';

import { cn } from '@/lib/cn';

// KPI tile — uppercase label, large mono number, optional delta /
// caption beneath. Designed to live in a 5-up strip per ADR-0021.
// Self-contained: callers compose strips with grid + gap-px on a
// border-color background to get the hairline-separated row look.

export interface KpiTileProps extends HTMLAttributes<HTMLDivElement> {
    label: ReactNode;
    /** Big value, usually formatted currency or count. */
    value: ReactNode;
    /** Caption beneath the value (delta, range, sync time, etc). */
    caption?: ReactNode;
    /** Visual emphasis for the caption: 'accent' green, 'danger' red. */
    captionTone?: 'muted' | 'accent' | 'danger';
}

export const KpiTile = forwardRef<HTMLDivElement, KpiTileProps>(
    function KpiTile(
        { className, label, value, caption, captionTone = 'muted', ...props },
        ref,
    ) {
        return (
            <div
                ref={ref}
                className={cn('bg-surface px-4 py-3.5', className)}
                {...props}
            >
                <div className="mb-1 text-[0.6875rem] font-medium uppercase tracking-wider text-text-muted">
                    {label}
                </div>
                <div className="font-mono text-xl font-bold tabular-nums tracking-tight text-text">
                    {value}
                </div>
                {caption !== undefined ? (
                    <div
                        className={cn(
                            'font-mono text-[0.6875rem] tabular-nums',
                            captionTone === 'accent' && 'text-state-success',
                            captionTone === 'danger' && 'text-state-danger',
                            captionTone === 'muted' && 'text-text-muted',
                        )}
                    >
                        {caption}
                    </div>
                ) : null}
            </div>
        );
    },
);
