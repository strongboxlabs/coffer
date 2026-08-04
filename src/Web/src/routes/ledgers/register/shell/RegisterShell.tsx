import type { ReactNode } from 'react';

import { RegisterStates } from './RegisterStates';

// Shared register chrome (ADR-0030 reuse; ADR-0021 revision).
//
// Both the bank and investment registers render their list through this
// one shell so the chrome can't drift — previously each page hand-rolled
// the surface + header band, and they diverged (the investment list was
// missing the white `bg-surface` wrapper, so its rows showed the grey
// app canvas, and the header-band padding was tuned differently on each
// side).
//
// The shell owns the white list surface (so rows read on white, not the
// grey canvas) and the grey column-header band (one definition, one
// padding scheme). Everything below the header — the scroll surface
// (see RegisterScrollSurface), and any footer / popovers / menus — is
// passed as `children`, so each page keeps its own structure and the
// footer stays a flow child pinned at the bottom of the flex column.

export interface RegisterShellProps {
    /** `grid-template-columns` shared by the header band and the rows. */
    columns: string;
    /** The `role="columnheader"` cells for the header band. */
    headerCells: ReactNode;
    /** Primary toolbar (e.g. the "+ New transaction" button + hints). */
    toolbar: ReactNode;
    /** Optional inline new-transaction editor, shown above the band. */
    newTxnEditor?: ReactNode;
    /** Optional block above the toolbar (e.g. the investment HoldingsPanel). */
    topSlot?: ReactNode;
    /** Everything below the header: the scroll surface + footer / overlays. */
    children: ReactNode;
    /** List-area gating (mig 164). The toolbar + column-header band ALWAYS
     *  render; only the list `children` swap to the loading / error / empty
     *  placeholder. This lives in the shell so the filter/search controls never
     *  unmount mid-search (focus + local search text preserved) and neither
     *  register page can reintroduce the "search box blanks on refetch" drift
     *  (a filter change resets the window → initialLoaded flips false). */
    initialLoaded: boolean;
    initialError: unknown;
    isEmpty: boolean;
    /** Filter-aware empty copy ("no matches" vs "no transactions"). */
    filterActive?: boolean;
}

export function RegisterShell({
    columns,
    headerCells,
    toolbar,
    newTxnEditor,
    topSlot,
    children,
    initialLoaded,
    initialError,
    isEmpty,
    filterActive,
}: RegisterShellProps) {
    return (
        <section
            aria-labelledby="register-heading"
            className="flex min-h-0 flex-1 flex-col bg-surface"
        >
            <h2 id="register-heading" className="sr-only">
                Register
            </h2>

            {topSlot}

            {/* Header group: toolbar + optional new-txn editor +
                column-header band. `shrink-0` so it sits above the
                scroll surface; `pr-12` shares the scroll surface's
                gutter so the AMOUNT/BALANCE headers line up with their
                cells. */}
            <div className="shrink-0 bg-surface pr-12">
                {toolbar}
                {newTxnEditor}
                <div
                    role="row"
                    style={{ gridTemplateColumns: columns }}
                    className="grid items-center gap-2 border-b border-border bg-surface-header px-3 py-1.5 text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted"
                >
                    {headerCells}
                </div>
            </div>

            {/* Only the LIST area gates on load/error/empty — the toolbar +
                header above stay mounted so the search box keeps focus. */}
            <RegisterStates
                initialLoaded={initialLoaded}
                initialError={initialError}
                isEmpty={isEmpty}
                filterActive={filterActive}
            >
                {children}
            </RegisterStates>
        </section>
    );
}
