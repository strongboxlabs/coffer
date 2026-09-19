import type { ReactNode } from 'react';

import { ApiError } from '@/lib/api';
import { Button } from '@/components/ui/Button';
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
    isCategory = false,
    hasSubcategories = false,
    subtreeIncluded = false,
    onIncludeSubcategories,
    children,
}: {
    initialLoaded: boolean;
    initialError: unknown;
    isEmpty: boolean;
    /** When true, the empty state is caused by an active filter hiding rows —
     *  the account isn't actually empty — so the copy points at the filter. */
    filterActive?: boolean;
    /** This register belongs to a CATEGORY, not a real account. */
    isCategory?: boolean;
    /** …and that category has sub-categories, which is where its money is. */
    hasSubcategories?: boolean;
    /** The subtree is ALREADY in scope — so an empty register means the whole
     *  subtree is empty, not that the reader is looking at the wrong level. */
    subtreeIncluded?: boolean;
    /** Widen to the subtree. Present only when there is a subtree to widen to. */
    onIncludeSubcategories?: () => void;
    children: ReactNode;
}) {
    if (initialError) {
        return (
            <div className="p-6">
                <div
                    role="alert"
                    className="rounded border border-state-danger/40 bg-state-danger-soft/40 p-4 text-sm text-state-danger"
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
        // A PARENT CATEGORY IS EMPTY BY CONSTRUCTION — every posting sits on its
        // children. It is not an account waiting for an import, and saying so
        // sent people looking for data that was never going to arrive. This is
        // the one empty state that has an action attached, because the thing the
        // reader wants is one click away.
        const parentRollup = isCategory === true && hasSubcategories === true
            && filterActive !== true && subtreeIncluded !== true;
        return (
            <div className="p-6">
                <Panel className="border-dashed">
                    <PanelBody className="py-10 text-center">
                        <p className="text-sm font-medium text-text">
                            {filterActive
                                ? 'No transactions match the current filter.'
                                : parentRollup
                                    ? 'Nothing is filed directly under this category.'
                                    : isCategory === true
                                        ? 'No transactions in this category.'
                                        : 'No transactions in this account.'}
                        </p>
                        <p className="mt-2 text-sm text-text-muted">
                            {filterActive
                                ? 'Adjust or clear the search / filters above.'
                                : parentRollup
                                    ? 'Its transactions belong to its sub-categories.'
                                    : isCategory === true
                                        ? 'Transactions appear here once something is categorised this way.'
                                        : 'Import a statement or wait for the next sync.'}
                        </p>
                        {/* The app's own button, not a hand-rolled one. The
                            first version was accent-coloured text in a thin
                            box, which read as a warning link rather than an
                            action and matched nothing else on the page. */}
                        {parentRollup && onIncludeSubcategories !== undefined ? (
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                onClick={onIncludeSubcategories}
                                className="mt-4"
                            >
                                Include sub-categories
                            </Button>
                        ) : null}
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
