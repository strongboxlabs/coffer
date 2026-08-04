import {
    useEffect,
    useMemo,
    useRef,
    useState,
    type KeyboardEvent,
} from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useLocation, useNavigate, useParams } from '@tanstack/react-router';
import {
    AlarmClock,
    ChevronDown,
    Cog,
    FolderTree,
    LayoutDashboard,
    LineChart,
    LogOut,
    Plus,
    RefreshCw,
    Settings,
    ShieldCheck,
    Tag,
    Wallet,
    type LucideIcon,
} from 'lucide-react';

import {
    addAccountGroupMember,
    createAccountGroup,
    deleteAccountGroup,
    fetchAccountGroups,
    fetchAccounts,
    fetchCurrentUser,
    fetchFeedConnections,
    fetchVisibleLedgers,
    patchAccountGroup,
    removeAccountGroupMember,
    setAccountActive,
    syncAllConnections,
} from '@/lib/api';
import { accountTypeMeta, accountTypeOrder } from '@/lib/accountTypes';
import { performLogout } from '@/lib/auth';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import type {
    AccountGroupSummary,
    AccountSummary,
    LedgerSummary,
} from '@/lib/types';
import {
    Sidebar,
    SidebarFooter,
    SidebarHeader,
    SidebarNav,
    SidebarNavLink,
    SidebarSection,
} from '@/components/ui/SidebarLayout';
import { IconButton } from '@/components/ui/IconButton';
import {
    ContextMenu,
    type ContextMenuAnchor,
    type ContextMenuItem,
} from '@/components/ui/ContextMenu';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';

// AuthedSidebar — left rail for every authed surface.
//
// User-curated "tabs" (migration 033): the strip below the ledger
// picker holds an "All" tab (built-in, always first) plus 0..N
// user-named tabs. Active tab filters the visible account list to
// just that tab's members; "All" shows every account grouped by
// type as before. Tab membership is per (user, ledger): a shared
// ledger has potentially-different curations per user.
//
// Two ways to mutate:
//   • Right-click an account → "Add to <tab>" / "Remove from <tab>".
//   • Right-click a user tab → "Rename" / "Delete".
//   • "+" at the end of the strip → inline new-tab input.
//
// Active-tab state is in-memory (defaults to "All" every refresh).
// Persisting last-active-tab is a follow-up.

interface AccountGroup {
    label: string;
    icon: LucideIcon;
    accounts: readonly AccountSummary[];
}

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
            queryClient.invalidateQueries({ queryKey: ['sync-runs', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['feed-connection-accounts', ledgerId] });
        },
    });

    // Inactive-account lifecycle: when ON, the sidebar fetches with
    // ?includeInactive=true so inactive rows surface alongside
    // active ones (rendered greyed / strikethrough). Default OFF;
    // session-scoped — resets on refresh (a per-user preference
    // store is a separate follow-up).
    const [showInactive, setShowInactive] = useState(false);
    const accountsQuery = useQuery({
        // Key includes showInactive so toggling refetches via cache
        // miss rather than reusing the stale active-only list.
        queryKey: ['accounts', ledgerId, { includeInactive: showInactive }],
        queryFn: () => fetchAccounts(ledgerId!, { includeInactive: showInactive }),
        enabled: ledgerId !== undefined,
    });
    const groupsQuery = useQuery({
        queryKey: ['account-groups', ledgerId],
        queryFn: () => fetchAccountGroups(ledgerId!),
        enabled: ledgerId !== undefined,
    });

    const ledger = findById(ledgersQuery.data, ledgerId);
    // useMemo so the `?? []` fallback doesn't churn array
    // identity each render — eliminates a downstream useMemo /
    // useEffect dep-equality warning.
    const accounts = useMemo<readonly AccountSummary[]>(
        () => accountsQuery.data ?? [],
        [accountsQuery.data],
    );
    const userGroups = useMemo<readonly AccountGroupSummary[]>(
        () => groupsQuery.data ?? [],
        [groupsQuery.data],
    );

    // Active tab. null = the implicit "All" tab. Re-set to null
    // when the ledger changes so we don't carry a stale group id
    // across ledgers (and so we land on "All" by default per the
    // design discussion).
    const [activeGroupId, setActiveGroupId] = useState<string | null>(null);
    useEffect(() => { setActiveGroupId(null); }, [ledgerId]);
    // If the active group is deleted (or its id is no longer in
    // the fetched list), fall back to "All" — avoids the rail
    // silently emptying after a delete.
    useEffect(() => {
        if (activeGroupId === null) return;
        if (!userGroups.some((g) => g.id === activeGroupId)) {
            setActiveGroupId(null);
        }
    }, [activeGroupId, userGroups]);

    const activeGroup = userGroups.find((g) => g.id === activeGroupId) ?? null;

    // Filter accounts to active tab's members when a user tab is
    // selected; otherwise show all. Either way the account-type
    // grouping (Banking / Credit / …) is applied below so the
    // reading model is consistent across tabs.
    const visibleAccounts = useMemo(() => {
        if (activeGroup === null) return accounts;
        const memberSet = new Set(activeGroup.memberAccountIds);
        return accounts.filter((a) => memberSet.has(a.id));
    }, [accounts, activeGroup]);

    const groups = useMemo(
        () => groupAccounts(visibleAccounts),
        [visibleAccounts],
    );

    const invalidateGroups = () =>
        queryClient.invalidateQueries({ queryKey: ['account-groups', ledgerId] });

    const createMutation = useMutation({
        mutationFn: (name: string) =>
            createAccountGroup(ledgerId!, { name }),
        onSuccess: (res) => {
            invalidateGroups();
            setActiveGroupId(res.id);
        },
    });
    const renameMutation = useMutation({
        mutationFn: (args: { groupId: string; name: string }) =>
            patchAccountGroup(ledgerId!, args.groupId, { name: args.name }),
        onSuccess: invalidateGroups,
    });
    const deleteMutation = useMutation({
        mutationFn: (groupId: string) =>
            deleteAccountGroup(ledgerId!, groupId),
        onSuccess: invalidateGroups,
    });
    const addMemberMutation = useMutation({
        mutationFn: (args: { groupId: string; accountId: string }) =>
            addAccountGroupMember(ledgerId!, args.groupId, args.accountId),
        onSuccess: invalidateGroups,
    });
    const removeMemberMutation = useMutation({
        mutationFn: (args: { groupId: string; accountId: string }) =>
            removeAccountGroupMember(ledgerId!, args.groupId, args.accountId),
        onSuccess: invalidateGroups,
    });
    // Inactive-account lifecycle: deactivate / reactivate flips
    // accounts.is_active. Invalidates both the accounts list (so
    // the deactivated row disappears from the default-filtered list)
    // and the groups query (memberships are unchanged but balance
    // roll-ups in A.5 read isActive on members).
    const setActiveMutation = useMutation({
        mutationFn: (args: { accountId: string; active: boolean }) =>
            setAccountActive(ledgerId!, args.accountId, args.active),
        onSuccess: () => {
            // Invalidate both flavors of the accounts query (default
            // active-only AND the includeInactive=true variant used
            // by the sidebar's "Show inactive" toggle). The shared
            // prefix ['accounts', ledgerId] catches both keys.
            queryClient.invalidateQueries({
                queryKey: ['accounts', ledgerId],
            });
            invalidateGroups();
        },
    });
    // Confirm-dialog state for deactivation. Reactivation is silent
    // (it never loses information). The dialog stays generic in v1 —
    // the locked decision in follow-ups.md mentions surfacing balance
    // / open-position counts, but that requires fetches we don't
    // already have on hand at sidebar render time. Captured as a
    // refinement after this slice ships.
    const [deactivateConfirm, setDeactivateConfirm] = useState<AccountSummary | null>(null);

    // Inline-edit state. `creatingTab` shows the "+ inline input";
    // `editingGroupId` flips a single tab's label into rename mode.
    const [creatingTab, setCreatingTab] = useState(false);
    const [editingGroupId, setEditingGroupId] = useState<string | null>(null);

    // Two distinct context menus, one for accounts, one for tabs.
    // Closing one closes both (only one is open at a time).
    const [accountMenu, setAccountMenu] = useState<{
        anchor: ContextMenuAnchor;
        account: AccountSummary;
    } | null>(null);
    const [groupMenu, setGroupMenu] = useState<{
        anchor: ContextMenuAnchor;
        group: AccountGroupSummary;
    } | null>(null);
    // Ledger-switch popover, anchored under the picker button.
    const [ledgerMenuAnchor, setLedgerMenuAnchor] = useState<ContextMenuAnchor | null>(null);
    // Anchor the switch-ledger dropdown under the whole picker row, not
    // the small chevron button that opens it.
    const ledgerPickerRef = useRef<HTMLDivElement | null>(null);
    const closeMenus = () => { setAccountMenu(null); setGroupMenu(null); };

    const logoutMutation = useMutation({
        mutationFn: performLogout,
        onSettled: () => {
            queryClient.clear();
            navigate({ to: '/login', replace: true });
        },
    });

    const displayName =
        userQuery.data?.displayName ?? userQuery.data?.username ?? '';

    const accountMenuItems: readonly ContextMenuItem[] = accountMenu
        ? buildAccountMenuItems(
            accountMenu.account,
            userGroups,
            activeGroup,
            (groupId, accountId_) => addMemberMutation.mutate({ groupId, accountId: accountId_ }),
            (groupId, accountId_) => removeMemberMutation.mutate({ groupId, accountId: accountId_ }),
            (account, active) => {
                if (active) {
                    // Reactivation is silent — never loses info.
                    setActiveMutation.mutate({ accountId: account.id, active: true });
                } else {
                    setDeactivateConfirm(account);
                }
            },
          )
        : [];

    const groupMenuItems: readonly ContextMenuItem[] = groupMenu
        ? [
            {
                id: 'rename',
                label: 'Rename',
                onSelect: () => { setEditingGroupId(groupMenu.group.id); },
            },
            {
                id: 'delete',
                label: 'Delete',
                danger: true,
                onSelect: () => { deleteMutation.mutate(groupMenu.group.id); },
            },
          ]
        : [];

    return (
        <Sidebar>
            <SidebarHeader>
                {/* The wordmark goes home (ADR-0090). It used to be an inert
                    <span>, so the universal "click the logo" gesture did
                    nothing — and since nothing else in this rail linked to `/`,
                    the /system breadcrumb was the ONLY route back to the ledger
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
                        className="h-4 w-4 text-accent"
                        strokeWidth={2.25}
                        aria-hidden
                    />
                    <span className="text-sm font-bold tracking-tight">Coffer</span>
                </Link>
                {/* System settings (ADR-0060) — deployment-wide: About
                    (version info, ADR-0044) + admin Backups. Anchored to the
                    app identity (the Coffer wordmark), NOT the user footer or
                    the per-ledger Settings: it's the install's settings, not
                    this ledger's or this user's. */}
                <IconButton
                    aria-label="System settings"
                    onClick={() => navigate({ to: '/system' })}
                >
                    <Cog className="h-3.5 w-3.5" aria-hidden />
                </IconButton>
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
                                setAccountMenu(null);
                                setGroupMenu(null);
                                setLedgerMenuAnchor(
                                    r ? { x: r.left, y: r.bottom } : { x: 0, y: 0 },
                                );
                            }}
                        >
                            <span className="h-1.5 w-1.5 shrink-0 rounded-sm bg-accent" />
                            <span className="min-w-0 flex-1 truncate text-left">
                                {ledger?.name ?? 'Ledger'}
                            </span>
                            <ChevronDown className="h-3.5 w-3.5 shrink-0 text-text-subtle" aria-hidden />
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
                            setAccountMenu(null);
                            setGroupMenu(null);
                            setLedgerMenuAnchor(
                                r ? { x: r.left, y: r.bottom } : { x: 0, y: 0 },
                            );
                        }}
                    >
                        <span className="h-1.5 w-1.5 shrink-0 rounded-sm bg-text-subtle/40" />
                        <span className="min-w-0 flex-1 truncate text-left">
                            Manage ledgers
                        </span>
                        <ChevronDown className="h-3.5 w-3.5 shrink-0 text-text-subtle" aria-hidden />
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
                        disabled={syncAllMutation.isPending}
                    >
                        <RefreshCw
                            className={
                                'h-3.5 w-3.5 text-text-subtle ' +
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
                        {([
                            { to: '/ledgers/$ledgerId', label: 'Overview', icon: LayoutDashboard, suffix: '' },
                            { to: '/ledgers/$ledgerId/accounts', label: 'Accounts', icon: Wallet, suffix: '/accounts', exact: true },
                            { to: '/ledgers/$ledgerId/categories', label: 'Categories', icon: FolderTree, suffix: '/categories' },
                            { to: '/ledgers/$ledgerId/tags', label: 'Tags', icon: Tag, suffix: '/tags' },
                            { to: '/ledgers/$ledgerId/securities', label: 'Securities', icon: LineChart, suffix: '/securities' },
                            { to: '/ledgers/$ledgerId/reminders', label: 'Reminders', icon: AlarmClock, suffix: '/reminders' },
                            { to: '/ledgers/$ledgerId/settings', label: 'Settings', icon: Settings, suffix: '/settings' },
                        ] as const).map((d) => {
                            const base = `/ledgers/${ledgerId}`;
                            const target = `${base}${d.suffix}`;
                            const active = d.suffix === ''
                                ? pathname === base
                                : 'exact' in d && d.exact
                                    ? pathname === target
                                    : pathname === target || pathname.startsWith(`${target}/`);
                            const Icon = d.icon;
                            return (
                                <SidebarNavLink key={d.label} asChild active={active}>
                                    <Link to={d.to} params={{ ledgerId }}>
                                        <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden />
                                        <span>{d.label}</span>
                                    </Link>
                                </SidebarNavLink>
                            );
                        })}
                    </div>
                ) : null}

                {/* The account section: filter tabs directly above the account
                    list they filter, separated from the destinations nav above
                    by a divider. */}
                {ledgerId ? (
                    <div className="mt-1 border-t border-border/60 pt-1">
                        <TabStrip
                            groups={userGroups}
                            activeGroupId={activeGroupId}
                            onSelect={(gid) => setActiveGroupId(gid)}
                            editingGroupId={editingGroupId}
                            onCommitRename={(gid, name) => {
                                renameMutation.mutate({ groupId: gid, name });
                                setEditingGroupId(null);
                            }}
                            onCancelRename={() => setEditingGroupId(null)}
                            creatingTab={creatingTab}
                            onStartCreate={() => setCreatingTab(true)}
                            onCommitCreate={(name) => {
                                createMutation.mutate(name);
                                setCreatingTab(false);
                            }}
                            onCancelCreate={() => setCreatingTab(false)}
                            onContextMenuGroup={(group, anchor) => {
                                setAccountMenu(null);
                                setGroupMenu({ anchor, group });
                            }}
                        />
                    </div>
                ) : null}
                {ledgerId && activeGroup !== null && visibleAccounts.length === 0 ? (
                    <div className="mx-3 mt-2 rounded border border-dashed border-border/60 px-2 py-1.5 text-[0.6875rem] italic leading-snug text-text-subtle">
                        No accounts in this tab yet. Right-click an account
                        (switch to <button
                            type="button"
                            className="not-italic font-medium text-accent hover:underline"
                            onClick={() => setActiveGroupId(null)}
                        >All</button>) to add it.
                    </div>
                ) : null}

                {ledgerId && groups.length > 0 ? (
                    <>
                        {groups.map((group) => (
                            <AccountGroupRows
                                key={group.label}
                                group={group}
                                ledgerId={ledgerId}
                                activeAccountId={accountId}
                                onContextMenuAccount={(account, anchor) => {
                                    setGroupMenu(null);
                                    setAccountMenu({ anchor, account });
                                }}
                            />
                        ))}
                    </>
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
                        className="h-3 w-3 cursor-pointer"
                    />
                    <span>Show inactive accounts</span>
                </label>
                <div className="flex items-center gap-2">
                    <span className="flex h-6 w-6 items-center justify-center rounded-full bg-accent text-[0.625rem] font-semibold text-text-inverse">
                        {displayName ? displayName[0]!.toUpperCase() : '·'}
                    </span>
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
                        <ShieldCheck className="h-3.5 w-3.5" aria-hidden />
                    </IconButton>
                    <IconButton
                        aria-label="Sign out"
                        onClick={() => logoutMutation.mutate()}
                        disabled={logoutMutation.isPending}
                    >
                        <LogOut className="h-3.5 w-3.5" aria-hidden />
                    </IconButton>
                </div>
            </SidebarFooter>

            {accountMenu ? (
                <ContextMenu
                    anchor={accountMenu.anchor}
                    items={accountMenuItems}
                    onClose={closeMenus}
                />
            ) : null}
            {groupMenu ? (
                <ContextMenu
                    anchor={groupMenu.anchor}
                    items={groupMenuItems}
                    onClose={closeMenus}
                />
            ) : null}
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
                confirmDisabled={setActiveMutation.isPending}
            />
        </Sidebar>
    );
}

// --------------------------------------------------------------------
// Tab strip
// --------------------------------------------------------------------

interface TabStripProps {
    groups: readonly AccountGroupSummary[];
    activeGroupId: string | null;
    onSelect: (groupId: string | null) => void;
    editingGroupId: string | null;
    onCommitRename: (groupId: string, name: string) => void;
    onCancelRename: () => void;
    creatingTab: boolean;
    onStartCreate: () => void;
    onCommitCreate: (name: string) => void;
    onCancelCreate: () => void;
    onContextMenuGroup: (
        group: AccountGroupSummary,
        anchor: ContextMenuAnchor,
    ) => void;
}

function TabStrip({
    groups,
    activeGroupId,
    onSelect,
    editingGroupId,
    onCommitRename,
    onCancelRename,
    creatingTab,
    onStartCreate,
    onCommitCreate,
    onCancelCreate,
    onContextMenuGroup,
}: TabStripProps) {
    return (
        <div
            role="tablist"
            aria-label="Account tabs"
            className="flex flex-wrap items-center gap-1 border-b border-border/60 px-2 py-1.5"
        >
            <Tab
                label="All"
                active={activeGroupId === null}
                onSelect={() => onSelect(null)}
            />
            {groups.map((g) =>
                editingGroupId === g.id ? (
                    <InlineTabInput
                        key={g.id}
                        initial={g.name}
                        onCommit={(name) => onCommitRename(g.id, name)}
                        onCancel={onCancelRename}
                    />
                ) : (
                    <Tab
                        key={g.id}
                        label={g.name}
                        active={activeGroupId === g.id}
                        onSelect={() => onSelect(g.id)}
                        onContextMenu={(anchor) => onContextMenuGroup(g, anchor)}
                    />
                ),
            )}
            {creatingTab ? (
                <InlineTabInput
                    initial=""
                    placeholder="New tab name"
                    onCommit={(name) => onCommitCreate(name)}
                    onCancel={onCancelCreate}
                />
            ) : (
                <button
                    type="button"
                    aria-label="New tab"
                    title="New tab"
                    onClick={onStartCreate}
                    className="flex h-6 w-6 items-center justify-center rounded text-text-subtle hover:bg-surface-hover hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                    <Plus className="h-3.5 w-3.5" aria-hidden />
                </button>
            )}
        </div>
    );
}

function Tab({
    label,
    active,
    onSelect,
    onContextMenu,
}: {
    label: string;
    active: boolean;
    onSelect: () => void;
    onContextMenu?: (anchor: ContextMenuAnchor) => void;
}) {
    return (
        <button
            type="button"
            role="tab"
            aria-selected={active}
            onClick={onSelect}
            onContextMenu={(e) => {
                if (!onContextMenu) return;
                e.preventDefault();
                onContextMenu({ x: e.clientX, y: e.clientY });
            }}
            className={
                'rounded px-2 py-0.5 text-[0.6875rem] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ' +
                (active
                    ? 'bg-accent text-text-inverse'
                    : 'text-text-muted hover:bg-surface-hover hover:text-text')
            }
        >
            {label}
        </button>
    );
}

function InlineTabInput({
    initial,
    placeholder,
    onCommit,
    onCancel,
}: {
    initial: string;
    placeholder?: string;
    onCommit: (name: string) => void;
    onCancel: () => void;
}) {
    const [value, setValue] = useState(initial);
    const inputRef = useRef<HTMLInputElement | null>(null);
    useEffect(() => {
        inputRef.current?.focus();
        inputRef.current?.select();
    }, []);
    function commitOrCancel() {
        const trimmed = value.trim();
        if (trimmed.length === 0) onCancel();
        else onCommit(trimmed);
    }
    function handleKeyDown(e: KeyboardEvent<HTMLInputElement>) {
        if (e.key === 'Enter') {
            e.preventDefault();
            commitOrCancel();
        } else if (e.key === 'Escape') {
            e.preventDefault();
            onCancel();
        }
    }
    return (
        <input
            ref={inputRef}
            type="text"
            value={value}
            placeholder={placeholder}
            onChange={(e) => setValue(e.target.value)}
            onKeyDown={handleKeyDown}
            onBlur={commitOrCancel}
            className="h-6 w-24 rounded border border-accent bg-surface px-1.5 text-[0.6875rem] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        />
    );
}

// --------------------------------------------------------------------
// Account groups + context menu items
// --------------------------------------------------------------------

function AccountGroupRows({
    group,
    ledgerId,
    activeAccountId,
    onContextMenuAccount,
}: {
    group: AccountGroup;
    ledgerId: string;
    activeAccountId: string | undefined;
    onContextMenuAccount: (
        account: AccountSummary,
        anchor: ContextMenuAnchor,
    ) => void;
}) {
    const Icon = group.icon;
    return (
        <>
            <SidebarSection>
                <span className="flex items-center gap-1.5">
                    <Icon className="h-3 w-3" aria-hidden />
                    {group.label}
                </span>
            </SidebarSection>
            {/* Offset each category's accounts under a faint left rail
                so the groups read as distinct blocks (ADR-0021). */}
            <div className="mb-1 ml-2 space-y-px border-l border-border pl-1.5">
            {group.accounts.map((account) => (
                <SidebarNavLink
                    key={account.id}
                    asChild
                    active={account.id === activeAccountId}
                    // Denser account list (overrides the default
                    // py-[0.3rem]) — the rail-grouped rows read fine
                    // tighter and fit more accounts per screen.
                    className="py-0.5"
                >
                    <Link
                        to="/ledgers/$ledgerId/accounts/$accountId"
                        params={{ ledgerId, accountId: account.id }}
                        className="flex w-full items-center gap-2"
                        onContextMenu={(e) => {
                            e.preventDefault();
                            onContextMenuAccount(account, {
                                x: e.clientX,
                                y: e.clientY,
                            });
                        }}
                    >
                        <span
                            className="h-1 w-1 rounded-full bg-text-subtle"
                            aria-hidden
                        />
                        {/* Inactive accounts (surfaced only when the
                            "Show inactive" toggle is on) render
                            opacity-60 + strikethrough so the user can
                            tell at a glance they're not in the
                            default-active set. Active accounts render
                            unchanged. */}
                        <span className={
                            'truncate ' +
                            (account.isActive ? '' : 'opacity-60 line-through')
                        }>
                            {account.name}
                        </span>
                        {account.needsReviewCount > 0 ? (
                            // Slice 2c.2: bank-feed review dot per
                            // ADR-0021 — present-vs-absent signal
                            // (not a number). Slate-teal accent on
                            // the accent palette. Title carries the
                            // count for screen readers + hover.
                            <span
                                className="ml-auto h-1.5 w-1.5 shrink-0 rounded-full bg-accent"
                                title={`${account.needsReviewCount} transaction${account.needsReviewCount === 1 ? '' : 's'} to review`}
                                aria-label={`${account.needsReviewCount} transactions to review`}
                            />
                        ) : null}
                    </Link>
                </SidebarNavLink>
            ))}
            </div>
        </>
    );
}

/** Build the context-menu items for an account row: which user
 *  tabs is it NOT yet in (= "Add to" entries), plus a "Remove from
 *  <currentTab>" item when viewing a user tab that contains it.
 *  Empty when there are no tabs at all — the menu UI guards on
 *  items.length and won't open. */
function buildAccountMenuItems(
    account: AccountSummary,
    groups: readonly AccountGroupSummary[],
    activeGroup: AccountGroupSummary | null,
    onAdd: (groupId: string, accountId: string) => void,
    onRemove: (groupId: string, accountId: string) => void,
    onToggleActive: (account: AccountSummary, active: boolean) => void,
): readonly ContextMenuItem[] {
    const items: ContextMenuItem[] = [];
    for (const g of groups) {
        const isMember = g.memberAccountIds.includes(account.id);
        if (isMember) continue;
        items.push({
            id: `add:${g.id}`,
            label: `Add to "${g.name}"`,
            onSelect: () => onAdd(g.id, account.id),
        });
    }
    if (activeGroup && activeGroup.memberAccountIds.includes(account.id)) {
        items.push({
            id: `remove:${activeGroup.id}`,
            label: `Remove from "${activeGroup.name}"`,
            danger: true,
            onSelect: () => onRemove(activeGroup.id, account.id),
        });
    }
    // Inactive-account lifecycle: deactivate / reactivate toggle.
    // System accounts (Holdings siblings, Uncategorized) are gated
    // by the API; the menu item is hidden for them to avoid showing
    // an option the user can't action. The toggle is always
    // available on user-created accounts.
    if (!account.isSystem) {
        items.push({
            id: 'toggle-active',
            label: account.isActive ? 'Deactivate' : 'Reactivate',
            danger: account.isActive,
            onSelect: () => onToggleActive(account, !account.isActive),
        });
    }
    if (items.length === 0) {
        items.push({
            id: 'noop',
            label: 'No tabs available — create one with the + above',
            disabled: true,
            onSelect: () => {},
        });
    }
    return items;
}

// --------------------------------------------------------------------
// Account-type grouping — one section per account type (Banking / Cash /
// Credit cards / Investments / Assets / Liabilities / Loans), labelled,
// iconed, and ordered by the shared accountTypes metadata so this list
// stays in lock-step with the Ledger Hub's account sections.
// --------------------------------------------------------------------

function groupAccounts(accounts: readonly AccountSummary[]): AccountGroup[] {
    const byType = new Map<string, AccountSummary[]>();

    for (const account of accounts) {
        // System rows (Holdings siblings, Uncategorized) stay hidden;
        // inactive rows surface here via the includeInactive fetch
        // (mig 106 collapsed the old is_hidden flag into is_active).
        if (account.isSystem) continue;
        // `holding` is the system-side sibling of an investment account —
        // fold it into Investments rather than grouping on its own.
        const type =
            account.accountType === 'holding'
                ? 'investment'
                : account.accountType;
        // `category` rows are budget categories, not accounts.
        if (type === 'category') continue;
        const bucket = byType.get(type);
        if (bucket) bucket.push(account);
        else byType.set(type, [account]);
    }

    return [...byType.entries()]
        .sort(([a], [b]) => accountTypeOrder(a) - accountTypeOrder(b))
        .map(([type, members]) => {
            const meta = accountTypeMeta(type);
            return { label: meta.label, icon: meta.icon, accounts: members };
        });
}

function findById<T extends { id: string }>(
    items: readonly T[] | undefined,
    id: string | undefined,
): T | undefined {
    if (!items || id === undefined) return undefined;
    return items.find((i) => i.id === id);
}

// Re-export the LedgerSummary type so other modules can import it
// alongside the picker if desired.
export type { LedgerSummary };
