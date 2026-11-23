import {
    Fragment,
    useEffect,
    useMemo,
    useRef,
    useState,
} from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link, useLocation, useNavigate, useParams } from '@tanstack/react-router';
import {
    AlarmClock,
    ChevronDown,
    Cog,
    LayoutDashboard,
    LineChart,
    ListTree,
    LogOut,
    PanelLeftClose,
    PanelLeftOpen,
    RefreshCw,
    Settings,
    ShieldCheck,
    Target,
    BarChart3,
    ChevronRight,
} from 'lucide-react';

import {
    fetchAccountBalances,
    fetchAccountFolders,
    fetchAccounts,
    fetchAccountSubtotals,
    fetchCurrentUser,
    fetchFeedConnections,
    fetchVisibleLedgers,
    setAccountActive,
    syncAllConnections,
} from '@/lib/api';
import { buildSections, formatTotal, type TypeSection } from '@/lib/accountTree';
import { performLogout } from '@/lib/auth';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { formatCurrency } from '@/lib/money';
import { invalidateHomeReads, invalidateLedgerRegister } from '@/lib/registerInvalidation';
import { usePersistedDisclosure } from '@/lib/usePersistedDisclosure';
import type {
    AccountBalance,
    AccountFolder,
    AccountSummary,
    LedgerSummary,
} from '@/lib/types';
import {
    Sidebar,
    SidebarFooter,
    SidebarHeader,
    SidebarNav,
    SidebarNavLink,
} from '@/components/ui/SidebarLayout';
import { IconButton } from '@/components/ui/IconButton';
import {
    ContextMenu,
    type ContextMenuAnchor,
} from '@/components/ui/ContextMenu';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { PinnedReportsGroup, useHasReportPins } from './PinnedReportsGroup';
import { SidebarAccounts } from './SidebarAccounts';
import { SidebarResizeHandle } from './SidebarResizeHandle';
import { AttentionDot } from '@/components/AttentionDot';
import { useLedgerAttention } from '@/lib/useLedgerAttention';
import { useSystemAttention } from '@/lib/useSystemAttention';

// AuthedSidebar — left rail for every authed surface.
//
// Top to bottom: the wordmark with the System gear and the collapse button;
// the ledger picker; the ledger's destinations; then its accounts, one section
// per account type, with folders nested inside each (FU-006) and a balance on
// every row (FU-052). SidebarAccounts draws and edits that list.
//
// The rail's shape is the reader's, per browser (FU-051, ADR-0057's amendment):
// a handle on the right edge resizes it, and the header button or Ctrl+\
// collapses it to a 56px column of icons. Both are remembered in localStorage,
// and the width is applied before first paint by public/theme-init.js.

const DESTINATIONS = [
    { to: '/ledgers/$ledgerId', label: 'Overview', icon: LayoutDashboard, suffix: '' },
    { to: '/ledgers/$ledgerId/budget', label: 'Budget', icon: Target, suffix: '/budget' },
    // Structure (ADR-0111): accounts, categories and tags, one entry with a tab each. It opens
    // on Accounts. Exact on that path, because /accounts/<id> is a register, which is not
    // Structure; the other two tabs are named so they light the entry too.
    {
        to: '/ledgers/$ledgerId/accounts', label: 'Structure', icon: ListTree, suffix: '/accounts',
        exact: true, alsoUnder: ['/categories', '/tags'],
    },
    { to: '/ledgers/$ledgerId/reports', label: 'Reports', icon: BarChart3, suffix: '/reports' },
    { to: '/ledgers/$ledgerId/securities', label: 'Securities', icon: LineChart, suffix: '/securities' },
    { to: '/ledgers/$ledgerId/reminders', label: 'Reminders', icon: AlarmClock, suffix: '/reminders' },
    { to: '/ledgers/$ledgerId/settings', label: 'Settings', icon: Settings, suffix: '/settings' },
] as const;

type Destination = (typeof DESTINATIONS)[number];

function isActiveDestination(d: Destination, ledgerId: string, pathname: string): boolean {
    const base = `/ledgers/${ledgerId}`;
    const target = `${base}${d.suffix}`;
    if (d.suffix === '') return pathname === base;
    if ('exact' in d && d.exact) {
        if (pathname === target) return true;
        return 'alsoUnder' in d
            && d.alsoUnder.some((s) => pathname === `${base}${s}` || pathname.startsWith(`${base}${s}/`));
    }
    return pathname === target || pathname.startsWith(`${target}/`);
}

/** The keyboard shortcut that collapses and expands the rail. */
const TOGGLE_HINT = 'Ctrl+\\';

export function AuthedSidebar() {
    const queryClient = useQueryClient();
    const navigate = useNavigate();

    const { ledgerId: routeLedgerId, accountId } = useParams({ strict: false }) as {
        ledgerId?: string;
        accountId?: string;
    };
    const { pathname } = useLocation();


    const userQuery = useQuery({
        queryKey: ['me'],
        queryFn: fetchCurrentUser,
    });
    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });

    // Effective ledger for the rail. On non-ledger surfaces (e.g. /system, the
    // landing) the route carries no ledgerId, which would collapse the rail to a
    // dead "No ledger selected". Fall back to the last ledger viewed this
    // session, then to the first visible ledger — so the destinations + account
    // nav stay put and clicking one returns to a ledger. Only truly empty when
    // the user has no ledgers at all.
    const [lastLedgerId, setLastLedgerId] = useState<string | undefined>(undefined);
    useEffect(() => {
        if (routeLedgerId) setLastLedgerId(routeLedgerId);
    }, [routeLedgerId]);
    const ledgerId = routeLedgerId ?? lastLedgerId ?? ledgersQuery.data?.[0]?.id;

    // One fetch for all three indicators in this rail, shared with the ones on the
    // Manage ledgers page and the Notifications tab (FU-049). React Query serves every
    // consumer of this key from a single request, which is what makes putting an
    // indicator on a component that renders on every page affordable.
    const attention = useLedgerAttention();
    // What the INSTALLATION needs, behind the gear (admins only; ADR-0102 amended for ADR-0110).
    const systemAttention = useSystemAttention();
    const gearLabel = systemAttention.gearSentence
        ? `System settings: ${systemAttention.gearSentence}`
        : 'System settings';
    const thisLedger = attention.severityFor(ledgerId);
    const thisLedgerCount = attention.countFor(ledgerId);

    // "Sync all" lives next to the ledger picker — the ledger-wide pull, shown
    // only when this ledger actually has bank connections.
    const connectionsQuery = useQuery({
        queryKey: ['feed-connections', ledgerId],
        queryFn: () => fetchFeedConnections(ledgerId!),
        enabled: !!ledgerId,
    });
    const hasConnections = (connectionsQuery.data?.length ?? 0) > 0;
    const syncAllMutation = useMutation({
        mutationFn: () => syncAllConnections(ledgerId!),
        // A ledger-wide pull touches transactions + balances across accounts.
        // Refresh the register surface (rows via the ADR-0079 canonical key, plus
        // buckets / accounts / holdings) and the feed status the settings panel
        // shows — instead of a blanket invalidate that still couldn't reach the
        // register's bespoke row window.
        onSuccess: () => {
            invalidateLedgerRegister(queryClient, ledgerId!);
            queryClient.invalidateQueries({ queryKey: ['feed-connections', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['ledger-operations', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['feed-connection-accounts', ledgerId] });
        },
    });

    // Collapsed to the icon rail, per browser: the same persisted flag the
    // pinned-reports disclosure below uses (the hook reads it synchronously, so
    // the first render is already the remembered shape). Its toggle serves the
    // header button, the resize handle's Enter and Ctrl+\.
    // The stored flag is "collapsed", so its default is false: expanded.
    const [collapsed, toggleCollapsed] = usePersistedDisclosure('coffer.sidebar.collapsed', false);
    // Ctrl+\ from anywhere, unless the reader is typing (a backslash in a memo
    // is a backslash).
    useEffect(() => {
        function onKeyDown(e: KeyboardEvent) {
            if (e.key !== '\\' || !(e.ctrlKey || e.metaKey) || e.altKey) return;
            const el = e.target as HTMLElement | null;
            const tag = el?.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el?.isContentEditable) return;
            e.preventDefault();
            toggleCollapsed();
        }
        window.addEventListener('keydown', onKeyDown);
        return () => window.removeEventListener('keydown', onKeyDown);
    }, [toggleCollapsed]);
    // The button that toggled unmounts with the shape it belonged to; hand
    // focus to its counterpart rather than dropping it on the body. Only when
    // the shape actually changed: comparing with the previous value, not a
    // first-render flag, keeps a mount from taking focus even when StrictMode
    // runs the effect twice.
    const toggleRef = useRef<HTMLButtonElement | null>(null);
    const prevCollapsed = useRef(collapsed);
    useEffect(() => {
        if (prevCollapsed.current === collapsed) return;
        prevCollapsed.current = collapsed;
        if (document.activeElement === null || document.activeElement === document.body) {
            toggleRef.current?.focus();
        }
    }, [collapsed]);

    // Inactive-account lifecycle: when ON, inactive rows surface alongside
    // active ones (rendered greyed / strikethrough). Default OFF; session-scoped,
    // resets on refresh. The list always carries inactive accounts, because a
    // folder's or a section's total counts them either way (ADR-0085); this
    // decides only which rows are drawn.
    const [showInactive, setShowInactive] = useState(false);

    // Collapsed by default: the rail belongs to the account list, and pinned
    // reports are a shortcut over it that must not push accounts down the
    // moment someone pins a third thing. Remembered per browser, as the rest of
    // the rail's shape is.
    const [reportsOpen, toggleReportsOpen] = usePersistedDisclosure('coffer.sidebar.reportsOpen', false);
    const hasPins = useHasReportPins(ledgerId);
    const accountsQuery = useQuery({
        // The same cache the bank register reads (includeInactive: true).
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId!, { includeInactive: true }),
        enabled: ledgerId !== undefined,
    });
    // Under the ['accounts', ledgerId] prefix, so every write that refreshes the
    // account list refreshes the figures too.
    const balancesQuery = useQuery({
        queryKey: ['accounts', ledgerId, 'balances'],
        queryFn: () => fetchAccountBalances(ledgerId!),
        enabled: ledgerId !== undefined,
    });
    const foldersQuery = useQuery({
        queryKey: ['account-folders', ledgerId],
        queryFn: () => fetchAccountFolders(ledgerId!),
        enabled: ledgerId !== undefined,
    });
    // The section and folder totals, converted on the server (ADR-0114 D7). Under the
    // folders' key, so a folder write refreshes them, and refreshed with the home
    // screen's reads by every write that refreshes the accounts (invalidateHomeReads).
    const subtotalsQuery = useQuery({
        queryKey: ['account-folders', ledgerId, 'subtotals'],
        queryFn: () => fetchAccountSubtotals(ledgerId!),
        enabled: ledgerId !== undefined,
    });

    // Folders are ledger data: only an owner or an editor may change them.
    const { ledger, canEdit } = useLedgerRole(ledgerId);
    // useMemo so the `?? []` fallback doesn't churn array
    // identity each render — eliminates a downstream useMemo /
    // useEffect dep-equality warning.
    const accounts = useMemo<readonly AccountSummary[]>(
        () => accountsQuery.data ?? [],
        [accountsQuery.data],
    );
    const folders = useMemo<readonly AccountFolder[]>(
        () => foldersQuery.data ?? [],
        [foldersQuery.data],
    );
    const balances = useMemo<ReadonlyMap<string, AccountBalance>>(
        () => new Map((balancesQuery.data ?? []).map((b) => [b.accountId, b])),
        [balancesQuery.data],
    );
    const sections = useMemo(
        () => buildSections(accounts, folders, balances, subtotalsQuery.data, showInactive),
        [accounts, folders, balances, subtotalsQuery.data, showInactive],
    );

    // Inactive-account lifecycle: deactivate / reactivate flips
    // accounts.is_active. The shared prefix ['accounts', ledgerId] refreshes
    // the list and the figures; folder membership is unchanged. The home
    // screen's reads depend on it too, so they refresh beside it (ADR-0079).
    const setActiveMutation = useMutation({
        mutationFn: (args: { accountId: string; active: boolean }) =>
            setAccountActive(ledgerId!, args.accountId, args.active),
        onSuccess: () => {
            queryClient.invalidateQueries({
                queryKey: ['accounts', ledgerId],
            });
            invalidateHomeReads(queryClient, ledgerId!);
        },
    });
    // Confirm-dialog state for deactivation. Reactivation is silent
    // (it never loses information). The dialog stays generic in v1 —
    // the locked decision in follow-ups.md mentions surfacing balance
    // / open-position counts, but that requires fetches we don't
    // already have on hand at sidebar render time. Captured as a
    // refinement after this slice ships.
    const [deactivateConfirm, setDeactivateConfirm] = useState<AccountSummary | null>(null);

    // Ledger-switch popover, anchored under the picker button.
    const [ledgerMenuAnchor, setLedgerMenuAnchor] = useState<ContextMenuAnchor | null>(null);
    // Anchor the switch-ledger dropdown under the whole picker row, not
    // the small chevron button that opens it.
    const ledgerPickerRef = useRef<HTMLDivElement | null>(null);

    const logoutMutation = useMutation({
        mutationFn: performLogout,
        onSettled: () => {
            queryClient.clear();
            navigate({ to: '/login', replace: true });
        },
    });

    const displayName =
        userQuery.data?.displayName ?? userQuery.data?.username ?? '';

    const avatar = (
        <span className="flex size-control-24px shrink-0 items-center justify-center rounded-full bg-accent text-[0.625rem] font-semibold text-text-inverse">
            {displayName ? displayName[0]!.toUpperCase() : '·'}
        </span>
    );

    if (collapsed) {
        const activeType = accounts.find((a) => a.id === accountId)?.accountType;
        return (
            <Sidebar collapsed>
                <SidebarHeader className="flex-col gap-1 px-1.5">
                    {/* The dot rides on the logo's corner, as the merge badge
                        rides on a provenance icon, so the logo stays in the
                        column every rail icon is centred in. */}
                    <Link
                        to="/"
                        aria-label="Coffer"
                        title="Coffer"
                        className="relative flex items-center rounded hover:opacity-80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    >
                        <LineChart
                            className="size-icon-md text-accent"
                            strokeWidth={2.25}
                            aria-hidden
                        />
                        <AttentionDot
                            severity={attention.worst}
                            context="Coffer"
                            className="absolute -right-1 -top-1"
                        />
                    </Link>
                    <IconButton
                        ref={toggleRef}
                        aria-label="Expand sidebar"
                        title={`Expand sidebar (${TOGGLE_HINT})`}
                        onClick={toggleCollapsed}
                    >
                        <PanelLeftOpen className="size-icon-sm" aria-hidden />
                    </IconButton>
                </SidebarHeader>

                <SidebarNav>
                    {ledgerId ? (
                        <>
                            <div className="mb-1 space-y-px">
                                {DESTINATIONS.map((d) => {
                                    const Icon = d.icon;
                                    return (
                                        <SidebarNavLink
                                            key={d.label}
                                            asChild
                                            active={isActiveDestination(d, ledgerId, pathname)}
                                            className="justify-center"
                                        >
                                            <Link to={d.to} params={{ ledgerId }} activeOptions={{ exact: true }} aria-label={d.label} title={d.label}>
                                                {/* The dot on the icon's corner rather than
                                                    beside it: a 56px rail has no room for both
                                                    in a row, and beside it would push Settings'
                                                    icon out of the column the others sit in. */}
                                                <span className="relative flex shrink-0">
                                                    <Icon className="size-icon-sm" aria-hidden />
                                                    {d.suffix === '/settings' ? (
                                                        <AttentionDot
                                                            severity={thisLedger}
                                                            count={thisLedgerCount}
                                                            context="This ledger"
                                                            className="absolute -right-1 -top-1"
                                                        />
                                                    ) : null}
                                                </span>
                                            </Link>
                                        </SidebarNavLink>
                                    );
                                })}
                            </div>
                            {sections.length > 0 ? (
                                <div className="mt-1 space-y-px border-t border-border/60 pt-1">
                                    {sections.map((section) => (
                                        <CollapsedSection
                                            key={section.accountType}
                                            section={section}
                                            active={section.accountType === activeType}
                                            onExpand={toggleCollapsed}
                                        />
                                    ))}
                                </div>
                            ) : null}
                        </>
                    ) : null}
                </SidebarNav>

                <SidebarFooter className="flex justify-center">
                    <span title={displayName || 'Signed in'}>{avatar}</span>
                </SidebarFooter>
            </Sidebar>
        );
    }

    return (
        <Sidebar
            className="relative"
            // The reader's width, set before paint by theme-init.js and live by
            // the handle; with none set, the 224px token Sidebar uses.
            style={{ width: 'var(--coffer-sidebar-width, var(--spacing-fixed-224px))' }}
        >
            <SidebarHeader>
                {/* The wordmark goes home (ADR-0090). As an inert <span> the
                    universal "click the logo" gesture would do nothing — and
                    since nothing else in this rail links to `/`, the /system
                    breadcrumb would be the ONLY route back to the ledger
                    list. */}
                <Link
                    to="/"
                    // No aria-label: the visible "Coffer" text names it. An
                    // aria-label of "All ledgers" here collided with the nav item
                    // of the same name on /system — two links, one accessible
                    // name, indistinguishable to a screen reader.
                    className="flex items-center gap-1.5 rounded hover:opacity-80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                    <LineChart
                        className="size-icon-md text-accent"
                        strokeWidth={2.25}
                        aria-hidden
                    />
                    <span className="text-sm font-bold tracking-tight">Coffer</span>
                    {/* Any ledger, anywhere (FU-049). The rail is on every page, so
                        this is the only place the question "is something wrong
                        SOMEWHERE" can be answered without first choosing a ledger.

                        No count. A number here would be a sum across ledgers, which
                        answers a question nobody asks; counts belong where they are
                        actionable — the Manage ledgers row and the Notifications tab. */}
                    <AttentionDot
                        severity={attention.worst}
                        context="Coffer"
                        className="ml-0.5"
                    />
                </Link>
                <div className="flex items-center gap-0.5">
                    {/* System settings (ADR-0060) — deployment-wide: About
                        (version info, ADR-0044) + admin Backups. Anchored to the
                        app identity (the Coffer wordmark), NOT the user footer or
                        the per-ledger Settings: it's the install's settings, not
                        this ledger's or this user's. */}
                    {/* The gear answers for the install, as the wordmark answers for the ledgers:
                        a failed backup, a job switched off, or a release waiting, drawn by the
                        same dot. The sentence is the button's own label, so the dot is
                        decorative and not read a second time. */}
                    <span className="relative inline-flex">
                        <IconButton
                            aria-label={gearLabel}
                            title={systemAttention.gearSentence ? gearLabel : undefined}
                            onClick={() => navigate({ to: '/system' })}
                        >
                            <Cog className="size-icon-sm" aria-hidden />
                        </IconButton>
                        <span aria-hidden className="pointer-events-none absolute -right-0.5 -top-0.5">
                            <AttentionDot
                                severity={systemAttention.gear}
                                context="System settings"
                                sentence={gearLabel}
                            />
                        </span>
                    </span>
                    {/* The collapse button ADR-0021 Rule 1 always listed (FU-051). */}
                    <IconButton
                        ref={toggleRef}
                        aria-label="Collapse sidebar"
                        title={`Collapse sidebar (${TOGGLE_HINT})`}
                        onClick={toggleCollapsed}
                    >
                        <PanelLeftClose className="size-icon-sm" aria-hidden />
                    </IconButton>
                </div>
            </SidebarHeader>

            {/* Ledger picker — two controls:
                  • the name + ⌄ is one simple dropdown that switches
                    ledger;
                  • the ⋯ opens this ledger's page (the Hub), which is the
                    single home for settings / bank feeds / accounts.
                Switching ledgers and managing the current one are
                different intents, so they get separate controls — but
                management funnels through the Hub rather than sprouting
                a per-feature icon in the rail. */}
            <div
                ref={ledgerPickerRef}
                className="flex items-center gap-0.5 border-b border-border bg-surface px-2 py-1.5"
            >
                {ledgerId ? (
                        <button
                            type="button"
                            aria-label="Switch ledger"
                            aria-haspopup="menu"
                            title="Switch ledger"
                            className="flex min-w-0 flex-1 items-center gap-1.5 rounded px-2 py-1 text-xs font-semibold text-text hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                            onClick={() => {
                                const r = ledgerPickerRef.current?.getBoundingClientRect();
                                setLedgerMenuAnchor(
                                    r ? { x: r.left, y: r.bottom } : { x: 0, y: 0 },
                                );
                            }}
                        >
                            <span className="size-dot-sm shrink-0 rounded-sm bg-accent" />
                            <span className="min-w-0 flex-1 truncate text-left">
                                {ledger?.name ?? 'Ledger'}
                            </span>
                            <ChevronDown className="size-icon-sm shrink-0 text-text-subtle" aria-hidden />
                        </button>
                ) : (
                    /* No ledger at all (a fresh install, before the first one is
                       created or imported). Same control, same dropdown — it just
                       has no current ledger to name, so it offers the way in
                       rather than reporting a dead "No ledger selected". */
                    <button
                        type="button"
                        aria-label="Manage ledgers"
                        aria-haspopup="menu"
                        title="Manage ledgers"
                        className="flex min-w-0 flex-1 items-center gap-1.5 rounded px-2 py-1 text-xs font-semibold text-text-subtle hover:bg-surface-hover hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                        onClick={() => {
                            const r = ledgerPickerRef.current?.getBoundingClientRect();
                            setLedgerMenuAnchor(
                                r ? { x: r.left, y: r.bottom } : { x: 0, y: 0 },
                            );
                        }}
                    >
                        <span className="size-dot-sm shrink-0 rounded-sm bg-text-subtle/40" />
                        <span className="min-w-0 flex-1 truncate text-left">
                            Manage ledgers
                        </span>
                        {/* No ledger is selected, so the only scope available here is
                            "any of them" — the same aggregate the wordmark shows. */}
                        <AttentionDot severity={attention.worst} context="Some ledger" />
                        <ChevronDown className="size-icon-sm shrink-0 text-text-subtle" aria-hidden />
                    </button>
                )}
                {ledgerId && hasConnections ? (
                    <IconButton
                        aria-label={
                            syncAllMutation.isPending
                                ? 'Syncing all connections…'
                                : 'Sync all bank connections'
                        }
                        title="Sync all bank connections"
                        onClick={() => syncAllMutation.mutate()}
                        pending={syncAllMutation.isPending}
                    >
                        <RefreshCw
                            className={
                                'size-icon-sm text-text-subtle ' +
                                (syncAllMutation.isPending ? 'animate-spin' : '')
                            }
                            aria-hidden
                        />
                    </IconButton>
                ) : null}
            </div>

            <SidebarNav>
                {/* Per-ledger destinations — persistent, 1-click from anywhere
                    (replaces the Overview's in-page header nav + the obscure
                    ⋯-to-hub path). The account list follows below. */}
                {ledgerId ? (
                    <div className="mb-1 space-y-px">
                        {DESTINATIONS.map((d) => {
                            const active = isActiveDestination(d, ledgerId, pathname);
                            const Icon = d.icon;
                            // The Reports row carries a disclosure for its
                            // pinned reports. A separate "Pinned" header was
                            // one label too many: the rail already says
                            // Reports, and a second word underneath to say
                            // "these are the pinned ones" spends a line
                            // explaining what the indent shows.
                            const isReports = d.suffix === '/reports';
                            return (
                                <Fragment key={d.label}>
                                <div className={isReports && hasPins ? 'relative' : undefined}>
                                <SidebarNavLink asChild active={active}>
                                    {/* Exact, because the router's own "active" is a prefix match: the
                                        Overview's path begins every other page in the ledger and
                                        /accounts begins every register, so each would be announced as
                                        the current page on all of them. isActiveDestination decides
                                        what is current; the router must not claim more. */}
                                    <Link to={d.to} params={{ ledgerId }} activeOptions={{ exact: true }}>
                                        <Icon className="size-icon-sm shrink-0" aria-hidden />
                                        <span>{d.label}</span>
                                        {/* Only Settings carries it: everything this
                                            ledger has been told is reachable from
                                            there, and a dot on Overview would point at
                                            a screen that cannot explain it. Trailing,
                                            so the labels stay left-aligned. */}
                                        {d.suffix === '/settings' ? (
                                            <AttentionDot
                                                severity={thisLedger}
                                                count={thisLedgerCount}
                                                context="This ledger"
                                                className="ml-auto"
                                            />
                                        ) : null}
                                    </Link>
                                </SidebarNavLink>
                                {/* Beside the link, never inside it: a button
                                    nested in an anchor is invalid, and a click
                                    on it would also navigate. */}
                                {isReports && hasPins ? (
                                    <button
                                        type="button"
                                        aria-expanded={reportsOpen}
                                        aria-label={reportsOpen ? 'Hide pinned reports' : 'Show pinned reports'}
                                        onClick={toggleReportsOpen}
                                        className="absolute inset-y-0 right-1 flex items-center rounded px-1 text-text-subtle hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                    >
                                        {reportsOpen
                                            ? <ChevronDown aria-hidden className="size-icon-xs" />
                                            : <ChevronRight aria-hidden className="size-icon-xs" />}
                                    </button>
                                ) : null}
                                </div>
                                {/* Directly beneath Reports, which is the one
                                    destination it belongs to. Rendered after
                                    the whole map it would land under Settings —
                                    the last item — which reads as pinned
                                    reports being a settings thing. */}
                                {isReports && reportsOpen ? (
                                    <PinnedReportsGroup ledgerId={ledgerId} pathname={pathname} />
                                ) : null}
                                </Fragment>
                            );
                        })}
                    </div>
                ) : null}

                {/* The account list, separated from the destinations above by a
                    divider. Keyed by ledger, so each ledger's open folders are
                    its own. */}
                {ledgerId ? (
                    <div className="mt-1 border-t border-border/60 pt-1">
                        <SidebarAccounts
                            key={ledgerId}
                            ledgerId={ledgerId}
                            sections={sections}
                            folders={folders}
                            activeAccountId={accountId}
                            canEdit={canEdit}
                            onToggleActive={(account, active) => {
                                if (active) {
                                    // Reactivation is silent — never loses info.
                                    setActiveMutation.mutate({ accountId: account.id, active: true });
                                } else {
                                    setDeactivateConfirm(account);
                                }
                            }}
                        />
                    </div>
                ) : null}
            </SidebarNav>

            <SidebarFooter>
                {/* "Show inactive" toggle — surfaces deactivated
                    accounts (greyed / strikethrough) alongside active
                    ones. Session-scoped (resets on refresh). */}
                <label className="mb-1.5 flex cursor-pointer items-center gap-2 text-[0.6875rem] text-text-subtle hover:text-text">
                    <input
                        type="checkbox"
                        checked={showInactive}
                        onChange={(e) => setShowInactive(e.target.checked)}
                        className="size-icon-xs cursor-pointer"
                    />
                    <span>Show inactive accounts</span>
                </label>
                <div className="flex items-center gap-2">
                    {avatar}
                    <span className="flex-1 truncate text-xs font-medium text-text">
                        {displayName || 'Signed in'}
                    </span>
                    {/* Account security — manage passkeys + recovery codes
                        (ADR-0013). Per-user, so it lives in the user footer
                        (vs. the install-wide gear by the wordmark). */}
                    <IconButton
                        aria-label="Account security"
                        onClick={() => navigate({ to: '/account/security' })}
                    >
                        <ShieldCheck className="size-icon-sm" aria-hidden />
                    </IconButton>
                    <IconButton
                        aria-label="Sign out"
                        onClick={() => logoutMutation.mutate()}
                        pending={logoutMutation.isPending}
                    >
                        <LogOut className="size-icon-sm" aria-hidden />
                    </IconButton>
                </div>
            </SidebarFooter>

            <SidebarResizeHandle onToggleCollapse={toggleCollapsed} />

            {ledgerMenuAnchor ? (
                <ContextMenu
                    anchor={ledgerMenuAnchor}
                    items={[
                        ...(ledgersQuery.data ?? []).map((l) => ({
                            id: l.id,
                            // Mark the current ledger; selecting any ledger
                            // navigates to it (switching context).
                            label: (l.id === ledgerId ? '✓ ' : '') + l.name,
                            onSelect: () => {
                                navigate({
                                    to: '/ledgers/$ledgerId',
                                    params: { ledgerId: l.id },
                                });
                            },
                        })),
                        // Ledger MANAGEMENT lives with the ledgers (ADR-0090),
                        // not beside the System gear. `/` is the manage-ledgers
                        // surface — create, import, open — so its entry point
                        // belongs in the ledger dropdown, which is already the
                        // ledger domain. Putting it at rail top-level next to
                        // the gear would conflate ledger management with
                        // install-wide settings; they are unrelated.
                        {
                            id: '__manage__',
                            label: 'Manage ledgers…',
                            onSelect: () => navigate({ to: '/' }),
                        },
                    ]}
                    onClose={() => setLedgerMenuAnchor(null)}
                />
            ) : null}
            <ConfirmDialog
                open={deactivateConfirm !== null}
                title={`Deactivate "${deactivateConfirm?.name ?? ''}"?`}
                body={
                    <>
                        <p>
                            The account stays in your data — historical
                            transactions are preserved — but it disappears
                            from pickers and the sidebar's default view.
                        </p>
                        <p className="mt-2">
                            You can reactivate it later from the sidebar's
                            "Show inactive" view.
                        </p>
                    </>
                }
                confirmLabel="Deactivate"
                variant="danger"
                onConfirm={() => {
                    if (deactivateConfirm) {
                        setActiveMutation.mutate({
                            accountId: deactivateConfirm.id,
                            active: false,
                        });
                    }
                    setDeactivateConfirm(null);
                }}
                onCancel={() => setDeactivateConfirm(null)}
                isConfirming={setActiveMutation.isPending}
            />
        </Sidebar>
    );
}

/**
 * One account type on the collapsed rail: its icon, named with its total.
 * There is no tooltip primitive, so `title` carries what the section would
 * have shown at its top level. Choosing it opens the rail, where that section
 * is.
 */
function CollapsedSection({
    section,
    active,
    onExpand,
}: {
    section: TypeSection;
    active: boolean;
    onExpand: () => void;
}) {
    const Icon = section.icon;
    const total = formatTotal(section.total);
    const name = total === '' ? section.label : `${section.label}, ${total}`;
    const lines = [
        name,
        ...section.children.map((node) =>
            node.kind === 'folder'
                ? joinFigure(node.folder.name, formatTotal(node.total))
                : joinFigure(
                    node.account.name,
                    node.balance === undefined
                        ? ''
                        : formatCurrency(node.balance.balance, node.balance.currencyCode),
                )),
    ];
    return (
        <SidebarNavLink asChild active={active} className="w-full justify-center">
            <button type="button" aria-label={name} title={lines.join('\n')} onClick={onExpand}>
                <Icon className="size-icon-sm shrink-0" aria-hidden />
            </button>
        </SidebarNavLink>
    );
}

function joinFigure(name: string, figure: string): string {
    return figure === '' ? name : `${name}, ${figure}`;
}

// Re-export the LedgerSummary type so other modules can import it
// alongside the picker if desired.
export type { LedgerSummary };
