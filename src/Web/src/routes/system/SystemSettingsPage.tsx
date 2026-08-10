import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';

import { fetchCurrentUser } from '@/lib/api';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { AboutPanel } from './AboutPanel';
import { BackupsPanel } from './BackupsPanel';
import { MasterKeyPanel } from './MasterKeyPanel';
import { McpSettingsPanel } from './McpSettingsPanel';
import { McpClientsPanel } from './McpClientsPanel';
import { McpAuditPanel } from './McpAuditPanel';
import { UsersPanel } from './UsersPanel';

export type SystemTab = 'about' | 'backups' | 'encryption' | 'mcp' | 'users';

/**
 * Absent or unrecognised → About, mirroring `coerceSettingsTab` for the
 * per-ledger page. Exported so the route's `validateSearch` and the sidebar's
 * System section agree with this page on what a valid tab is.
 */
export function coerceSystemTab(value: unknown): SystemTab {
    return value === 'backups' || value === 'encryption' || value === 'mcp' || value === 'users'
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
export function SystemSettingsPage() {
    const userQuery = useQuery({ queryKey: ['me'], queryFn: fetchCurrentUser });
    const isAdmin = userQuery.data?.isAdmin ?? false;

    const tabs: ReadonlyArray<{ id: SystemTab; label: string }> = isAdmin
        ? [
              { id: 'about', label: 'About' },
              // Encryption before Backups: the master key is what makes a backup's
              // sealed secrets portable, so it's the more fundamental of the two —
              // and its own tab, not a card under Backups (ADR-0092). The key wraps
              // bank-feed tokens, the backup passphrase AND the Drive connection, so
              // filing it under one of the three made "where is my master key?" a
              // hunt. It also has a lifecycle of its own now (view, rotate).
              { id: 'encryption', label: 'Encryption' },
              { id: 'backups', label: 'Backups' },
              { id: 'mcp', label: 'MCP' },
              { id: 'users', label: 'Users' },
          ]
        : [{ id: 'about', label: 'About' }];

    // The tab lives in the URL (ADR-0090), so it can be linked, bookmarked and
    // reached with the back button — and so the sidebar's System section can
    // point at individual tabs.
    //
    // This used to be component state seeded ONLY from `?tab=backups` (for the
    // Google Drive OAuth callback). Every other value fell through to About, so
    // `?tab=mcp` silently showed the wrong tab, and switching tabs left the URL
    // stale — a reload or a shared link never landed where you were.
    const navigate = useNavigate();
    const search = useSearch({ strict: false }) as { tab?: string };
    const tab = coerceSystemTab(search.tab);

    // If a non-admin somehow lands on an admin tab, fall back to About. The
    // underlying APIs are RequireAdmin; this is UX, not the security boundary.
    const activeTab: SystemTab = tab !== 'about' && !isAdmin ? 'about' : tab;

    const setTab = (next: SystemTab) =>
        navigate({
            to: '/system',
            search: next === 'about' ? {} : { tab: next },
            replace: true,   // tab switching shouldn't stack history entries
        });

    return (
        <MainArea>
            <TopBar>
                {/* Just "System" (ADR-0090). This used to render
                    "All ledgers / System", asserting a hierarchy that does not
                    exist: /system is a SIBLING of / in the router, and its scope
                    is the whole install — the ledger list does not contain it.
                    The "All ledgers" crumb was also the only way back to the
                    ledger list, which made a breadcrumb load-bearing for
                    navigation; the sidebar now owns that. */}
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
                                    activeTab === t.id
                                        ? '-mb-px border-b-2 border-accent px-3 py-1.5 font-semibold text-text'
                                        : '-mb-px border-b-2 border-transparent px-3 py-1.5 text-text-muted hover:text-text'
                                }
                            >
                                {t.label}
                            </button>
                        ))}
                    </nav>

                    {activeTab === 'about' ? <AboutPanel /> : null}
                    {activeTab === 'backups' && isAdmin ? <BackupsPanel /> : null}
                    {activeTab === 'encryption' && isAdmin ? <MasterKeyPanel /> : null}
                    {activeTab === 'mcp' && isAdmin ? (
                        <div className="space-y-4">
                            <McpSettingsPanel />
                            <McpClientsPanel />
                            <McpAuditPanel />
                        </div>
                    ) : null}
                    {activeTab === 'users' && isAdmin ? <UsersPanel /> : null}
                </div>
            </MainPane>
        </MainArea>
    );
}
