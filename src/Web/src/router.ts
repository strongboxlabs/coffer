import {
    createRootRouteWithContext,
    createRoute,
    createRouter,
    redirect,
} from '@tanstack/react-router';
import type { QueryClient } from '@tanstack/react-query';

import { RootLayout, AuthedOutlet } from '@/App';
import { LoginPage } from '@/routes/login/LoginPage';
import { RecoveryLoginPage } from '@/routes/login/RecoveryLoginPage';
import { SetupPage } from '@/routes/setup/SetupPage';
import { InvitePage } from '@/routes/invite/InvitePage';
import { AccountSecurityPage } from '@/routes/account/AccountSecurityPage';
import { LandingPage } from '@/routes/landing/LandingPage';
import { ImportLedgerPage } from '@/routes/imports/ImportLedgerPage';
import { ConsentPage } from '@/routes/oauth/ConsentPage';
import { LedgerDetailPage } from '@/routes/ledgers/LedgerDetailPage';
import { AccountsManagementPage } from '@/routes/ledgers/accounts/AccountsManagementPage';
import { RegisterRouter } from '@/routes/ledgers/register/RegisterRouter';
import { SecuritiesCatalogPage } from '@/routes/ledgers/SecuritiesCatalogPage';
import { CategoriesPage } from '@/routes/ledgers/CategoriesPage';
import { TagsPage } from '@/routes/ledgers/TagsPage';
import { SecurityDetailPage } from '@/routes/ledgers/SecurityDetailPage';
import { SettingsPage } from '@/routes/ledgers/settings/SettingsPage';
import { RemindersPage } from '@/routes/ledgers/reminders/RemindersPage';
import { coerceSettingsTab, type SettingsTab } from '@/routes/ledgers/settings/settingsTabs';
import {
    SystemSettingsPage,
    coerceSystemTab,
    type SystemTab,
} from '@/routes/system/SystemSettingsPage';
import { StyleGuidePage } from '@/routes/__styleguide/StyleGuidePage';
import { fetchCurrentUser } from '@/lib/api';

// Code-based route tree. Every route declaration lives here in
// reading order, top-to-bottom. The alternative (file-based routing
// with codegen) was rejected for this codebase — see memory
// feedback_frontend_engineering_posture: explicit over magic.
//
// The router context carries the TanStack Query client so route
// loaders can use it without prop-drilling.

export interface RouterContext {
    queryClient: QueryClient;
}

// --- Root --------------------------------------------------------------
// The root route is the shell that every other route renders inside.
// Component lives in App.tsx so this file has no React JSX — that
// makes the react-refresh boundary cleanly "components-only files
// for HMR-tracked code."
const rootRoute = createRootRouteWithContext<RouterContext>()({
    component: RootLayout,
});

// --- Public routes -----------------------------------------------------
const loginRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/login',
    component: LoginPage,
});

// /login/recovery — account-recovery fallback (ADR-0013). Public, like
// /login: the recovery code IS the credential. Reached from a link on the
// passkey sign-in page.
const recoveryLoginRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/login/recovery',
    component: RecoveryLoginPage,
});

// /setup/$token — the first-run bootstrap ceremony. Public because
// the bootstrap token IS the authentication credential at this point;
// the API validates it server-side. The token rides in the path
// rather than a query param so the operator can paste a single URL
// to the user without quoting concerns (ADR-0013 design).
const setupRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/setup/$token',
    component: SetupPage,
});

// /invite/$token — accept an invite link (ADR-0083 slice B). Public, like /setup:
// the invite token IS the credential; the API validates it server-side.
const inviteRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/invite/$token',
    component: InvitePage,
});

// --- Protected routes --------------------------------------------------
// `beforeLoad` runs before the route's component renders. We check
// the auth state via the API: if the request returns 401, the loader
// redirects to /login. The check is fast (single round trip) and
// happens once per route navigation; TanStack Query caches the user
// for subsequent reads.
//
// Why a server round trip instead of a localStorage flag: the cookie
// is HttpOnly (the SPA can't read it), so the only authoritative
// "am I authenticated" answer comes from the server. localStorage is
// also a XSS target — we don't store auth state client-side.
const authedRoute = createRoute({
    getParentRoute: () => rootRoute,
    id: '_authed',
    beforeLoad: async ({ context, location }) => {
        try {
            await context.queryClient.fetchQuery({
                queryKey: ['me'],
                queryFn: fetchCurrentUser,
                staleTime: 30_000,
            });
        } catch (error) {
            if (isUnauthorized(error)) {
                throw redirect({
                    to: '/login',
                    search: { next: location.pathname },
                });
            }
            throw error;
        }
    },
    component: AuthedOutlet,
});

const landingRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/',
    component: LandingPage,
});

// /imports/moneydance — create a new ledger from a Moneydance export
// (ADR-0071 D2). Authed; any user can seed a new ledger they own. Reached
// from the "Import from Moneydance" action on the landing page.
const importLedgerRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/imports/moneydance',
    component: ImportLedgerPage,
});

// (/welcome was removed in ADR-0088. It existed to confirm the ledger setup
// had just created and point at the CLI importer — both obsolete: setup no
// longer creates a ledger, the in-app importer shipped, and the ledger hub at
// `/` is the post-setup landing.)

// /ledgers/$ledgerId — per-ledger detail (accounts list).
const ledgerDetailRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId',
    component: LedgerDetailPage,
});

// /ledgers/$ledgerId/accounts — accounts management (ADR-0050). The Ledger
// Hub's "Manage accounts →" links here for create / edit across all account
// types. Distinct from the /accounts/$accountId register route below (that
// one carries the id segment); the router matches by specificity.
const accountsManagementRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/accounts',
    component: AccountsManagementPage,
});

// /ledgers/$ledgerId/accounts/$accountId — per-account register
// (virtualised). Sibling route rather than nested under the detail
// route because the register is a full-page surface, not a child of
// the accounts list — they're alternative views into the same
// ledger.
//
// Search params:
//   * `focus` (optional) — owning header id of a row the register
//     should scroll-to + focus on load. Used by the "Show other side"
//     row action to navigate from one account's register to its
//     counterparty with the matching transaction still in view.
// /ledgers/$ledgerId/securities — Securities catalog (slice A3).
// Full management surface; the Ledger Hub's Securities section links
// here for search / add / edit / deactivate.
const securitiesCatalogRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/securities',
    component: SecuritiesCatalogPage,
});

// /ledgers/$ledgerId/settings — per-ledger settings (ADR-0037
// slice 2). Tabbed surface; snapshots is the only inhabitant in
// v1. Future neighbors (rename ledger, delete ledger, etc.) land
// alongside.
const settingsRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/settings',
    component: SettingsPage,
    // Active tab is URL state (ADR-0069 nav swap): tabs deep-link + survive
    // refresh, and the Overview's "View activity" link targets the Activity
    // tab. Absent/invalid → General, carried as a clean URL with no ?tab.
    validateSearch: (search: Record<string, unknown>): { tab?: SettingsTab } => {
        const tab = coerceSettingsTab(search.tab);
        return tab === 'general' ? {} : { tab };
    },
});

// /ledgers/$ledgerId/reminders — recurring-transaction reminders hub
// (ADR-0049): calendar + agenda (Upcoming) and the series List. Sibling of
// settings/feeds — a per-ledger surface, not a drilldown from the accounts list.
const remindersRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/reminders',
    component: RemindersPage,
});

// /ledgers/$ledgerId/categories — Categories destination (ADR-0069 nav swap:
// promoted from a Settings tab to a top-level nav surface). Manage the income /
// expense hierarchy; each category links to its register. Activity moved the
// other way — into a Settings tab (?tab=activity).
const categoriesRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/categories',
    component: CategoriesPage,
});

// /ledgers/$ledgerId/tags — Tags destination (Tags v1). Manage the tag
// dictionary (rename / recolour / merge / delete / cleanup); mirrors the
// Categories destination above.
const tagsRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/tags',
    component: TagsPage,
});

// /ledgers/$ledgerId/securities/$securityId — Securities Detail.
// Hero + recent transactions + recent prices.
const securityDetailRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/securities/$securityId',
    component: SecurityDetailPage,
});

const registerRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/ledgers/$ledgerId/accounts/$accountId',
    // A4.d Phase 1: dispatcher picks BankRegisterPage (today = the
    // legacy RegisterPage) or InvestmentRegisterPage based on the
    // resolved account's type. Bank/credit/cash/asset/liability/loan
    // accounts continue to use RegisterPage unchanged; investment
    // accounts get the new standalone page (ADR-0030 §3).
    component: RegisterRouter,
    // Return an object whose `focus` key is conditionally present
    // (not always-present-with-undefined). That distinction matters
    // to TanStack Router's type inference: if `focus` is always in
    // the returned object, `search` becomes required on every `<Link
    // to="/ledgers/...">` even when the caller doesn't care about
    // focus. The branched return narrows the inferred type to
    // `{ focus?: string }`, making the param truly optional at the
    // call site.
    validateSearch: (search: Record<string, unknown>): { focus?: string } =>
        typeof search.focus === 'string' ? { focus: search.focus } : {},
});

// /system — deployment-wide (non-ledger) settings (ADR-0060): About (everyone)
// + Backups (admin-only tab). No admin gate on the route itself — About is
// universal; the Backups tab self-hides for non-admins and the
// /api/admin/backups endpoints are RequireAdmin server-side regardless.
const systemRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/system',
    component: SystemSettingsPage,
    // Active tab is URL state, same contract as the per-ledger settings route
    // above (ADR-0069 / ADR-0090): tabs deep-link and survive refresh, and the
    // sidebar's System section links straight to them. Absent/invalid → About,
    // carried as a clean URL with no ?tab.
    //
    // Previously the tab was component state seeded ONLY from ?tab=backups (for
    // the Drive OAuth callback), so ?tab=mcp silently rendered About and
    // switching tabs left the URL stale.
    validateSearch: (search: Record<string, unknown>): { tab?: SystemTab } => {
        const tab = coerceSystemTab(search.tab);
        return tab === 'about' ? {} : { tab };
    },
});

// /account/security — per-user passkey + recovery-code management
// (ADR-0013 follow-through). Authed; the landing spot after a recovery-code
// sign-in (?recovered shows a re-key nudge).
const accountSecurityRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/account/security',
    component: AccountSecurityPage,
    validateSearch: (search: Record<string, unknown>): { recovered?: boolean } =>
        search.recovered === true || search.recovered === 'true' ? { recovered: true } : {},
});

// /oauth/consent — OAuth authorization consent (ADR-0063). Authed: /oauth/authorize
// (server) redirects an authenticated-but-not-yet-consented user here with the
// OAuth request preserved in the query; Allow/Deny POST it back to the server.
const oauthConsentRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/oauth/consent',
    component: ConsentPage,
});

// /__styleguide — dev-only route that renders every token + primitive
// from ADR-0021 in one auditable surface. Gated on `import.meta.env.DEV`
// so it's literally absent from the production bundle (the route is
// excluded from the tree, not just rendered conditionally). The
// double-underscore prefix is the convention for "system" routes that
// aren't part of the product surface.
const devRoutes = import.meta.env.DEV
    ? [
          createRoute({
              getParentRoute: () => rootRoute,
              path: '/__styleguide',
              component: StyleGuidePage,
          }),
      ]
    : [];

// --- Tree assembly -----------------------------------------------------
const routeTree = rootRoute.addChildren([
    loginRoute,
    recoveryLoginRoute,
    setupRoute,
    inviteRoute,
    authedRoute.addChildren([
        landingRoute,
        importLedgerRoute,
        ledgerDetailRoute,
        accountsManagementRoute,
        securitiesCatalogRoute,
        securityDetailRoute,
        settingsRoute,
        remindersRoute,
        categoriesRoute,
        tagsRoute,
        registerRoute,
        systemRoute,
        accountSecurityRoute,
        oauthConsentRoute,
    ]),
    ...devRoutes,
]);

export function createAppRouter(queryClient: QueryClient) {
    return createRouter({
        routeTree,
        defaultPreload: 'intent',
        context: { queryClient },
    });
}

/** Module-augmentation so router calls are type-safe. */
declare module '@tanstack/react-router' {
    interface Register {
        router: ReturnType<typeof createAppRouter>;
    }
}

function isUnauthorized(error: unknown): boolean {
    return (
        typeof error === 'object' &&
        error !== null &&
        'status' in error &&
        (error as { status: unknown }).status === 401
    );
}
