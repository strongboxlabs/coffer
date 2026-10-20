import { Outlet } from '@tanstack/react-router';

import { AuthedSidebar } from '@/components/AuthedSidebar';
import { SidebarLayout } from '@/components/ui/SidebarLayout';

// React components that the router wires up. Only components are
// exported from this file so react-refresh / HMR boundaries stay
// clean; the router-tree factory lives in src/router.ts.

/** Root layout — every route renders inside this shell. */
export function RootLayout() {
    return (
        <div className="min-h-dvh bg-surface-muted text-text font-sans">
            <Outlet />
        </div>
    );
}

/**
 * Outlet wrapper for the authed subtree. The auth check itself is in
 * the route's `beforeLoad`; this component renders the persistent
 * sidebar shell (brand + ledger picker + grouped account nav + user
 * card) per ADR-0021. Each authed route's component renders the
 * `MainArea` (`TopBar` + `MainPane`) for its surface.
 */
export function AuthedOutlet() {
    return (
        <SidebarLayout>
            <AuthedSidebar />
            <Outlet />
        </SidebarLayout>
    );
}
