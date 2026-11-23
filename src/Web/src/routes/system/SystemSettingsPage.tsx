import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';

import { fetchCurrentUser } from '@/lib/api';
import { AttentionDot } from '@/components/AttentionDot';
import type { AttentionSeverity } from '@/lib/useLedgerAttention';
import { sentenceFor, useSystemAttention } from '@/lib/useSystemAttention';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { AboutPanel } from './AboutPanel';
import { AppearancePanel } from './AppearancePanel';
import { BackupsPanel } from './BackupsPanel';
import { MasterKeyPanel } from './MasterKeyPanel';
import { McpSettingsPanel } from './McpSettingsPanel';
import { McpConnectedAppsPanel } from './McpConnectedAppsPanel';
import { McpAuditPanel } from './McpAuditPanel';
import { NotificationsPanel } from './NotificationsPanel';
import { UpgradePanel } from './UpgradePanel';
import { UsersPanel } from './UsersPanel';

export type SystemTab =
    | 'about'
    | 'appearance'
    | 'backups'
    | 'encryption'
    | 'mcp'
    | 'notifications'
    | 'upgrade'
    | 'users';

/**
 * Absent or unrecognised → About, mirroring `coerceSettingsTab` for the
 * per-ledger page. Exported so the route's `validateSearch` and the sidebar's
 * System section agree with this page on what a valid tab is.
 */
export function coerceSystemTab(value: unknown): SystemTab {
    return value === 'appearance' || value === 'backups' || value === 'encryption'
        || value === 'mcp' || value === 'notifications' || value === 'upgrade' || value === 'users'
        ? value
        : 'about';
}

/**
 * `/system` — deployment-wide (non-ledger) settings (ADR-0060). Reached from
 * the gear by the brand. Tabbed like the per-ledger Settings page, but its
 * scope is the whole install: About (version, for everyone) plus the
 * admin-only tabs — Encryption, Backups, MCP, Users. Those are hidden for
 * non-admins and the underlying APIs are RequireAdmin — this page is UX, not
 * the security boundary. Future system tabs slot in here.
 */
/** Tabs every signed-in user gets, admin or not. */
const OPEN_TABS: ReadonlySet<SystemTab> = new Set<SystemTab>(['about', 'appearance']);

export function SystemSettingsPage() {
    const userQuery = useQuery({ queryKey: ['me'], queryFn: fetchCurrentUser });
    const isAdmin = userQuery.data?.isAdmin ?? false;

    // The trail the gear starts, followed down to the tab that holds the thing (ADR-0102, amended
    // for ADR-0110). Upgrade carries the upgrade alone and Notifications everything else, so one
    // subject never lights two tabs.
    const attention = useSystemAttention();
    const dots: Partial<Record<SystemTab, { severity: AttentionSeverity | null; sentence: string }>> = {
        upgrade: {
            severity: attention.upgrade,
            sentence: sentenceFor(0, true, attention.upgradeVersion) ?? '',
        },
        notifications: {
            severity: attention.problems,
            sentence: sentenceFor(attention.problemCount, false, null) ?? '',
        },
    };

    const tabs: ReadonlyArray<{ id: SystemTab; label: string }> = isAdmin
        ? [
              { id: 'about', label: 'About' },
              // Appearance sits with About as the two tabs everyone gets.
              { id: 'appearance', label: 'Appearance' },
              // Encryption before Backups: the master key is what makes a backup's
              // sealed secrets portable, so it's the more fundamental of the two —
              // and its own tab, not a card under Backups (ADR-0092). The key wraps
              // bank-feed tokens, the backup passphrase AND the Drive connection, so
              // filing it under one of the three would make "where is my master
              // key?" a hunt. It also has a lifecycle of its own (view, rotate).
              { id: 'encryption', label: 'Encryption' },
              { id: 'backups', label: 'Backups' },
              // Next to Backups on purpose: an upgrade starts with a backup and the
              // two are read together (ADR-0110).
              { id: 'upgrade', label: 'Upgrade' },
              { id: 'mcp', label: 'MCP' },
              // Notifications reports on whether a backup actually happened, as
              // Upgrade does on the backup it takes.
              { id: 'notifications', label: 'Notifications' },
              { id: 'users', label: 'Users' },
          ]
        : [
              { id: 'about', label: 'About' },
              { id: 'appearance', label: 'Appearance' },
          ];

    // The tab lives in the URL (ADR-0090), so it can be linked, bookmarked and
    // reached with the back button — and so the sidebar's System section can
    // point at individual tabs.
    //
    // Not component state seeded only from `?tab=backups` (the Google Drive
    // OAuth callback): then every other value falls through to About, so
    // `?tab=mcp` silently shows the wrong tab, and switching tabs leaves the URL
    // stale — a reload or a shared link never lands where you were.
    const navigate = useNavigate();
    const search = useSearch({ strict: false }) as { tab?: string };
    const tab = coerceSystemTab(search.tab);

    // If a non-admin somehow lands on an ADMIN tab, fall back to About. The
    // underlying APIs are RequireAdmin; this is UX, not the security boundary.
    // Listed as the open set rather than `!== 'about'`, so adding another
    // everyone-tab does not silently make it admin-only.
    const activeTab: SystemTab = OPEN_TABS.has(tab) || isAdmin ? tab : 'about';

    const setTab = (next: SystemTab) =>
        navigate({
            to: '/system',
            search: next === 'about' ? {} : { tab: next },
            replace: true,   // tab switching shouldn't stack history entries
        });

    return (
        <MainArea>
            <TopBar>
                {/* Just "System" (ADR-0090). "All ledgers / System" would
                    assert a hierarchy that does not exist: /system is a SIBLING
                    of / in the router, and its scope is the whole install — the
                    ledger list does not contain it. Nor is a breadcrumb the way
                    back to the ledger list; the sidebar owns that. */}
                <Breadcrumb items={[{ label: 'System' }]} />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header>
                        <h1 className="text-xl font-semibold tracking-tight">System</h1>
                        <p className="mt-0.5 text-sm text-text-muted">
                            Deployment-wide settings.
                        </p>
                    </header>

                    <nav
                        role="tablist"
                        aria-label="System sections"
                        className="flex items-end border-b border-border text-xs"
                    >
                        {tabs.map((t) => (
                            <button
                                key={t.id}
                                type="button"
                                role="tab"
                                aria-selected={activeTab === t.id}
                                onClick={() => setTab(t.id)}
                                className={
                                    '-mb-px border-b-2 px-3 py-1.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent '
                                    + (activeTab === t.id
                                        ? 'border-accent font-semibold text-text'
                                        : 'border-transparent text-text-muted hover:text-text')
                                }
                            >
                                {t.label}
                                {dots[t.id]?.severity ? (
                                    <>
                                        {/* A real space: the tab's accessible name is label then
                                            sentence, and inline children join without one. */}
                                        {' '}
                                        <AttentionDot
                                            severity={dots[t.id]!.severity}
                                            context={t.label}
                                            sentence={dots[t.id]!.sentence}
                                            className="ml-0.5 align-middle"
                                        />
                                    </>
                                ) : null}
                            </button>
                        ))}
                    </nav>

                    {activeTab === 'about' ? <AboutPanel /> : null}
                    {activeTab === 'appearance' ? <AppearancePanel /> : null}
                    {activeTab === 'backups' && isAdmin ? <BackupsPanel /> : null}
                    {activeTab === 'encryption' && isAdmin ? <MasterKeyPanel /> : null}
                    {activeTab === 'upgrade' && isAdmin ? (
                        <UpgradePanel onOpenBackups={() => setTab('backups')} />
                    ) : null}
                    {activeTab === 'notifications' && isAdmin ? (
                        <NotificationsPanel />
                    ) : null}
                    {activeTab === 'mcp' && isAdmin ? (
                        <div className="space-y-4">
                            <McpSettingsPanel />
                            <McpConnectedAppsPanel />
                            <McpAuditPanel />
                        </div>
                    ) : null}
                    {activeTab === 'users' && isAdmin ? <UsersPanel /> : null}
                </div>
            </MainPane>
        </MainArea>
    );
}
