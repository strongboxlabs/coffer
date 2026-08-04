import type { ReactNode } from 'react';

import { ApiError } from '@/lib/api';
import { Panel, PanelBody } from '@/components/ui/Panel';

/**
 * Loading / error / empty placeholder shared by BOTH registers (feedback:
 * registers unified by default). Renders exactly one of: an error alert, a
 * loading line, the empty-state panel, or — when the window has content — the
 * `children` (the register list). Precedence is error → loading → empty →
 * content, so a load failure always surfaces (bank previously checked loading
 * first, which could mask an error while `initialLoaded` was still false).
 *
 * Bank rendered a styled EmptyState + a typed error message; investment
 * rendered plain inline text. This is the single treatment for both.
 */
export function RegisterStates({
    initialLoaded,
    initialError,
    isEmpty,
    filterActive = false,
    children,
}: {
    initialLoaded: boolean;
    initialError: unknown;
    isEmpty: boolean;
    /** When true, the empty state is caused by an active filter hiding rows —
     *  the account isn't actually empty — so the copy points at the filter. */
    filterActive?: boolean;
    children: ReactNode;
}) {
    if (initialError) {
        return (
            <div className="p-6">
                <div
                    role="alert"
                    className="rounded border border-state-danger/40 bg-state-danger-bg/40 p-4 text-sm text-state-danger"
                >
                    {registerErrorMessage(initialError)}
                </div>
            </div>
        );
    }
    if (!initialLoaded) {
        return (
            <div className="p-6">
                <p className="text-sm text-text-subtle">Loading…</p>
            </div>
        );
    }
    if (isEmpty) {
        return (
            <div className="p-6">
                <Panel className="border-dashed">
                    <PanelBody className="py-10 text-center">
                        <p className="text-sm font-medium text-text">
                            {filterActive
                                ? 'No transactions match the current filter.'
                                : 'No transactions in this account.'}
                        </p>
                        <p className="mt-2 text-sm text-text-muted">
                            {filterActive
                                ? 'Adjust or clear the search / filters above.'
                                : 'Import a statement or wait for the next sync.'}
                        </p>
                    </PanelBody>
                </Panel>
            </div>
        );
    }
    return <>{children}</>;
}

/** Best-effort human message for a register load failure. */
function registerErrorMessage(error: unknown): string {
    if (error instanceof ApiError) return error.detail;
    if (
        typeof error === 'object' &&
        error !== null &&
        'message' in error &&
        typeof (error as { message: unknown }).message === 'string' &&
        (error as { message: string }).message.length > 0
    ) {
        return (error as { message: string }).message;
    }
    return 'Could not load the register.';
}
