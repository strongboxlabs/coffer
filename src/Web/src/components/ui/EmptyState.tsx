import type { ReactNode } from 'react';

import { Panel, PanelBody } from './Panel';
import { cn } from '@/lib/cn';

// EmptyState — the one empty-state treatment (was ~4 ad-hoc variants + two
// private copies). A centered message + optional hint + optional action inside
// a Panel. Drop in where a list/table/section has nothing to show.

export function EmptyState({
    message,
    hint,
    action,
    className,
}: {
    message: ReactNode;
    hint?: ReactNode;
    action?: ReactNode;
    className?: string;
}) {
    return (
        <Panel className={className}>
            <PanelBody className="py-10 text-center">
                <p className="text-sm font-medium text-text">{message}</p>
                {hint ? <p className="mt-1 text-sm text-text-muted">{hint}</p> : null}
                {action ? <div className="mt-3 flex justify-center">{action}</div> : null}
            </PanelBody>
        </Panel>
    );
}

/**
 * Inline (Panel-less) variant for empty sub-sections that already sit inside a
 * Panel/card — same typography, no nested border.
 */
export function EmptyStateInline({
    message,
    hint,
    className,
}: {
    message: ReactNode;
    hint?: ReactNode;
    className?: string;
}) {
    return (
        <div className={cn('px-4 py-6 text-center', className)}>
            <p className="text-sm font-medium text-text">{message}</p>
            {hint ? <p className="mt-1 text-sm text-text-muted">{hint}</p> : null}
        </div>
    );
}
