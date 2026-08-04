import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';

import { fetchVisibleLedgers } from '@/lib/api';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { GeneralPanel } from './GeneralPanel';
import { MembersPanel } from './MembersPanel';
import { SnapshotsPanel } from './SnapshotsPanel';
import { FeedConnectionsPanel } from './FeedConnectionsPanel';
import { MarketDataPanel } from './MarketDataPanel';
import { DashboardLayoutPanel } from './DashboardLayoutPanel';
import { ActivityPanel } from './ActivityPanel';
import { SETTINGS_TABS, coerceSettingsTab, type SettingsTab } from './settingsTabs';

/**
 * `/ledgers/:ledgerId/settings` — per-ledger settings (ADR-0037 slice 2).
 * Tabbed: General (name + maintenance + danger zone), Snapshots (backups),
 * Bank feeds (SimpleFIN), Quotes (price providers), Activity (ledger-operation
 * timeline), and the Dashboard layout.
 *
 * The active tab is URL state (`?tab=`) so tabs deep-link and survive refresh,
 * and the Overview's "View activity" link can target the Activity tab directly
 * (ADR-0069 nav swap). Absent/invalid → General. Categories graduated to its
 * own top-level destination in the same swap.
 */
export function SettingsPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const navigate = useNavigate();
    const search = useSearch({ strict: false }) as { tab?: string };
    const tab = coerceSettingsTab(search.tab);
    const setTab = (next: SettingsTab) =>
        navigate({
            to: '/ledgers/$ledgerId/settings',
            params: { ledgerId },
            // Keep plain /settings clean: General is the default, so it carries
            // no ?tab (matches the route's validateSearch).
            search: next === 'general' ? {} : { tab: next },
        });

    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: ledger?.name ?? 'Ledger',
                            node: ledger ? (
                                <Link
                                    to="/ledgers/$ledgerId"
                                    params={{ ledgerId }}
                                    className="hover:text-text"
                                >
                                    {ledger.name}
                                </Link>
                            ) : (
                                'Ledger'
                            ),
                        },
                        { label: 'Settings' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header>
                        <h1 className="text-xl font-semibold tracking-tight">
                            Settings
                        </h1>
                        <p className="mt-0.5 text-sm text-text-muted">
                            Per-ledger administration.
                        </p>
                    </header>

                    <nav
                        role="tablist"
                        aria-label="Settings sections"
                        className="flex items-end border-b border-border text-xs"
                    >
                        {SETTINGS_TABS.map((t) => (
                            <button
                                key={t.id}
                                type="button"
                                role="tab"
                                aria-selected={tab === t.id}
                                onClick={() => setTab(t.id)}
                                className={
                                    tab === t.id
                                        ? '-mb-px border-b-2 border-accent px-3 py-1.5 font-semibold text-text'
                                        : '-mb-px border-b-2 border-transparent px-3 py-1.5 text-text-muted hover:text-text'
                                }
                            >
                                {t.label}
                            </button>
                        ))}
                    </nav>

                    {tab === 'general' ? <GeneralPanel ledgerId={ledgerId} /> : null}
                    {tab === 'members' ? <MembersPanel ledgerId={ledgerId} /> : null}
                    {tab === 'snapshots' ? <SnapshotsPanel ledgerId={ledgerId} /> : null}
                    {tab === 'feeds' ? <FeedConnectionsPanel ledgerId={ledgerId} /> : null}
                    {tab === 'quotes' ? <MarketDataPanel ledgerId={ledgerId} /> : null}
                    {tab === 'activity' ? <ActivityPanel ledgerId={ledgerId} /> : null}
                    {tab === 'dashboard' ? <DashboardLayoutPanel ledgerId={ledgerId} /> : null}
                </div>
            </MainPane>
        </MainArea>
    );
}
