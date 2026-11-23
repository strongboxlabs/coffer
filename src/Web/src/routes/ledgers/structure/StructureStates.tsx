import type { ReactNode } from 'react';

import { Button } from '@/components/ui/Button';
import { EmptyState } from '@/components/ui/EmptyState';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { SkeletonRows } from '@/components/ui/Skeleton';

// The four states a list of Structure can be in besides showing rows (ADR-0111): on its
// way, empty, searched into nothing, and failed. Each is drawn once, here, so the tabs
// cannot disagree about how a list that has nothing to show looks.

/** The list is on its way: placeholder rows where it will land. */
export function ListLoading({ noun }: { noun: string }) {
    return (
        <Panel>
            <SkeletonRows rows={6} label={`Loading ${noun}`} />
        </Panel>
    );
}

/** The ledger has none yet. `action` is the way to make the first, for a reader who may. */
export function ListEmpty({ message, hint, action }: { message: string; hint: string; action?: ReactNode }) {
    return <EmptyState message={message} hint={hint} action={action} />;
}

/** The ledger has some, and the search matches none of them. */
export function ListNoMatches({ query, noun, onClear }: { query: string; noun: string; onClear: () => void }) {
    return (
        <EmptyState
            message={`Nothing matches “${query.trim()}”`}
            hint={`Check the spelling, or clear the search to see every ${noun}.`}
            action={<Button type="button" variant="secondary" size="sm" onClick={onClear}>Clear search</Button>}
        />
    );
}

/**
 * An action on a row that has no dialog of its own to say so in (Reactivate, Remove unused)
 * failed. Says so above the list, where the reader is looking, instead of leaving the row as it
 * was and the reader wondering whether anything happened.
 */
export function ActionError({ message }: { message: string }) {
    return (
        <p role="alert" className="rounded border border-state-danger/40 bg-state-danger-soft px-3 py-2 text-sm text-state-danger">
            {message}
        </p>
    );
}

/** The list could not be read. Says so, and offers the one thing that may help. */
export function ListError({ message, onRetry }: { message: string; onRetry: () => void }) {
    return (
        <Panel className="border-state-danger/40 bg-state-danger-soft">
            <PanelBody className="flex items-center justify-between gap-3">
                <p role="alert" className="text-sm text-state-danger">{message}</p>
                <Button type="button" variant="secondary" size="sm" onClick={onRetry}>Try again</Button>
            </PanelBody>
        </Panel>
    );
}
