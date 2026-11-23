import { AlertTriangle, Building2 } from 'lucide-react';

import { Button } from '@/components/ui/Button';

import { demoStepPercent, demoStepText, type DemoRow } from './demoBuild';

const ROLE = 'text-[0.6875rem] font-medium uppercase tracking-wider text-text-subtle';

/**
 * One Demo build in the hub's list (ADR-0115 D9). Preparing: not a link, since there
 * is no ledger to open yet, with the step in progress and a bar. Failed and
 * interrupted: the reason, and Try again, which starts a new build under the same
 * name (a failed build kept nothing).
 */
export function DemoBuildRow({
    row,
    onRetry,
    retryPending,
}: {
    row: DemoRow;
    onRetry: () => void;
    retryPending: boolean;
}) {
    const name = (
        <span className="flex items-center gap-2">
            <Building2 className="size-icon-md text-text-subtle" aria-hidden />
            <span className="text-sm font-medium text-text-muted">{row.name}</span>
        </span>
    );

    if (row.kind === 'preparing') {
        return (
            <li>
                <div aria-disabled="true" className="px-4 py-3">
                    <div className="flex items-center justify-between gap-3">
                        {name}
                        <span className={ROLE}>Preparing</span>
                    </div>
                    <div role="status" className="mt-2 space-y-2">
                        <p className="text-sm text-text-muted">
                            {demoStepText(row.stepNumber, row.total, row.stepLabel)}
                        </p>
                        <div className="h-fixed-8px w-full overflow-hidden rounded bg-surface-hover">
                            <div
                                className="h-full bg-accent transition-all"
                                style={{ width: `${demoStepPercent(row.stepNumber, row.total)}%` }}
                            />
                        </div>
                    </div>
                </div>
            </li>
        );
    }

    return (
        <li>
            <div className="px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                    {name}
                    <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        pending={retryPending}
                        onClick={onRetry}
                    >
                        Try again
                    </Button>
                </div>
                <p role="alert" className="mt-2 flex items-center gap-2 text-sm text-state-danger">
                    <AlertTriangle className="size-icon-lg" aria-hidden />
                    {row.message}
                </p>
            </div>
        </li>
    );
}
